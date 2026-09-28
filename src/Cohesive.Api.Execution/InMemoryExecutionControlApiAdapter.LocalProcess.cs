using Cohesive.Execution;
using Cohesive.Processes.Compilation;
using Cohesive.Processes.Execution;
using Cohesive.Storage.Processes;

namespace Cohesive.Api.Execution;

public sealed partial class InMemoryExecutionControlApiAdapter
{
    /// <summary>Binds this adapter's existing in-memory start registry to local durable Process execution.</summary>
    /// <param name="plans">Exact prepared plan resolver; unknown definitions fail before admission.</param>
    /// <param name="runtimes">Invocation-scoped runtime binding for the admitted authority.</param>
    /// <param name="activationContext">Stable activation context projected from retained admission evidence.</param>
    /// <param name="maximumActivations">Maximum immediately runnable activations per dispatch.</param>
    /// <returns>A start dispatcher preserving the registry's command, idempotency and instance decisions.</returns>
    /// <exception cref="ArgumentNullException">A binding is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The activation bound is not positive.</exception>
    /// <exception cref="InvalidOperationException">This adapter delegates start admission elsewhere, an exact plan
    /// is missing, admission is rejected outside canonical start semantics, or local execution cannot proceed.</exception>
    /// <remarks>The registry lifetime remains in-memory. Checkpoint durability follows the supplied runtime.
    /// No background scheduling is promised: quiescence or a bounded cut requires later driving. Cancellation
    /// may interrupt work after admission; replay reuses the retained receipt. Do not expose this registry's
    /// independent lifecycle mutation endpoints as control over the supplied durable runtime.</remarks>
    public ExecutionProcessStartDispatcher CreateLocalProcessStartDispatcher(
        Func<ExecutionDefinitionReference, CompiledProcessPlan?> plans,
        Func<OperationContext, InteractionAuthorityScope, ProcessDurableRuntime> runtimes,
        Func<ProcessStartReceipt, ProcessActivationContext> activationContext,
        int maximumActivations = 128)
    {
        ArgumentNullException.ThrowIfNull(plans);
        ArgumentNullException.ThrowIfNull(runtimes);
        ArgumentNullException.ThrowIfNull(activationContext);
        if (maximumActivations <= 0) throw new ArgumentOutOfRangeException(nameof(maximumActivations));
        if (startDispatcher is not null)
            throw new InvalidOperationException("The local binding requires this adapter's own admission registry.");
        return async (context, request, invocation) =>
        {
            context.ThrowIfCancellationRequested();
            var plan = plans(request.Definition);
            if (plan is null || plan.DefinitionReference != request.Definition)
                throw new InvalidOperationException("The exact Process plan is not bound locally.");
            var dispatched = Dispatch(catalog.Start, request, invocation);
            if (dispatched.Body is not ProcessStartResult result)
                throw new InvalidOperationException("Local Process admission did not return a canonical start result.");
            if (result.IsConflict) return result;
            ProcessEntry entry;
            lock (processRegistryGate)
                entry = processes[new(invocation.Authorization.AuthorityScope, result.Admission!.Continuation.ProcessInstanceId)];
            var receipt = entry.Receipt;
            var runtime = runtimes(context, receipt.Request.Context.Authorization.AuthorityScope)
                ?? throw new InvalidOperationException("The local runtime binding returned null.");
            var initialized = await runtime.InitializeAsync(context, plan, receipt).ConfigureAwait(false);
            if (initialized.Disposition is not (ProcessDurableRuntimeDisposition.Applied or ProcessDurableRuntimeDisposition.Replayed))
                throw new InvalidOperationException($"Local Process initialization failed: {initialized.Disposition}.");
            var advanced = await runtime.AdvanceAsync(context, plan, receipt.Request.InitialContinuation,
                activationContext(receipt), maximumActivations).ConfigureAwait(false);
            if (advanced.Disposition is not (ProcessDurableRuntimeDisposition.Applied or ProcessDurableRuntimeDisposition.Replayed
                or ProcessDurableRuntimeDisposition.Terminal or ProcessDurableRuntimeDisposition.Paused))
                throw new InvalidOperationException($"Local Process advancement failed: {advanced.Disposition}.");
            if (advanced.Snapshot is { } snapshot)
                lock (entry.Gate) entry.State = snapshot.Checkpoint.Control;
            return result;
        };
    }
}
