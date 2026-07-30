using System.Text.Json;
using Cohesive.Execution;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class DurableOperationConformanceTests
{
    [Fact]
    public async Task EK06_AfterLocalCommitBeforeDispatch_RecoveryDispatchesThePersistedRequest()
    {
        var fixture = DurableOperationTestFixture.Create();
        var committed = fixture.CreateState();
        var adapter = new DurableOperationFakeAdapter(fixture.RequestContract)
            .Script(committed.OperationId, fixture.Success());

        Assert.Equal(DurableOperationStatus.Pending, committed.Status);
        Assert.Empty(adapter.Invocations);

        var recoveredExecutor = new DurableOperationReferenceExecutor(fixture.Catalog);
        var execution = await ExecuteOnceAsync(
            recoveredExecutor,
            committed,
            adapter,
            new("operation-attempt/recovered"),
            claimant: "worker/recovered",
            DurableOperationTestFixture.CreatedAtUtc.AddMinutes(1));
        var acknowledged = recoveredExecutor.RecordObservation(
            execution.DispatchedState,
            execution.Claim.AttemptId,
            execution.Claim.Fence,
            execution.Observation,
            DurableOperationTestFixture.CreatedAtUtc.AddMinutes(2));
        var admitted = recoveredExecutor.AdmitResult(
            acknowledged.State,
            new(committed.Request.ResponseTarget, DurableOperationResultArrival.Eligible));

        Assert.Single(adapter.Invocations);
        Assert.Equal(committed.Request, adapter.Invocations[0].Request);
        Assert.Equal(DurableOperationObservationDisposition.Acknowledged, acknowledged.Disposition);
        Assert.NotNull(acknowledged.State.Acknowledgement);
        Assert.Equal(DurableOperationAdmissionDisposition.Accepted, admitted.Admission?.Disposition);
        Assert.Equal(DurableOperationStatus.Dispositioned, admitted.State.Status);
    }

    [Fact]
    public async Task EK06_AfterExternalSuccessBeforeAcknowledgement_RecoveryMayRepeatPhysicalCallButNotLogicalConsequence()
    {
        var fixture = DurableOperationTestFixture.Create();
        var committed = fixture.CreateState();
        var adapter = new DurableOperationFakeAdapter(fixture.RequestContract)
            .Script(committed.OperationId, fixture.Success(), fixture.Success());
        var first = await ExecuteOnceAsync(
            fixture.Executor,
            committed,
            adapter,
            new("operation-attempt/1"),
            claimant: "worker/a",
            DurableOperationTestFixture.CreatedAtUtc);

        Assert.IsType<DurableOperationOutcomeObservation>(first.Observation);
        Assert.Null(first.DispatchedState.Acknowledgement);
        Assert.Single(adapter.Invocations);

        var recoveredExecutor = new DurableOperationReferenceExecutor(fixture.Catalog);
        var recoveredClaim = recoveredExecutor.Claim(
            first.DispatchedState,
            new("operation-attempt/2"),
            claimant: "worker/b",
            DurableOperationTestFixture.CreatedAtUtc.AddMinutes(5));
        var secondClaim = Assert.IsType<DurableOperationClaim>(recoveredClaim.Claim);
        var secondDispatch = recoveredExecutor.BeginDispatch(
            recoveredClaim.State,
            secondClaim.AttemptId,
            secondClaim.Fence,
            DurableOperationTestFixture.CreatedAtUtc.AddMinutes(5));
        var secondObservation = await DurableOperationReferenceExecutor.ExecuteAsync(
            DurableOperationTestFixture.ContextAt(DurableOperationTestFixture.CreatedAtUtc.AddMinutes(5)),
            Assert.IsType<DurableOperationInvocation>(secondDispatch.Invocation),
            adapter);
        var acknowledged = recoveredExecutor.RecordObservation(
            secondDispatch.State,
            secondClaim.AttemptId,
            secondClaim.Fence,
            secondObservation,
            DurableOperationTestFixture.CreatedAtUtc.AddMinutes(6));
        var admitted = recoveredExecutor.AdmitResult(
            acknowledged.State,
            new(committed.Request.ResponseTarget, DurableOperationResultArrival.Eligible));

        Assert.Equal(2, adapter.Invocations.Count);
        Assert.Equal(1, adapter.LogicalConsequenceCount);
        Assert.Equal(
            [committed.OperationId, committed.OperationId],
            adapter.Invocations.Select(static invocation => invocation.Request.Context.EmissionId));
        Assert.All(
            adapter.Invocations,
            invocation =>
            {
                Assert.Equal(committed.Request.Context.CorrelationId, invocation.Request.Context.CorrelationId);
                Assert.Equal(committed.Request.Context.IdempotencyKey, invocation.Request.Context.IdempotencyKey);
                Assert.Equal(committed.Request.Contract, invocation.Request.Contract);
                Assert.Equal(committed.Request.Payload, invocation.Request.Payload);
            });
        Assert.Equal(2, acknowledged.State.Attempts.Length);
        Assert.NotEqual(
            acknowledged.State.Attempts[0].Claim.AttemptId,
            acknowledged.State.Attempts[1].Claim.AttemptId);
        Assert.True(
            acknowledged.State.Attempts[0].Claim.Fence.Value
            < acknowledged.State.Attempts[1].Claim.Fence.Value);
        Assert.Equal(DurableOperationObservationDisposition.Acknowledged, acknowledged.Disposition);
        Assert.Equal(DurableOperationAdmissionDisposition.Accepted, admitted.Admission?.Disposition);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EK06_AfterAcknowledgementBeforeContinuationCheckpoint_RecoveryDoesNotRedispatch(
        bool transitionTarget)
    {
        var fixture = DurableOperationTestFixture.Create();
        InteractionTarget target = transitionTarget
            ? DurableOperationTestFixture.TransitionTarget()
            : DurableOperationTestFixture.ProcessTarget();
        var committed = fixture.CreateState(target: target);
        var adapter = new DurableOperationFakeAdapter(fixture.RequestContract)
            .Script(committed.OperationId, fixture.Success());
        var execution = await ExecuteOnceAsync(
            fixture.Executor,
            committed,
            adapter,
            new("operation-attempt/1"),
            claimant: "worker/a",
            DurableOperationTestFixture.CreatedAtUtc);
        var acknowledgementCommit = fixture.Executor.RecordObservation(
            execution.DispatchedState,
            execution.Claim.AttemptId,
            execution.Claim.Fence,
            execution.Observation,
            DurableOperationTestFixture.CreatedAtUtc.AddMinutes(2)).State;

        Assert.NotNull(acknowledgementCommit.Acknowledgement);
        Assert.Null(acknowledgementCommit.Admission);
        Assert.Single(adapter.Invocations);

        var recoveredExecutor = new DurableOperationReferenceExecutor(fixture.Catalog);
        var admitted = recoveredExecutor.AdmitResult(
            acknowledgementCommit,
            new(target, DurableOperationResultArrival.Eligible));
        var replay = recoveredExecutor.AdmitResult(
            admitted.State,
            new(target, DurableOperationResultArrival.Late));

        Assert.Single(adapter.Invocations);
        Assert.Equal(DurableOperationAdmissionDisposition.Accepted, admitted.Admission?.Disposition);
        Assert.True(admitted.Admission?.AdvancesTarget);
        Assert.Equal(DurableOperationAdmissionResultKind.Duplicate, replay.Kind);
        Assert.Equal(admitted.Admission, replay.Admission);
        Assert.Same(admitted.State, replay.State);
    }

    [Fact]
    public async Task AmbiguousAttempt_WithReconcileBeforeRetry_InvokesReconciliationInsteadOfBlindRedispatch()
    {
        var fixture = DurableOperationTestFixture.Create(
            retry: RequestRetrySemantics.ReconcileBeforeRetry,
            ambiguousOutcome: RequestResolutionSemantics.Reconcile,
            unresolvedOutcome: RequestResolutionSemantics.Reconcile,
            idempotencyEvidence: DurableOperationIdempotencyEvidence.None);
        var initial = fixture.CreateState();
        var ambiguous = fixture.Failure(
            DurableOperationFailurePhase.PostCommitPreAcknowledgement,
            DurableOperationEffectEvidence.Ambiguous);
        var adapter = new DurableOperationFakeAdapter(
                fixture.RequestContract,
                DurableOperationIdempotencyEvidence.None,
                DurableOperationReconciliationCapability.Supported)
            .Script(initial.OperationId, ambiguous)
            .ScriptReconciliation(new DurableOperationReconciledOutcome(
                new RequestResultOutcome(
                    new("result"),
                    DurableOperationTestFixture.StringValue("reconciled"))));
        var execution = await ExecuteOnceAsync(
            fixture.Executor,
            initial,
            adapter,
            new("operation-attempt/1"),
            claimant: "worker/a",
            DurableOperationTestFixture.CreatedAtUtc);
        var failed = fixture.Executor.RecordObservation(
            execution.DispatchedState,
            execution.Claim.AttemptId,
            execution.Claim.Fence,
            execution.Observation,
            DurableOperationTestFixture.CreatedAtUtc.AddMinutes(2));
        var blindRetry = fixture.Executor.Claim(
            failed.State,
            new("operation-attempt/2"),
            claimant: "worker/b",
            DurableOperationTestFixture.CreatedAtUtc.AddMinutes(3));
        var recoveryIntent = Assert.IsType<DurableOperationRecoveryIntent>(
            DurableOperationReferenceExecutor.GetRecoveryIntent(failed.State));
        var options = InteractionEnvelopeJsonSerializer.CreateOptions();
        var persisted = JsonSerializer.Serialize(failed.State, options);
        var restored = Assert.IsType<DurableOperationState>(
            JsonSerializer.Deserialize<DurableOperationState>(persisted, options));
        var restoredIntent = Assert.IsType<DurableOperationRecoveryIntent>(
            DurableOperationReferenceExecutor.GetRecoveryIntent(restored));

        var reconciliation = await DurableOperationReferenceExecutor.ReconcileAsync(
            DurableOperationTestFixture.ContextAt(DurableOperationTestFixture.CreatedAtUtc.AddMinutes(3)),
            restored,
            adapter);
        var stale = fixture.Executor.RecordReconciliation(
            restored,
            execution.Claim.AttemptId,
            new(execution.Claim.Fence.Value + 1),
            reconciliation,
            DurableOperationTestFixture.CreatedAtUtc.AddMinutes(3));
        var resolved = fixture.Executor.RecordReconciliation(
            restored,
            execution.Claim.AttemptId,
            execution.Claim.Fence,
            reconciliation,
            DurableOperationTestFixture.CreatedAtUtc.AddMinutes(3));
        var replay = fixture.Executor.RecordReconciliation(
            resolved.State,
            execution.Claim.AttemptId,
            execution.Claim.Fence,
            reconciliation,
            DurableOperationTestFixture.CreatedAtUtc.AddMinutes(4));
        var retroactiveReplay = Assert.Throws<ArgumentException>(() => fixture.Executor.RecordReconciliation(
            resolved.State,
            execution.Claim.AttemptId,
            execution.Claim.Fence,
            reconciliation,
            DurableOperationTestFixture.CreatedAtUtc.AddMinutes(2)));
        var staleAfterResolution = fixture.Executor.RecordReconciliation(
            resolved.State,
            execution.Claim.AttemptId,
            new(execution.Claim.Fence.Value + 1),
            reconciliation,
            DurableOperationTestFixture.CreatedAtUtc.AddMinutes(4));
        var lateAdapterResult = fixture.Executor.RecordObservation(
            resolved.State,
            execution.Claim.AttemptId,
            execution.Claim.Fence,
            fixture.Success("reconciled"),
            DurableOperationTestFixture.CreatedAtUtc.AddMinutes(4));
        var resolvedJson = JsonSerializer.Serialize(resolved.State, options);
        var restoredResolved = Assert.IsType<DurableOperationState>(
            JsonSerializer.Deserialize<DurableOperationState>(resolvedJson, options));

        Assert.Equal(DurableOperationObservationDisposition.ReconciliationRequired, failed.Disposition);
        Assert.Equal(DurableOperationStatus.ReconciliationRequired, failed.State.Status);
        Assert.Equal(DurableOperationClaimDisposition.RecoveryRequired, blindRetry.Disposition);
        Assert.Single(adapter.Invocations);
        Assert.Single(adapter.Reconciliations);
        Assert.Equal(initial.OperationId, adapter.Reconciliations[0].Request.Context.EmissionId);
        Assert.Equal(initial.DeduplicationKey, adapter.Reconciliations[0].DeduplicationKey);
        Assert.Equal(recoveryIntent, restoredIntent);
        Assert.Equal(recoveryIntent.Identity, adapter.Reconciliations[0].Identity);
        Assert.Equal(recoveryIntent, adapter.Reconciliations[0].Intent);
        Assert.Equal(fixture.Binding.ReconciliationTarget, recoveryIntent.Target);
        Assert.Equal(DurableOperationObservationDisposition.StaleFence, stale.Disposition);
        Assert.Equal(DurableOperationObservationDisposition.Acknowledged, resolved.Disposition);
        Assert.Equal("reconciled", resolved.State.Acknowledgement?.Outcome.Value.Value?.String);
        Assert.Equal(recoveryIntent.Identity, resolved.State.Acknowledgement?.RecoveryIdentity);
        Assert.Equal(DurableOperationObservationDisposition.Replayed, replay.Disposition);
        Assert.Same(resolved.State, replay.State);
        Assert.Equal("observedAtUtc", retroactiveReplay.ParamName);
        Assert.Equal(DurableOperationObservationDisposition.StaleFence, staleAfterResolution.Disposition);
        Assert.Equal(DurableOperationObservationDisposition.LateResult, lateAdapterResult.Disposition);
        var evidence = Assert.Single(restoredResolved.Reconciliations);
        Assert.Equal(execution.Claim.AttemptId, evidence.AttemptId);
        Assert.Equal(execution.Claim.Fence, evidence.Fence);
        Assert.Equal(reconciliation, evidence.Observation);
        Assert.Equal(DurableOperationAttemptStage.Resolved, restoredResolved.CurrentAttempt?.Stage);
        Assert.Equal(ambiguous.Failure, restoredResolved.CurrentAttempt?.Failure);
        Assert.Equal(failed.State.CurrentAttempt?.CompletedAtUtc, restoredResolved.CurrentAttempt?.CompletedAtUtc);
    }

    [Fact]
    public async Task ExhaustedNonAmbiguousFailure_CanFollowItsAuthoredUnresolvedReconciliationPath()
    {
        var fixture = DurableOperationTestFixture.Create(
            retry: RequestRetrySemantics.Never,
            ambiguousOutcome: RequestResolutionSemantics.Escalate,
            unresolvedOutcome: RequestResolutionSemantics.Reconcile,
            idempotencyEvidence: DurableOperationIdempotencyEvidence.None);
        var initial = fixture.CreateState("emission/request/reconcile-unresolved");
        var adapter = new DurableOperationFakeAdapter(
                fixture.RequestContract,
                DurableOperationIdempotencyEvidence.None,
                DurableOperationReconciliationCapability.Supported)
            .Script(
                initial.OperationId,
                fixture.Failure(
                    DurableOperationFailurePhase.PreCall,
                    DurableOperationEffectEvidence.NotExecuted))
            .ScriptReconciliation(new DurableOperationReconciledOutcome(fixture.Success("recovered").Outcome));
        var execution = await ExecuteOnceAsync(
            fixture.Executor,
            initial,
            adapter,
            new("operation-attempt/reconcile-unresolved"),
            claimant: "worker/a",
            DurableOperationTestFixture.CreatedAtUtc);
        var failed = fixture.Executor.RecordObservation(
            execution.DispatchedState,
            execution.Claim.AttemptId,
            execution.Claim.Fence,
            execution.Observation,
            DurableOperationTestFixture.CreatedAtUtc.AddMinutes(1));

        var observation = await DurableOperationReferenceExecutor.ReconcileAsync(
            DurableOperationTestFixture.ContextAt(DurableOperationTestFixture.CreatedAtUtc.AddMinutes(2)),
            failed.State,
            adapter);
        var resolved = fixture.Executor.RecordReconciliation(
            failed.State,
            execution.Claim.AttemptId,
            execution.Claim.Fence,
            observation,
            DurableOperationTestFixture.CreatedAtUtc.AddMinutes(2));

        Assert.Equal(DurableOperationObservationDisposition.ReconciliationRequired, failed.Disposition);
        Assert.Equal(DurableOperationEffectEvidence.NotExecuted, failed.State.CurrentAttempt?.Failure?.EffectEvidence);
        Assert.Equal(DurableOperationObservationDisposition.Acknowledged, resolved.Disposition);
        Assert.Equal("recovered", resolved.State.Acknowledgement?.Outcome.Value.Value?.String);
        Assert.Equal(
            DurableOperationRecoveryRequirement.Reconcile,
            resolved.State.Acknowledgement?.RecoveryIdentity?.Requirement);
    }

    [Fact]
    public void SemanticTimeout_WinsWhilePendingOrClaimedBeforeDispatch()
    {
        var fixture = DurableOperationTestFixture.Create(timeoutAfter: TimeSpan.FromMinutes(2));
        var pending = fixture.CreateState("emission/request/timeout-pending");
        var beforeDeadline = fixture.Executor.ResolveTimeout(
            pending,
            fixture.Timeout(),
            DurableOperationTestFixture.CreatedAtUtc.AddMinutes(1));
        var blockedClaim = fixture.Executor.Claim(
            pending,
            new("operation-attempt/deadline-elapsed"),
            claimant: "worker/a",
            DurableOperationTestFixture.CreatedAtUtc.AddMinutes(2));
        var pendingTimeout = fixture.Executor.ResolveTimeout(
            blockedClaim.State,
            fixture.Timeout(),
            DurableOperationTestFixture.CreatedAtUtc.AddMinutes(2));

        var claimed = fixture.Executor.Claim(
            fixture.CreateState("emission/request/timeout-claimed"),
            new("operation-attempt/claimed"),
            claimant: "worker/a",
            DurableOperationTestFixture.CreatedAtUtc);
        var claimedTimeout = fixture.Executor.ResolveTimeout(
            claimed.State,
            fixture.Timeout(),
            DurableOperationTestFixture.CreatedAtUtc.AddMinutes(2));

        Assert.Equal(DurableOperationObservationDisposition.InvalidEvidence, beforeDeadline.Disposition);
        Assert.Same(pending, beforeDeadline.State);
        Assert.Equal(DurableOperationClaimDisposition.DeadlineElapsed, blockedClaim.Disposition);
        Assert.Same(pending, blockedClaim.State);
        Assert.Equal(DurableOperationObservationDisposition.Acknowledged, pendingTimeout.Disposition);
        Assert.Null(pendingTimeout.State.Acknowledgement?.AttemptId);
        Assert.Equal(fixture.TimeoutReplyContract, pendingTimeout.State.Acknowledgement?.ReplyContract);
        Assert.Empty(pendingTimeout.State.Attempts);
        Assert.Equal(DurableOperationObservationDisposition.Acknowledged, claimedTimeout.Disposition);
        Assert.Null(claimedTimeout.State.Acknowledgement?.AttemptId);
        Assert.Equal(DurableOperationAttemptStage.Failed, claimedTimeout.State.CurrentAttempt?.Stage);
        Assert.Equal(DurableOperationFailurePhase.PreCall, claimedTimeout.State.CurrentAttempt?.Failure?.Phase);
        Assert.Equal(
            DurableOperationEffectEvidence.NotExecuted,
            claimedTimeout.State.CurrentAttempt?.Failure?.EffectEvidence);
        Assert.Equal(
            DurableOperationFailureCodes.TimedOutBeforeDispatch,
            claimedTimeout.State.CurrentAttempt?.Failure?.Code);
    }

    [Fact]
    public void SemanticTimeout_WinsInFlightAsAmbiguousAndMakesAnAdapterResultLate()
    {
        var fixture = DurableOperationTestFixture.Create(timeoutAfter: TimeSpan.FromMinutes(2));
        var claimed = fixture.Executor.Claim(
            fixture.CreateState("emission/request/timeout-in-flight"),
            new("operation-attempt/in-flight"),
            claimant: "worker/a",
            DurableOperationTestFixture.CreatedAtUtc);
        var claim = Assert.IsType<DurableOperationClaim>(claimed.Claim);
        var dispatched = fixture.Executor.BeginDispatch(
            claimed.State,
            claim.AttemptId,
            claim.Fence,
            DurableOperationTestFixture.CreatedAtUtc.AddMinutes(1));

        var resultAtDeadline = fixture.Executor.RecordObservation(
            dispatched.State,
            claim.AttemptId,
            claim.Fence,
            fixture.Success("arrived-at-deadline"),
            DurableOperationTestFixture.CreatedAtUtc.AddMinutes(2));
        var timedOut = fixture.Executor.ResolveTimeout(
            dispatched.State,
            fixture.Timeout(),
            DurableOperationTestFixture.CreatedAtUtc.AddMinutes(2));
        var lateResult = fixture.Executor.RecordObservation(
            timedOut.State,
            claim.AttemptId,
            claim.Fence,
            fixture.Success("arrived-after-timeout"),
            DurableOperationTestFixture.CreatedAtUtc.AddMinutes(3));

        Assert.Equal(DurableOperationObservationDisposition.DeadlineElapsed, resultAtDeadline.Disposition);
        Assert.Same(dispatched.State, resultAtDeadline.State);
        Assert.Null(resultAtDeadline.State.Acknowledgement);
        Assert.Equal(DurableOperationObservationDisposition.Acknowledged, timedOut.Disposition);
        Assert.Null(timedOut.State.Acknowledgement?.AttemptId);
        Assert.Equal(DurableOperationAttemptStage.Failed, timedOut.State.CurrentAttempt?.Stage);
        Assert.Equal(DurableOperationFailurePhase.InCall, timedOut.State.CurrentAttempt?.Failure?.Phase);
        Assert.Equal(
            DurableOperationEffectEvidence.Ambiguous,
            timedOut.State.CurrentAttempt?.Failure?.EffectEvidence);
        Assert.Equal(
            DurableOperationFailureCodes.TimedOutInFlight,
            timedOut.State.CurrentAttempt?.Failure?.Code);
        Assert.Equal(DurableOperationObservationDisposition.LateResult, lateResult.Disposition);
        Assert.Same(timedOut.State, lateResult.State);
        Assert.IsType<RequestTimeoutOutcome>(lateResult.State.Acknowledgement?.Outcome);
    }

    [Fact]
    public void ElapsedDeadline_PreemptsSemanticCancellationUntilTypedTimeoutIsPersisted()
    {
        var fixture = DurableOperationTestFixture.Create(
            timeoutAfter: TimeSpan.FromMinutes(2),
            supportsCancellation: true);
        var pending = fixture.CreateState("emission/request/timeout-before-cancel");

        var cancellation = fixture.Executor.ResolveCancellation(
            pending,
            fixture.Cancellation("too-late"),
            DurableOperationTestFixture.CreatedAtUtc.AddMinutes(2));
        var timeout = fixture.Executor.ResolveTimeout(
            cancellation.State,
            fixture.Timeout(),
            DurableOperationTestFixture.CreatedAtUtc.AddMinutes(2));

        Assert.Equal(DurableOperationObservationDisposition.DeadlineElapsed, cancellation.Disposition);
        Assert.Null(cancellation.State.Acknowledgement);
        Assert.Equal(DurableOperationObservationDisposition.Acknowledged, timeout.Disposition);
        Assert.IsType<RequestTimeoutOutcome>(timeout.State.Acknowledgement?.Outcome);
    }

    [Fact]
    public void SemanticCancellation_WinsPendingPreDispatchAndInFlightWithTypedEvidence()
    {
        var fixture = DurableOperationTestFixture.Create(supportsCancellation: true);
        var pending = fixture.Executor.ResolveCancellation(
            fixture.CreateState("emission/request/cancel-pending"),
            fixture.Cancellation("pending"),
            DurableOperationTestFixture.CreatedAtUtc);

        var claimed = fixture.Executor.Claim(
            fixture.CreateState("emission/request/cancel-claimed"),
            new("operation-attempt/cancel-claimed"),
            claimant: "worker/a",
            DurableOperationTestFixture.CreatedAtUtc);
        var beforeDispatch = fixture.Executor.ResolveCancellation(
            claimed.State,
            fixture.Cancellation("before-dispatch"),
            DurableOperationTestFixture.CreatedAtUtc.AddMinutes(1));

        var inFlightClaim = fixture.Executor.Claim(
            fixture.CreateState("emission/request/cancel-in-flight"),
            new("operation-attempt/cancel-in-flight"),
            claimant: "worker/a",
            DurableOperationTestFixture.CreatedAtUtc);
        var inFlightLease = Assert.IsType<DurableOperationClaim>(inFlightClaim.Claim);
        var dispatched = fixture.Executor.BeginDispatch(
            inFlightClaim.State,
            inFlightLease.AttemptId,
            inFlightLease.Fence,
            DurableOperationTestFixture.CreatedAtUtc.AddMinutes(1));
        var inFlight = fixture.Executor.ResolveCancellation(
            dispatched.State,
            fixture.Cancellation("in-flight"),
            DurableOperationTestFixture.CreatedAtUtc.AddMinutes(2));
        var late = fixture.Executor.RecordObservation(
            inFlight.State,
            inFlightLease.AttemptId,
            inFlightLease.Fence,
            fixture.Success("late-after-cancel"),
            DurableOperationTestFixture.CreatedAtUtc.AddMinutes(3));

        Assert.Equal(DurableOperationObservationDisposition.Acknowledged, pending.Disposition);
        Assert.Null(pending.State.Acknowledgement?.AttemptId);
        Assert.Equal(fixture.CancellationReplyContract, pending.State.Acknowledgement?.ReplyContract);
        Assert.Equal(
            DurableOperationFailureCodes.CancelledBeforeDispatch,
            beforeDispatch.State.CurrentAttempt?.Failure?.Code);
        Assert.Equal(
            DurableOperationEffectEvidence.NotExecuted,
            beforeDispatch.State.CurrentAttempt?.Failure?.EffectEvidence);
        Assert.Equal(
            DurableOperationFailureCodes.CancelledInFlight,
            inFlight.State.CurrentAttempt?.Failure?.Code);
        Assert.Equal(
            DurableOperationEffectEvidence.Ambiguous,
            inFlight.State.CurrentAttempt?.Failure?.EffectEvidence);
        Assert.Null(inFlight.State.Acknowledgement?.AttemptId);
        Assert.Equal(DurableOperationObservationDisposition.LateResult, late.Disposition);
        Assert.IsType<RequestCancellationOutcome>(late.State.Acknowledgement?.Outcome);
    }

    [Fact]
    public async Task HostCancellation_InterruptsDispatchWithoutCreatingSemanticCancellationEvidence()
    {
        var fixture = DurableOperationTestFixture.Create(supportsCancellation: true);
        var initial = fixture.CreateState("emission/request/host-cancelled");
        var claimed = fixture.Executor.Claim(
            initial,
            new("operation-attempt/host-cancelled"),
            claimant: "worker/a",
            DurableOperationTestFixture.CreatedAtUtc);
        var claim = Assert.IsType<DurableOperationClaim>(claimed.Claim);
        var dispatched = fixture.Executor.BeginDispatch(
            claimed.State,
            claim.AttemptId,
            claim.Fence,
            DurableOperationTestFixture.CreatedAtUtc.AddMinutes(1));
        var invocation = Assert.IsType<DurableOperationInvocation>(dispatched.Invocation);
        var adapter = new DurableOperationFakeAdapter(fixture.RequestContract)
            .Script(initial.OperationId, fixture.Success());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var context = DurableOperationTestFixture
            .ContextAt(DurableOperationTestFixture.CreatedAtUtc.AddMinutes(1))
            .WithCancellationToken(cancellation.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await DurableOperationReferenceExecutor.ExecuteAsync(context, invocation, adapter));

        Assert.Empty(adapter.Invocations);
        Assert.Equal(DurableOperationStatus.Dispatched, dispatched.State.Status);
        Assert.Null(dispatched.State.Acknowledgement);
        Assert.IsNotType<RequestCancellationOutcome>(dispatched.State.Acknowledgement?.Outcome);
    }

    [Fact]
    public void Escalation_UsesStableFencedIntentAndPersistsItsTypedOutcome()
    {
        var fixture = DurableOperationTestFixture.Create(
            retry: RequestRetrySemantics.Never,
            ambiguousOutcome: RequestResolutionSemantics.Escalate,
            unresolvedOutcome: RequestResolutionSemantics.Escalate,
            idempotencyEvidence: DurableOperationIdempotencyEvidence.None);
        var claimed = fixture.Executor.Claim(
            fixture.CreateState("emission/request/escalate"),
            new("operation-attempt/escalate"),
            claimant: "worker/a",
            DurableOperationTestFixture.CreatedAtUtc);
        var claim = Assert.IsType<DurableOperationClaim>(claimed.Claim);
        var dispatched = fixture.Executor.BeginDispatch(
            claimed.State,
            claim.AttemptId,
            claim.Fence,
            DurableOperationTestFixture.CreatedAtUtc.AddMinutes(1));
        var failed = fixture.Executor.RecordObservation(
            dispatched.State,
            claim.AttemptId,
            claim.Fence,
            fixture.Failure(
                DurableOperationFailurePhase.PostCommitPreAcknowledgement,
                DurableOperationEffectEvidence.Ambiguous),
            DurableOperationTestFixture.CreatedAtUtc.AddMinutes(2));
        var intent = Assert.IsType<DurableOperationRecoveryIntent>(
            DurableOperationReferenceExecutor.GetRecoveryIntent(failed.State));
        var options = InteractionEnvelopeJsonSerializer.CreateOptions();
        var restored = Assert.IsType<DurableOperationState>(
            JsonSerializer.Deserialize<DurableOperationState>(
                JsonSerializer.Serialize(failed.State, options),
                options));
        var restoredIntent = Assert.IsType<DurableOperationRecoveryIntent>(
            DurableOperationReferenceExecutor.GetRecoveryIntent(restored));
        var staleIdentity = new DurableOperationRecoveryIdentity(
            restored.OperationId,
            claim.AttemptId,
            new(claim.Fence.Value + 1),
            DurableOperationRecoveryRequirement.Escalate);
        var outcome = new RequestFailureOutcome(
            new("failure"),
            DurableOperationTestFixture.StringValue("operator-rejected"));

        var stale = fixture.Executor.ResolveEscalation(
            restored,
            staleIdentity,
            outcome,
            evidence: null,
            replyOrigin: null,
            DurableOperationTestFixture.CreatedAtUtc.AddMinutes(3));
        var resolved = fixture.Executor.ResolveEscalation(
            restored,
            restoredIntent.Identity,
            outcome,
            DurableOperationTestFixture.StringValue("ticket/42"),
            replyOrigin: null,
            DurableOperationTestFixture.CreatedAtUtc.AddMinutes(3));
        var replay = fixture.Executor.ResolveEscalation(
            resolved.State,
            restoredIntent.Identity,
            outcome,
            DurableOperationTestFixture.StringValue("ticket/42"),
            replyOrigin: null,
            DurableOperationTestFixture.CreatedAtUtc.AddMinutes(4));

        Assert.Equal(DurableOperationObservationDisposition.EscalationRequired, failed.Disposition);
        Assert.Equal(DurableOperationStatus.EscalationRequired, failed.State.Status);
        Assert.Equal(intent, restoredIntent);
        Assert.Equal(DurableOperationRecoveryRequirement.Escalate, intent.Identity.Requirement);
        Assert.Equal(claim.AttemptId, intent.Identity.SourceAttemptId);
        Assert.Equal(claim.Fence, intent.Identity.SourceFence);
        Assert.Equal(fixture.Binding.EscalationTarget, intent.Target);
        Assert.Equal(restored.Request, intent.Request);
        Assert.Equal(restored.DeduplicationKey, intent.DeduplicationKey);
        Assert.Equal(DurableOperationObservationDisposition.StaleFence, stale.Disposition);
        Assert.Equal(DurableOperationObservationDisposition.Acknowledged, resolved.Disposition);
        Assert.Equal(DurableOperationAttemptStage.Resolved, resolved.State.CurrentAttempt?.Stage);
        Assert.Equal("operator-rejected", resolved.State.Acknowledgement?.Outcome.Value.Value?.String);
        Assert.Equal("ticket/42", resolved.State.Acknowledgement?.AdapterEvidence?.Value?.String);
        Assert.Equal(restoredIntent.Identity, resolved.State.Acknowledgement?.RecoveryIdentity);
        Assert.Equal(DurableOperationObservationDisposition.Replayed, replay.Disposition);
        Assert.Same(resolved.State, replay.State);
    }

    [Fact]
    public void ChildTerminalOutcomeRecovery_RequiresAndPersistsExactReplyOrigin()
    {
        var fixture = DurableOperationTestFixture.Create(
            retry: RequestRetrySemantics.Never,
            ambiguousOutcome: RequestResolutionSemantics.TerminalFailure,
            unresolvedOutcome: RequestResolutionSemantics.TerminalFailure,
            idempotencyEvidence: DurableOperationIdempotencyEvidence.None,
            supportsChildOutcomes: true);
        var (initial, replyOrigin) = fixture.CreateChildState("emission/request/child-terminal");
        var failed = FailAmbiguously(fixture, initial, "child-terminal");
        var outcome = new RequestFailureOutcome(
            new("failure"),
            DurableOperationTestFixture.StringValue("child-failed"));

        var missingOrigin = fixture.Executor.ResolveTerminalOutcome(
            failed.State,
            outcome,
            replyOrigin: null,
            DurableOperationTestFixture.CreatedAtUtc.AddMinutes(3));
        var resolved = fixture.Executor.ResolveTerminalOutcome(
            failed.State,
            outcome,
            replyOrigin,
            DurableOperationTestFixture.CreatedAtUtc.AddMinutes(3));
        var options = InteractionEnvelopeJsonSerializer.CreateOptions();
        var restored = Assert.IsType<DurableOperationState>(
            JsonSerializer.Deserialize<DurableOperationState>(
                JsonSerializer.Serialize(resolved.State, options),
                options));
        var reply = restored.CreateReply(
            new("emission/reply/child-terminal"),
            new("idempotency/reply/child-terminal"),
            ordering: null,
            restored.Request.Context.Provenance);
        var conflictingOrigin = new ProcessInteractionOrigin(
            replyOrigin.Definition,
            replyOrigin.Node,
            replyOrigin.Continuation,
            new("activation/other-terminal"),
            replyOrigin.Token);
        var conflictingReplay = fixture.Executor.ResolveTerminalOutcome(
            restored,
            outcome,
            conflictingOrigin,
            DurableOperationTestFixture.CreatedAtUtc.AddMinutes(4));

        Assert.Equal(DurableOperationObservationDisposition.TerminalOutcomeRequired, failed.Disposition);
        Assert.Equal(DurableOperationStatus.TerminalOutcomeRequired, failed.State.Status);
        Assert.Equal(DurableOperationObservationDisposition.InvalidEvidence, missingOrigin.Disposition);
        Assert.Equal(DurableOperationObservationDisposition.Acknowledged, resolved.Disposition);
        Assert.Equal(replyOrigin, restored.Acknowledgement?.ReplyOrigin);
        Assert.Equal(replyOrigin, reply.Context.Origin);
        Assert.Equal(DurableOperationObservationDisposition.ConflictingOutcome, conflictingReplay.Disposition);
    }

    [Fact]
    public void ChildEscalationRecovery_RequiresExactReplyOriginForResolutionAndReplay()
    {
        var fixture = DurableOperationTestFixture.Create(
            retry: RequestRetrySemantics.Never,
            ambiguousOutcome: RequestResolutionSemantics.Escalate,
            unresolvedOutcome: RequestResolutionSemantics.Escalate,
            idempotencyEvidence: DurableOperationIdempotencyEvidence.None,
            supportsChildOutcomes: true);
        var (initial, replyOrigin) = fixture.CreateChildState("emission/request/child-escalation");
        var failed = FailAmbiguously(fixture, initial, "child-escalation");
        var intent = Assert.IsType<DurableOperationRecoveryIntent>(
            DurableOperationReferenceExecutor.GetRecoveryIntent(failed.State));
        var outcome = new RequestFailureOutcome(
            new("failure"),
            DurableOperationTestFixture.StringValue("operator-rejected-child"));
        var evidence = DurableOperationTestFixture.StringValue("ticket/child-42");

        var missingOrigin = fixture.Executor.ResolveEscalation(
            failed.State,
            intent.Identity,
            outcome,
            evidence,
            replyOrigin: null,
            DurableOperationTestFixture.CreatedAtUtc.AddMinutes(3));
        var resolved = fixture.Executor.ResolveEscalation(
            failed.State,
            intent.Identity,
            outcome,
            evidence,
            replyOrigin,
            DurableOperationTestFixture.CreatedAtUtc.AddMinutes(3));
        var conflictingOrigin = new ProcessInteractionOrigin(
            replyOrigin.Definition,
            replyOrigin.Node,
            replyOrigin.Continuation,
            new("activation/other-escalation"),
            replyOrigin.Token);
        var conflictingReplay = fixture.Executor.ResolveEscalation(
            resolved.State,
            intent.Identity,
            outcome,
            evidence,
            conflictingOrigin,
            DurableOperationTestFixture.CreatedAtUtc.AddMinutes(4));
        var replay = fixture.Executor.ResolveEscalation(
            resolved.State,
            intent.Identity,
            outcome,
            evidence,
            replyOrigin,
            DurableOperationTestFixture.CreatedAtUtc.AddMinutes(4));

        Assert.Equal(DurableOperationObservationDisposition.EscalationRequired, failed.Disposition);
        Assert.Equal(DurableOperationObservationDisposition.InvalidEvidence, missingOrigin.Disposition);
        Assert.Equal(DurableOperationObservationDisposition.Acknowledged, resolved.Disposition);
        Assert.Equal(replyOrigin, resolved.State.Acknowledgement?.ReplyOrigin);
        Assert.Equal(DurableOperationObservationDisposition.ConflictingOutcome, conflictingReplay.Disposition);
        Assert.Equal(DurableOperationObservationDisposition.Replayed, replay.Disposition);
        Assert.Same(resolved.State, replay.State);
    }

    [Theory]
    [InlineData(DurableOperationFailurePhase.PreCall, DurableOperationEffectEvidence.NotExecuted)]
    [InlineData(DurableOperationFailurePhase.InCall, DurableOperationEffectEvidence.Ambiguous)]
    [InlineData(DurableOperationFailurePhase.PostCallPreCommit, DurableOperationEffectEvidence.NotCommitted)]
    [InlineData(DurableOperationFailurePhase.PostCommitPreAcknowledgement, DurableOperationEffectEvidence.Ambiguous)]
    public void FailurePhaseEvidence_IsRetainedAndProducesAnExplicitRecoveryDecision(
        DurableOperationFailurePhase phase,
        DurableOperationEffectEvidence evidence)
    {
        var fixture = DurableOperationTestFixture.Create();
        var initial = fixture.CreateState();
        var claimResult = fixture.Executor.Claim(
            initial,
            new("operation-attempt/1"),
            claimant: "worker/a",
            DurableOperationTestFixture.CreatedAtUtc);
        var claim = Assert.IsType<DurableOperationClaim>(claimResult.Claim);
        var dispatch = fixture.Executor.BeginDispatch(
            claimResult.State,
            claim.AttemptId,
            claim.Fence,
            DurableOperationTestFixture.CreatedAtUtc.AddMinutes(1));
        var observation = fixture.Executor.RecordObservation(
            dispatch.State,
            claim.AttemptId,
            claim.Fence,
            fixture.Failure(phase, evidence),
            DurableOperationTestFixture.CreatedAtUtc.AddMinutes(2));

        Assert.Equal(DurableOperationObservationDisposition.RetryEligible, observation.Disposition);
        Assert.Equal(DurableOperationRecoveryRequirement.Retry, observation.State.RecoveryRequirement);
        Assert.Equal(phase, observation.State.CurrentAttempt?.Failure?.Phase);
        Assert.Equal(evidence, observation.State.CurrentAttempt?.Failure?.EffectEvidence);
        Assert.Equal(DurableOperationAttemptStage.Failed, observation.State.CurrentAttempt?.Stage);
    }

    [Fact]
    public async Task PartialPhysicalBatch_RetriesOnlyTheUnacknowledgedRequestOperation()
    {
        var fixture = DurableOperationTestFixture.Create();
        DurableOperationState[] initial =
        [
            fixture.CreateState("emission/request/a"),
            fixture.CreateState("emission/request/b"),
            fixture.CreateState("emission/request/c")
        ];
        var adapter = new DurableOperationFakeAdapter(fixture.RequestContract)
            .Script(initial[0].OperationId, fixture.Success("a"))
            .Script(
                initial[1].OperationId,
                fixture.Failure(
                    DurableOperationFailurePhase.PreCall,
                    DurableOperationEffectEvidence.NotExecuted),
                fixture.Success("b"))
            .Script(initial[2].OperationId, fixture.Success("c"));

        DurableOperationState[] dispatchedStates = new DurableOperationState[initial.Length];
        DurableOperationInvocation[] invocations = new DurableOperationInvocation[initial.Length];
        for (var index = 0; index < initial.Length; index++)
        {
            var claimed = fixture.Executor.Claim(
                initial[index],
                new($"operation-attempt/{(char)('a' + index)}/1"),
                claimant: "worker/batch-1",
                DurableOperationTestFixture.CreatedAtUtc);
            var claim = Assert.IsType<DurableOperationClaim>(claimed.Claim);
            var dispatched = fixture.Executor.BeginDispatch(
                claimed.State,
                claim.AttemptId,
                claim.Fence,
                DurableOperationTestFixture.CreatedAtUtc);
            dispatchedStates[index] = dispatched.State;
            invocations[index] = Assert.IsType<DurableOperationInvocation>(dispatched.Invocation);
        }

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await DurableOperationReferenceExecutor.ExecuteBatchAsync(
                DurableOperationTestFixture.ContextAt(DurableOperationTestFixture.CreatedAtUtc),
                [invocations[0], invocations[0]],
                adapter));

        var firstBatch = await DurableOperationReferenceExecutor.ExecuteBatchAsync(
            DurableOperationTestFixture.ContextAt(DurableOperationTestFixture.CreatedAtUtc),
            [.. invocations],
            adapter);

        DurableOperationState[] afterFirstBatch = new DurableOperationState[firstBatch.Length];
        for (var index = 0; index < firstBatch.Length; index++)
        {
            var evidence = firstBatch[index];
            afterFirstBatch[index] = fixture.Executor.RecordObservation(
                dispatchedStates[index],
                evidence.AttemptId,
                evidence.Fence,
                evidence.Observation,
                DurableOperationTestFixture.CreatedAtUtc.AddMinutes(2)).State;
        }

        Assert.Equal(DurableOperationStatus.Acknowledged, afterFirstBatch[0].Status);
        Assert.Equal(DurableOperationStatus.RetryEligible, afterFirstBatch[1].Status);
        Assert.Equal(DurableOperationStatus.Acknowledged, afterFirstBatch[2].Status);

        var retryClaim = fixture.Executor.Claim(
            afterFirstBatch[1],
            new("operation-attempt/b/2"),
            claimant: "worker/batch-2",
            DurableOperationTestFixture.CreatedAtUtc.AddMinutes(3));
        var retryLease = Assert.IsType<DurableOperationClaim>(retryClaim.Claim);
        var retryDispatch = fixture.Executor.BeginDispatch(
            retryClaim.State,
            retryLease.AttemptId,
            retryLease.Fence,
            DurableOperationTestFixture.CreatedAtUtc.AddMinutes(3));
        var retryInvocation = Assert.IsType<DurableOperationInvocation>(retryDispatch.Invocation);
        var retryBatch = await DurableOperationReferenceExecutor.ExecuteBatchAsync(
            DurableOperationTestFixture.ContextAt(DurableOperationTestFixture.CreatedAtUtc.AddMinutes(3)),
            [retryInvocation],
            adapter);
        var retryEvidence = Assert.Single(retryBatch);
        var completedB = fixture.Executor.RecordObservation(
            retryDispatch.State,
            retryEvidence.AttemptId,
            retryEvidence.Fence,
            retryEvidence.Observation,
            DurableOperationTestFixture.CreatedAtUtc.AddMinutes(4)).State;

        Assert.Equal(2, adapter.Batches.Count);
        Assert.Equal(3, adapter.Batches[0].Length);
        Assert.Single(adapter.Batches[1]);
        Assert.Equal("emission/request/b", adapter.Batches[1][0].Request.Context.EmissionId.Value);
        Assert.Equal(
            [
                "emission/request/a",
                "emission/request/b",
                "emission/request/c",
                "emission/request/b"
            ],
            adapter.Invocations.Select(static invocation => invocation.Request.Context.EmissionId.Value));
        Assert.Single(afterFirstBatch[0].Attempts);
        Assert.Single(afterFirstBatch[2].Attempts);
        Assert.Equal(2, completedB.Attempts.Length);
        Assert.Equal(DurableOperationStatus.Acknowledged, completedB.Status);
        Assert.Equal(3, adapter.LogicalConsequenceCount);
    }

    static async Task<AttemptExecution> ExecuteOnceAsync(
        DurableOperationReferenceExecutor executor,
        DurableOperationState state,
        DurableOperationFakeAdapter adapter,
        OperationAttemptId attemptId,
        string claimant,
        DateTimeOffset observedAtUtc)
    {
        var claimResult = executor.Claim(state, attemptId, claimant, observedAtUtc);
        Assert.Equal(DurableOperationClaimDisposition.Claimed, claimResult.Disposition);
        var claim = Assert.IsType<DurableOperationClaim>(claimResult.Claim);
        var dispatch = executor.BeginDispatch(
            claimResult.State,
            claim.AttemptId,
            claim.Fence,
            observedAtUtc);
        Assert.Equal(DurableOperationDispatchDisposition.Dispatched, dispatch.Disposition);
        var invocation = Assert.IsType<DurableOperationInvocation>(dispatch.Invocation);
        var observation = await DurableOperationReferenceExecutor.ExecuteAsync(
            DurableOperationTestFixture.ContextAt(observedAtUtc),
            invocation,
            adapter);
        return new(dispatch.State, claim, observation);
    }

    static DurableOperationObservationResult FailAmbiguously(
        DurableOperationTestFixture fixture,
        DurableOperationState state,
        string identity)
    {
        var claimed = fixture.Executor.Claim(
            state,
            new($"operation-attempt/{identity}"),
            claimant: "worker/a",
            DurableOperationTestFixture.CreatedAtUtc);
        var claim = Assert.IsType<DurableOperationClaim>(claimed.Claim);
        var dispatched = fixture.Executor.BeginDispatch(
            claimed.State,
            claim.AttemptId,
            claim.Fence,
            DurableOperationTestFixture.CreatedAtUtc.AddMinutes(1));
        return fixture.Executor.RecordObservation(
            dispatched.State,
            claim.AttemptId,
            claim.Fence,
            fixture.Failure(
                DurableOperationFailurePhase.PostCommitPreAcknowledgement,
                DurableOperationEffectEvidence.Ambiguous),
            DurableOperationTestFixture.CreatedAtUtc.AddMinutes(2));
    }

    sealed record AttemptExecution(
        DurableOperationState DispatchedState,
        DurableOperationClaim Claim,
        DurableOperationAttemptObservation Observation);
}
