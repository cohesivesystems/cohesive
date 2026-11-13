using Cohesive.Execution;
using Cohesive.Processes.Execution;
using Cohesive.Processes.Machines;

namespace Cohesive.Storage.Processes;

public sealed partial class MachineReactionHost
{
    async Task<(ProcessDurableStoreSnapshot Snapshot, MachineReactionProgress? Blocked)> AdmitPrerequisiteAsync(
        OperationContext context, ProcessDurableStoreSnapshot snapshot, MachineReactionInvocation invocation)
    {
        // The durable inbox is the handoff boundary. Once admitted, replay consumes the exact same proof.
        if (HasPrerequisiteProof(snapshot, invocation)) return (snapshot, null);
        var progress = await InspectPrerequisiteAsync(context, invocation).ConfigureAwait(false);
        if (progress.Status != MachineReactionStatus.Completed) return (snapshot, progress);

        var operation = progress.Receipt!.Request.Operation;
        var wait = snapshot.Checkpoint.Continuation.Waits.Single(item => item.Active && item.Node == MachineReaction.PrerequisiteNode);
        var target = new ProcessTokenInteractionTarget(snapshot.Checkpoint.ContinuationIdentity, wait.Token, wait.RegistrationId);
        var identity = snapshot.Checkpoint.ContinuationIdentity.ProcessInstanceId.Value + "/prerequisite";
        var signal = new SignalEnvelope(InteractionEnvelope.CurrentSchemaVersion,
            new(new(identity), new ProcessInteractionOrigin(prerequisite!.Policy.Payload.Process.Reference,
                operation.Node, operation.Continuation, operation.Activation, operation.Token),
                new(identity), causationId: null, Authority, new(identity), ordering: null,
                new(InteractionDurabilityDemand.Durable, InteractionVisibilityDemand.AfterOriginCommit), Policy.Document.Metadata.Provenance),
            new SignalContractReference(Policy.PrerequisiteSignal!.Reference),
            PortableValue.Concrete(new(new ScalarTypeRef(ScalarTypeKind.String)), ObservationValue.FromString(progress.Receipt.Commit.Fingerprint.Value)), target);
        var admitted = await store.AdmitInputAsync(context, snapshot.Checkpoint.ContinuationIdentity.ProcessInstanceId,
            new(target, signal), context.UtcNow).ConfigureAwait(false);
        if (admitted.Disposition is not (ProcessStoreMutationDisposition.Applied or ProcessStoreMutationDisposition.Replayed)
            || admitted.Snapshot is null)
            throw new InvalidOperationException("Prerequisite evidence admission is unconfirmed; recover the same reaction.");
        return (admitted.Snapshot, null);
    }

    bool HasPrerequisiteProof(ProcessDurableStoreSnapshot snapshot, MachineReactionInvocation invocation)
    {
        if (snapshot.Checkpoint.Inbox.IsEmpty) return false;
        var required = invocation.PrerequisiteInstance(prerequisite!.Policy);
        var found = false;
        foreach (var entry in snapshot.Checkpoint.Inbox)
        {
            if (entry.Input.Envelope is not SignalEnvelope signal
                || signal.Contract != new SignalContractReference(Policy.PrerequisiteSignal!.Reference)) continue;
            var wait = snapshot.Checkpoint.Continuation.Waits.SingleOrDefault(item => item.Node == MachineReaction.PrerequisiteNode);
            if (wait is null || signal.Target != new ProcessTokenInteractionTarget(snapshot.Checkpoint.ContinuationIdentity, wait.Token, wait.RegistrationId)
                || signal.Context.AuthorityScope != Authority
                || signal.Context.EmissionId.Value != snapshot.Checkpoint.ContinuationIdentity.ProcessInstanceId.Value + "/prerequisite"
                || signal.Context.IdempotencyKey.Value != signal.Context.EmissionId.Value
                || signal.Context.Origin is not ProcessInteractionOrigin origin || origin.Definition != prerequisite.Policy.Payload.Process.Reference
                || origin.Continuation != MachineReactionInvocation.ContinuationFor(required) || origin.Node != MachineReaction.InvocationNode
                || entry.Receipt is { Disposition: not ProcessInputAdmissionDisposition.Consumed })
                throw new InvalidOperationException("Retained prerequisite evidence has incompatible authority, origin, target or disposition.");
            found = true;
        }
        return found;
    }

    async Task<MachineReactionProgress> InspectPrerequisiteAsync(OperationContext context, MachineReactionInvocation invocation)
    {
        var required = invocation.PrerequisiteInstance(prerequisite!.Policy);
        return PrerequisiteDisposition(required, await ReadPrerequisiteAsync(context, required).ConfigureAwait(false));
    }

    async Task<MachineReactionProgress?> ReadPrerequisiteAsync(OperationContext context, ProcessInstanceId required)
    {
        var predecessor = await prerequisite!.LoadOwnedAsync(context, required).ConfigureAwait(false);
        if (predecessor is null) return null;
        await prerequisite.AuthorizeRecoveryAsync(context, predecessor).ConfigureAwait(false);
        return await prerequisite.InspectSnapshot(context, predecessor).ConfigureAwait(false);
    }

    static MachineReactionProgress PrerequisiteDisposition(ProcessInstanceId required, MachineReactionProgress? progress)
    {
        if (progress is null) return Waiting(required);
        if (progress.Status == MachineReactionStatus.Abandoned)
            return new(MachineReactionStatus.Failed, Diagnostic: new("machine.reaction.prerequisite.abandoned", DiagnosticSeverity.Error,
                $"Prerequisite '{required.Value}' was abandoned; explicitly abandon this dependent or reconcile the business intent."), Attempts: [], Prerequisite: required);
        if (progress.Status is MachineReactionStatus.Rejected or MachineReactionStatus.Failed)
            return new(MachineReactionStatus.Failed, Diagnostic: new(progress.Status == MachineReactionStatus.Rejected
                ? "machine.reaction.prerequisite.rejected" : "machine.reaction.prerequisite.failed",
                DiagnosticSeverity.Error, $"Prerequisite '{required.Value}' did not succeed; the dependent target has not been called."), Attempts: [], Prerequisite: required);
        if (progress.Status == MachineReactionStatus.Indeterminate)
            return new(MachineReactionStatus.Pending, Diagnostic: new("machine.reaction.prerequisite.indeterminate", DiagnosticSeverity.Warning,
                $"Prerequisite '{required.Value}' has uncertain target commitment; its receipt must be reconciled before dispatch."), Attempts: [], Prerequisite: required);
        return progress.Status == MachineReactionStatus.Completed ? progress : Waiting(required);
    }

    static MachineReactionProgress Waiting(ProcessInstanceId prerequisite) => new(MachineReactionStatus.Pending,
        Diagnostic: new("machine.reaction.prerequisite.waiting", DiagnosticSeverity.Info,
            $"Waiting for prerequisite '{prerequisite.Value}' and its successful target receipt."), Attempts: [], Prerequisite: prerequisite);
}
