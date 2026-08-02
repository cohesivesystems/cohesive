using Cohesive.Execution;
using Cohesive.Processes.Execution;
using Cohesive.Storage.Processes;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class ProcessDurableCheckpointChronologyTests
{
    [Fact]
    public void InboxDisposition_CannotPredateDurableAdmission()
    {
        var fixture = ProcessDurabilityTestFixture.Create();
        var entry = Assert.Single(fixture.Checkpoint.Inbox);
        var receipt = new ProcessInputReceipt(
            entry.Input,
            ProcessInputAdmissionDisposition.Stale,
            ProcessInputAdmissionReason.Stale,
            entry.AdmittedAtUtc.AddTicks(-1));

        Assert.Throws<ArgumentException>(() => new ProcessDurableInboxEntry(
            entry.Input,
            entry.AdmittedAtUtc,
            receipt,
            fixture.Checkpoint.ContinuationIdentity));
    }

    [Fact]
    public void ActivationCommit_CannotPredateItsObservation()
    {
        var fixture = ProcessDurabilityTestFixture.Create();

        Assert.Throws<ArgumentException>(() => new ProcessActivationCommitReceipt(
            sequence: 1,
            fixture.Checkpoint.ContinuationIdentity,
            ProcessStorageContentFingerprints.Continuation(fixture.Checkpoint.Continuation),
            ProcessStorageContentFingerprints.Continuation(fixture.Checkpoint.Continuation),
            fixture.Activation,
            fixture.Decision.Disposition,
            fixture.Decision.Evidence,
            fixture.Activation.ObservedAtUtc.AddTicks(-1)));
    }

    [Fact]
    public void Checkpoint_RejectsChildLedgerEvidenceAfterItsUpdate()
    {
        var fixture = ProcessDurabilityTestFixture.Create();
        var future = fixture.Checkpoint.UpdatedAtUtc.AddTicks(1);
        var operation = Assert.Single(fixture.Checkpoint.Operations);
        var inbox = Assert.Single(fixture.Checkpoint.Inbox);
        var emission = Assert.Single(fixture.Checkpoint.Emissions);

        Assert.Throws<ArgumentException>(() => Copy(
            fixture.Checkpoint,
            operations: [new(operation.Key, operation.OperationDefinition, operation.Result, future)]));
        Assert.Throws<ArgumentException>(() => Copy(
            fixture.Checkpoint,
            inbox: [new(inbox.Input, future)]));
        Assert.Throws<ArgumentException>(() => Copy(
            fixture.Checkpoint,
            emissions: [new(emission.Envelope, future)]));
    }

    [Fact]
    public void Checkpoint_RejectsNestedPhysicalAttemptEvidenceAfterItsUpdate()
    {
        var fixture = ProcessDurabilityTestFixture.Create();
        var operation = fixture.DurableOperation;
        var future = fixture.Checkpoint.UpdatedAtUtc.AddTicks(1);
        var claim = new DurableOperationClaim(
            new("operation-attempt/future"),
            "worker/future",
            new(1),
            future,
            future.Add(operation.Binding.ClaimLease));
        var advanced = new DurableOperationState(
            operation.SchemaVersion,
            operation.Request,
            operation.Binding,
            operation.CreatedAtUtc,
            [new(1, claim, DurableOperationAttemptStage.Claimed)],
            operation.Reconciliations,
            operation.RecoveryRequirement,
            operation.Acknowledgement,
            operation.Admission);

        Assert.Throws<ArgumentException>(() => Copy(
            fixture.Checkpoint,
            durableOperations: [advanced]));
    }

    static ProcessDurableCheckpoint Copy(
        ProcessDurableCheckpoint source,
        System.Collections.Immutable.ImmutableArray<ProcessOperationReceipt> operations = default,
        System.Collections.Immutable.ImmutableArray<ProcessDurableInboxEntry> inbox = default,
        System.Collections.Immutable.ImmutableArray<ProcessEmissionRecord> emissions = default,
        System.Collections.Immutable.ImmutableArray<DurableOperationState> durableOperations = default) =>
        new(
            source.SchemaVersion,
            source.Start,
            source.Continuation,
            source.Control,
            source.Activations,
            operations.IsDefault ? source.Operations : operations,
            inbox.IsDefault ? source.Inbox : inbox,
            emissions.IsDefault ? source.Emissions : emissions,
            durableOperations.IsDefault ? source.DurableOperations : durableOperations,
            source.CreatedAtUtc,
            source.UpdatedAtUtc);
}
