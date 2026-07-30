using System.Text.Json;
using System.Text.Json.Nodes;
using Cohesive.Execution;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class DurableOperationContractTests
{
    [Fact]
    public void TryCreate_ValidBindingRetainsCanonicalLogicalIdentityAndStartsPending()
    {
        var fixture = DurableOperationTestFixture.Create();
        var request = fixture.Request();

        var validation = fixture.Executor.TryCreate(
            request,
            fixture.Binding,
            DurableOperationTestFixture.CreatedAtUtc,
            out var state);

        Assert.True(validation.IsValid, DurableOperationTestFixture.FormatDiagnostics(validation));
        var created = Assert.IsType<DurableOperationState>(state);
        Assert.Equal(DurableOperationState.CurrentSchemaVersion, created.SchemaVersion);
        Assert.Equal(request.Context.EmissionId, created.OperationId);
        Assert.Equal(request.Context.IdempotencyKey, created.DeduplicationKey.IdempotencyKey);
        Assert.Equal(request.Context.AuthorityScope, created.DeduplicationKey.AuthorityScope);
        Assert.Equal(request.Contract, created.DeduplicationKey.RequestContract);
        Assert.Equal(DurableOperationStatus.Pending, created.Status);
        Assert.Empty(created.Attempts);
        Assert.Null(created.Acknowledgement);
        Assert.Null(created.Admission);
    }

    [Fact]
    public void StateConstructor_RejectsLegacyNestedRequestEnvelopeSchema()
    {
        var fixture = DurableOperationTestFixture.Create();
        var current = fixture.Request();
        var legacy = new RequestEnvelope(
            new("cohesive-interaction-envelope/v1"),
            current.Context,
            current.Contract,
            current.Payload,
            current.ResponseTarget);

        var exception = Assert.Throws<ArgumentException>(() => new DurableOperationState(
            DurableOperationState.CurrentSchemaVersion,
            legacy,
            fixture.Binding,
            DurableOperationTestFixture.CreatedAtUtc));

        Assert.Equal("request", exception.ParamName);
    }

    [Fact]
    public void TryCreate_RejectsIncompleteReplyMappingsAndUnsafeStableIdentityRetry()
    {
        var fixture = DurableOperationTestFixture.Create();
        var incomplete = BindingLike(
            fixture,
            replies: [new(new("result"), fixture.ResultReplyContract)]);
        var unsafeRetry = BindingLike(
            fixture,
            idempotencyEvidence: DurableOperationIdempotencyEvidence.None);

        var incompleteValidation = fixture.Executor.TryCreate(
            fixture.Request("emission/request/incomplete"),
            incomplete,
            DurableOperationTestFixture.CreatedAtUtc,
            out var incompleteState);
        var retryValidation = fixture.Executor.TryCreate(
            fixture.Request("emission/request/unsafe-retry"),
            unsafeRetry,
            DurableOperationTestFixture.CreatedAtUtc,
            out var retryState);

        Assert.Null(incompleteState);
        Assert.Contains(
            incompleteValidation.Diagnostics,
            static diagnostic => diagnostic.Code == DurableOperationDiagnosticCodes.ReplyBindingIncomplete);
        Assert.Null(retryState);
        Assert.Contains(
            retryValidation.Diagnostics,
            static diagnostic => diagnostic.Code == DurableOperationDiagnosticCodes.RetryEvidenceInsufficient);
    }

    [Fact]
    public void TryCreate_ReconcileBeforeRetryRequiresAnExactReconciliationTarget()
    {
        var fixture = DurableOperationTestFixture.Create(
            retry: RequestRetrySemantics.ReconcileBeforeRetry,
            ambiguousOutcome: RequestResolutionSemantics.Reconcile,
            unresolvedOutcome: RequestResolutionSemantics.Reconcile,
            idempotencyEvidence: DurableOperationIdempotencyEvidence.None);
        var withoutReconciliation = BindingLike(
            fixture,
            reconciliationTarget: null,
            escalationTarget: null,
            idempotencyEvidence: DurableOperationIdempotencyEvidence.None);

        var validation = fixture.Executor.TryCreate(
            fixture.Request("emission/request/missing-reconcile"),
            withoutReconciliation,
            DurableOperationTestFixture.CreatedAtUtc,
            out var state);

        Assert.Null(state);
        Assert.Contains(
            validation.Diagnostics,
            static diagnostic => diagnostic.Code == DurableOperationDiagnosticCodes.ReconciliationBindingInvalid);
    }

    [Fact]
    public void Claim_LiveLeaseReplaysForOwnerBlocksCompetitorAndReclaimsWithHigherFenceAtExpiry()
    {
        var fixture = DurableOperationTestFixture.Create();
        var initial = fixture.CreateState();
        var firstId = new OperationAttemptId("operation-attempt/1");
        var secondId = new OperationAttemptId("operation-attempt/2");

        var first = fixture.Executor.Claim(
            initial,
            firstId,
            claimant: "worker/a",
            DurableOperationTestFixture.CreatedAtUtc);
        var replay = fixture.Executor.Claim(
            first.State,
            firstId,
            claimant: "worker/a",
            DurableOperationTestFixture.CreatedAtUtc.AddMinutes(1));
        var busy = fixture.Executor.Claim(
            first.State,
            secondId,
            claimant: "worker/b",
            DurableOperationTestFixture.CreatedAtUtc.AddMinutes(1));
        var reclaimed = fixture.Executor.Claim(
            first.State,
            secondId,
            claimant: "worker/b",
            DurableOperationTestFixture.CreatedAtUtc.AddMinutes(5));

        Assert.Equal(DurableOperationClaimDisposition.Claimed, first.Disposition);
        Assert.Equal(1, first.Claim?.Fence.Value);
        Assert.Equal(DurableOperationClaimDisposition.Replayed, replay.Disposition);
        Assert.Same(first.State, replay.State);
        Assert.Equal(first.Claim, replay.Claim);
        Assert.Equal(DurableOperationClaimDisposition.Busy, busy.Disposition);
        Assert.Same(first.State, busy.State);
        Assert.Equal(DurableOperationClaimDisposition.Claimed, reclaimed.Disposition);
        Assert.Equal(2, reclaimed.Claim?.Fence.Value);
        Assert.Equal(2, reclaimed.State.Attempts.Length);
        Assert.Equal(DurableOperationAttemptStage.Failed, reclaimed.State.Attempts[0].Stage);
        Assert.Equal(
            DurableOperationEffectEvidence.NotExecuted,
            reclaimed.State.Attempts[0].Failure?.EffectEvidence);
        Assert.Equal(DurableOperationAttemptStage.Claimed, reclaimed.State.Attempts[1].Stage);

        var staleDispatch = fixture.Executor.BeginDispatch(
            reclaimed.State,
            firstId,
            Assert.IsType<DurableOperationClaim>(first.Claim).Fence,
            DurableOperationTestFixture.CreatedAtUtc.AddMinutes(5));
        Assert.Equal(DurableOperationDispatchDisposition.StaleFence, staleDispatch.Disposition);
        Assert.Same(reclaimed.State, staleDispatch.State);
    }

    [Fact]
    public void RenewClaim_ExtendsOneFenceAndMakesReplayStaleAndExpiryExplicit()
    {
        var fixture = DurableOperationTestFixture.Create();
        var initial = fixture.CreateState();
        var claimed = fixture.Executor.Claim(
            initial,
            new("operation-attempt/renewed"),
            claimant: "worker/a",
            DurableOperationTestFixture.CreatedAtUtc);
        var claim = Assert.IsType<DurableOperationClaim>(claimed.Claim);

        var renewed = fixture.Executor.RenewClaim(
            claimed.State,
            claim.AttemptId,
            claim.Fence,
            claimant: "worker/a",
            DurableOperationTestFixture.CreatedAtUtc.AddMinutes(4));
        var replay = fixture.Executor.RenewClaim(
            renewed.State,
            claim.AttemptId,
            claim.Fence,
            claimant: "worker/a",
            DurableOperationTestFixture.CreatedAtUtc.AddMinutes(4));
        var stale = fixture.Executor.RenewClaim(
            renewed.State,
            claim.AttemptId,
            new(claim.Fence.Value + 1),
            claimant: "worker/a",
            DurableOperationTestFixture.CreatedAtUtc.AddMinutes(4));
        var expired = fixture.Executor.RenewClaim(
            claimed.State,
            claim.AttemptId,
            claim.Fence,
            claimant: "worker/a",
            DurableOperationTestFixture.CreatedAtUtc.AddMinutes(5));

        Assert.Equal(DurableOperationRenewalDisposition.Renewed, renewed.Disposition);
        Assert.Equal(claim.AttemptId, renewed.Claim?.AttemptId);
        Assert.Equal(claim.Fence, renewed.Claim?.Fence);
        Assert.Equal(claim.ClaimedAtUtc, renewed.Claim?.ClaimedAtUtc);
        Assert.Equal(
            DurableOperationTestFixture.CreatedAtUtc.AddMinutes(9),
            renewed.Claim?.ExpiresAtUtc);
        Assert.Equal(DurableOperationRenewalDisposition.Replayed, replay.Disposition);
        Assert.Same(renewed.State, replay.State);
        Assert.Equal(renewed.Claim, replay.Claim);
        Assert.Equal(DurableOperationRenewalDisposition.StaleFence, stale.Disposition);
        Assert.Same(renewed.State, stale.State);
        Assert.Equal(DurableOperationRenewalDisposition.LeaseExpired, expired.Disposition);
        Assert.Equal(DurableOperationAttemptStage.Failed, expired.State.CurrentAttempt?.Stage);
        Assert.Equal(
            DurableOperationFailureCodes.ClaimExpiredBeforeDispatch,
            expired.State.CurrentAttempt?.Failure?.Code);
    }

    [Fact]
    public void RenewClaim_AllowsAnOperationLongerThanTheOriginalLeaseToAcknowledge()
    {
        var fixture = DurableOperationTestFixture.Create();
        var claimed = fixture.Executor.Claim(
            fixture.CreateState(),
            new("operation-attempt/long-call"),
            claimant: "worker/a",
            DurableOperationTestFixture.CreatedAtUtc);
        var claim = Assert.IsType<DurableOperationClaim>(claimed.Claim);
        var dispatched = fixture.Executor.BeginDispatch(
            claimed.State,
            claim.AttemptId,
            claim.Fence,
            DurableOperationTestFixture.CreatedAtUtc.AddMinutes(1));
        var renewed = fixture.Executor.RenewClaim(
            dispatched.State,
            claim.AttemptId,
            claim.Fence,
            claimant: "worker/a",
            DurableOperationTestFixture.CreatedAtUtc.AddMinutes(4));

        var acknowledged = fixture.Executor.RecordObservation(
            renewed.State,
            claim.AttemptId,
            claim.Fence,
            fixture.Success("long-call-complete"),
            DurableOperationTestFixture.CreatedAtUtc.AddMinutes(6));

        Assert.Equal(DurableOperationRenewalDisposition.Renewed, renewed.Disposition);
        Assert.Equal(DurableOperationObservationDisposition.Acknowledged, acknowledged.Disposition);
        Assert.Equal(DurableOperationStatus.Acknowledged, acknowledged.State.Status);
        Assert.Equal("long-call-complete", acknowledged.State.Acknowledgement?.Outcome.Value.Value?.String);
    }

    [Fact]
    public void Observation_OldFenceIsObservableAndCannotAcknowledgeTheReplacementAttempt()
    {
        var fixture = DurableOperationTestFixture.Create();
        var initial = fixture.CreateState();
        var firstClaim = fixture.Executor.Claim(
            initial,
            new("operation-attempt/1"),
            claimant: "worker/a",
            DurableOperationTestFixture.CreatedAtUtc);
        var firstDispatch = fixture.Executor.BeginDispatch(
            firstClaim.State,
            Assert.IsType<DurableOperationClaim>(firstClaim.Claim).AttemptId,
            firstClaim.Claim.Fence,
            DurableOperationTestFixture.CreatedAtUtc.AddMinutes(1));
        var replacement = fixture.Executor.Claim(
            firstDispatch.State,
            new("operation-attempt/2"),
            claimant: "worker/b",
            DurableOperationTestFixture.CreatedAtUtc.AddMinutes(5));

        var stale = fixture.Executor.RecordObservation(
            replacement.State,
            firstClaim.Claim.AttemptId,
            firstClaim.Claim.Fence,
            fixture.Success("stale-success"),
            DurableOperationTestFixture.CreatedAtUtc.AddMinutes(5));

        Assert.Equal(DurableOperationObservationDisposition.StaleFence, stale.Disposition);
        Assert.Same(replacement.State, stale.State);
        Assert.Null(stale.State.Acknowledgement);
        Assert.Equal("operation-attempt/2", stale.State.CurrentAttempt?.Claim.AttemptId.Value);
    }

    [Fact]
    public void DispatchAndAcknowledgement_ReplayIdenticalEvidenceAndRejectAConflictingOutcome()
    {
        var fixture = DurableOperationTestFixture.Create();
        var claimed = fixture.Executor.Claim(
            fixture.CreateState(),
            new("operation-attempt/1"),
            claimant: "worker/a",
            DurableOperationTestFixture.CreatedAtUtc);
        var claim = Assert.IsType<DurableOperationClaim>(claimed.Claim);
        var dispatched = fixture.Executor.BeginDispatch(
            claimed.State,
            claim.AttemptId,
            claim.Fence,
            DurableOperationTestFixture.CreatedAtUtc.AddMinutes(1));
        var dispatchReplay = fixture.Executor.BeginDispatch(
            dispatched.State,
            claim.AttemptId,
            claim.Fence,
            DurableOperationTestFixture.CreatedAtUtc.AddMinutes(1));
        var success = fixture.Success();
        var acknowledged = fixture.Executor.RecordObservation(
            dispatched.State,
            claim.AttemptId,
            claim.Fence,
            success,
            DurableOperationTestFixture.CreatedAtUtc.AddMinutes(2));
        var acknowledgementReplay = fixture.Executor.RecordObservation(
            acknowledged.State,
            claim.AttemptId,
            claim.Fence,
            success,
            DurableOperationTestFixture.CreatedAtUtc.AddMinutes(3));
        var retroactiveReplay = Assert.Throws<ArgumentException>(() => fixture.Executor.RecordObservation(
            acknowledged.State,
            claim.AttemptId,
            claim.Fence,
            success,
            DurableOperationTestFixture.CreatedAtUtc.AddMinutes(1)));
        var conflicting = fixture.Executor.RecordObservation(
            acknowledged.State,
            claim.AttemptId,
            claim.Fence,
            new DurableOperationOutcomeObservation(
                new RequestFailureOutcome(new("failure"), DurableOperationTestFixture.StringValue("rejected"))),
            DurableOperationTestFixture.CreatedAtUtc.AddMinutes(3));
        var staleFence = fixture.Executor.RecordObservation(
            acknowledged.State,
            claim.AttemptId,
            new(claim.Fence.Value + 1),
            success,
            DurableOperationTestFixture.CreatedAtUtc.AddMinutes(3));

        Assert.Equal(DurableOperationDispatchDisposition.Dispatched, dispatched.Disposition);
        Assert.Equal(DurableOperationDispatchDisposition.Replayed, dispatchReplay.Disposition);
        Assert.Equal(dispatched.Invocation, dispatchReplay.Invocation);
        Assert.Equal(DurableOperationObservationDisposition.Acknowledged, acknowledged.Disposition);
        Assert.Equal(DurableOperationStatus.Acknowledged, acknowledged.State.Status);
        Assert.Equal(DurableOperationObservationDisposition.Replayed, acknowledgementReplay.Disposition);
        Assert.Same(acknowledged.State, acknowledgementReplay.State);
        Assert.Equal("observedAtUtc", retroactiveReplay.ParamName);
        Assert.Equal(DurableOperationObservationDisposition.ConflictingOutcome, conflicting.Disposition);
        Assert.Same(acknowledged.State, conflicting.State);
        Assert.Equal(DurableOperationObservationDisposition.StaleFence, staleFence.Disposition);
        Assert.Same(acknowledged.State, staleFence.State);
    }

    [Fact]
    public void BeginDispatch_ReplayWithoutIdempotencyEvidenceRequiresRecoveryAndReturnsNoInvocation()
    {
        var fixture = DurableOperationTestFixture.Create(
            retry: RequestRetrySemantics.Never,
            ambiguousOutcome: RequestResolutionSemantics.Reconcile,
            unresolvedOutcome: RequestResolutionSemantics.Escalate,
            idempotencyEvidence: DurableOperationIdempotencyEvidence.None);
        var claimed = fixture.Executor.Claim(
            fixture.CreateState(),
            new("operation-attempt/unsafe-replay"),
            claimant: "worker/a",
            DurableOperationTestFixture.CreatedAtUtc);
        var claim = Assert.IsType<DurableOperationClaim>(claimed.Claim);
        var dispatched = fixture.Executor.BeginDispatch(
            claimed.State,
            claim.AttemptId,
            claim.Fence,
            DurableOperationTestFixture.CreatedAtUtc.AddMinutes(1));

        var replay = fixture.Executor.BeginDispatch(
            dispatched.State,
            claim.AttemptId,
            claim.Fence,
            DurableOperationTestFixture.CreatedAtUtc.AddMinutes(2));

        Assert.Equal(DurableOperationDispatchDisposition.RecoveryRequired, replay.Disposition);
        Assert.Null(replay.Invocation);
        Assert.Equal(DurableOperationStatus.ReconciliationRequired, replay.State.Status);
        Assert.Equal(DurableOperationAttemptStage.Failed, replay.State.CurrentAttempt?.Stage);
        Assert.Equal(DurableOperationFailurePhase.InCall, replay.State.CurrentAttempt?.Failure?.Phase);
        Assert.Equal(
            DurableOperationEffectEvidence.Ambiguous,
            replay.State.CurrentAttempt?.Failure?.EffectEvidence);
        Assert.Equal(
            DurableOperationFailureCodes.UnsafeDispatchReplay,
            replay.State.CurrentAttempt?.Failure?.Code);
    }

    [Fact]
    public async Task AdapterExecution_RequiresAnExactDeclaredRequestContract()
    {
        var fixture = DurableOperationTestFixture.Create();
        var claimed = fixture.Executor.Claim(
            fixture.CreateState(),
            new("operation-attempt/adapter-contract"),
            claimant: "worker/a",
            DurableOperationTestFixture.CreatedAtUtc);
        var claim = Assert.IsType<DurableOperationClaim>(claimed.Claim);
        var dispatched = fixture.Executor.BeginDispatch(
            claimed.State,
            claim.AttemptId,
            claim.Fence,
            DurableOperationTestFixture.CreatedAtUtc.AddMinutes(1));
        var invocation = Assert.IsType<DurableOperationInvocation>(dispatched.Invocation);
        var exactAdapter = new DurableOperationFakeAdapter(fixture.RequestContract)
            .Script(invocation.Request.Context.EmissionId, fixture.Success());
        var otherContract = new RequestContractReference(
            DurableOperationTestFixture.DefinitionReference("interaction/request/other", 'f'));
        var wrongAdapter = new DurableOperationFakeAdapter(otherContract)
            .Script(invocation.Request.Context.EmissionId, fixture.Success());

        var observation = await DurableOperationReferenceExecutor.ExecuteAsync(
            DurableOperationTestFixture.ContextAt(DurableOperationTestFixture.CreatedAtUtc.AddMinutes(1)),
            invocation,
            exactAdapter);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await DurableOperationReferenceExecutor.ExecuteAsync(
                DurableOperationTestFixture.ContextAt(DurableOperationTestFixture.CreatedAtUtc.AddMinutes(1)),
                invocation,
                wrongAdapter));

        Assert.True(exactAdapter.Capabilities.Supports(fixture.RequestContract));
        Assert.False(exactAdapter.Capabilities.Supports(otherContract));
        Assert.IsType<DurableOperationOutcomeObservation>(observation);
        Assert.Contains("exact Request contract", exception.Message, StringComparison.Ordinal);
        Assert.Empty(wrongAdapter.Invocations);
    }

    [Fact]
    public void PhysicalObservation_CannotFabricateAnEndogenousTimeoutOutcome()
    {
        var fixture = DurableOperationTestFixture.Create(timeoutAfter: TimeSpan.FromMinutes(2));
        var claimed = fixture.Executor.Claim(
            fixture.CreateState("emission/request/physical-timeout"),
            new("operation-attempt/physical-timeout"),
            claimant: "worker/a",
            DurableOperationTestFixture.CreatedAtUtc);
        var claim = Assert.IsType<DurableOperationClaim>(claimed.Claim);
        var dispatched = fixture.Executor.BeginDispatch(
            claimed.State,
            claim.AttemptId,
            claim.Fence,
            DurableOperationTestFixture.CreatedAtUtc.AddMinutes(1));

        var observation = fixture.Executor.RecordObservation(
            dispatched.State,
            claim.AttemptId,
            claim.Fence,
            new DurableOperationOutcomeObservation(fixture.Timeout("fabricated")),
            DurableOperationTestFixture.CreatedAtUtc.AddMinutes(1));

        Assert.Equal(DurableOperationObservationDisposition.InvalidEvidence, observation.Disposition);
        Assert.Same(dispatched.State, observation.State);
        Assert.Null(observation.State.Acknowledgement);
    }

    [Theory]
    [InlineData(
        DurableOperationFailurePhase.PreCall,
        DurableOperationEffectEvidence.Ambiguous)]
    [InlineData(
        DurableOperationFailurePhase.PostCallPreCommit,
        DurableOperationEffectEvidence.NotExecuted)]
    [InlineData(
        DurableOperationFailurePhase.PostCommitPreAcknowledgement,
        DurableOperationEffectEvidence.NotCommitted)]
    public void FailureEvidence_RejectsContradictoryPhaseAndExternalEffectClaims(
        DurableOperationFailurePhase phase,
        DurableOperationEffectEvidence effectEvidence)
    {
        Assert.Throws<ArgumentException>(() => new DurableOperationFailure(
            phase,
            effectEvidence,
            DurableOperationFailureDisposition.Retryable,
            "adapter.contradiction"));
    }

    [Fact]
    public void State_WithAcknowledgementAndAdmission_RoundTripsItsPortableHistory()
    {
        var fixture = DurableOperationTestFixture.Create();
        var state = Acknowledge(fixture, fixture.CreateState());
        var admitted = fixture.Executor.AdmitResult(
            state,
            new(state.Request.ResponseTarget, DurableOperationResultArrival.Eligible));
        var options = InteractionEnvelopeJsonSerializer.CreateOptions();

        var json = JsonSerializer.Serialize(admitted.State, options);
        var restored = JsonSerializer.Deserialize<DurableOperationState>(json, options);

        Assert.Equal(admitted.State, restored);
        Assert.Equal(DurableOperationStatus.Dispositioned, restored?.Status);
        Assert.Equal(state.OperationId, restored?.OperationId);
        Assert.Equal(state.Request.Context.CorrelationId, restored?.Request.Context.CorrelationId);
        Assert.Equal(state.Request.Context.IdempotencyKey, restored?.Request.Context.IdempotencyKey);
        Assert.Equal(DurableOperationAdmissionDisposition.Accepted, restored?.Admission?.Disposition);
    }

    [Fact]
    public void State_CreateReplyPreservesItsExactRequestContext()
    {
        var fixture = DurableOperationTestFixture.Create();
        var acknowledged = Acknowledge(fixture, fixture.CreateState());

        var reply = acknowledged.CreateReply(
            new("emission/reply/1"),
            new("idempotency/reply/1"),
            ordering: null,
            acknowledged.Request.Context.Provenance);

        Assert.Equal(acknowledged.Request.Context.EmissionId, reply.Context.CausationId);
        Assert.Equal(acknowledged.Request.Context.CorrelationId, reply.Context.CorrelationId);
        Assert.Equal(acknowledged.Request.Context.AuthorityScope, reply.Context.AuthorityScope);
        Assert.Equal(acknowledged.Request.Context.Delivery, reply.Context.Delivery);
        Assert.Equal(acknowledged.Acknowledgement?.ReplyContract, reply.Contract);
        Assert.Equal(acknowledged.Acknowledgement?.Outcome, reply.Outcome);
    }

    [Fact]
    public void StateJson_UsesStringEncodedFencesAndRejectsAnUnknownSchemaVersion()
    {
        var fixture = DurableOperationTestFixture.Create();
        var claimed = fixture.Executor.Claim(
            fixture.CreateState(),
            new("operation-attempt/wire-contract"),
            claimant: "worker/a",
            DurableOperationTestFixture.CreatedAtUtc);
        var state = claimed.State;
        var options = InteractionEnvelopeJsonSerializer.CreateOptions();

        var maximumFenceJson = JsonSerializer.Serialize(new OperationFence(long.MaxValue), options);
        Assert.Throws<JsonException>(() => JsonSerializer.Serialize(default(OperationFence), options));
        var json = JsonSerializer.Serialize(state, options);
        using var document = JsonDocument.Parse(json);
        var fence = document.RootElement
            .GetProperty("attempts")[0]
            .GetProperty("claim")
            .GetProperty("fence");
        var unknownSchema = JsonNode.Parse(json)?.AsObject()
            ?? throw new InvalidOperationException("Serialized durable state must be a JSON object.");
        unknownSchema["schemaVersion"] = "cohesive-durable-operation/v999";

        var exception = Assert.Throws<ArgumentException>(() =>
            JsonSerializer.Deserialize<DurableOperationState>(unknownSchema.ToJsonString(), options));

        Assert.Equal($"\"{long.MaxValue}\"", maximumFenceJson);
        Assert.Equal(JsonValueKind.String, fence.ValueKind);
        Assert.Equal("1", fence.GetString());
        Assert.Equal("schemaVersion", exception.ParamName);
    }

    internal static DurableOperationState Acknowledge(
        DurableOperationTestFixture fixture,
        DurableOperationState state,
        string attemptId = "operation-attempt/1")
    {
        var claimResult = fixture.Executor.Claim(
            state,
            new(attemptId),
            claimant: "worker/a",
            DurableOperationTestFixture.CreatedAtUtc);
        var claim = Assert.IsType<DurableOperationClaim>(claimResult.Claim);
        var dispatch = fixture.Executor.BeginDispatch(
            claimResult.State,
            claim.AttemptId,
            claim.Fence,
            DurableOperationTestFixture.CreatedAtUtc.AddMinutes(1));
        var acknowledgement = fixture.Executor.RecordObservation(
            dispatch.State,
            claim.AttemptId,
            claim.Fence,
            fixture.Success(),
            DurableOperationTestFixture.CreatedAtUtc.AddMinutes(2));
        Assert.Equal(DurableOperationObservationDisposition.Acknowledged, acknowledgement.Disposition);
        return acknowledgement.State;
    }

    static DurableRequestBinding BindingLike(
        DurableOperationTestFixture fixture,
        IReadOnlyList<DurableReplyBinding>? replies = null,
        DurableOperationIdempotencyEvidence? idempotencyEvidence = null,
        DurableOperationResolutionTarget? reconciliationTarget = null,
        DurableOperationResolutionTarget? escalationTarget = null) =>
        new(
            fixture.Binding.Request,
            replies is null ? fixture.Binding.Replies : [.. replies],
            fixture.Binding.MaxAttempts,
            fixture.Binding.ClaimLease,
            fixture.Binding.TimeoutAfter,
            idempotencyEvidence ?? fixture.Binding.IdempotencyEvidence,
            fixture.Binding.TerminalFailureOutcome,
            reconciliationTarget,
            escalationTarget);
}
