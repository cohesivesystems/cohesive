using Cohesive.Execution;
using Cohesive.Processes.Compilation;
using Cohesive.Processes.Execution;

namespace Cohesive.Storage.Processes;

public sealed partial class ProcessDurableRuntime
{
    /// <summary>Advances an admitted local Process across a bounded number of immediately runnable durable cuts.</summary>
    /// <param name="context">Physical worker context and cancellation.</param>
    /// <param name="plan">Exact prepared Process plan.</param>
    /// <param name="continuation">Expected admitted attempt.</param>
    /// <param name="activationContext">Stable trusted activation binding, retained unchanged across recovery.</param>
    /// <param name="maximumActivations">Positive upper bound on activations attempted by this call.</param>
    /// <returns>The last native activation outcome. A DurableCut at the bound still requires another call;
    /// quiescence requires external evidence. This method does not claim background scheduling.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The activation bound is not positive.</exception>
    /// <exception cref="OperationCanceledException">Worker cancellation is requested.</exception>
    /// <remarks>Each activation retains the existing lease, checkpoint, operation receipt and commit machinery.
    /// Identity and logical time derive from the preceding checkpoint, so retrying after an uncommitted handoff
    /// reuses them. Provider and host failures propagate. No timers, inputs, retries or terminal results are invented.</remarks>
    public async Task<ProcessDurableActivationResult> AdvanceAsync(OperationContext context, CompiledProcessPlan plan,
        ProcessContinuationIdentity continuation, ProcessActivationContext activationContext, int maximumActivations = 128)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(continuation);
        ArgumentNullException.ThrowIfNull(activationContext);
        if (maximumActivations <= 0) throw new ArgumentOutOfRangeException(nameof(maximumActivations));
        ProcessDurableActivationResult? last = null;
        for (var step = 0; step < maximumActivations; step++)
        {
            context.ThrowIfCancellationRequested();
            var loaded = await store.LoadAsync(context, continuation.ProcessInstanceId).ConfigureAwait(false);
            if (loaded is null) return new(ProcessDurableRuntimeDisposition.NotFound);
            var checkpoint = loaded.Checkpoint;
            var validation = ProcessCheckpointCompatibilityValidator.Validate(plan, checkpoint);
            if (!validation.IsValid)
                return new(ProcessDurableRuntimeDisposition.Incompatible, loaded, diagnostics: validation.Diagnostics);
            if (checkpoint.ContinuationIdentity != continuation
                || checkpoint.Start.Request.Context.Authorization.AuthorityScope != activationContext.AuthorityScope)
                return new(ProcessDurableRuntimeDisposition.IdentityConflict, loaded);
            if (checkpoint.Continuation.Terminal.Kind != ExecutionTerminalOutcomeKind.None)
                return new(ProcessDurableRuntimeDisposition.Terminal, loaded);
            var ordinal = checkpoint.Activations.Length;
            var activation = new ProcessActivation(new($"local-advance/{ordinal}"),
                ordinal == 0 ? ProcessActivationCause.Start : ProcessActivationCause.Continue,
                checkpoint.UpdatedAtUtc, activationContext);
            last = await ActivateAsync(context, plan, continuation, activation).ConfigureAwait(false);
            if (last.Disposition is not (ProcessDurableRuntimeDisposition.Applied or ProcessDurableRuntimeDisposition.Replayed)
                || last.Decision?.Disposition != ProcessActivationDisposition.DurableCut)
                return last;
        }
        return last!;
    }
}
