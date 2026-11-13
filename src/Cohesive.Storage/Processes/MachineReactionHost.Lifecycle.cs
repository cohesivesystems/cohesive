using Cohesive.Execution;
using Cohesive.Processes.Execution;

namespace Cohesive.Storage.Processes;

/// <summary>Authorized evidence projection of a reaction's execution and operator lifecycle.</summary>
/// <param name="Instance">Stable reaction instance.</param>
/// <param name="Progress">Target evidence, physical attempts and prerequisite identity.</param>
/// <param name="Control">Canonical Process lifecycle, revision and attributable command receipts.</param>
/// <param name="PrerequisiteProgress">Current authorized prerequisite evidence, or null if absent or independent.</param>
/// <param name="Episode">Typed episode identity from the retained source snapshot, or null for non-episode policies.</param>
public sealed record MachineReactionInspection(ProcessInstanceId Instance, MachineReactionProgress Progress,
    ProcessControlState Control, MachineReactionProgress? PrerequisiteProgress, PortableValue? Episode = null);

public sealed partial class MachineReactionHost
{
    internal ProcessInstanceId? RequiredPrerequisite(ProcessStartRequest start)
    {
        if (prerequisite is null) return null;
        var invocation = MachineReactionInvocation.Restore(Policy, start);
        return invocation.PrerequisiteInstance(prerequisite.Policy);
    }

    /// <summary>Explains a reaction and its immediate prerequisite without executing either target.</summary>
    /// <param name="context">Trusted caller identity and store access.</param>
    /// <param name="instance">Exact instance to inspect.</param>
    /// <returns>Lifecycle and target evidence, or null for absent or unrelated work.</returns>
    /// <exception cref="ArgumentNullException">Context is null.</exception>
    /// <exception cref="UnauthorizedAccessException">Recovery/evidence access is not authorized.</exception>
    /// <exception cref="InvalidOperationException">Retained evidence is inconsistent.</exception>
    /// <exception cref="OperationCanceledException">The caller cancels inspection.</exception>
    public async Task<MachineReactionInspection?> InspectLifecycleAsync(OperationContext context, ProcessInstanceId instance)
    {
        ArgumentNullException.ThrowIfNull(context);
        var snapshot = await LoadOwnedAsync(context, instance).ConfigureAwait(false);
        if (snapshot is null) return null;
        return await InspectLifecycleSnapshotAsync(context, snapshot).ConfigureAwait(false);
    }

    internal async Task<MachineReactionInspection> InspectLifecycleSnapshotAsync(OperationContext context, ProcessDurableStoreSnapshot snapshot)
    {
        await AuthorizeRecoveryAsync(context, snapshot).ConfigureAwait(false);
        var invocation = MachineReactionInvocation.Restore(Policy, snapshot.Checkpoint.Start.Request);
        var progress = await InspectOwnSnapshot(context, snapshot, invocation).ConfigureAwait(false);
        MachineReactionProgress? dependency = null;
        if (prerequisite is not null && progress.Prerequisite is { } required)
        {
            dependency = await ReadPrerequisiteAsync(context, required).ConfigureAwait(false);
            if (RequiresPrerequisiteEvidence(snapshot, progress, invocation))
            {
                var disposition = PrerequisiteDisposition(required, dependency);
                if (disposition.Status != MachineReactionStatus.Completed)
                    progress = disposition with { Attempts = progress.Attempts, Prerequisite = required };
            }
        }
        return new(snapshot.Checkpoint.ContinuationIdentity.ProcessInstanceId, progress, snapshot.Checkpoint.Control, dependency,
            invocation.Episode);
    }

    /// <summary>Abandons future reaction execution using canonical cooperative Process cancellation.</summary>
    /// <remarks>Cancellation is not compensation or proof of non-commit. An interrupted target call may remain
    /// indeterminate after cancellation. Inspect its evidence; never create a new operation to retry uncertain work.
    /// No dependent is implicitly cancelled. Preserve the exact command when retrying an uncertain control response.</remarks>
    /// <param name="context">Trusted operator context.</param>
    /// <param name="command">Stable cancellation command carrying the inspected control revision and reason.</param>
    /// <param name="authorize">Fresh operator authorization for this exact command and retained start; recovery permission alone does not grant abandonment.</param>
    /// <returns>Canonical durable control result, including conflicts, replay and retained command evidence.</returns>
    /// <exception cref="ArgumentNullException">Context, command or authorization callback is null.</exception>
    /// <exception cref="UnauthorizedAccessException">Operator authorization or authority matching fails.</exception>
    /// <exception cref="InvalidOperationException">Retained evidence is inconsistent.</exception>
    /// <exception cref="OperationCanceledException">The caller cancels the operation before a physical boundary.</exception>
    public async Task<ProcessDurableControlResult> AbandonAsync(OperationContext context, CancelProcessCommand command,
        Func<OperationContext, ProcessStartRequest, CancelProcessCommand, ValueTask<bool>> authorize)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(authorize);
        var snapshot = await LoadOwnedAsync(context, command.Context.ProcessInstanceId).ConfigureAwait(false);
        if (snapshot is null) return new(ProcessDurableRuntimeDisposition.NotFound);
        if (command.Context.Authorization.AuthorityScope != Authority
            || !await authorize(context, snapshot.Checkpoint.Start.Request, command).ConfigureAwait(false))
            throw new UnauthorizedAccessException("Reaction abandonment is denied.");
        var start = snapshot.Checkpoint.Start.Request;
        return await runtime.CancelAsync(context, plan, command,
            new(Authority, new(start.InitialContinuation.ProcessInstanceId.Value),
                new(InteractionDurabilityDemand.Durable, InteractionVisibilityDemand.AfterOriginCommit), start.Context.Provenance)).ConfigureAwait(false);
    }

    async Task<ProcessDurableStoreSnapshot?> LoadOwnedAsync(OperationContext context, ProcessInstanceId instance)
    {
        var snapshot = await store.LoadAsync(context, instance).ConfigureAwait(false);
        return snapshot is not null && snapshot.Checkpoint.Start.Request.Definition == Policy.Payload.Process.Reference
            && snapshot.Checkpoint.Start.Request.Context.Authorization.AuthorityScope == Authority ? snapshot : null;
    }

    async ValueTask AuthorizeRecoveryAsync(OperationContext context, ProcessDurableStoreSnapshot snapshot)
    {
        if (recovery is null || !await recovery.Authorize(context, snapshot.Checkpoint.Start.Request).ConfigureAwait(false))
            throw new UnauthorizedAccessException("Reaction recovery access is denied.");
    }
}
