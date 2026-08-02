using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Execution;
using Cohesive.Processes.IR;
using Cohesive.Storage.Processes;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class ProcessDurableRuntimeOperationTests
{
    static readonly TimeSpan WorkerLease = TimeSpan.FromMinutes(5);

    [Fact]
    public async Task Ek06_RequestOriginAndDispatchMarkerCommitBeforeExternalAdapterExecution()
    {
        var fixture = ProcessDurabilityTestFixture.Create(
            definitionId: "process/durable-runtime-operation/origin-before-dispatch",
            semanticVariant: "origin-before-dispatch");
        var store = await InitializeStoreAsync(fixture, "origin-before-dispatch");
        var adapter = new InspectingSuccessAdapter(
            store,
            fixture.Checkpoint.ContinuationIdentity.ProcessInstanceId,
            fixture.Request.Contract,
            Success());
        var runtime = Runtime(store, adapter);

        var result = await runtime.AdvanceOperationAsync(
            Context(ProcessDurabilityTestFixture.CheckpointedAtUtc.AddMinutes(1)),
            fixture.Plan,
            fixture.Checkpoint.ContinuationIdentity.ProcessInstanceId,
            fixture.Request.Context.EmissionId);

        Assert.True(adapter.SawOrigin);
        Assert.True(adapter.SawDispatchMarker);
        Assert.Equal(DurableOperationStatus.Dispositioned, result.Operation?.Status);
    }

    [Fact]
    public async Task Ek06_ExternalSuccessAtomicallyAcknowledgesAndAdmitsDeterministicReply()
    {
        var fixture = ProcessDurabilityTestFixture.Create(
            definitionId: "process/durable-runtime-operation/success-reply",
            semanticVariant: "success-reply");
        var store = await InitializeStoreAsync(fixture, "success-reply");
        var adapter = new DurableOperationFakeAdapter(fixture.Request.Contract)
            .Script(fixture.Request.Context.EmissionId, Success("adapter-accepted"));
        var runtime = Runtime(store, adapter);

        var result = await runtime.AdvanceOperationAsync(
            Context(ProcessDurabilityTestFixture.CheckpointedAtUtc.AddMinutes(1)),
            fixture.Plan,
            fixture.Checkpoint.ContinuationIdentity.ProcessInstanceId,
            fixture.Request.Context.EmissionId);

        Assert.Equal(ProcessDurableRuntimeDisposition.Applied, result.Disposition);
        var operation = Assert.IsType<DurableOperationState>(result.Operation);
        Assert.Equal(DurableOperationStatus.Dispositioned, operation.Status);
        Assert.Equal(DurableOperationAdmissionDisposition.Accepted, operation.Admission?.Disposition);
        var snapshot = Assert.IsType<ProcessDurableStoreSnapshot>(result.Snapshot);
        var replyId = ProcessDurableRuntimeIdentities.OperationReply(operation.OperationId);
        var replyEntry = Assert.Single(snapshot.Checkpoint.Inbox, entry => entry.EmissionId == replyId);
        var reply = Assert.IsType<ReplyEnvelope>(replyEntry.Input.Envelope);
        Assert.Equal(fixture.Request.Context.EmissionId, reply.InReplyTo);
        Assert.Equal(fixture.Request.Context.Origin, reply.Context.Origin);
        Assert.Equal(fixture.Request.Context.Provenance, reply.Context.Provenance);
        Assert.Equal(
            ProcessDurableRuntimeIdentities.OperationReplyIdempotency(operation.OperationId),
            reply.Context.IdempotencyKey);
        Assert.Single(adapter.Invocations);
    }

    [Fact]
    public async Task Ek06_RestoreRejectsAcceptedAdmissionWithoutItsCanonicalReplyInboxProjection()
    {
        var fixture = ProcessDurabilityTestFixture.Create(
            definitionId: "process/durable-runtime-operation/missing-admitted-reply",
            semanticVariant: "missing-admitted-reply");
        var store = await InitializeStoreAsync(fixture, "missing-admitted-reply");
        var adapter = new DurableOperationFakeAdapter(fixture.Request.Contract)
            .Script(fixture.Request.Context.EmissionId, Success("adapter-accepted"));
        var runtime = Runtime(store, adapter);
        var executed = await runtime.AdvanceOperationAsync(
            Context(ProcessDurabilityTestFixture.CheckpointedAtUtc.AddMinutes(1)),
            fixture.Plan,
            fixture.Checkpoint.ContinuationIdentity.ProcessInstanceId,
            fixture.Request.Context.EmissionId);
        var checkpoint = Assert.IsType<ProcessDurableStoreSnapshot>(executed.Snapshot).Checkpoint;
        var operation = Assert.Single(checkpoint.DurableOperations);
        var replyId = ProcessDurableRuntimeIdentities.OperationReply(operation.OperationId);
        var forged = ProcessDurabilityTestFixture.CopyCheckpoint(
            checkpoint,
            inbox: [.. checkpoint.Inbox.Where(entry => entry.EmissionId != replyId)]);

        var validation = ProcessDurableCheckpointJsonSerializer.TryDeserialize(
            ProcessDurableCheckpointJsonSerializer.Serialize(forged),
            fixture.Plan,
            out var restored);

        Assert.False(validation.IsValid);
        Assert.NotNull(restored);
        Assert.Contains(
            validation.Diagnostics,
            static diagnostic =>
                diagnostic.Code == ProcessCheckpointDiagnosticCodes.InboxReceiptIncompatible
                && diagnostic.Location == "/durableOperations/0/admission");
    }

    [Fact]
    public async Task Ek06_AcknowledgedRecoverySkipsAdapterResolutionAndAdmitsReply()
    {
        var fixture = ProcessDurabilityTestFixture.Create(
            definitionId: "process/durable-runtime-operation/acknowledged-recovery",
            semanticVariant: "acknowledged-recovery");
        var executor = new DurableOperationReferenceExecutor(
            fixture.Plan.ValidationContext.InteractionContracts
            ?? throw new InvalidOperationException("The fixture requires interaction contracts."));
        var observedAtUtc = ProcessDurabilityTestFixture.CheckpointedAtUtc;
        var claimed = executor.Claim(
            fixture.DurableOperation,
            new("operation-attempt/preacknowledged"),
            "worker/preacknowledged",
            observedAtUtc);
        var claim = Assert.IsType<DurableOperationClaim>(claimed.Claim);
        var dispatched = executor.BeginDispatch(
            claimed.State,
            claim.AttemptId,
            claim.Fence,
            observedAtUtc);
        var acknowledged = executor.RecordObservation(
            dispatched.State,
            claim.AttemptId,
            claim.Fence,
            Success("already-acknowledged"),
            observedAtUtc);
        Assert.Equal(DurableOperationStatus.Acknowledged, acknowledged.State.Status);
        var checkpoint = ProcessDurabilityTestFixture.CopyCheckpoint(
            fixture.Checkpoint,
            durableOperations: [acknowledged.State]);
        var store = await InitializeStoreAsync(checkpoint, "acknowledged-recovery");
        var resolver = new RejectingAdapterResolver();
        var runtime = Runtime(store, resolver);

        var result = await runtime.AdvanceOperationAsync(
            Context(ProcessDurabilityTestFixture.CheckpointedAtUtc.AddMinutes(1)),
            fixture.Plan,
            fixture.Checkpoint.ContinuationIdentity.ProcessInstanceId,
            fixture.Request.Context.EmissionId);

        Assert.Equal(0, resolver.ResolutionCalls);
        Assert.Equal(DurableOperationStatus.Dispositioned, result.Operation?.Status);
        Assert.Contains(
            Assert.IsType<ProcessDurableStoreSnapshot>(result.Snapshot).Checkpoint.Inbox,
            entry => entry.EmissionId
                == ProcessDurableRuntimeIdentities.OperationReply(fixture.Request.Context.EmissionId));
    }

    [Fact]
    public async Task Ek06_AmbiguousExternalExceptionPersistsReconciliationThenRetryEligibility()
    {
        var fixture = ProcessDurabilityTestFixture.Create(
            definitionId: "process/durable-runtime-operation/ambiguous-exception",
            semanticVariant: "ambiguous-exception",
            durableOperationRetry: RequestRetrySemantics.ReconcileBeforeRetry);
        var controlExecutor = new ProcessControlReferenceExecutor(
            fixture.Plan.ValidationContext.InteractionContracts
            ?? throw new InvalidOperationException("The fixture requires interaction contracts."));
        var affinity = new ProcessAttemptAffinityObservation(
            new(
                new(fixture.Control.ProcessInstanceId, fixture.Control.CurrentAttempt.AttemptId),
                fixture.Control.Revision),
            new(
                new("request-affinity"),
                ProcessDurabilityTestFixture.StringValue("same-attempt-affinity")),
            ProcessDurabilityTestFixture.CheckpointedAtUtc);
        var bound = controlExecutor.BindAttemptAffinity(fixture.Control, affinity);
        Assert.Equal(ProcessControlDecisionDisposition.AffinityBound, bound.Disposition);
        var checkpoint = ProcessDurabilityTestFixture.CopyCheckpoint(
            fixture.Checkpoint,
            control: bound.State);
        var store = await InitializeStoreAsync(checkpoint, "ambiguous-exception");
        var adapter = new AmbiguousThenNotExecutedAdapter(
            store,
            fixture.Checkpoint.ContinuationIdentity.ProcessInstanceId,
            fixture.Request.Contract);
        var runtime = Runtime(store, adapter);

        var result = await runtime.AdvanceOperationAsync(
            Context(ProcessDurabilityTestFixture.CheckpointedAtUtc.AddMinutes(1)),
            fixture.Plan,
            fixture.Checkpoint.ContinuationIdentity.ProcessInstanceId,
            fixture.Request.Context.EmissionId);

        Assert.Equal(ProcessDurableRuntimeDisposition.Applied, result.Disposition);
        var operation = Assert.IsType<DurableOperationState>(result.Operation);
        Assert.Equal(DurableOperationStatus.RetryEligible, operation.Status);
        var attempt = Assert.Single(operation.Attempts);
        Assert.Equal(DurableOperationAttemptStage.Failed, attempt.Stage);
        Assert.Equal(DurableOperationEffectEvidence.Ambiguous, attempt.Failure?.EffectEvidence);
        Assert.IsType<DurableOperationConfirmedNotExecuted>(Assert.Single(operation.Reconciliations).Observation);
        Assert.Equal(1, adapter.ExecutionCalls);
        Assert.Equal(1, adapter.ReconciliationCalls);
        Assert.True(adapter.SawReconciliationRequired);
        Assert.Equal(attempt.Claim.AttemptId, adapter.ReconciledAttemptId);
        Assert.Equal(affinity, Assert.Single(
            Assert.IsType<ProcessDurableStoreSnapshot>(result.Snapshot)
                .Checkpoint.Control.CurrentAttempt.AffinityBindings));
        Assert.Null(operation.Acknowledgement);
        Assert.Null(operation.Admission);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ek06_ProviderCancellationWithoutCallerCancellationPersistsAmbiguousFailure(
        bool taskCanceledException)
    {
        var fixture = ProcessDurabilityTestFixture.Create(
            definitionId: $"process/durable-runtime-operation/provider-cancellation/{taskCanceledException}",
            semanticVariant: $"provider-cancellation/{taskCanceledException}");
        var store = await InitializeStoreAsync(fixture, $"provider-cancellation-{taskCanceledException}");
        var adapter = new ProviderCancellationAdapter(
            fixture.Request.Contract,
            taskCanceledException,
            reconciliationCancellation: false);
        var runtime = Runtime(store, adapter);

        var result = await runtime.AdvanceOperationAsync(
            Context(ProcessDurabilityTestFixture.CheckpointedAtUtc.AddMinutes(1)),
            fixture.Plan,
            fixture.Checkpoint.ContinuationIdentity.ProcessInstanceId,
            fixture.Request.Context.EmissionId);

        Assert.Equal(ProcessDurableRuntimeDisposition.Applied, result.Disposition);
        var operation = Assert.IsType<DurableOperationState>(result.Operation);
        Assert.Equal(DurableOperationStatus.RetryEligible, operation.Status);
        var attempt = Assert.Single(operation.Attempts);
        Assert.Equal(DurableOperationAttemptStage.Failed, attempt.Stage);
        Assert.Equal(DurableOperationFailurePhase.InCall, attempt.Failure?.Phase);
        Assert.Equal(DurableOperationEffectEvidence.Ambiguous, attempt.Failure?.EffectEvidence);
        Assert.Equal(
            ConservativeProcessOperationExceptionClassifier.AmbiguousAdapterException,
            attempt.Failure?.Code);
        Assert.Equal(1, adapter.ExecutionCalls);
        Assert.Equal(0, adapter.ReconciliationCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ek06_ProviderReconciliationCancellationPersistsUnresolvedEvidence(
        bool taskCanceledException)
    {
        var fixture = ProcessDurabilityTestFixture.Create(
            definitionId: $"process/durable-runtime-operation/reconciliation-cancellation/{taskCanceledException}",
            semanticVariant: $"reconciliation-cancellation/{taskCanceledException}",
            durableOperationRetry: RequestRetrySemantics.ReconcileBeforeRetry);
        var checkpoint = ProcessDurabilityTestFixture.CopyCheckpoint(
            fixture.Checkpoint,
            durableOperations: [CreateReconciliationRequiredOperation(fixture)]);
        var store = await InitializeStoreAsync(checkpoint, $"reconciliation-cancellation-{taskCanceledException}");
        var adapter = new ProviderCancellationAdapter(
            fixture.Request.Contract,
            taskCanceledException,
            reconciliationCancellation: true);
        var runtime = Runtime(store, adapter);

        var result = await runtime.AdvanceOperationAsync(
            Context(ProcessDurabilityTestFixture.CheckpointedAtUtc.AddMinutes(1)),
            fixture.Plan,
            checkpoint.ContinuationIdentity.ProcessInstanceId,
            fixture.Request.Context.EmissionId);

        Assert.Equal(ProcessDurableRuntimeDisposition.Applied, result.Disposition);
        var operation = Assert.IsType<DurableOperationState>(result.Operation);
        Assert.Equal(DurableOperationStatus.EscalationRequired, operation.Status);
        Assert.IsType<DurableOperationUnresolved>(Assert.Single(operation.Reconciliations).Observation);
        Assert.Equal(0, adapter.ExecutionCalls);
        Assert.Equal(1, adapter.ReconciliationCalls);
    }

    [Fact]
    public async Task Ek06_ProviderReconciliationExceptionPersistsUnresolvedEvidence()
    {
        var fixture = ProcessDurabilityTestFixture.Create(
            definitionId: "process/durable-runtime-operation/reconciliation-exception",
            semanticVariant: "reconciliation-exception",
            durableOperationRetry: RequestRetrySemantics.ReconcileBeforeRetry);
        var checkpoint = ProcessDurabilityTestFixture.CopyCheckpoint(
            fixture.Checkpoint,
            durableOperations: [CreateReconciliationRequiredOperation(fixture)]);
        var store = await InitializeStoreAsync(checkpoint, "reconciliation-exception");
        var adapter = new ThrowingReconciliationAdapter(fixture.Request.Contract);
        var runtime = Runtime(store, adapter);

        var result = await runtime.AdvanceOperationAsync(
            Context(ProcessDurabilityTestFixture.CheckpointedAtUtc.AddMinutes(1)),
            fixture.Plan,
            checkpoint.ContinuationIdentity.ProcessInstanceId,
            fixture.Request.Context.EmissionId);

        Assert.Equal(ProcessDurableRuntimeDisposition.Applied, result.Disposition);
        var operation = Assert.IsType<DurableOperationState>(result.Operation);
        Assert.Equal(DurableOperationStatus.EscalationRequired, operation.Status);
        Assert.IsType<DurableOperationUnresolved>(Assert.Single(operation.Reconciliations).Observation);
        Assert.Equal(1, adapter.ReconciliationCalls);
    }

    [Fact]
    public async Task Ek06_CallerCancellationStillPropagatesWithoutInventingAdapterFailure()
    {
        var fixture = ProcessDurabilityTestFixture.Create(
            definitionId: "process/durable-runtime-operation/caller-cancellation",
            semanticVariant: "caller-cancellation");
        var store = await InitializeStoreAsync(fixture, "caller-cancellation");
        var adapter = new CallerCancellationAdapter(fixture.Request.Contract);
        var runtime = Runtime(store, adapter);
        using var cancellation = new CancellationTokenSource();
        var context = OperationContext.Create(
            timeProvider: new FixedTimeProvider(ProcessDurabilityTestFixture.CheckpointedAtUtc.AddMinutes(1)),
            cancellationToken: cancellation.Token);

        var advancing = runtime.AdvanceOperationAsync(
            context,
            fixture.Plan,
            fixture.Checkpoint.ContinuationIdentity.ProcessInstanceId,
            fixture.Request.Context.EmissionId);
        await adapter.Entered;
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => advancing);
        var snapshot = Assert.IsType<ProcessDurableStoreSnapshot>(await store.LoadAsync(
            Context(ProcessDurabilityTestFixture.CheckpointedAtUtc.AddMinutes(1)),
            fixture.Checkpoint.ContinuationIdentity.ProcessInstanceId));
        var operation = Assert.Single(snapshot.Checkpoint.DurableOperations);
        Assert.Equal(DurableOperationStatus.Dispatched, operation.Status);
        Assert.Null(operation.CurrentAttempt?.Failure);
    }

    [Fact]
    public async Task Ek06_CrashBeforeAcknowledgementCommitRedispatchesStableAttemptAndDeduplicationKey()
    {
        var fixture = ProcessDurabilityTestFixture.Create(
            definitionId: "process/durable-runtime-operation/acknowledgement-crash",
            semanticVariant: "acknowledgement-crash");
        var aggregateCuts = 0;
        var crashed = false;
        var store = new InMemoryProcessDurableStore(crash =>
        {
            if (crash.MutationKind != ProcessStoreMutationKind.AggregateCommit
                || crash.Phase != ProcessStoreCrashPhase.BeforeAtomicCommit)
            {
                return false;
            }
            aggregateCuts++;
            if (crashed || aggregateCuts != 3)
            {
                return false;
            }
            crashed = true;
            return true;
        });
        await InitializeStoreAsync(store, fixture.Checkpoint, "acknowledgement-crash");
        var adapter = new DurableOperationFakeAdapter(fixture.Request.Contract)
            .Script(
                fixture.Request.Context.EmissionId,
                Success("first-observation"),
                Success("first-observation"));
        var runtime = Runtime(store, adapter, maxAmbiguousStoreMutationAttempts: 1);
        var context = Context(ProcessDurabilityTestFixture.CheckpointedAtUtc.AddMinutes(1));

        var interrupted = await runtime.AdvanceOperationAsync(
            context,
            fixture.Plan,
            fixture.Checkpoint.ContinuationIdentity.ProcessInstanceId,
            fixture.Request.Context.EmissionId);
        var afterCrash = Assert.IsType<ProcessDurableStoreSnapshot>(await store.LoadAsync(
            context,
            fixture.Checkpoint.ContinuationIdentity.ProcessInstanceId));

        Assert.Equal(ProcessDurableRuntimeDisposition.CommitOutcomeUnknown, interrupted.Disposition);
        Assert.Equal(
            DurableOperationStatus.Dispatched,
            Assert.Single(afterCrash.Checkpoint.DurableOperations).Status);

        var recovered = await runtime.AdvanceOperationAsync(
            context,
            fixture.Plan,
            fixture.Checkpoint.ContinuationIdentity.ProcessInstanceId,
            fixture.Request.Context.EmissionId);

        Assert.Equal(DurableOperationStatus.Dispositioned, recovered.Operation?.Status);
        Assert.Equal(2, adapter.Invocations.Count);
        Assert.Equal(adapter.Invocations[0].AttemptId, adapter.Invocations[1].AttemptId);
        Assert.Equal(adapter.Invocations[0].Fence, adapter.Invocations[1].Fence);
        Assert.Equal(adapter.Invocations[0].DeduplicationKey, adapter.Invocations[1].DeduplicationKey);
        Assert.Equal(1, adapter.LogicalConsequenceCount);
    }

    [Fact]
    public async Task Ek06_CrashBeforeResultAdmissionLaterCommitsExactlyOneDeterministicReply()
    {
        var fixture = ProcessDurabilityTestFixture.Create(
            definitionId: "process/durable-runtime-operation/admission-crash",
            semanticVariant: "admission-crash");
        var aggregateCuts = 0;
        var crashed = false;
        var store = new InMemoryProcessDurableStore(crash =>
        {
            if (crash.MutationKind != ProcessStoreMutationKind.AggregateCommit
                || crash.Phase != ProcessStoreCrashPhase.BeforeAtomicCommit)
            {
                return false;
            }
            aggregateCuts++;
            if (crashed || aggregateCuts != 4)
            {
                return false;
            }
            crashed = true;
            return true;
        });
        await InitializeStoreAsync(store, fixture.Checkpoint, "admission-crash");
        var adapter = new DurableOperationFakeAdapter(fixture.Request.Contract)
            .Script(fixture.Request.Context.EmissionId, Success("acknowledged-before-crash"));
        var runtime = Runtime(store, adapter, maxAmbiguousStoreMutationAttempts: 1);
        var context = Context(ProcessDurabilityTestFixture.CheckpointedAtUtc.AddMinutes(1));

        var interrupted = await runtime.AdvanceOperationAsync(
            context,
            fixture.Plan,
            fixture.Checkpoint.ContinuationIdentity.ProcessInstanceId,
            fixture.Request.Context.EmissionId);
        var afterCrash = Assert.IsType<ProcessDurableStoreSnapshot>(await store.LoadAsync(
            context,
            fixture.Checkpoint.ContinuationIdentity.ProcessInstanceId));

        Assert.Equal(ProcessDurableRuntimeDisposition.CommitOutcomeUnknown, interrupted.Disposition);
        Assert.Equal(
            DurableOperationStatus.Acknowledged,
            Assert.Single(afterCrash.Checkpoint.DurableOperations).Status);
        var replyId = ProcessDurableRuntimeIdentities.OperationReply(fixture.Request.Context.EmissionId);
        Assert.DoesNotContain(afterCrash.Checkpoint.Inbox, entry => entry.EmissionId == replyId);

        var recovered = await runtime.AdvanceOperationAsync(
            context,
            fixture.Plan,
            fixture.Checkpoint.ContinuationIdentity.ProcessInstanceId,
            fixture.Request.Context.EmissionId);
        var replay = await runtime.AdvanceOperationAsync(
            context,
            fixture.Plan,
            fixture.Checkpoint.ContinuationIdentity.ProcessInstanceId,
            fixture.Request.Context.EmissionId);

        Assert.Equal(DurableOperationStatus.Dispositioned, recovered.Operation?.Status);
        Assert.Equal(ProcessDurableRuntimeDisposition.Replayed, replay.Disposition);
        var final = Assert.IsType<ProcessDurableStoreSnapshot>(replay.Snapshot);
        Assert.Single(final.Checkpoint.Inbox, entry => entry.EmissionId == replyId);
        Assert.Single(adapter.Invocations);
    }

    [Fact]
    public async Task Ek06_MissingOperationDoesNotAcquireProcessWorker()
    {
        var fixture = ProcessDurabilityTestFixture.Create(
            definitionId: "process/durable-runtime-operation/missing-preflight",
            semanticVariant: "missing-preflight");
        var store = await InitializeStoreAsync(fixture, "missing-preflight");
        var before = Assert.IsType<ProcessDurableStoreSnapshot>(await store.LoadAsync(
            Context(ProcessDurabilityTestFixture.CheckpointedAtUtc),
            fixture.Checkpoint.ContinuationIdentity.ProcessInstanceId));
        var adapter = new DurableOperationFakeAdapter(fixture.Request.Contract);
        var runtime = Runtime(store, adapter);

        var result = await runtime.AdvanceOperationAsync(
            Context(ProcessDurabilityTestFixture.CheckpointedAtUtc.AddMinutes(1)),
            fixture.Plan,
            fixture.Checkpoint.ContinuationIdentity.ProcessInstanceId,
            new("emission/request/not-retained"));
        var after = Assert.IsType<ProcessDurableStoreSnapshot>(await store.LoadAsync(
            Context(ProcessDurabilityTestFixture.CheckpointedAtUtc.AddMinutes(1)),
            fixture.Checkpoint.ContinuationIdentity.ProcessInstanceId));

        Assert.Equal(ProcessDurableRuntimeDisposition.Rejected, result.Disposition);
        Assert.Equal(before.Revision, after.Revision);
        Assert.Null(after.WorkerLease);
        Assert.Empty(adapter.Invocations);
    }

    [Fact]
    public async Task Ek06_LiveOperationClaimByAnotherWorkerBlocksBeforeProcessWorkerAcquisition()
    {
        var fixture = ProcessDurabilityTestFixture.Create(
            definitionId: "process/durable-runtime-operation/foreign-live-claim",
            semanticVariant: "foreign-live-claim");
        var executor = Executor(fixture);
        var claimed = executor.Claim(
            fixture.DurableOperation,
            new("operation-attempt/foreign-live"),
            "worker/foreign",
            ProcessDurabilityTestFixture.CheckpointedAtUtc);
        var checkpoint = ProcessDurabilityTestFixture.CopyCheckpoint(
            fixture.Checkpoint,
            durableOperations: [claimed.State]);
        var store = await InitializeStoreAsync(checkpoint, "foreign-live-claim");
        var before = Assert.IsType<ProcessDurableStoreSnapshot>(await store.LoadAsync(
            Context(ProcessDurabilityTestFixture.CheckpointedAtUtc),
            checkpoint.ContinuationIdentity.ProcessInstanceId));
        var adapter = new DurableOperationFakeAdapter(fixture.Request.Contract);
        var runtime = Runtime(store, adapter);

        var result = await runtime.AdvanceOperationAsync(
            Context(ProcessDurabilityTestFixture.CheckpointedAtUtc.AddMinutes(1)),
            fixture.Plan,
            checkpoint.ContinuationIdentity.ProcessInstanceId,
            fixture.Request.Context.EmissionId);
        var after = Assert.IsType<ProcessDurableStoreSnapshot>(await store.LoadAsync(
            Context(ProcessDurabilityTestFixture.CheckpointedAtUtc.AddMinutes(1)),
            checkpoint.ContinuationIdentity.ProcessInstanceId));

        Assert.Equal(ProcessDurableRuntimeDisposition.LeaseHeld, result.Disposition);
        Assert.Equal(before.Revision, after.Revision);
        Assert.Null(after.WorkerLease);
        Assert.Empty(adapter.Invocations);
        Assert.Equal("worker/foreign", result.Operation?.CurrentAttempt?.Claim.Claimant);
    }

    [Fact]
    public async Task Ek06_ExpiredOperationClaimIsClosedAndRedispatchedUnderNewFence()
    {
        var fixture = ProcessDurabilityTestFixture.Create(
            definitionId: "process/durable-runtime-operation/expired-claim",
            semanticVariant: "expired-claim");
        var executor = Executor(fixture);
        var claimed = executor.Claim(
            fixture.DurableOperation,
            new("operation-attempt/expired"),
            "worker/foreign",
            ProcessDurabilityTestFixture.CheckpointedAtUtc);
        var expiredClaim = Assert.IsType<DurableOperationClaim>(claimed.Claim);
        var checkpoint = ProcessDurabilityTestFixture.CopyCheckpoint(
            fixture.Checkpoint,
            durableOperations: [claimed.State]);
        var store = await InitializeStoreAsync(checkpoint, "expired-claim");
        var adapter = new DurableOperationFakeAdapter(fixture.Request.Contract)
            .Script(fixture.Request.Context.EmissionId, Success("reclaimed"));
        var runtime = Runtime(store, adapter);

        var result = await runtime.AdvanceOperationAsync(
            Context(ProcessDurabilityTestFixture.CheckpointedAtUtc.AddMinutes(6)),
            fixture.Plan,
            checkpoint.ContinuationIdentity.ProcessInstanceId,
            fixture.Request.Context.EmissionId);

        Assert.Equal(DurableOperationStatus.Dispositioned, result.Operation?.Status);
        Assert.Equal(2, result.Operation?.Attempts.Length);
        Assert.Equal(DurableOperationAttemptStage.Failed, result.Operation?.Attempts[0].Stage);
        var invocation = Assert.Single(adapter.Invocations);
        Assert.NotEqual(expiredClaim.AttemptId, invocation.AttemptId);
        Assert.True(invocation.Fence.Value > expiredClaim.Fence.Value);
    }

    [Fact]
    public async Task Ek06_PreCallCapabilityFailureIsNotClassifiedAsAmbiguousAdapterEvidence()
    {
        var fixture = ProcessDurabilityTestFixture.Create(
            definitionId: "process/durable-runtime-operation/pre-call-capability",
            semanticVariant: "pre-call-capability");
        var store = await InitializeStoreAsync(fixture, "pre-call-capability");
        var adapter = new CapabilitiesChangeAfterDispatchAdapter(fixture.Request.Contract);
        var runtime = Runtime(store, adapter);

        var result = await runtime.AdvanceOperationAsync(
            Context(ProcessDurabilityTestFixture.CheckpointedAtUtc.AddMinutes(1)),
            fixture.Plan,
            fixture.Checkpoint.ContinuationIdentity.ProcessInstanceId,
            fixture.Request.Context.EmissionId);

        Assert.Equal(ProcessDurableRuntimeDisposition.Incompatible, result.Disposition);
        Assert.Equal(DurableOperationStatus.Dispatched, result.Operation?.Status);
        Assert.Null(result.Operation?.CurrentAttempt?.Failure);
        Assert.Equal(DurableOperationRecoveryRequirement.None, result.Operation?.RecoveryRequirement);
        Assert.Equal(0, adapter.ExecutionCalls);
    }

    [Fact]
    public async Task Ek06_LongAdapterCallRenewsAggregateAndOperationLeasesWithoutChangingAttemptFence()
    {
        var fixture = ProcessDurabilityTestFixture.Create(
            definitionId: "process/durable-runtime-operation/in-flight-renewal",
            semanticVariant: "in-flight-renewal");
        var lease = TimeSpan.FromMilliseconds(500);
        var originalBinding = fixture.DurableOperation.Binding;
        var shortBinding = new DurableRequestBinding(
            originalBinding.Request,
            originalBinding.Replies,
            originalBinding.MaxAttempts,
            lease,
            originalBinding.TimeoutAfter,
            originalBinding.IdempotencyEvidence,
            originalBinding.TerminalFailureOutcome,
            originalBinding.ReconciliationTarget,
            originalBinding.EscalationTarget);
        var operation = new DurableOperationState(
            DurableOperationState.CurrentSchemaVersion,
            fixture.DurableOperation.Request,
            shortBinding,
            fixture.DurableOperation.CreatedAtUtc);
        var checkpoint = ProcessDurabilityTestFixture.CopyCheckpoint(
            fixture.Checkpoint,
            durableOperations: [operation]);
        var store = await InitializeStoreAsync(checkpoint, "in-flight-renewal");
        var adapter = new BlockingSuccessAdapter(fixture.Request.Contract);
        var runtime = Runtime(store, adapter, workerLease: lease);
        var startedAtUtc = ProcessDurabilityTestFixture.CheckpointedAtUtc.AddMinutes(1);
        var timeProvider = new MutableTimeProvider(initialUtcNow: startedAtUtc);
        var context = OperationContext.Create(timeProvider: timeProvider);

        var advance = runtime.AdvanceOperationAsync(
            context,
            fixture.Plan,
            checkpoint.ContinuationIdentity.ProcessInstanceId,
            fixture.Request.Context.EmissionId);
        await adapter.Entered.WaitAsync(TimeSpan.FromSeconds(5));
        var initiallyOwned = Assert.IsType<ProcessDurableStoreSnapshot>(await store.LoadAsync(
            context,
            checkpoint.ContinuationIdentity.ProcessInstanceId));
        var initialWorker = Assert.IsType<ProcessWorkerLease>(initiallyOwned.WorkerLease);
        var initialAttempt = Assert.IsType<DurableOperationAttempt>(
            Assert.Single(initiallyOwned.Checkpoint.DurableOperations).CurrentAttempt);
        var firstRenewalAtUtc = startedAtUtc.AddMilliseconds(400);
        var secondRenewalAtUtc = startedAtUtc.AddMilliseconds(800);
        try
        {
            timeProvider.SetUtcNow(firstRenewalAtUtc);
            await WaitForSnapshotAsync(
                store,
                context,
                checkpoint.ContinuationIdentity.ProcessInstanceId,
                snapshot => HasOperationOwnershipRenewalAt(snapshot, firstRenewalAtUtc));
            timeProvider.SetUtcNow(secondRenewalAtUtc);
            await WaitForSnapshotAsync(
                store,
                context,
                checkpoint.ContinuationIdentity.ProcessInstanceId,
                snapshot => HasOperationOwnershipRenewalAt(snapshot, secondRenewalAtUtc));
            Assert.True(secondRenewalAtUtc > initialWorker.ExpiresAtUtc);
            Assert.True(secondRenewalAtUtc > initialAttempt.Claim.ExpiresAtUtc);
        }
        finally
        {
            adapter.Complete();
        }
        var result = await advance.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(DurableOperationStatus.Dispositioned, result.Operation?.Status);
        var retainedAttempt = Assert.Single(Assert.IsType<DurableOperationState>(result.Operation).Attempts);
        var invocation = Assert.IsType<DurableOperationInvocation>(adapter.Invocation);
        Assert.Equal(invocation.AttemptId, retainedAttempt.Claim.AttemptId);
        Assert.Equal(invocation.Fence, retainedAttempt.Claim.Fence);
        Assert.True(retainedAttempt.Claim.RenewedAtUtc > retainedAttempt.Claim.ClaimedAtUtc);
        var worker = Assert.IsType<ProcessWorkerLease>(result.Snapshot?.WorkerLease);
        Assert.True(worker.RenewedAtUtc > worker.ClaimedAtUtc);
    }

    [Fact]
    public async Task Ek06_ConcurrentAdvanceOfSameOperationUsesOnePhysicalInvocation()
    {
        var fixture = ProcessDurabilityTestFixture.Create(
            definitionId: "process/durable-runtime-operation/single-flight",
            semanticVariant: "single-flight");
        var store = await InitializeStoreAsync(fixture, "single-flight");
        var adapter = new BlockingSuccessAdapter(fixture.Request.Contract);
        var runtime = Runtime(store, adapter);
        var context = Context(ProcessDurabilityTestFixture.CheckpointedAtUtc.AddMinutes(1));

        var first = runtime.AdvanceOperationAsync(
            context,
            fixture.Plan,
            fixture.Checkpoint.ContinuationIdentity.ProcessInstanceId,
            fixture.Request.Context.EmissionId);
        await adapter.Entered.WaitAsync(TimeSpan.FromSeconds(5));
        var second = runtime.AdvanceOperationAsync(
            context,
            fixture.Plan,
            fixture.Checkpoint.ContinuationIdentity.ProcessInstanceId,
            fixture.Request.Context.EmissionId);
        await Task.Delay(TimeSpan.FromMilliseconds(50));

        Assert.Equal(1, adapter.ExecutionCalls);
        Assert.False(second.IsCompleted);

        adapter.Complete();
        var results = await Task.WhenAll(first, second);

        Assert.All(results, result => Assert.Equal(DurableOperationStatus.Dispositioned, result.Operation?.Status));
        Assert.Equal(1, adapter.ExecutionCalls);
    }

    [Fact]
    public async Task Ek06_LongReconciliationRenewsWorkerAndRemainsSingleFlight()
    {
        var fixture = ProcessDurabilityTestFixture.Create(
            definitionId: "process/durable-runtime-operation/reconciliation-single-flight",
            semanticVariant: "reconciliation-single-flight",
            durableOperationRetry: RequestRetrySemantics.ReconcileBeforeRetry);
        var reconciliationRequired = CreateReconciliationRequiredOperation(fixture);
        var checkpoint = ProcessDurabilityTestFixture.CopyCheckpoint(
            fixture.Checkpoint,
            durableOperations: [reconciliationRequired]);
        var store = await InitializeStoreAsync(checkpoint, "reconciliation-single-flight");
        var lease = TimeSpan.FromMilliseconds(500);
        var adapter = new BlockingReconciledOutcomeAdapter(fixture.Request.Contract);
        var runtime = Runtime(store, adapter, workerLease: lease);
        var startedAtUtc = ProcessDurabilityTestFixture.CheckpointedAtUtc.AddMinutes(1);
        var timeProvider = new MutableTimeProvider(initialUtcNow: startedAtUtc);
        var context = OperationContext.Create(timeProvider: timeProvider);

        var first = runtime.AdvanceOperationAsync(
            context,
            fixture.Plan,
            checkpoint.ContinuationIdentity.ProcessInstanceId,
            fixture.Request.Context.EmissionId);
        await adapter.Entered.WaitAsync(TimeSpan.FromSeconds(5));
        var initiallyOwned = Assert.IsType<ProcessDurableStoreSnapshot>(await store.LoadAsync(
            context,
            checkpoint.ContinuationIdentity.ProcessInstanceId));
        var initialWorker = Assert.IsType<ProcessWorkerLease>(initiallyOwned.WorkerLease);
        var second = runtime.AdvanceOperationAsync(
            context,
            fixture.Plan,
            checkpoint.ContinuationIdentity.ProcessInstanceId,
            fixture.Request.Context.EmissionId);
        var firstRenewalAtUtc = startedAtUtc.AddMilliseconds(400);
        var secondRenewalAtUtc = startedAtUtc.AddMilliseconds(800);
        try
        {
            timeProvider.SetUtcNow(firstRenewalAtUtc);
            await WaitForSnapshotAsync(
                store,
                context,
                checkpoint.ContinuationIdentity.ProcessInstanceId,
                snapshot => snapshot.WorkerLease?.RenewedAtUtc == firstRenewalAtUtc);
            timeProvider.SetUtcNow(secondRenewalAtUtc);
            await WaitForSnapshotAsync(
                store,
                context,
                checkpoint.ContinuationIdentity.ProcessInstanceId,
                snapshot => snapshot.WorkerLease?.RenewedAtUtc == secondRenewalAtUtc);
            Assert.True(secondRenewalAtUtc > initialWorker.ExpiresAtUtc);
            Assert.Equal(1, adapter.ReconciliationCalls);
            Assert.False(second.IsCompleted);
        }
        finally
        {
            adapter.Complete();
        }
        var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.All(results, result => Assert.Equal(DurableOperationStatus.Dispositioned, result.Operation?.Status));
        Assert.Equal(1, adapter.ReconciliationCalls);
        Assert.Equal(0, adapter.ExecutionCalls);
        var worker = Assert.IsType<ProcessWorkerLease>(results[0].Snapshot?.WorkerLease);
        Assert.True(worker.RenewedAtUtc > worker.ClaimedAtUtc);
    }

    [Fact]
    public async Task Ek06_ElapsedPendingDeadlineReturnsRecoveryWithoutWorkerOrAdapterMutation()
    {
        var fixture = ProcessDurabilityTestFixture.Create(
            definitionId: "process/durable-runtime-operation/elapsed-pending-deadline",
            semanticVariant: "elapsed-pending-deadline",
            durableOperationTimeoutAfter: TimeSpan.FromMinutes(1));
        var store = await InitializeStoreAsync(fixture, "elapsed-pending-deadline");
        var adapter = new DurableOperationFakeAdapter(fixture.Request.Contract)
            .Script(fixture.Request.Context.EmissionId, Success("must-not-run"));
        var runtime = Runtime(store, adapter);
        var before = Assert.IsType<ProcessDurableStoreSnapshot>(await store.LoadAsync(
            Context(ProcessDurabilityTestFixture.CheckpointedAtUtc),
            fixture.Checkpoint.ContinuationIdentity.ProcessInstanceId));

        var result = await runtime.AdvanceOperationAsync(
            Context(ProcessDurabilityTestFixture.CheckpointedAtUtc.AddSeconds(1)),
            fixture.Plan,
            fixture.Checkpoint.ContinuationIdentity.ProcessInstanceId,
            fixture.Request.Context.EmissionId);

        Assert.Equal(ProcessDurableRuntimeDisposition.Rejected, result.Disposition);
        Assert.Equal(DurableOperationStatus.Pending, result.Operation?.Status);
        Assert.Equal(before.Revision, result.Snapshot?.Revision);
        Assert.Null(result.Snapshot?.WorkerLease);
        Assert.Empty(adapter.Invocations);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic =>
                diagnostic.Code == ProcessDurableRuntimeDiagnosticCodes.OperationRecoveryRequired);
    }

    [Fact]
    public async Task Ek06_ReconciliationDeadlineCrossingAfterAcquisitionIsStructuredAndDoesNotCallAdapter()
    {
        var fixture = ProcessDurabilityTestFixture.Create(
            definitionId: "process/durable-runtime-operation/crossing-reconciliation-deadline",
            semanticVariant: "crossing-reconciliation-deadline",
            durableOperationRetry: RequestRetrySemantics.ReconcileBeforeRetry,
            durableOperationTimeoutAfter: TimeSpan.FromMinutes(2));
        var reconciliationRequired = CreateReconciliationRequiredOperation(fixture);
        var checkpoint = ProcessDurabilityTestFixture.CopyCheckpoint(
            fixture.Checkpoint,
            durableOperations: [reconciliationRequired]);
        var store = await InitializeStoreAsync(checkpoint, "crossing-reconciliation-deadline");
        var adapter = new ReconciliationProbeAdapter(fixture.Request.Contract);
        var runtime = Runtime(store, adapter);
        var timeProvider = new StepAtReadTimeProvider(
            ProcessDurabilityTestFixture.CheckpointedAtUtc.AddSeconds(30),
            ProcessDurabilityTestFixture.ActivatedAtUtc.AddMinutes(2),
            readsBeforeStep: 2);

        var result = await runtime.AdvanceOperationAsync(
            OperationContext.Create(timeProvider: timeProvider),
            fixture.Plan,
            checkpoint.ContinuationIdentity.ProcessInstanceId,
            fixture.Request.Context.EmissionId);

        Assert.Equal(ProcessDurableRuntimeDisposition.Rejected, result.Disposition);
        Assert.Equal(DurableOperationStatus.ReconciliationRequired, result.Operation?.Status);
        Assert.NotNull(result.Snapshot?.WorkerLease);
        Assert.Equal(0, adapter.ExecutionCalls);
        Assert.Equal(0, adapter.ReconciliationCalls);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic =>
                diagnostic.Code == ProcessDurableRuntimeDiagnosticCodes.OperationRecoveryRequired);
    }

    [Fact]
    public async Task Ek06_PausedPendingOperationDoesNoWorkThenContinuesWithSameAttemptAndAffinity()
    {
        var fixture = ProcessDurabilityTestFixture.Create(
            definitionId: "process/durable-runtime-operation/paused-pending",
            semanticVariant: "paused-pending");
        var store = await InitializeStoreAsync(fixture, "paused-pending");
        var adapter = new DurableOperationFakeAdapter(fixture.Request.Contract)
            .Script(fixture.Request.Context.EmissionId, Success("after-continue"));
        var runtime = Runtime(store, adapter);
        var commands = ProcessControlTestFixture.Create();
        var affinityAt = ProcessDurabilityTestFixture.CheckpointedAtUtc.AddSeconds(1);
        var affinity = ProcessControlTestFixture.Affinity(
            slot: "node/index-generation",
            value: "generation/retained");
        var bound = await runtime.BindAttemptAffinityAsync(
            Context(affinityAt),
            fixture.Plan,
            new(
                new(
                    new(
                        fixture.Checkpoint.Control.ProcessInstanceId,
                        fixture.Checkpoint.Control.CurrentAttempt.AttemptId),
                    fixture.Checkpoint.Control.Revision),
                affinity,
                affinityAt));
        var boundCheckpoint = Assert.IsType<ProcessDurableStoreSnapshot>(bound.Snapshot).Checkpoint;
        var pauseAt = affinityAt.AddSeconds(1);
        var paused = await runtime.ApplyControlAsync(
            Context(pauseAt),
            fixture.Plan,
            commands.Pause(
                boundCheckpoint.Control,
                id: "pause/pending-operation",
                issuedAtUtc: pauseAt));
        var pausedSnapshot = Assert.IsType<ProcessDurableStoreSnapshot>(paused.Snapshot);

        var blocked = await runtime.AdvanceOperationAsync(
            Context(pauseAt.AddSeconds(1)),
            fixture.Plan,
            fixture.Checkpoint.ContinuationIdentity.ProcessInstanceId,
            fixture.Request.Context.EmissionId);

        Assert.Equal(ProcessDurableRuntimeDisposition.Paused, blocked.Disposition);
        Assert.Empty(adapter.Invocations);
        Assert.Equal(pausedSnapshot.Revision, blocked.Snapshot?.Revision);

        var continueAt = pauseAt.AddSeconds(2);
        var continued = await runtime.ApplyControlAsync(
            Context(continueAt),
            fixture.Plan,
            commands.Continue(
                pausedSnapshot.Checkpoint.Control,
                id: "continue/pending-operation",
                issuedAtUtc: continueAt));
        var completed = await runtime.AdvanceOperationAsync(
            Context(continueAt.AddSeconds(1)),
            fixture.Plan,
            fixture.Checkpoint.ContinuationIdentity.ProcessInstanceId,
            fixture.Request.Context.EmissionId);

        Assert.Equal(DurableOperationStatus.Dispositioned, completed.Operation?.Status);
        Assert.Single(adapter.Invocations);
        Assert.Single(Assert.IsType<DurableOperationState>(completed.Operation).Attempts);
        Assert.Equal(
            affinity,
            Assert.Single(Assert.IsType<ProcessDurableStoreSnapshot>(continued.Snapshot)
                .Checkpoint.Control.CurrentAttempt.AffinityBindings).Affinity);
    }

    [Fact]
    public async Task Ek06_ReuseWithoutClosedWaitWinnerDurablyFallsBackToRejectedWithDiagnostic()
    {
        var fixture = ProcessDurabilityTestFixture.Create(
            definitionId: "process/durable-runtime-operation/reuse-without-winner",
            semanticVariant: "reuse-without-winner",
            durableOperationLateResult: RequestResultDisposition.ReusePriorDisposition);
        var acknowledged = CreateAcknowledgedOperation(fixture, "closed-before-winner");
        var checkpoint = ProcessDurabilityTestFixture.CopyCheckpoint(
            fixture.Checkpoint,
            durableOperations: [acknowledged]);
        var store = await InitializeStoreAsync(checkpoint, "reuse-without-winner");
        var adapter = new DurableOperationFakeAdapter(fixture.Request.Contract);
        var runtime = Runtime(store, adapter);
        var controls = ProcessControlTestFixture.Create();
        var cancelAt = ProcessDurabilityTestFixture.CheckpointedAtUtc.AddSeconds(1);
        var cancelled = await runtime.CancelAsync(
            Context(cancelAt),
            fixture.Plan,
            controls.Cancel(
                checkpoint.Control,
                id: "cancel/reuse-without-winner",
                issuedAtUtc: cancelAt),
            fixture.Activation.Context);
        Assert.Equal(ProcessDurableRuntimeDisposition.Applied, cancelled.Disposition);

        var result = await runtime.AdvanceOperationAsync(
            Context(cancelAt.AddSeconds(1)),
            fixture.Plan,
            checkpoint.ContinuationIdentity.ProcessInstanceId,
            fixture.Request.Context.EmissionId);

        Assert.Equal(ProcessDurableRuntimeDisposition.Applied, result.Disposition);
        Assert.Equal(DurableOperationStatus.Dispositioned, result.Operation?.Status);
        Assert.Equal(
            DurableOperationAdmissionDisposition.ReusedPriorDisposition,
            result.Operation?.Admission?.Disposition);
        Assert.Equal(
            DurableOperationAdmissionDisposition.Rejected,
            result.Operation?.Admission?.PriorDisposition);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == ProcessDurableRuntimeDiagnosticCodes.OperationRecoveryRequired);
        Assert.Empty(adapter.Invocations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ek06_ClosedOriginAttemptCannotStartPendingOrRetryPhysicalWork(bool retryEligible)
    {
        var scenario = retryEligible ? "retry" : "pending";
        var fixture = ProcessDurabilityTestFixture.Create(
            definitionId: $"process/durable-runtime-operation/closed-origin-{scenario}",
            semanticVariant: $"closed-origin-{scenario}",
            recoveryPolicy: ProcessRecoveryPolicy.RestartAttempt);
        var operation = retryEligible
            ? CreateRetryEligibleOperation(fixture)
            : fixture.DurableOperation;
        var checkpoint = ProcessDurabilityTestFixture.CopyCheckpoint(
            fixture.Checkpoint,
            durableOperations: [operation]);
        var store = await InitializeStoreAsync(checkpoint, $"closed-origin-{scenario}");
        var adapter = new DurableOperationFakeAdapter(fixture.Request.Contract)
            .Script(fixture.Request.Context.EmissionId, Success("must-not-run"));
        var runtime = Runtime(store, adapter);
        var commands = ProcessControlTestFixture.Create();
        var restartAt = ProcessDurabilityTestFixture.CheckpointedAtUtc.AddSeconds(1);
        var restarted = await runtime.ApplyControlAsync(
            Context(restartAt),
            fixture.Plan,
            commands.Restart(
                checkpoint.Control,
                newAttemptId: $"process-attempt/closed-origin-{scenario}",
                id: $"restart/closed-origin-{scenario}",
                issuedAtUtc: restartAt));
        Assert.True(
            restarted.Disposition == ProcessDurableRuntimeDisposition.Applied,
            FormatDiagnostics(restarted.Diagnostics));

        var result = await runtime.AdvanceOperationAsync(
            Context(restartAt.AddSeconds(1)),
            fixture.Plan,
            checkpoint.ContinuationIdentity.ProcessInstanceId,
            fixture.Request.Context.EmissionId);

        Assert.Equal(ProcessDurableRuntimeDisposition.Terminal, result.Disposition);
        Assert.Equal(operation.Status, result.Operation?.Status);
        Assert.Empty(adapter.Invocations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ek06_OrdinaryTerminalContinuationCannotStartPendingOrRetryPhysicalWork(bool retryEligible)
    {
        var scenario = retryEligible ? "retry" : "pending";
        var fixture = ProcessDurabilityTestFixture.Create(
            definitionId: $"process/durable-runtime-operation/terminal-origin-{scenario}",
            semanticVariant: $"terminal-origin-{scenario}");
        var operation = retryEligible
            ? CreateRetryEligibleOperation(fixture)
            : fixture.DurableOperation;
        var checkpoint = ProcessDurabilityTestFixture.CopyCheckpoint(
            fixture.Checkpoint,
            durableOperations: [operation]);
        var store = await InitializeStoreAsync(checkpoint, $"terminal-origin-{scenario}");
        var adapter = new DurableOperationFakeAdapter(fixture.Request.Contract)
            .Script(fixture.Request.Context.EmissionId, Success("must-not-run"));
        var runtime = Runtime(store, adapter);
        var terminalAtUtc = ProcessDurabilityTestFixture.CheckpointedAtUtc.AddSeconds(1);
        var terminalActivation = new ProcessActivation(
            new($"activation/terminal-origin-{scenario}"),
            ProcessActivationCause.Interaction,
            terminalAtUtc,
            fixture.Activation.Context,
            [fixture.PendingReply]);
        var terminal = await runtime.ActivateAsync(
            Context(terminalAtUtc),
            fixture.Plan,
            checkpoint.ContinuationIdentity,
            terminalActivation);
        var terminalSnapshot = Assert.IsType<ProcessDurableStoreSnapshot>(terminal.Snapshot);
        Assert.Equal(ExecutionTerminalOutcomeKind.Completed, terminalSnapshot.Checkpoint.Continuation.Terminal.Kind);

        var result = await runtime.AdvanceOperationAsync(
            Context(terminalAtUtc.AddSeconds(1)),
            fixture.Plan,
            checkpoint.ContinuationIdentity.ProcessInstanceId,
            fixture.Request.Context.EmissionId);

        Assert.Equal(ProcessDurableRuntimeDisposition.Terminal, result.Disposition);
        Assert.Equal(operation.Status, result.Operation?.Status);
        Assert.Equal(terminalSnapshot.Revision, result.Snapshot?.Revision);
        Assert.Empty(adapter.Invocations);
    }

    [Fact]
    public async Task Ek06_InFlightDispatchedEvidenceMayCompleteAfterOriginAttemptRestarts()
    {
        var fixture = ProcessDurabilityTestFixture.Create(
            definitionId: "process/durable-runtime-operation/in-flight-origin-restart",
            semanticVariant: "in-flight-origin-restart",
            recoveryPolicy: ProcessRecoveryPolicy.RestartAttempt);
        var store = await InitializeStoreAsync(fixture, "in-flight-origin-restart");
        var adapter = new BlockingSuccessAdapter(fixture.Request.Contract);
        var runtime = Runtime(store, adapter);
        var commands = ProcessControlTestFixture.Create();
        var dispatchAt = ProcessDurabilityTestFixture.CheckpointedAtUtc.AddSeconds(1);
        var operationTime = new MutableTimeProvider(dispatchAt);
        var advancing = runtime.AdvanceOperationAsync(
            OperationContext.Create(timeProvider: operationTime),
            fixture.Plan,
            fixture.Checkpoint.ContinuationIdentity.ProcessInstanceId,
            fixture.Request.Context.EmissionId);
        await adapter.Entered.WaitAsync(TimeSpan.FromSeconds(5));
        var dispatched = Assert.IsType<ProcessDurableStoreSnapshot>(await store.LoadAsync(
            Context(dispatchAt),
            fixture.Checkpoint.ContinuationIdentity.ProcessInstanceId));
        Assert.Equal(
            DurableOperationStatus.Dispatched,
            Assert.Single(dispatched.Checkpoint.DurableOperations).Status);

        var restartAt = dispatchAt.AddSeconds(1);
        var restarted = await runtime.ApplyControlAsync(
            Context(restartAt),
            fixture.Plan,
            commands.Restart(
                dispatched.Checkpoint.Control,
                newAttemptId: "process-attempt/after-in-flight-request",
                id: "restart/while-request-in-flight",
                issuedAtUtc: restartAt));
        Assert.True(
            restarted.Disposition == ProcessDurableRuntimeDisposition.Applied,
            FormatDiagnostics(restarted.Diagnostics));

        operationTime.SetUtcNow(restartAt.AddSeconds(1));
        adapter.Complete();
        var result = await advancing;

        Assert.Equal(DurableOperationStatus.Dispositioned, result.Operation?.Status);
        Assert.Equal(DurableOperationAdmissionDisposition.Rejected, result.Operation?.Admission?.Disposition);
        Assert.Equal(1, adapter.ExecutionCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ek06_ClosedOriginRetainedClaimMayFinishUnderItsExactOperationFence(bool dispatched)
    {
        var stage = dispatched ? "dispatched" : "claimed";
        var fixture = ProcessDurabilityTestFixture.Create(
            definitionId: $"process/durable-runtime-operation/retained-{stage}-origin-restart",
            semanticVariant: $"retained-{stage}-origin-restart",
            recoveryPolicy: ProcessRecoveryPolicy.RestartAttempt);
        var executor = Executor(fixture);
        var claimedAtUtc = ProcessDurabilityTestFixture.CheckpointedAtUtc;
        var claimed = executor.Claim(
            fixture.DurableOperation,
            new($"operation-attempt/retained-{stage}"),
            "worker/runtime-operation-tests",
            claimedAtUtc);
        var claim = Assert.IsType<DurableOperationClaim>(claimed.Claim);
        var retained = dispatched
            ? executor.BeginDispatch(
                claimed.State,
                claim.AttemptId,
                claim.Fence,
                claimedAtUtc).State
            : claimed.State;
        var checkpoint = ProcessDurabilityTestFixture.CopyCheckpoint(
            fixture.Checkpoint,
            durableOperations: [retained]);
        var store = await InitializeStoreAsync(checkpoint, $"retained-{stage}-origin-restart");
        var adapter = new DurableOperationFakeAdapter(fixture.Request.Contract)
            .Script(fixture.Request.Context.EmissionId, Success($"retained-{stage}"));
        var runtime = Runtime(store, adapter);
        var commands = ProcessControlTestFixture.Create();
        var restartAt = claimedAtUtc.AddSeconds(1);
        var restarted = await runtime.ApplyControlAsync(
            Context(restartAt),
            fixture.Plan,
            commands.Restart(
                checkpoint.Control,
                newAttemptId: $"process-attempt/after-retained-{stage}",
                id: $"restart/after-retained-{stage}",
                issuedAtUtc: restartAt));
        Assert.True(
            restarted.Disposition == ProcessDurableRuntimeDisposition.Applied,
            FormatDiagnostics(restarted.Diagnostics));

        var result = await runtime.AdvanceOperationAsync(
            Context(restartAt.AddSeconds(1)),
            fixture.Plan,
            checkpoint.ContinuationIdentity.ProcessInstanceId,
            fixture.Request.Context.EmissionId);

        Assert.Equal(DurableOperationStatus.Dispositioned, result.Operation?.Status);
        var invocation = Assert.Single(adapter.Invocations);
        Assert.Equal(claim.AttemptId, invocation.AttemptId);
        Assert.Equal(claim.Fence, invocation.Fence);
        var finalAttempt = Assert.Single(Assert.IsType<DurableOperationState>(result.Operation).Attempts);
        Assert.Equal(claim.AttemptId, finalAttempt.Claim.AttemptId);
        Assert.Equal(claim.Fence, finalAttempt.Claim.Fence);
    }

    [Fact]
    public async Task Ek06_PausedDispatchedOperationDoesNotRedispatchAfterCrashRecovery()
    {
        var fixture = ProcessDurabilityTestFixture.Create(
            definitionId: "process/durable-runtime-operation/paused-dispatched",
            semanticVariant: "paused-dispatched");
        var executor = Executor(fixture);
        var claimedAtUtc = ProcessDurabilityTestFixture.CheckpointedAtUtc;
        var claimed = executor.Claim(
            fixture.DurableOperation,
            new("operation-attempt/paused-dispatched"),
            "worker/runtime-operation-tests",
            claimedAtUtc);
        var claim = Assert.IsType<DurableOperationClaim>(claimed.Claim);
        var dispatched = executor.BeginDispatch(
            claimed.State,
            claim.AttemptId,
            claim.Fence,
            claimedAtUtc);
        var checkpoint = ProcessDurabilityTestFixture.CopyCheckpoint(
            fixture.Checkpoint,
            durableOperations: [dispatched.State]);
        var store = await InitializeStoreAsync(checkpoint, "paused-dispatched");
        var adapter = new DurableOperationFakeAdapter(fixture.Request.Contract)
            .Script(fixture.Request.Context.EmissionId, Success("must-not-redispatch"));
        var runtime = Runtime(store, adapter);
        var pauseAt = claimedAtUtc.AddSeconds(1);
        var paused = await runtime.ApplyControlAsync(
            Context(pauseAt),
            fixture.Plan,
            ProcessControlTestFixture.Create().Pause(
                checkpoint.Control,
                id: "pause/dispatched-operation",
                issuedAtUtc: pauseAt));
        var pausedSnapshot = Assert.IsType<ProcessDurableStoreSnapshot>(paused.Snapshot);

        var blocked = await runtime.AdvanceOperationAsync(
            Context(pauseAt.AddSeconds(1)),
            fixture.Plan,
            checkpoint.ContinuationIdentity.ProcessInstanceId,
            fixture.Request.Context.EmissionId);

        Assert.Equal(ProcessDurableRuntimeDisposition.Paused, blocked.Disposition);
        Assert.Equal(pausedSnapshot.Revision, blocked.Snapshot?.Revision);
        Assert.Equal(DurableOperationStatus.Dispatched, blocked.Operation?.Status);
        Assert.Empty(adapter.Invocations);
    }

    [Fact]
    public async Task Ek06_PreemptedDeterministicReplyIdentityReturnsStructuredIdentityConflict()
    {
        var fixture = ProcessDurabilityTestFixture.Create(
            definitionId: "process/durable-runtime-operation/reply-identity-conflict",
            semanticVariant: "reply-identity-conflict");
        var acknowledged = CreateAcknowledgedOperation(fixture, "reply-identity-conflict");
        var deterministicReply = acknowledged.CreateReply(
            ProcessDurableRuntimeIdentities.OperationReply(acknowledged.OperationId),
            ProcessDurableRuntimeIdentities.OperationReplyIdempotency(acknowledged.OperationId),
            acknowledged.Request.Context.Ordering,
            acknowledged.Request.Context.Provenance);
        var preemptingReply = new ReplyEnvelope(
            deterministicReply.SchemaVersion,
            deterministicReply.Context,
            deterministicReply.Contract,
            deterministicReply.InReplyTo,
            new RequestResultOutcome(
                new("result"),
                ProcessDurabilityTestFixture.StringValue("preempting-content")));
        var preemptingInput = new ProcessActivationInput(
            Assert.IsType<ProcessTokenInteractionTarget>(fixture.Request.ResponseTarget),
            preemptingReply);
        var checkpoint = ProcessDurabilityTestFixture.CopyCheckpoint(
            fixture.Checkpoint,
            inbox: [.. fixture.Checkpoint.Inbox, new(preemptingInput, fixture.Checkpoint.UpdatedAtUtc)],
            durableOperations: [acknowledged]);
        var store = await InitializeStoreAsync(checkpoint, "reply-identity-conflict");
        var adapter = new DurableOperationFakeAdapter(fixture.Request.Contract);
        var runtime = Runtime(store, adapter);

        var result = await runtime.AdvanceOperationAsync(
            Context(ProcessDurabilityTestFixture.CheckpointedAtUtc.AddSeconds(1)),
            fixture.Plan,
            checkpoint.ContinuationIdentity.ProcessInstanceId,
            fixture.Request.Context.EmissionId);

        Assert.Equal(ProcessDurableRuntimeDisposition.IdentityConflict, result.Disposition);
        Assert.Equal(DurableOperationStatus.Acknowledged, result.Operation?.Status);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic =>
                diagnostic.Code == ProcessDurableRuntimeDiagnosticCodes.OperationReplyIdentityConflict);
        Assert.Empty(adapter.Invocations);
        var persisted = Assert.IsType<ProcessDurableStoreSnapshot>(await store.LoadAsync(
            Context(ProcessDurabilityTestFixture.CheckpointedAtUtc.AddSeconds(2)),
            checkpoint.ContinuationIdentity.ProcessInstanceId));
        Assert.Equal(
            DurableOperationStatus.Acknowledged,
            Assert.Single(persisted.Checkpoint.DurableOperations).Status);
        Assert.Equal(
            ProcessStorageContentFingerprints.Input(preemptingInput),
            ProcessStorageContentFingerprints.Input(Assert.Single(
                persisted.Checkpoint.Inbox,
                entry => entry.EmissionId == deterministicReply.Context.EmissionId).Input));
    }

    [Fact]
    public async Task Ek06_SameOwnerDispatchedClaimIsRenewedBeforeSafeRedispatch()
    {
        var fixture = ProcessDurabilityTestFixture.Create(
            definitionId: "process/durable-runtime-operation/pre-dispatch-renewal",
            semanticVariant: "pre-dispatch-renewal");
        var executor = Executor(fixture);
        var claimedAtUtc = ProcessDurabilityTestFixture.CheckpointedAtUtc;
        var claimed = executor.Claim(
            fixture.DurableOperation,
            new("operation-attempt/pre-dispatch-renewal"),
            "worker/runtime-operation-tests",
            claimedAtUtc);
        var claim = Assert.IsType<DurableOperationClaim>(claimed.Claim);
        var dispatched = executor.BeginDispatch(
            claimed.State,
            claim.AttemptId,
            claim.Fence,
            claimedAtUtc);
        var checkpoint = ProcessDurabilityTestFixture.CopyCheckpoint(
            fixture.Checkpoint,
            durableOperations: [dispatched.State]);
        var store = await InitializeStoreAsync(checkpoint, "pre-dispatch-renewal");
        var adapter = new DurableOperationFakeAdapter(fixture.Request.Contract)
            .Script(fixture.Request.Context.EmissionId, Success("after-pre-dispatch-renewal"));
        var runtime = Runtime(store, adapter);
        var redispatchAt = claim.ExpiresAtUtc.AddSeconds(-1);

        var result = await runtime.AdvanceOperationAsync(
            Context(redispatchAt),
            fixture.Plan,
            checkpoint.ContinuationIdentity.ProcessInstanceId,
            fixture.Request.Context.EmissionId);

        var retainedAttempt = Assert.Single(Assert.IsType<DurableOperationState>(result.Operation).Attempts);
        var invocation = Assert.Single(adapter.Invocations);
        Assert.Equal(claim.AttemptId, invocation.AttemptId);
        Assert.Equal(claim.Fence, invocation.Fence);
        Assert.Equal(claim.AttemptId, retainedAttempt.Claim.AttemptId);
        Assert.Equal(claim.Fence, retainedAttempt.Claim.Fence);
        Assert.Equal(redispatchAt, retainedAttempt.Claim.RenewedAtUtc);
        Assert.True(retainedAttempt.Claim.ExpiresAtUtc > claim.ExpiresAtUtc);
    }

    static async Task<InMemoryProcessDurableStore> InitializeStoreAsync(
        ProcessDurabilityTestFixture fixture,
        string scenario) =>
        await InitializeStoreAsync(fixture.Checkpoint, scenario);

    static async Task<InMemoryProcessDurableStore> InitializeStoreAsync(
        ProcessDurableCheckpoint checkpoint,
        string scenario)
    {
        var store = new InMemoryProcessDurableStore();
        await InitializeStoreAsync(store, checkpoint, scenario);
        return store;
    }

    static async Task InitializeStoreAsync(
        InMemoryProcessDurableStore store,
        ProcessDurableCheckpoint checkpoint,
        string scenario)
    {
        var initialized = await store.InitializeAsync(
            Context(ProcessDurabilityTestFixture.CheckpointedAtUtc),
            new($"commit/runtime-operation/{scenario}"),
            checkpoint);
        Assert.Equal(ProcessStoreMutationDisposition.Applied, initialized.Disposition);
    }

    static async Task<ProcessDurableStoreSnapshot> WaitForSnapshotAsync(
        InMemoryProcessDurableStore store,
        OperationContext context,
        ProcessInstanceId instanceId,
        Func<ProcessDurableStoreSnapshot, bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            while (true)
            {
                var snapshot = await store.LoadAsync(context, instanceId);
                if (snapshot is not null && predicate(snapshot))
                {
                    return snapshot;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(10), timeout.Token);
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw new TimeoutException("The expected durable Process snapshot was not observed within five seconds.");
        }
    }

    static bool HasOperationOwnershipRenewalAt(
        ProcessDurableStoreSnapshot snapshot,
        DateTimeOffset expectedRenewedAtUtc) =>
        snapshot.WorkerLease?.RenewedAtUtc == expectedRenewedAtUtc
        && snapshot.Checkpoint.DurableOperations.Length == 1
        && snapshot.Checkpoint.DurableOperations[0].CurrentAttempt?.Claim.RenewedAtUtc == expectedRenewedAtUtc;

    static ProcessDurableRuntime Runtime(
        IProcessDurableStore store,
        IDurableOperationAdapter adapter,
        int maxAmbiguousStoreMutationAttempts = 3,
        TimeSpan? workerLease = null) =>
        Runtime(store, new SingleAdapterResolver(adapter), maxAmbiguousStoreMutationAttempts, workerLease);

    static ProcessDurableRuntime Runtime(
        IProcessDurableStore store,
        IProcessDurableOperationAdapterResolver resolver,
        int maxAmbiguousStoreMutationAttempts = 3,
        TimeSpan? workerLease = null) =>
        new(
            store,
            RejectingHost.Instance,
            new(
                "worker/runtime-operation-tests",
                workerLease ?? WorkerLease,
                maxAmbiguousStoreMutationAttempts),
            operationAdapterResolver: resolver);

    static DurableOperationOutcomeObservation Success(string value = "accepted") =>
        new(new RequestResultOutcome(new("result"), ProcessDurabilityTestFixture.StringValue(value)));

    static DurableOperationReferenceExecutor Executor(ProcessDurabilityTestFixture fixture) =>
        new(fixture.Plan.ValidationContext.InteractionContracts
            ?? throw new InvalidOperationException("The fixture requires interaction contracts."));

    static DurableOperationState CreateReconciliationRequiredOperation(
        ProcessDurabilityTestFixture fixture)
    {
        var executor = Executor(fixture);
        var observedAtUtc = ProcessDurabilityTestFixture.CheckpointedAtUtc;
        var claimed = executor.Claim(
            fixture.DurableOperation,
            new("operation-attempt/reconciliation-required"),
            "worker/reconciliation-source",
            observedAtUtc);
        var claim = Assert.IsType<DurableOperationClaim>(claimed.Claim);
        var dispatched = executor.BeginDispatch(
            claimed.State,
            claim.AttemptId,
            claim.Fence,
            observedAtUtc);
        var failed = executor.RecordObservation(
            dispatched.State,
            claim.AttemptId,
            claim.Fence,
            new DurableOperationFailureObservation(new(
                DurableOperationFailurePhase.InCall,
                DurableOperationEffectEvidence.Ambiguous,
                DurableOperationFailureDisposition.Retryable,
                "test.ambiguous")),
            observedAtUtc);
        Assert.Equal(DurableOperationStatus.ReconciliationRequired, failed.State.Status);
        return failed.State;
    }

    static DurableOperationState CreateAcknowledgedOperation(
        ProcessDurabilityTestFixture fixture,
        string attemptId)
    {
        var executor = Executor(fixture);
        var observedAtUtc = ProcessDurabilityTestFixture.CheckpointedAtUtc;
        var claimed = executor.Claim(
            fixture.DurableOperation,
            new($"operation-attempt/{attemptId}"),
            "worker/acknowledgement-source",
            observedAtUtc);
        var claim = Assert.IsType<DurableOperationClaim>(claimed.Claim);
        var dispatched = executor.BeginDispatch(
            claimed.State,
            claim.AttemptId,
            claim.Fence,
            observedAtUtc);
        var acknowledged = executor.RecordObservation(
            dispatched.State,
            claim.AttemptId,
            claim.Fence,
            Success("acknowledged"),
            observedAtUtc);
        Assert.Equal(DurableOperationStatus.Acknowledged, acknowledged.State.Status);
        return acknowledged.State;
    }

    static DurableOperationState CreateRetryEligibleOperation(ProcessDurabilityTestFixture fixture)
    {
        var executor = Executor(fixture);
        var observedAtUtc = ProcessDurabilityTestFixture.CheckpointedAtUtc;
        var claimed = executor.Claim(
            fixture.DurableOperation,
            new("operation-attempt/retry-eligible"),
            "worker/retry-source",
            observedAtUtc);
        var claim = Assert.IsType<DurableOperationClaim>(claimed.Claim);
        var dispatched = executor.BeginDispatch(
            claimed.State,
            claim.AttemptId,
            claim.Fence,
            observedAtUtc);
        var failed = executor.RecordObservation(
            dispatched.State,
            claim.AttemptId,
            claim.Fence,
            new DurableOperationFailureObservation(new(
                DurableOperationFailurePhase.PreCall,
                DurableOperationEffectEvidence.NotExecuted,
                DurableOperationFailureDisposition.Retryable,
                "test.retryable")),
            observedAtUtc);
        Assert.Equal(DurableOperationStatus.RetryEligible, failed.State.Status);
        return failed.State;
    }

    static OperationContext Context(DateTimeOffset utcNow) =>
        OperationContext.Create(timeProvider: new FixedTimeProvider(utcNow));

    sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    sealed class StepAtReadTimeProvider(
        DateTimeOffset before,
        DateTimeOffset after,
        int readsBeforeStep) : TimeProvider
    {
        int reads;

        public override DateTimeOffset GetUtcNow() =>
            Interlocked.Increment(ref reads) <= readsBeforeStep ? before : after;
    }

    sealed class MutableTimeProvider(DateTimeOffset initialUtcNow) : TimeProvider
    {
        long utcTicks = initialUtcNow.UtcTicks;

        public override DateTimeOffset GetUtcNow() =>
            new(Interlocked.Read(ref utcTicks), TimeSpan.Zero);

        internal void SetUtcNow(DateTimeOffset utcNow) =>
            Interlocked.Exchange(ref utcTicks, utcNow.UtcTicks);
    }

    sealed class SingleAdapterResolver(IDurableOperationAdapter adapter)
        : IProcessDurableOperationAdapterResolver
    {
        public bool TryResolve(RequestEnvelope request, out IDurableOperationAdapter? resolved)
        {
            resolved = adapter;
            return true;
        }
    }

    sealed class RejectingAdapterResolver : IProcessDurableOperationAdapterResolver
    {
        internal int ResolutionCalls { get; private set; }

        public bool TryResolve(RequestEnvelope request, out IDurableOperationAdapter? adapter)
        {
            ResolutionCalls++;
            throw new InvalidOperationException("An acknowledged operation must not resolve or execute an adapter.");
        }
    }

    sealed class InspectingSuccessAdapter(
        IProcessDurableStore store,
        ProcessInstanceId instanceId,
        RequestContractReference request,
        DurableOperationAttemptObservation observation) : IDurableOperationAdapter
    {
        public DurableOperationAdapterCapabilities Capabilities { get; } = new(
            DurableOperationIdempotencyEvidence.TargetDeduplication,
            DurableOperationReconciliationCapability.Supported,
            [request]);

        internal bool SawOrigin { get; private set; }

        internal bool SawDispatchMarker { get; private set; }

        public async ValueTask<DurableOperationAttemptObservation> ExecuteAsync(
            OperationContext context,
            DurableOperationInvocation invocation)
        {
            var snapshot = await store.LoadAsync(context, instanceId);
            var checkpoint = Assert.IsType<ProcessDurableStoreSnapshot>(snapshot).Checkpoint;
            SawOrigin = checkpoint.Emissions.Any(entry =>
                entry.EmissionId == invocation.Request.Context.EmissionId
                && entry.Envelope == invocation.Request);
            SawDispatchMarker = checkpoint.DurableOperations.Any(state =>
                state.OperationId == invocation.Request.Context.EmissionId
                && state.CurrentAttempt?.Stage == DurableOperationAttemptStage.Dispatched);
            return observation;
        }

        public ValueTask<DurableOperationReconciliationObservation> ReconcileAsync(
            OperationContext context,
            DurableOperationReconciliationRequest request) =>
            throw new InvalidOperationException("Successful execution does not require reconciliation.");
    }

    sealed class AmbiguousThenNotExecutedAdapter(
        IProcessDurableStore store,
        ProcessInstanceId instanceId,
        RequestContractReference request) : IDurableOperationAdapter
    {
        public DurableOperationAdapterCapabilities Capabilities { get; } = new(
            DurableOperationIdempotencyEvidence.TargetDeduplication,
            DurableOperationReconciliationCapability.Supported,
            [request]);

        internal int ExecutionCalls { get; private set; }

        internal int ReconciliationCalls { get; private set; }

        internal bool SawReconciliationRequired { get; private set; }

        internal OperationAttemptId? ReconciledAttemptId { get; private set; }

        public ValueTask<DurableOperationAttemptObservation> ExecuteAsync(
            OperationContext context,
            DurableOperationInvocation invocation)
        {
            ExecutionCalls++;
            throw new ExternalOperationException("The external commit outcome is unknown.");
        }

        public async ValueTask<DurableOperationReconciliationObservation> ReconcileAsync(
            OperationContext context,
            DurableOperationReconciliationRequest request)
        {
            ReconciliationCalls++;
            var snapshot = await store.LoadAsync(context, instanceId);
            var operation = Assert.Single(
                Assert.IsType<ProcessDurableStoreSnapshot>(snapshot).Checkpoint.DurableOperations);
            SawReconciliationRequired = operation.Status == DurableOperationStatus.ReconciliationRequired;
            ReconciledAttemptId = operation.CurrentAttempt?.Claim.AttemptId;
            return new DurableOperationConfirmedNotExecuted();
        }
    }

    sealed class ProviderCancellationAdapter(
        RequestContractReference request,
        bool taskCanceledException,
        bool reconciliationCancellation) : IDurableOperationAdapter
    {
        public DurableOperationAdapterCapabilities Capabilities { get; } = new(
            DurableOperationIdempotencyEvidence.TargetDeduplication,
            reconciliationCancellation
                ? DurableOperationReconciliationCapability.Supported
                : DurableOperationReconciliationCapability.Unsupported,
            [request]);

        internal int ExecutionCalls { get; private set; }

        internal int ReconciliationCalls { get; private set; }

        public ValueTask<DurableOperationAttemptObservation> ExecuteAsync(
            OperationContext context,
            DurableOperationInvocation invocation)
        {
            ExecutionCalls++;
            return taskCanceledException
                ? ValueTask.FromException<DurableOperationAttemptObservation>(
                    new TaskCanceledException("The provider timed out independently."))
                : ValueTask.FromException<DurableOperationAttemptObservation>(
                    new OperationCanceledException("The provider cancelled independently."));
        }

        public ValueTask<DurableOperationReconciliationObservation> ReconcileAsync(
            OperationContext context,
            DurableOperationReconciliationRequest request)
        {
            ReconciliationCalls++;
            return taskCanceledException
                ? ValueTask.FromException<DurableOperationReconciliationObservation>(
                    new TaskCanceledException("The reconciliation provider timed out independently."))
                : ValueTask.FromException<DurableOperationReconciliationObservation>(
                    new OperationCanceledException("The reconciliation provider cancelled independently."));
        }
    }

    sealed class CallerCancellationAdapter(RequestContractReference request) : IDurableOperationAdapter
    {
        readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public DurableOperationAdapterCapabilities Capabilities { get; } = new(
            DurableOperationIdempotencyEvidence.TargetDeduplication,
            DurableOperationReconciliationCapability.Unsupported,
            [request]);

        internal Task Entered => entered.Task;

        public async ValueTask<DurableOperationAttemptObservation> ExecuteAsync(
            OperationContext context,
            DurableOperationInvocation invocation)
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, context.TimeProvider, context.CancellationToken);
            throw new InvalidOperationException("Caller cancellation must interrupt the adapter wait.");
        }

        public ValueTask<DurableOperationReconciliationObservation> ReconcileAsync(
            OperationContext context,
            DurableOperationReconciliationRequest request) =>
            throw new InvalidOperationException("Caller-cancellation coverage does not reconcile.");
    }

    sealed class ThrowingReconciliationAdapter(RequestContractReference request) : IDurableOperationAdapter
    {
        public DurableOperationAdapterCapabilities Capabilities { get; } = new(
            DurableOperationIdempotencyEvidence.TargetDeduplication,
            DurableOperationReconciliationCapability.Supported,
            [request]);

        internal int ReconciliationCalls { get; private set; }

        public ValueTask<DurableOperationAttemptObservation> ExecuteAsync(
            OperationContext context,
            DurableOperationInvocation invocation) =>
            throw new InvalidOperationException("Reconciliation coverage must not execute the operation again.");

        public ValueTask<DurableOperationReconciliationObservation> ReconcileAsync(
            OperationContext context,
            DurableOperationReconciliationRequest request)
        {
            ReconciliationCalls++;
            return ValueTask.FromException<DurableOperationReconciliationObservation>(
                new ExternalOperationException("The reconciliation provider failed without conclusive evidence."));
        }
    }

    sealed class CapabilitiesChangeAfterDispatchAdapter(RequestContractReference request)
        : IDurableOperationAdapter
    {
        int capabilityReads;

        public DurableOperationAdapterCapabilities Capabilities => ++capabilityReads == 1
            ? new(
                DurableOperationIdempotencyEvidence.TargetDeduplication,
                DurableOperationReconciliationCapability.Supported,
                [request])
            : new(
                DurableOperationIdempotencyEvidence.TargetDeduplication,
                DurableOperationReconciliationCapability.Supported,
                [new RequestContractReference(
                    DurableOperationTestFixture.DefinitionReference(
                        "interaction/request/not-supported-by-adapter",
                        'f'))]);

        internal int ExecutionCalls { get; private set; }

        public ValueTask<DurableOperationAttemptObservation> ExecuteAsync(
            OperationContext context,
            DurableOperationInvocation invocation)
        {
            ExecutionCalls++;
            return ValueTask.FromResult<DurableOperationAttemptObservation>(Success("unexpected"));
        }

        public ValueTask<DurableOperationReconciliationObservation> ReconcileAsync(
            OperationContext context,
            DurableOperationReconciliationRequest request) =>
            throw new InvalidOperationException("Capability preflight must prevent reconciliation.");
    }

    sealed class BlockingSuccessAdapter(RequestContractReference request)
        : IDurableOperationAdapter
    {
        readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        DurableOperationInvocation? invocation;
        int executionCalls;

        public DurableOperationAdapterCapabilities Capabilities { get; } = new(
            DurableOperationIdempotencyEvidence.TargetDeduplication,
            DurableOperationReconciliationCapability.Supported,
            [request]);

        internal Task Entered => entered.Task;

        internal int ExecutionCalls => executionCalls;

        internal DurableOperationInvocation? Invocation => Volatile.Read(ref invocation);

        internal void Complete() => completion.TrySetResult();

        public async ValueTask<DurableOperationAttemptObservation> ExecuteAsync(
            OperationContext context,
            DurableOperationInvocation invocation)
        {
            Interlocked.Increment(ref executionCalls);
            Volatile.Write(ref this.invocation, invocation);
            entered.TrySetResult();
            await completion.Task.WaitAsync(context.CancellationToken);
            return Success("single-flight");
        }

        public ValueTask<DurableOperationReconciliationObservation> ReconcileAsync(
            OperationContext context,
            DurableOperationReconciliationRequest request) =>
            throw new InvalidOperationException("Successful execution does not require reconciliation.");
    }

    sealed class BlockingReconciledOutcomeAdapter(RequestContractReference request)
        : IDurableOperationAdapter
    {
        readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int executionCalls;
        int reconciliationCalls;

        public DurableOperationAdapterCapabilities Capabilities { get; } = new(
            DurableOperationIdempotencyEvidence.TargetDeduplication,
            DurableOperationReconciliationCapability.Supported,
            [request]);

        internal Task Entered => entered.Task;

        internal int ExecutionCalls => executionCalls;

        internal int ReconciliationCalls => reconciliationCalls;

        internal void Complete() => completion.TrySetResult();

        public ValueTask<DurableOperationAttemptObservation> ExecuteAsync(
            OperationContext context,
            DurableOperationInvocation invocation)
        {
            Interlocked.Increment(ref executionCalls);
            throw new InvalidOperationException("A reconciliation-required operation must not execute again.");
        }

        public async ValueTask<DurableOperationReconciliationObservation> ReconcileAsync(
            OperationContext context,
            DurableOperationReconciliationRequest request)
        {
            Interlocked.Increment(ref reconciliationCalls);
            entered.TrySetResult();
            await completion.Task.WaitAsync(context.CancellationToken);
            return new DurableOperationReconciledOutcome(
                new RequestResultOutcome(
                    new("result"),
                    ProcessDurabilityTestFixture.StringValue("reconciled")));
        }
    }

    sealed class ReconciliationProbeAdapter(RequestContractReference request)
        : IDurableOperationAdapter
    {
        int executionCalls;
        int reconciliationCalls;

        public DurableOperationAdapterCapabilities Capabilities { get; } = new(
            DurableOperationIdempotencyEvidence.TargetDeduplication,
            DurableOperationReconciliationCapability.Supported,
            [request]);

        internal int ExecutionCalls => executionCalls;

        internal int ReconciliationCalls => reconciliationCalls;

        public ValueTask<DurableOperationAttemptObservation> ExecuteAsync(
            OperationContext context,
            DurableOperationInvocation invocation)
        {
            Interlocked.Increment(ref executionCalls);
            return ValueTask.FromResult<DurableOperationAttemptObservation>(Success("unexpected-execution"));
        }

        public ValueTask<DurableOperationReconciliationObservation> ReconcileAsync(
            OperationContext context,
            DurableOperationReconciliationRequest request)
        {
            Interlocked.Increment(ref reconciliationCalls);
            return ValueTask.FromResult<DurableOperationReconciliationObservation>(
                new DurableOperationUnresolved());
        }
    }

    sealed class ExternalOperationException(string message) : Exception(message);

    static string FormatDiagnostics(IEnumerable<DocumentValidationDiagnostic> diagnostics) =>
        string.Join(
            Environment.NewLine,
            diagnostics.Select(static diagnostic =>
                $"{diagnostic.Code}: {diagnostic.Message} ({diagnostic.Location})"));

    sealed class RejectingHost : IProcessReferenceHost
    {
        internal static RejectingHost Instance { get; } = new();

        public ProcessOperationResult InvokeTransition(ProcessTransitionInvocation invocation) =>
            throw new InvalidOperationException("Operation advancement must not invoke Process Transition hosts.");

        public ProcessOperationResult EvaluateRelation(ProcessRelationEvaluation evaluation) =>
            throw new InvalidOperationException("Operation advancement must not invoke Process Relation hosts.");

        public ProcessSignalTargetResult ResolveSignalTarget(ProcessSignalTargetResolution resolution) =>
            throw new InvalidOperationException("Operation advancement must not resolve Process Signal targets.");
    }
}
