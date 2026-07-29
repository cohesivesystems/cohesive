using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Processes.Execution;
using Cohesive.Processes.IR;
using Cohesive.Storage.Processes;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class InMemoryProcessDurableStoreSuccessorRegressionTests
{
    const string Worker = "worker/successor-regressions";

    static readonly DateTimeOffset StartedAtUtc = ProcessDurabilityTestFixture.CheckpointedAtUtc;

    static OperationContext Context { get; } = OperationContext.Create();

    [Fact]
    public async Task Commit_AcceptsRenewalDispatchAndAcknowledgementOfExistingAttempt()
    {
        var fixture = ProcessDurabilityTestFixture.Create(
            definitionId: "process/durable-store/attempt-success",
            semanticVariant: "attempt-success");
        var executor = Executor(fixture);
        var store = new InMemoryProcessDurableStore();
        var snapshot = await InitializeAndAcquireAsync(store, fixture.Checkpoint);
        OperationAttemptId attemptId = new("operation-attempt/success");

        var claimed = executor.Claim(
            fixture.DurableOperation,
            attemptId,
            "operation-worker/success",
            StartedAtUtc.AddMinutes(2));
        Assert.Equal(DurableOperationClaimDisposition.Claimed, claimed.Disposition);
        snapshot = await CommitOperationAsync(
            store,
            snapshot,
            "commit/operation/claimed",
            claimed.State,
            StartedAtUtc.AddMinutes(2));

        var renewed = executor.RenewClaim(
            claimed.State,
            attemptId,
            Assert.IsType<DurableOperationClaim>(claimed.Claim).Fence,
            "operation-worker/success",
            StartedAtUtc.AddMinutes(3));
        Assert.Equal(DurableOperationRenewalDisposition.Renewed, renewed.Disposition);
        snapshot = await CommitOperationAsync(
            store,
            snapshot,
            "commit/operation/renewed",
            renewed.State,
            StartedAtUtc.AddMinutes(3));

        var dispatched = executor.BeginDispatch(
            renewed.State,
            attemptId,
            Assert.IsType<DurableOperationClaim>(renewed.Claim).Fence,
            StartedAtUtc.AddMinutes(4));
        Assert.Equal(DurableOperationDispatchDisposition.Dispatched, dispatched.Disposition);
        snapshot = await CommitOperationAsync(
            store,
            snapshot,
            "commit/operation/dispatched",
            dispatched.State,
            StartedAtUtc.AddMinutes(4));

        var acknowledged = executor.RecordObservation(
            dispatched.State,
            attemptId,
            Assert.IsType<DurableOperationClaim>(renewed.Claim).Fence,
            Success("acknowledged"),
            StartedAtUtc.AddMinutes(5));
        Assert.Equal(DurableOperationObservationDisposition.Acknowledged, acknowledged.Disposition);
        snapshot = await CommitOperationAsync(
            store,
            snapshot,
            "commit/operation/acknowledged",
            acknowledged.State,
            StartedAtUtc.AddMinutes(5));

        var operation = Assert.Single(snapshot.Checkpoint.DurableOperations);
        var attempt = Assert.Single(operation.Attempts);
        Assert.Equal(DurableOperationAttemptStage.Acknowledged, attempt.Stage);
        Assert.Equal(StartedAtUtc.AddMinutes(3), attempt.Claim.RenewedAtUtc);
        Assert.Equal(StartedAtUtc.AddMinutes(4), attempt.DispatchedAtUtc);
        Assert.Equal(StartedAtUtc.AddMinutes(5), attempt.CompletedAtUtc);
        Assert.NotNull(operation.Acknowledgement);
    }

    [Fact]
    public async Task Commit_AcceptsFailedAttemptResolvedByReconciliation()
    {
        var fixture = ProcessDurabilityTestFixture.Create(
            definitionId: "process/durable-store/attempt-reconciliation",
            semanticVariant: "attempt-reconciliation",
            durableOperationRetry: RequestRetrySemantics.ReconcileBeforeRetry);
        var executor = Executor(fixture);
        var store = new InMemoryProcessDurableStore();
        var snapshot = await InitializeAndAcquireAsync(store, fixture.Checkpoint);
        OperationAttemptId attemptId = new("operation-attempt/reconciliation");

        var claimed = executor.Claim(
            fixture.DurableOperation,
            attemptId,
            "operation-worker/reconciliation",
            StartedAtUtc.AddMinutes(2));
        snapshot = await CommitOperationAsync(
            store,
            snapshot,
            "commit/reconciliation/claimed",
            claimed.State,
            StartedAtUtc.AddMinutes(2));
        var claim = Assert.IsType<DurableOperationClaim>(claimed.Claim);

        var dispatched = executor.BeginDispatch(
            claimed.State,
            attemptId,
            claim.Fence,
            StartedAtUtc.AddMinutes(3));
        snapshot = await CommitOperationAsync(
            store,
            snapshot,
            "commit/reconciliation/dispatched",
            dispatched.State,
            StartedAtUtc.AddMinutes(3));

        var failed = executor.RecordObservation(
            dispatched.State,
            attemptId,
            claim.Fence,
            new DurableOperationFailureObservation(
                new(
                    DurableOperationFailurePhase.InCall,
                    DurableOperationEffectEvidence.Ambiguous,
                    DurableOperationFailureDisposition.Retryable,
                    "tests.external-outcome-ambiguous")),
            StartedAtUtc.AddMinutes(4));
        Assert.Equal(DurableOperationObservationDisposition.ReconciliationRequired, failed.Disposition);
        snapshot = await CommitOperationAsync(
            store,
            snapshot,
            "commit/reconciliation/failed",
            failed.State,
            StartedAtUtc.AddMinutes(4));

        var resolved = executor.RecordReconciliation(
            failed.State,
            attemptId,
            claim.Fence,
            new DurableOperationReconciledOutcome(
                new RequestResultOutcome(
                    new("result"),
                    ProcessDurabilityTestFixture.StringValue("reconciled"))),
            StartedAtUtc.AddMinutes(5));
        Assert.Equal(DurableOperationObservationDisposition.Acknowledged, resolved.Disposition);
        snapshot = await CommitOperationAsync(
            store,
            snapshot,
            "commit/reconciliation/resolved",
            resolved.State,
            StartedAtUtc.AddMinutes(5));

        var operation = Assert.Single(snapshot.Checkpoint.DurableOperations);
        Assert.Equal(DurableOperationAttemptStage.Resolved, Assert.Single(operation.Attempts).Stage);
        Assert.Equal(DurableOperationRecoveryRequirement.None, operation.RecoveryRequirement);
        Assert.Single(operation.Reconciliations);
        Assert.NotNull(operation.Acknowledgement);
    }

    [Fact]
    public async Task Commit_RejectsAttemptRenewalRollbackAndClaimIdentityTampering()
    {
        var fixture = ProcessDurabilityTestFixture.Create(
            definitionId: "process/durable-store/attempt-regression",
            semanticVariant: "attempt-regression");
        var executor = Executor(fixture);
        var store = new InMemoryProcessDurableStore();
        var snapshot = await InitializeAndAcquireAsync(store, fixture.Checkpoint);
        OperationAttemptId attemptId = new("operation-attempt/regression");

        var claimed = executor.Claim(
            fixture.DurableOperation,
            attemptId,
            "operation-worker/original",
            StartedAtUtc.AddMinutes(2));
        snapshot = await CommitOperationAsync(
            store,
            snapshot,
            "commit/regression/claimed",
            claimed.State,
            StartedAtUtc.AddMinutes(2));
        var claim = Assert.IsType<DurableOperationClaim>(claimed.Claim);
        var renewed = executor.RenewClaim(
            claimed.State,
            attemptId,
            claim.Fence,
            "operation-worker/original",
            StartedAtUtc.AddMinutes(3));
        snapshot = await CommitOperationAsync(
            store,
            snapshot,
            "commit/regression/renewed",
            renewed.State,
            StartedAtUtc.AddMinutes(3));

        var rollback = await TryCommitOperationAsync(
            store,
            snapshot,
            "commit/regression/rollback",
            claimed.State,
            StartedAtUtc.AddMinutes(4));

        var currentAttempt = Assert.Single(renewed.State.Attempts);
        var tamperedClaim = new DurableOperationClaim(
            currentAttempt.Claim.AttemptId,
            "operation-worker/attacker",
            currentAttempt.Claim.Fence,
            currentAttempt.Claim.ClaimedAtUtc,
            currentAttempt.Claim.ExpiresAtUtc,
            currentAttempt.Claim.RenewedAtUtc);
        var tamperedState = new DurableOperationState(
            renewed.State.SchemaVersion,
            renewed.State.Request,
            renewed.State.Binding,
            renewed.State.CreatedAtUtc,
            [new(
                currentAttempt.Ordinal,
                tamperedClaim,
                currentAttempt.Stage,
                currentAttempt.DispatchedAtUtc,
                currentAttempt.CompletedAtUtc,
                currentAttempt.Failure)],
            renewed.State.Reconciliations,
            renewed.State.RecoveryRequirement,
            renewed.State.Acknowledgement,
            renewed.State.Admission);
        var tampered = await TryCommitOperationAsync(
            store,
            snapshot,
            "commit/regression/tampered",
            tamperedState,
            StartedAtUtc.AddMinutes(4));

        Assert.Equal(ProcessStoreMutationDisposition.IdentityConflict, rollback.Disposition);
        Assert.Equal(snapshot.Revision, rollback.Snapshot!.Revision);
        Assert.Equal(ProcessStoreMutationDisposition.IdentityConflict, tampered.Disposition);
        Assert.Equal(snapshot.Revision, tampered.Snapshot!.Revision);
        var retained = Assert.Single(tampered.Snapshot.Checkpoint.DurableOperations);
        Assert.Equal(StartedAtUtc.AddMinutes(3), Assert.Single(retained.Attempts).Claim.RenewedAtUtc);
        Assert.Equal("operation-worker/original", Assert.Single(retained.Attempts).Claim.Claimant);
    }

    [Fact]
    public async Task Commit_AcceptsRestartAttemptWithCleanContinuationAndRetainedCrossAttemptLedgers()
    {
        var fixture = ProcessDurabilityTestFixture.Create(
            definitionId: "process/durable-store/restart-attempt",
            semanticVariant: "restart-attempt",
            recoveryPolicy: ProcessRecoveryPolicy.RestartAttempt);
        var store = new InMemoryProcessDurableStore();
        var snapshot = await InitializeAndAcquireAsync(store, fixture.Checkpoint);
        ProcessAttemptId replacementAttempt = new("process-attempt/2");
        var observedAtUtc = StartedAtUtc.AddMinutes(2);
        var command = new RestartProcessAttemptCommand(
            ProcessControlCommand.CurrentSchemaVersion,
            new(
                new("control/restart-attempt"),
                new("idempotency/control/restart-attempt"),
                fixture.Checkpoint.ContinuationIdentity.ProcessInstanceId,
                fixture.Start.Request.Context.Authorization,
                observedAtUtc,
                fixture.Start.Request.Context.Provenance),
            new(fixture.Checkpoint.ContinuationIdentity, fixture.Control.Revision),
            new(
                replacementAttempt,
                ProcessAttemptCleanupRequirement.RetainEvidence,
                new("tests.restart-attempt")));
        var control = new ProcessControlReferenceExecutor(
            Assert.IsType<InteractionContractCatalog>(fixture.Plan.ValidationContext.InteractionContracts))
            .Apply(fixture.Control, command, observedAtUtc);
        var continuation = ProcessReferenceInterpreter.RestartAttempt(
            fixture.Plan,
            fixture.Checkpoint.Continuation,
            replacementAttempt);
        var replacement = Checkpoint(
            fixture.Checkpoint,
            observedAtUtc,
            continuation: continuation,
            control: control.State);
        var commit = new ProcessDurableCommit(
            new("commit/restart-attempt"),
            snapshot.Revision,
            Worker,
            snapshot.WorkerLease!.Fence,
            replacement,
            [],
            observedAtUtc);

        var result = await store.CommitAsync(Context, commit);

        Assert.Equal(ProcessControlDecisionDisposition.Applied, control.Disposition);
        Assert.Equal(ProcessStoreMutationDisposition.Applied, result.Disposition);
        var committed = Assert.IsType<ProcessDurableStoreSnapshot>(result.Snapshot).Checkpoint;
        Assert.Equal(replacementAttempt, committed.ContinuationIdentity.ProcessAttemptId);
        Assert.Equal(0, committed.Continuation.CompletedActivationCount);
        Assert.Single(committed.Continuation.Tokens);
        Assert.Empty(committed.Continuation.Waits);
        Assert.Empty(committed.Continuation.BufferedInputs);
        Assert.Empty(committed.Continuation.InputReceipts);
        Assert.Empty(committed.Continuation.OutstandingRequests);
        Assert.Equal(fixture.Checkpoint.Activations.AsEnumerable(), committed.Activations.AsEnumerable());
        Assert.Equal(fixture.Checkpoint.Operations.AsEnumerable(), committed.Operations.AsEnumerable());
        Assert.Equal(fixture.Checkpoint.Inbox.AsEnumerable(), committed.Inbox.AsEnumerable());
        Assert.Equal(fixture.Checkpoint.Emissions.AsEnumerable(), committed.Emissions.AsEnumerable());
        Assert.Equal(
            fixture.Checkpoint.DurableOperations.AsEnumerable(),
            committed.DurableOperations.AsEnumerable());
        Assert.Equal(ProcessControlAttemptDisposition.Abandoned, committed.Control.Attempts[0].Disposition);
        Assert.Equal(ProcessControlAttemptDisposition.Current, committed.Control.CurrentAttempt.Disposition);
        var compatibility = ProcessCheckpointCompatibilityValidator.Validate(fixture.Plan, committed);
        Assert.True(
            compatibility.IsValid,
            string.Join(
                Environment.NewLine,
                compatibility.Diagnostics.Select(static diagnostic =>
                    $"{diagnostic.Code} at {diagnostic.Location}: {diagnostic.Message}")));
    }

    static DurableOperationReferenceExecutor Executor(ProcessDurabilityTestFixture fixture) =>
        new(Assert.IsType<InteractionContractCatalog>(fixture.Plan.ValidationContext.InteractionContracts));

    static DurableOperationOutcomeObservation Success(string value) =>
        new(new RequestResultOutcome(
            new("result"),
            ProcessDurabilityTestFixture.StringValue(value)));

    static async Task<ProcessDurableStoreSnapshot> InitializeAndAcquireAsync(
        InMemoryProcessDurableStore store,
        ProcessDurableCheckpoint checkpoint)
    {
        var initialized = await store.InitializeAsync(
            Context,
            new($"commit/initialize/{checkpoint.Definition.DefinitionId.Value}"),
            checkpoint);
        Assert.Equal(ProcessStoreMutationDisposition.Applied, initialized.Disposition);
        var acquired = await store.AcquireWorkerAsync(
            Context,
            checkpoint.ContinuationIdentity.ProcessInstanceId,
            Worker,
            TimeSpan.FromHours(1),
            StartedAtUtc.AddMinutes(1));
        Assert.Equal(ProcessStoreMutationDisposition.Applied, acquired.Disposition);
        return Assert.IsType<ProcessDurableStoreSnapshot>(acquired.Snapshot);
    }

    static async Task<ProcessDurableStoreSnapshot> CommitOperationAsync(
        InMemoryProcessDurableStore store,
        ProcessDurableStoreSnapshot snapshot,
        string commitId,
        DurableOperationState operation,
        DateTimeOffset observedAtUtc)
    {
        var result = await TryCommitOperationAsync(
            store,
            snapshot,
            commitId,
            operation,
            observedAtUtc);
        Assert.Equal(ProcessStoreMutationDisposition.Applied, result.Disposition);
        return Assert.IsType<ProcessDurableStoreSnapshot>(result.Snapshot);
    }

    static Task<ProcessStoreMutationResult> TryCommitOperationAsync(
        InMemoryProcessDurableStore store,
        ProcessDurableStoreSnapshot snapshot,
        string commitId,
        DurableOperationState operation,
        DateTimeOffset observedAtUtc) =>
        store.CommitAsync(
            Context,
            new(
                new(commitId),
                snapshot.Revision,
                Worker,
                snapshot.WorkerLease!.Fence,
                Checkpoint(
                    snapshot.Checkpoint,
                    observedAtUtc,
                    durableOperations: [operation]),
                [],
                observedAtUtc));

    static ProcessDurableCheckpoint Checkpoint(
        ProcessDurableCheckpoint source,
        DateTimeOffset updatedAtUtc,
        ProcessContinuationState? continuation = null,
        ProcessControlState? control = null,
        ImmutableArray<DurableOperationState> durableOperations = default) =>
        new(
            source.SchemaVersion,
            source.Start,
            continuation ?? source.Continuation,
            control ?? source.Control,
            source.Activations,
            source.Operations,
            source.Inbox,
            source.Emissions,
            durableOperations.IsDefault ? source.DurableOperations : durableOperations,
            source.CreatedAtUtc,
            updatedAtUtc);
}
