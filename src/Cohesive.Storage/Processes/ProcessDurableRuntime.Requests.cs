using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Processes.Compilation;
using Cohesive.Processes.Execution;

namespace Cohesive.Storage.Processes;

public sealed partial class ProcessDurableRuntime
{
    /// <summary>Starts or resumes a bounded, sequential Request-driven Process using its canonical graph.</summary>
    /// <param name="context">Explicit cancellation and physical observation context.</param>
    /// <param name="plan">Exact compiled definition; the graph alone determines Request order and failure branches.</param>
    /// <param name="start">Original trusted start receipt, unchanged on every resume.</param>
    /// <param name="maximumCycles">Maximum dispatch or reply-consumption cycles in this invocation; positive, defaults to 64.</param>
    /// <returns>The last activation or blocking runtime result, including the current checkpoint and diagnostics. A successful runtime disposition does not imply semantic completion; inspect the checkpoint terminal outcome.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The cycle limit is not positive.</exception>
    /// <exception cref="OperationCanceledException">Caller cancellation was observed; retain original start and operation evidence.</exception>
    /// <remarks>
    /// This bounded convenience interpreter supports one outstanding Request at a time. It consumes only durably
    /// admitted replies, using their original admission timestamps and stable activation identities. Parallel
    /// Requests, external waits and unresolved operations return control without inventing a retry or bypassing
    /// a lease. All effects still pass through AdvanceOperationAsync and its declared binding/recovery policy.
    /// Physical store exceptions may propagate; callers must preserve exact evidence for reconciliation.
    /// </remarks>
    public async Task<ProcessDurableActivationResult> RunRequestsAsync(
        OperationContext context, CompiledProcessPlan plan, ProcessStartReceipt start, int maximumCycles = 64)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(start);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCycles);
        var initialized = await InitializeAsync(context, plan, start).ConfigureAwait(false);
        if (!Confirmed(initialized.Disposition))
            return new(initialized.Disposition, initialized.Snapshot, diagnostics: initialized.Diagnostics);
        var continuation = start.Request.InitialContinuation;
        var result = await ActivateAsync(context, plan, continuation,
            Activation("start", ProcessActivationCause.Start, start.AcceptedAtUtc)).ConfigureAwait(false);
        for (var cycle = 0; cycle < maximumCycles && Confirmed(result.Disposition); cycle++)
        {
            context.ThrowIfCancellationRequested();
            var snapshot = result.Snapshot!;
            var checkpoint = snapshot.Checkpoint;
            if (checkpoint.Continuation.Terminal.Kind != ExecutionTerminalOutcomeKind.None)
                return result;
            var replies = checkpoint.Inbox.Where(entry => entry.Receipt is null).ToArray();
            if (replies.Length == 1 && replies[0].Input.Envelope is ReplyEnvelope)
            {
                var reply = replies[0];
                result = await ActivateAsync(context, plan, continuation,
                    Activation("reply/" + reply.EmissionId.Value, ProcessActivationCause.Interaction,
                        reply.AdmittedAtUtc, [reply.Input])).ConfigureAwait(false);
                continue;
            }
            if (replies.Length != 0)
                return Blocked(snapshot, "Sequential Request execution requires one admitted reply.");
            var pending = checkpoint.DurableOperations.Where(operation => operation.Status != DurableOperationStatus.Dispositioned).ToArray();
            if (pending.Length != 1)
                return Blocked(snapshot, "Sequential Request execution requires exactly one outstanding operation; external waits or parallel work need another driver.");
            var advanced = await AdvanceOperationAsync(context, plan, continuation.ProcessInstanceId, pending[0].OperationId).ConfigureAwait(false);
            if (!Confirmed(advanced.Disposition))
                return new(advanced.Disposition, advanced.Snapshot, diagnostics: advanced.Diagnostics);
            if (advanced.Operation?.Status != DurableOperationStatus.Dispositioned)
                return Blocked(advanced.Snapshot!, "The original operation requires recovery: " + advanced.Operation?.Status);
            result = new(advanced.Disposition, advanced.Snapshot);
        }
        return Confirmed(result.Disposition) && result.Snapshot?.Checkpoint.Continuation.Terminal.Kind == ExecutionTerminalOutcomeKind.None
            ? Blocked(result.Snapshot!, "The bounded Request cycle budget was exhausted; resume the same start receipt.")
            : result;

        ProcessActivation Activation(string id, ProcessActivationCause cause, DateTimeOffset observedAtUtc,
            ImmutableArray<ProcessActivationInput> inputs = default) => new(new(id), cause, observedAtUtc,
                new(start.Request.Context.Authorization.AuthorityScope, new(continuation.ProcessInstanceId.Value),
                    new(InteractionDurabilityDemand.Durable, InteractionVisibilityDemand.AfterOriginCommit), plan.Document.Metadata.Provenance), inputs);
    }

    static bool Confirmed(ProcessDurableRuntimeDisposition disposition) =>
        disposition is ProcessDurableRuntimeDisposition.Applied or ProcessDurableRuntimeDisposition.Replayed;

    static ProcessDurableActivationResult Blocked(ProcessDurableStoreSnapshot snapshot, string message) =>
        new(ProcessDurableRuntimeDisposition.Unsupported, snapshot,
            diagnostics: [new(ProcessDurableRuntimeDiagnosticCodes.OperationRecoveryRequired, Cohesive.Model.DiagnosticSeverity.Error, message)]);
}
