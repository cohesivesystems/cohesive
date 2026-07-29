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
    public async Task Commit_RejectsFirstPhysicalAttemptWhileControlIsPaused()
    {
        var fixture = ProcessDurabilityTestFixture.Create(
            definitionId: "process/durable-store/paused-pending-operation",
            semanticVariant: "paused-pending-operation");
        var pausedAtUtc = StartedAtUtc.AddMinutes(1);
        var pausedControl = Pause(fixture, pausedAtUtc, "pending-operation");
        var checkpoint = Checkpoint(
            fixture.Checkpoint,
            pausedAtUtc,
            control: pausedControl);
        var store = new InMemoryProcessDurableStore();
        var snapshot = await InitializeAndAcquireAsync(
            store,
            checkpoint,
            pausedAtUtc.AddMinutes(1));
        var claimed = Executor(fixture).Claim(
            fixture.DurableOperation,
            new("operation-attempt/paused-pending"),
            "operation-worker/paused-pending",
            pausedAtUtc.AddMinutes(2));

        var result = await TryCommitOperationAsync(
            store,
            snapshot,
            "commit/paused-pending/claim",
            claimed.State,
            pausedAtUtc.AddMinutes(2));

        Assert.Equal(DurableOperationClaimDisposition.Claimed, claimed.Disposition);
        Assert.Equal(ProcessStoreMutationDisposition.IdentityConflict, result.Disposition);
        Assert.Equal(snapshot.Revision, result.Snapshot?.Revision);
        Assert.Equal(ProcessControlMode.Paused, result.Snapshot?.Checkpoint.Control.Mode);
        Assert.Empty(Assert.Single(result.Snapshot!.Checkpoint.DurableOperations).Attempts);
    }

    [Fact]
    public async Task Commit_RejectsRetryAttemptWhileControlIsPaused()
    {
        var fixture = ProcessDurabilityTestFixture.Create(
            definitionId: "process/durable-store/paused-retry-operation",
            semanticVariant: "paused-retry-operation");
        var executor = Executor(fixture);
        var claimed = executor.Claim(
            fixture.DurableOperation,
            new("operation-attempt/paused-retry/1"),
            "operation-worker/paused-retry",
            StartedAtUtc.AddMinutes(1));
        var firstClaim = Assert.IsType<DurableOperationClaim>(claimed.Claim);
        var dispatched = executor.BeginDispatch(
            claimed.State,
            firstClaim.AttemptId,
            firstClaim.Fence,
            StartedAtUtc.AddMinutes(2));
        var failed = executor.RecordObservation(
            dispatched.State,
            firstClaim.AttemptId,
            firstClaim.Fence,
            new DurableOperationFailureObservation(new(
                DurableOperationFailurePhase.PreCall,
                DurableOperationEffectEvidence.NotExecuted,
                DurableOperationFailureDisposition.Retryable,
                "tests.paused-retry")),
            StartedAtUtc.AddMinutes(3));
        var pausedAtUtc = StartedAtUtc.AddMinutes(4);
        var pausedControl = Pause(fixture, pausedAtUtc, "retry-operation");
        var checkpoint = Checkpoint(
            fixture.Checkpoint,
            pausedAtUtc,
            control: pausedControl,
            durableOperations: [failed.State]);
        var store = new InMemoryProcessDurableStore();
        var snapshot = await InitializeAndAcquireAsync(
            store,
            checkpoint,
            pausedAtUtc.AddMinutes(1));
        var retryClaim = executor.Claim(
            failed.State,
            new("operation-attempt/paused-retry/2"),
            "operation-worker/paused-retry",
            pausedAtUtc.AddMinutes(2));

        var result = await TryCommitOperationAsync(
            store,
            snapshot,
            "commit/paused-retry/claim",
            retryClaim.State,
            pausedAtUtc.AddMinutes(2));

        Assert.Equal(DurableOperationObservationDisposition.RetryEligible, failed.Disposition);
        Assert.Equal(DurableOperationStatus.RetryEligible, failed.State.Status);
        Assert.Equal(DurableOperationClaimDisposition.Claimed, retryClaim.Disposition);
        Assert.Equal(ProcessStoreMutationDisposition.IdentityConflict, result.Disposition);
        Assert.Equal(snapshot.Revision, result.Snapshot?.Revision);
        var retained = Assert.Single(result.Snapshot!.Checkpoint.DurableOperations);
        Assert.Equal(DurableOperationStatus.RetryEligible, retained.Status);
        Assert.Single(retained.Attempts);
    }

    [Fact]
    public async Task Commit_AllowsSameAttemptDispatchedCompletionWhileControlIsPaused()
    {
        var fixture = ProcessDurabilityTestFixture.Create(
            definitionId: "process/durable-store/paused-dispatched-completion",
            semanticVariant: "paused-dispatched-completion");
        var executor = Executor(fixture);
        var claimed = executor.Claim(
            fixture.DurableOperation,
            new("operation-attempt/paused-dispatched"),
            "operation-worker/paused-dispatched",
            StartedAtUtc.AddMinutes(1));
        var claim = Assert.IsType<DurableOperationClaim>(claimed.Claim);
        var dispatched = executor.BeginDispatch(
            claimed.State,
            claim.AttemptId,
            claim.Fence,
            StartedAtUtc.AddMinutes(2));
        var pausedAtUtc = StartedAtUtc.AddMinutes(3);
        var pausedControl = Pause(fixture, pausedAtUtc, "dispatched-completion");
        var checkpoint = Checkpoint(
            fixture.Checkpoint,
            pausedAtUtc,
            control: pausedControl,
            durableOperations: [dispatched.State]);
        var store = new InMemoryProcessDurableStore();
        var snapshot = await InitializeAndAcquireAsync(
            store,
            checkpoint,
            pausedAtUtc.AddMinutes(1));
        var acknowledged = executor.RecordObservation(
            dispatched.State,
            claim.AttemptId,
            claim.Fence,
            Success("completed-while-paused"),
            pausedAtUtc.AddMinutes(2));

        var result = await TryCommitOperationAsync(
            store,
            snapshot,
            "commit/paused-dispatched/acknowledge",
            acknowledged.State,
            pausedAtUtc.AddMinutes(2));

        Assert.Equal(DurableOperationObservationDisposition.Acknowledged, acknowledged.Disposition);
        Assert.Equal(ProcessStoreMutationDisposition.Applied, result.Disposition);
        Assert.Equal(ProcessControlMode.Paused, result.Snapshot?.Checkpoint.Control.Mode);
        var retained = Assert.Single(result.Snapshot!.Checkpoint.DurableOperations);
        Assert.Equal(DurableOperationStatus.Acknowledged, retained.Status);
        Assert.Equal(DurableOperationAttemptStage.Acknowledged, Assert.Single(retained.Attempts).Stage);
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
        var replacement = ProcessDurableCheckpointReducer.ApplyControl(
            fixture.Plan,
            fixture.Checkpoint,
            control,
            observedAtUtc);
        var commit = new ProcessDurableCommit(
            new("commit/restart-attempt"),
            snapshot.Revision,
            Worker,
            snapshot.WorkerLease!.Fence,
            replacement,
            [],
            observedAtUtc);

        var result = await ProcessDurabilityTestFixture.CommitAtEvidenceTimeAsync(store, commit);

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
        var closedInput = Assert.Single(committed.Inbox);
        Assert.Equal(
            ProcessStorageContentFingerprints.Input(Assert.Single(fixture.Checkpoint.Inbox).Input),
            ProcessStorageContentFingerprints.Input(closedInput.Input));
        Assert.Equal(ProcessInputAdmissionDisposition.Stale, closedInput.Receipt?.Disposition);
        Assert.Equal(fixture.Checkpoint.ContinuationIdentity, closedInput.DispositionContinuation);
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

    [Fact]
    public async Task Commit_RejectsRestartAttemptThatLeavesOldInboxEntryPending()
    {
        var fixture = ProcessDurabilityTestFixture.Create(
            definitionId: "process/durable-store/restart-pending-bypass",
            semanticVariant: "restart-pending-bypass",
            recoveryPolicy: ProcessRecoveryPolicy.RestartAttempt);
        var store = new InMemoryProcessDurableStore();
        var snapshot = await InitializeAndAcquireAsync(store, fixture.Checkpoint);
        ProcessAttemptId replacementAttempt = new("process-attempt/restart-pending-bypass");
        var observedAtUtc = StartedAtUtc.AddMinutes(2);
        var command = new RestartProcessAttemptCommand(
            ProcessControlCommand.CurrentSchemaVersion,
            new(
                new("control/restart-pending-bypass"),
                new("idempotency/control/restart-pending-bypass"),
                fixture.Checkpoint.ContinuationIdentity.ProcessInstanceId,
                fixture.Start.Request.Context.Authorization,
                observedAtUtc,
                fixture.Start.Request.Context.Provenance),
            new(fixture.Checkpoint.ContinuationIdentity, fixture.Control.Revision),
            new(
                replacementAttempt,
                ProcessAttemptCleanupRequirement.RetainEvidence,
                new("tests.restart-pending-bypass")));
        var control = new ProcessControlReferenceExecutor(
                Assert.IsType<InteractionContractCatalog>(
                    fixture.Plan.ValidationContext.InteractionContracts))
            .Apply(fixture.Control, command, observedAtUtc);
        var continuation = ProcessReferenceInterpreter.RestartAttempt(
            fixture.Plan,
            fixture.Checkpoint.Continuation,
            replacementAttempt);
        var malformed = Checkpoint(
            fixture.Checkpoint,
            observedAtUtc,
            continuation: continuation,
            control: control.State);

        var result = await CommitCheckpointAsync(
            store,
            snapshot,
            "commit/restart-pending-bypass",
            malformed,
            observedAtUtc);

        Assert.Equal(ProcessStoreMutationDisposition.IdentityConflict, result.Disposition);
        Assert.Equal(snapshot.Revision, result.Snapshot?.Revision);
        Assert.Equal(fixture.Checkpoint.ContinuationIdentity, result.Snapshot?.Checkpoint.ContinuationIdentity);
        Assert.Null(Assert.Single(result.Snapshot!.Checkpoint.Inbox).Receipt);
    }

    [Fact]
    public async Task Commit_RejectsFirstPhysicalAttemptInSamePauseCut()
    {
        var fixture = ProcessDurabilityTestFixture.Create(
            definitionId: "process/durable-store/pause-and-claim-same-cut",
            semanticVariant: "pause-and-claim-same-cut");
        var store = new InMemoryProcessDurableStore();
        var snapshot = await InitializeAndAcquireAsync(store, fixture.Checkpoint);
        var cutAtUtc = StartedAtUtc.AddMinutes(2);
        var pausedControl = Pause(fixture, cutAtUtc, "and-claim-same-cut");
        var claimed = Executor(fixture).Claim(
            fixture.DurableOperation,
            new("operation-attempt/pause-same-cut"),
            "operation-worker/pause-same-cut",
            cutAtUtc);
        var replacement = Checkpoint(
            fixture.Checkpoint,
            cutAtUtc,
            control: pausedControl,
            durableOperations: [claimed.State]);

        var result = await CommitCheckpointAsync(
            store,
            snapshot,
            "commit/pause-and-claim-same-cut",
            replacement,
            cutAtUtc);

        Assert.Equal(ProcessControlMode.Running, snapshot.Checkpoint.Control.Mode);
        Assert.Equal(ProcessControlMode.Paused, replacement.Control.Mode);
        Assert.Equal(ProcessStoreMutationDisposition.IdentityConflict, result.Disposition);
        Assert.Equal(snapshot.Revision, result.Snapshot?.Revision);
        Assert.Empty(Assert.Single(result.Snapshot!.Checkpoint.DurableOperations).Attempts);
    }

    [Fact]
    public async Task Commit_RejectsFirstPhysicalAttemptInSameRestartCut()
    {
        var fixture = ProcessDurabilityTestFixture.Create(
            definitionId: "process/durable-store/restart-and-claim-same-cut",
            semanticVariant: "restart-and-claim-same-cut",
            recoveryPolicy: ProcessRecoveryPolicy.RestartAttempt);
        var store = new InMemoryProcessDurableStore();
        var snapshot = await InitializeAndAcquireAsync(store, fixture.Checkpoint);
        var cutAtUtc = StartedAtUtc.AddMinutes(2);
        ProcessAttemptId replacementAttempt = new("process-attempt/restart-and-claim");
        var command = new RestartProcessAttemptCommand(
            ProcessControlCommand.CurrentSchemaVersion,
            new(
                new("control/restart-and-claim"),
                new("idempotency/control/restart-and-claim"),
                fixture.Checkpoint.ContinuationIdentity.ProcessInstanceId,
                fixture.Start.Request.Context.Authorization,
                cutAtUtc,
                fixture.Start.Request.Context.Provenance),
            new(fixture.Checkpoint.ContinuationIdentity, fixture.Control.Revision),
            new(
                replacementAttempt,
                ProcessAttemptCleanupRequirement.RetainEvidence,
                new("tests.restart-and-claim")));
        var control = new ProcessControlReferenceExecutor(
                Assert.IsType<InteractionContractCatalog>(
                    fixture.Plan.ValidationContext.InteractionContracts))
            .Apply(fixture.Control, command, cutAtUtc);
        var restarted = ProcessDurableCheckpointReducer.ApplyControl(
            fixture.Plan,
            fixture.Checkpoint,
            control,
            cutAtUtc);
        var claimed = Executor(fixture).Claim(
            fixture.DurableOperation,
            new("operation-attempt/restart-same-cut"),
            "operation-worker/restart-same-cut",
            cutAtUtc);
        var replacement = Checkpoint(
            restarted,
            cutAtUtc,
            durableOperations: [claimed.State]);

        var result = await CommitCheckpointAsync(
            store,
            snapshot,
            "commit/restart-and-claim-same-cut",
            replacement,
            cutAtUtc);

        Assert.Equal(ProcessControlAttemptDisposition.Abandoned, replacement.Control.Attempts[0].Disposition);
        Assert.Equal(ProcessStoreMutationDisposition.IdentityConflict, result.Disposition);
        Assert.Equal(snapshot.Revision, result.Snapshot?.Revision);
        Assert.Empty(Assert.Single(result.Snapshot!.Checkpoint.DurableOperations).Attempts);
    }

    [Fact]
    public async Task Commit_RejectsFirstPhysicalAttemptInSameCancellationCut()
    {
        var fixture = ProcessDurabilityTestFixture.Create(
            definitionId: "process/durable-store/cancel-and-claim-same-cut",
            semanticVariant: "cancel-and-claim-same-cut");
        var current = ProcessDurabilityTestFixture.CopyCheckpoint(
            fixture.Checkpoint,
            inbox: []);
        var store = new InMemoryProcessDurableStore();
        var snapshot = await InitializeAndAcquireAsync(store, current);
        var cutAtUtc = StartedAtUtc.AddMinutes(2);
        var command = new CancelProcessCommand(
            ProcessControlCommand.CurrentSchemaVersion,
            new(
                new("control/cancel-and-claim"),
                new("idempotency/control/cancel-and-claim"),
                current.ContinuationIdentity.ProcessInstanceId,
                fixture.Start.Request.Context.Authorization,
                cutAtUtc,
                fixture.Start.Request.Context.Provenance),
            new(current.ContinuationIdentity, current.Control.Revision),
            new("tests.cancel-and-claim"));
        var control = new ProcessControlReferenceExecutor(
                Assert.IsType<InteractionContractCatalog>(
                    fixture.Plan.ValidationContext.InteractionContracts))
            .Apply(current.Control, command, cutAtUtc);
        var cancellation = Assert.IsType<ProcessCancellationIntent>(control.Intent);
        var activation = new ProcessActivation(
            new("activation/cancel-and-claim"),
            ProcessActivationCause.Control,
            cutAtUtc,
            fixture.Activation.Context,
            cancellation: cancellation);
        var activationDecision = ProcessReferenceInterpreter.Activate(
            fixture.Plan,
            current.Continuation,
            activation,
            RejectingHost.Instance);
        var reduced = ProcessDurableCheckpointReducer.TryApplyActivation(
            fixture.Plan,
            current,
            activation,
            activationDecision,
            control.State,
            [],
            new BindingResolver(fixture.DurableOperation.Binding),
            cutAtUtc,
            out var cancelled,
            out var reductionDiagnostics);
        Assert.True(
            reduced,
            string.Join(
                Environment.NewLine,
                reductionDiagnostics.Select(static diagnostic => diagnostic.Message)));
        var terminal = Assert.IsType<ProcessDurableCheckpoint>(cancelled);
        var claimed = Executor(fixture).Claim(
            fixture.DurableOperation,
            new("operation-attempt/cancel-same-cut"),
            "operation-worker/cancel-same-cut",
            cutAtUtc);
        var replacement = Checkpoint(
            terminal,
            cutAtUtc,
            durableOperations: [claimed.State]);

        var result = await CommitCheckpointAsync(
            store,
            snapshot,
            "commit/cancel-and-claim-same-cut",
            replacement,
            cutAtUtc);

        Assert.Equal(ProcessActivationDisposition.Cancelled, activationDecision.Disposition);
        Assert.Equal(ProcessControlAttemptDisposition.Cancelled, replacement.Control.CurrentAttempt.Disposition);
        Assert.Equal(ProcessStoreMutationDisposition.IdentityConflict, result.Disposition);
        Assert.Equal(snapshot.Revision, result.Snapshot?.Revision);
        Assert.Empty(Assert.Single(result.Snapshot!.Checkpoint.DurableOperations).Attempts);
    }

    [Fact]
    public async Task Commit_RejectsFirstPhysicalAttemptInSameOrdinaryTerminalCut()
    {
        var fixture = ProcessDurabilityTestFixture.Create(
            definitionId: "process/durable-store/terminal-and-claim-same-cut",
            semanticVariant: "terminal-and-claim-same-cut");
        var store = new InMemoryProcessDurableStore();
        var snapshot = await InitializeAndAcquireAsync(store, fixture.Checkpoint);
        var cutAtUtc = StartedAtUtc.AddMinutes(2);
        var terminal = Complete(fixture, fixture.Checkpoint, cutAtUtc, "and-claim-same-cut");
        var claimed = Executor(fixture).Claim(
            Assert.Single(terminal.DurableOperations),
            new("operation-attempt/terminal-same-cut"),
            "operation-worker/terminal-same-cut",
            cutAtUtc);
        var replacement = Checkpoint(
            terminal,
            cutAtUtc,
            durableOperations: [claimed.State]);

        var result = await CommitCheckpointAsync(
            store,
            snapshot,
            "commit/terminal-and-claim-same-cut",
            replacement,
            cutAtUtc);

        Assert.Equal(ExecutionTerminalOutcomeKind.Completed, replacement.Continuation.Terminal.Kind);
        Assert.Equal(ProcessStoreMutationDisposition.IdentityConflict, result.Disposition);
        Assert.Equal(snapshot.Revision, result.Snapshot?.Revision);
        Assert.Empty(Assert.Single(result.Snapshot!.Checkpoint.DurableOperations).Attempts);
    }

    [Fact]
    public async Task Commit_RejectsFirstPhysicalAttemptAfterOrdinaryTerminalCut()
    {
        var fixture = ProcessDurabilityTestFixture.Create(
            definitionId: "process/durable-store/claim-after-terminal-cut",
            semanticVariant: "claim-after-terminal-cut");
        var terminalAtUtc = StartedAtUtc.AddMinutes(2);
        var terminal = Complete(fixture, fixture.Checkpoint, terminalAtUtc, "claim-after-terminal-cut");
        var store = new InMemoryProcessDurableStore();
        var snapshot = await InitializeAndAcquireAsync(
            store,
            terminal,
            terminalAtUtc.AddMinutes(1));
        var claimAtUtc = terminalAtUtc.AddMinutes(2);
        var claimed = Executor(fixture).Claim(
            Assert.Single(terminal.DurableOperations),
            new("operation-attempt/after-terminal-cut"),
            "operation-worker/after-terminal-cut",
            claimAtUtc);

        var result = await TryCommitOperationAsync(
            store,
            snapshot,
            "commit/claim-after-terminal-cut",
            claimed.State,
            claimAtUtc);

        Assert.Equal(ExecutionTerminalOutcomeKind.Completed, snapshot.Checkpoint.Continuation.Terminal.Kind);
        Assert.Equal(ProcessStoreMutationDisposition.IdentityConflict, result.Disposition);
        Assert.Equal(snapshot.Revision, result.Snapshot?.Revision);
        Assert.Empty(Assert.Single(result.Snapshot!.Checkpoint.DurableOperations).Attempts);
    }

    [Fact]
    public async Task Commit_AcceptsSameAttemptDispatchAfterOrdinaryTerminalCut()
    {
        var fixture = ProcessDurabilityTestFixture.Create(
            definitionId: "process/durable-store/retained-claim-after-terminal-cut",
            semanticVariant: "retained-claim-after-terminal-cut");
        var executor = Executor(fixture);
        var store = new InMemoryProcessDurableStore();
        var snapshot = await InitializeAndAcquireAsync(store, fixture.Checkpoint);
        var claimAtUtc = StartedAtUtc.AddMinutes(2);
        var claimed = executor.Claim(
            fixture.DurableOperation,
            new("operation-attempt/retained-before-terminal"),
            "operation-worker/retained-before-terminal",
            claimAtUtc);
        snapshot = await CommitOperationAsync(
            store,
            snapshot,
            "commit/claim-before-terminal",
            claimed.State,
            claimAtUtc);
        var terminalAtUtc = claimAtUtc.AddMinutes(1);
        var terminal = Complete(
            fixture,
            snapshot.Checkpoint,
            terminalAtUtc,
            "retained-claim-after-terminal-cut");
        snapshot = Assert.IsType<ProcessDurableStoreSnapshot>((await CommitCheckpointAsync(
            store,
            snapshot,
            "commit/ordinary-terminal-with-retained-claim",
            terminal,
            terminalAtUtc)).Snapshot);
        var claim = Assert.IsType<DurableOperationClaim>(claimed.Claim);
        var dispatchAtUtc = terminalAtUtc.AddMinutes(1);
        var dispatched = executor.BeginDispatch(
            Assert.Single(snapshot.Checkpoint.DurableOperations),
            claim.AttemptId,
            claim.Fence,
            dispatchAtUtc);

        var result = await TryCommitOperationAsync(
            store,
            snapshot,
            "commit/dispatch-retained-after-terminal",
            dispatched.State,
            dispatchAtUtc);

        Assert.Equal(DurableOperationDispatchDisposition.Dispatched, dispatched.Disposition);
        Assert.Equal(ProcessStoreMutationDisposition.Applied, result.Disposition);
        Assert.Equal(
            DurableOperationAttemptStage.Dispatched,
            Assert.Single(Assert.Single(result.Snapshot!.Checkpoint.DurableOperations).Attempts).Stage);
    }

    [Fact]
    public async Task Commit_RejectsAnotherActivationAfterOrdinaryTerminalCut()
    {
        var fixture = ProcessDurabilityTestFixture.Create(
            definitionId: "process/durable-store/activation-after-terminal-cut",
            semanticVariant: "activation-after-terminal-cut");
        var terminalAtUtc = StartedAtUtc.AddMinutes(2);
        var terminal = Complete(fixture, fixture.Checkpoint, terminalAtUtc, "before-late-activation");
        var store = new InMemoryProcessDurableStore();
        var snapshot = await InitializeAndAcquireAsync(
            store,
            terminal,
            terminalAtUtc.AddMinutes(1));
        var lateAtUtc = terminalAtUtc.AddMinutes(2);
        var lateActivation = Complete(
            fixture,
            terminal,
            lateAtUtc,
            "late-activation",
            includePendingReply: false);

        var result = await CommitCheckpointAsync(
            store,
            snapshot,
            "commit/activation-after-terminal-cut",
            lateActivation,
            lateAtUtc);

        Assert.Equal(ExecutionTerminalOutcomeKind.Completed, terminal.Continuation.Terminal.Kind);
        Assert.Equal(terminal.Activations.Length + 1, lateActivation.Activations.Length);
        Assert.Equal(ProcessStoreMutationDisposition.IdentityConflict, result.Disposition);
        Assert.Equal(snapshot.Revision, result.Snapshot?.Revision);
        Assert.Equal(terminal.Activations.Length, result.Snapshot?.Checkpoint.Activations.Length);
    }

    [Fact]
    public async Task Commit_RejectsNewLogicalEmissionAfterOrdinaryTerminalCut()
    {
        var fixture = ProcessDurabilityTestFixture.Create(
            definitionId: "process/durable-store/emission-after-terminal-cut",
            semanticVariant: "emission-after-terminal-cut");
        var terminalAtUtc = StartedAtUtc.AddMinutes(2);
        var terminal = Complete(fixture, fixture.Checkpoint, terminalAtUtc, "before-late-emission");
        var store = new InMemoryProcessDurableStore();
        var snapshot = await InitializeAndAcquireAsync(
            store,
            terminal,
            terminalAtUtc.AddMinutes(1));
        var appendedAtUtc = terminalAtUtc.AddMinutes(2);
        var lateRequest = RequestEmission(fixture, "late-after-terminal");
        var replacement = Checkpoint(
            terminal,
            appendedAtUtc,
            emissions: [.. terminal.Emissions, new(lateRequest, appendedAtUtc)]);

        var result = await CommitCheckpointAsync(
            store,
            snapshot,
            "commit/emission-after-terminal-cut",
            replacement,
            appendedAtUtc);

        Assert.Equal(ProcessStoreMutationDisposition.IdentityConflict, result.Disposition);
        Assert.Equal(snapshot.Revision, result.Snapshot?.Revision);
        Assert.Equal(terminal.Emissions.Length, result.Snapshot?.Checkpoint.Emissions.Length);
    }

    [Fact]
    public async Task Commit_RejectsFirstPublicationAttemptInSameOrdinaryTerminalCut()
    {
        var fixture = ProcessDurabilityTestFixture.Create(
            definitionId: "process/durable-store/publication-and-terminal-same-cut",
            semanticVariant: "publication-and-terminal-same-cut");
        var store = new InMemoryProcessDurableStore();
        var snapshot = await InitializeAndAcquireAsync(store, fixture.Checkpoint);
        var cutAtUtc = StartedAtUtc.AddMinutes(2);
        var terminal = Complete(fixture, fixture.Checkpoint, cutAtUtc, "and-publication-same-cut");
        var emission = ClaimPublication(
            fixture,
            Assert.Single(terminal.Emissions),
            new("publication-attempt/terminal-same-cut"),
            cutAtUtc);
        var replacement = Checkpoint(terminal, cutAtUtc, emissions: [emission]);

        var result = await CommitCheckpointAsync(
            store,
            snapshot,
            "commit/publication-and-terminal-same-cut",
            replacement,
            cutAtUtc);

        Assert.Equal(ExecutionTerminalOutcomeKind.Completed, replacement.Continuation.Terminal.Kind);
        Assert.Equal(ProcessStoreMutationDisposition.IdentityConflict, result.Disposition);
        Assert.Equal(snapshot.Revision, result.Snapshot?.Revision);
        Assert.Empty(Assert.Single(result.Snapshot!.Checkpoint.Emissions).Attempts);
    }

    [Fact]
    public async Task Commit_RejectsFirstPublicationAttemptAfterOrdinaryTerminalCut()
    {
        var fixture = ProcessDurabilityTestFixture.Create(
            definitionId: "process/durable-store/publication-after-terminal-cut",
            semanticVariant: "publication-after-terminal-cut");
        var terminalAtUtc = StartedAtUtc.AddMinutes(2);
        var terminal = Complete(fixture, fixture.Checkpoint, terminalAtUtc, "before-late-publication");
        var store = new InMemoryProcessDurableStore();
        var snapshot = await InitializeAndAcquireAsync(
            store,
            terminal,
            terminalAtUtc.AddMinutes(1));
        var claimAtUtc = terminalAtUtc.AddMinutes(2);
        var emission = ClaimPublication(
            fixture,
            Assert.Single(terminal.Emissions),
            new("publication-attempt/after-terminal-cut"),
            claimAtUtc);
        var replacement = Checkpoint(terminal, claimAtUtc, emissions: [emission]);

        var result = await CommitCheckpointAsync(
            store,
            snapshot,
            "commit/publication-after-terminal-cut",
            replacement,
            claimAtUtc);

        Assert.Equal(ProcessStoreMutationDisposition.IdentityConflict, result.Disposition);
        Assert.Equal(snapshot.Revision, result.Snapshot?.Revision);
        Assert.Empty(Assert.Single(result.Snapshot!.Checkpoint.Emissions).Attempts);
    }

    [Fact]
    public async Task Commit_AcceptsSamePublicationAttemptDispatchAfterOrdinaryTerminalCut()
    {
        var fixture = ProcessDurabilityTestFixture.Create(
            definitionId: "process/durable-store/retained-publication-after-terminal-cut",
            semanticVariant: "retained-publication-after-terminal-cut");
        var store = new InMemoryProcessDurableStore();
        var snapshot = await InitializeAndAcquireAsync(store, fixture.Checkpoint);
        var claimAtUtc = StartedAtUtc.AddMinutes(2);
        var claimed = ClaimPublication(
            fixture,
            Assert.Single(fixture.Checkpoint.Emissions),
            new("publication-attempt/retained-before-terminal"),
            claimAtUtc);
        snapshot = Assert.IsType<ProcessDurableStoreSnapshot>((await CommitCheckpointAsync(
            store,
            snapshot,
            "commit/publication-claim-before-terminal",
            Checkpoint(fixture.Checkpoint, claimAtUtc, emissions: [claimed]),
            claimAtUtc)).Snapshot);
        var terminalAtUtc = claimAtUtc.AddMinutes(1);
        var terminal = Complete(
            fixture,
            snapshot.Checkpoint,
            terminalAtUtc,
            "retained-publication-after-terminal-cut");
        snapshot = Assert.IsType<ProcessDurableStoreSnapshot>((await CommitCheckpointAsync(
            store,
            snapshot,
            "commit/ordinary-terminal-with-retained-publication",
            terminal,
            terminalAtUtc)).Snapshot);
        var dispatchAtUtc = terminalAtUtc.AddMinutes(1);
        var retained = Assert.Single(snapshot.Checkpoint.Emissions);
        var claimedAttempt = Assert.Single(retained.Attempts);
        var dispatched = new ProcessEmissionRecord(
            retained.Envelope,
            retained.EnqueuedAtUtc,
            [new(
                claimedAttempt.Ordinal,
                claimedAttempt.Claim,
                DurableOperationAttemptStage.Dispatched,
                dispatchAtUtc)]);
        var replacement = Checkpoint(
            snapshot.Checkpoint,
            dispatchAtUtc,
            emissions: [dispatched]);

        var result = await CommitCheckpointAsync(
            store,
            snapshot,
            "commit/dispatch-retained-publication-after-terminal",
            replacement,
            dispatchAtUtc);

        Assert.Equal(ProcessStoreMutationDisposition.Applied, result.Disposition);
        Assert.Equal(
            DurableOperationAttemptStage.Dispatched,
            Assert.Single(Assert.Single(result.Snapshot!.Checkpoint.Emissions).Attempts).Stage);
    }

    static DurableOperationReferenceExecutor Executor(ProcessDurabilityTestFixture fixture) =>
        new(Assert.IsType<InteractionContractCatalog>(fixture.Plan.ValidationContext.InteractionContracts));

    static DurableOperationOutcomeObservation Success(string value) =>
        new(new RequestResultOutcome(
            new("result"),
            ProcessDurabilityTestFixture.StringValue(value)));

    static ProcessEmissionRecord ClaimPublication(
        ProcessDurabilityTestFixture fixture,
        ProcessEmissionRecord emission,
        OperationAttemptId attemptId,
        DateTimeOffset claimedAtUtc)
    {
        var claimed = Executor(fixture).Claim(
            fixture.DurableOperation,
            attemptId,
            $"publication-worker/{attemptId.Value}",
            claimedAtUtc);
        Assert.Equal(DurableOperationClaimDisposition.Claimed, claimed.Disposition);
        return new(
            emission.Envelope,
            emission.EnqueuedAtUtc,
            [Assert.Single(claimed.State.Attempts)]);
    }

    static RequestEnvelope RequestEmission(ProcessDurabilityTestFixture fixture, string identity)
    {
        var template = fixture.Request;
        var context = template.Context;
        return new(
            template.SchemaVersion,
            new(
                new($"emission/request/{identity}"),
                context.Origin,
                context.CorrelationId,
                context.EmissionId,
                context.AuthorityScope,
                new($"idempotency/request/{identity}"),
                context.Ordering,
                context.Delivery,
                context.Provenance),
            template.Contract,
            template.Payload,
            template.ResponseTarget);
    }

    static ProcessControlState Pause(
        ProcessDurabilityTestFixture fixture,
        DateTimeOffset observedAtUtc,
        string identity)
    {
        var state = fixture.Checkpoint.Control;
        var command = new PauseProcessCommand(
            ProcessControlCommand.CurrentSchemaVersion,
            new(
                new($"control/pause-{identity}"),
                new($"idempotency/control/pause-{identity}"),
                state.ProcessInstanceId,
                fixture.Start.Request.Context.Authorization,
                observedAtUtc,
                fixture.Start.Request.Context.Provenance),
            new(
                fixture.Checkpoint.ContinuationIdentity,
                state.Revision));
        var decision = new ProcessControlReferenceExecutor(
                Assert.IsType<InteractionContractCatalog>(
                    fixture.Plan.ValidationContext.InteractionContracts))
            .Apply(state, command, observedAtUtc);
        Assert.Equal(ProcessControlDecisionDisposition.Applied, decision.Disposition);
        Assert.Equal(ProcessControlMode.Paused, decision.State.Mode);
        return decision.State;
    }

    static ProcessDurableCheckpoint Complete(
        ProcessDurabilityTestFixture fixture,
        ProcessDurableCheckpoint checkpoint,
        DateTimeOffset observedAtUtc,
        string identity,
        bool includePendingReply = true)
    {
        ImmutableArray<ProcessActivationInput> inputs = includePendingReply
            ? [fixture.PendingReply]
            : [];
        var activation = new ProcessActivation(
            new($"activation/terminal-{identity}"),
            ProcessActivationCause.Interaction,
            observedAtUtc,
            fixture.Activation.Context,
            inputs);
        var controller = new ProcessControlReferenceExecutor(
            Assert.IsType<InteractionContractCatalog>(fixture.Plan.ValidationContext.InteractionContracts));
        var begun = controller.BeginActivation(
            checkpoint.Control,
            new(
                new(checkpoint.ContinuationIdentity, checkpoint.Control.Revision),
                activation.Id,
                observedAtUtc));
        Assert.Equal(ProcessControlDecisionDisposition.ActivationStarted, begun.Disposition);
        var decision = ProcessReferenceInterpreter.Activate(
            fixture.Plan,
            checkpoint.Continuation,
            activation,
            RejectingHost.Instance);
        Assert.Equal(ProcessActivationDisposition.Completed, decision.Disposition);
        var safePoint = controller.ReachSafePoint(
            begun.State,
            new(
                new($"safe-point/terminal-{identity}"),
                new(checkpoint.ContinuationIdentity, begun.State.Revision),
                activation.Id,
                Assert.IsType<ExecutionNodeId>(decision.Evidence.SafePointNode),
                observedAtUtc));
        Assert.Equal(ProcessControlDecisionDisposition.SafePointReached, safePoint.Disposition);
        var reduced = ProcessDurableCheckpointReducer.TryApplyActivation(
            fixture.Plan,
            checkpoint,
            activation,
            decision,
            safePoint.State,
            [],
            new BindingResolver(fixture.DurableOperation.Binding),
            observedAtUtc,
            out var replacement,
            out var diagnostics);
        Assert.True(
            reduced,
            string.Join(Environment.NewLine, diagnostics.Select(static diagnostic => diagnostic.Message)));
        return Assert.IsType<ProcessDurableCheckpoint>(replacement);
    }

    static async Task<ProcessDurableStoreSnapshot> InitializeAndAcquireAsync(
        InMemoryProcessDurableStore store,
        ProcessDurableCheckpoint checkpoint,
        DateTimeOffset? acquiredAtUtc = null)
    {
        var initialized = await store.InitializeAsync(
            Context,
            new($"commit/initialize/{checkpoint.Definition.DefinitionId.Value}"),
            checkpoint);
        Assert.Equal(ProcessStoreMutationDisposition.Applied, initialized.Disposition);
        var acquired = await store.AcquireWorkerAsync(
            Context,
            checkpoint.ContinuationIdentity.ProcessInstanceId,
            initialized.Snapshot!.Revision,
            Worker,
            TimeSpan.FromHours(1),
            acquiredAtUtc ?? StartedAtUtc.AddMinutes(1));
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
        ProcessDurabilityTestFixture.CommitAtEvidenceTimeAsync(
            store,
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

    static Task<ProcessStoreMutationResult> CommitCheckpointAsync(
        InMemoryProcessDurableStore store,
        ProcessDurableStoreSnapshot snapshot,
        string commitId,
        ProcessDurableCheckpoint checkpoint,
        DateTimeOffset observedAtUtc) =>
        ProcessDurabilityTestFixture.CommitAtEvidenceTimeAsync(
            store,
            new(
                new(commitId),
                snapshot.Revision,
                Worker,
                snapshot.WorkerLease!.Fence,
                checkpoint,
                [],
                observedAtUtc));

    static ProcessDurableCheckpoint Checkpoint(
        ProcessDurableCheckpoint source,
        DateTimeOffset updatedAtUtc,
        ProcessContinuationState? continuation = null,
        ProcessControlState? control = null,
        ImmutableArray<ProcessEmissionRecord> emissions = default,
        ImmutableArray<DurableOperationState> durableOperations = default) =>
        new(
            source.SchemaVersion,
            source.Start,
            continuation ?? source.Continuation,
            control ?? source.Control,
            source.Activations,
            source.Operations,
            source.Inbox,
            emissions.IsDefault ? source.Emissions : emissions,
            durableOperations.IsDefault ? source.DurableOperations : durableOperations,
            source.CreatedAtUtc,
            updatedAtUtc);

    sealed class BindingResolver(DurableRequestBinding binding) : IProcessDurableRequestBindingResolver
    {
        public bool TryResolve(RequestEnvelope request, out DurableRequestBinding? resolved)
        {
            resolved = binding;
            return true;
        }
    }

    sealed class RejectingHost : IProcessReferenceHost
    {
        internal static RejectingHost Instance { get; } = new();

        public ProcessOperationResult InvokeTransition(ProcessTransitionInvocation invocation) =>
            throw new InvalidOperationException($"Unexpected Transition invocation at '{invocation.Node.Value}'.");

        public ProcessOperationResult EvaluateRelation(ProcessRelationEvaluation evaluation) =>
            throw new InvalidOperationException($"Unexpected Relation evaluation at '{evaluation.Node.Value}'.");

        public ProcessSignalTargetResult ResolveSignalTarget(ProcessSignalTargetResolution resolution) =>
            throw new InvalidOperationException($"Unexpected Signal resolution at '{resolution.Node.Value}'.");
    }
}
