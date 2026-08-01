using System.Text.Json;
using System.Text.Json.Nodes;
using Cohesive.Control;
using Cohesive.Execution;

namespace Cohesive.Tests.Storage.Control;

public sealed class ControlLimitUpdateReferenceReducerTests
{
    static readonly DateTimeOffset CreatedAtUtc =
        new(2026, 7, 29, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AcceptedUpdateRemainsPendingUntilExactLaterSafePoint()
    {
        var definition = Definition();
        var initial = State(definition);
        var command = Command(
            definition,
            initial,
            ControlTestFixture.Point(
                (ControlActuatorKind.Concurrency, 6),
                (ControlActuatorKind.BatchItems, 20)));

        var accepted = ControlLimitUpdateReferenceReducer.Submit(
            definition,
            initial,
            command,
            CreatedAtUtc.AddSeconds(2));

        Assert.Equal(ControlLimitUpdateDecisionDisposition.Accepted, accepted.Disposition);
        Assert.Equal(new ControlRevision("2"), accepted.State.Revision);
        Assert.Same(initial.OperatingPoint, accepted.State.OperatingPoint);
        Assert.Same(accepted.Receipt, accepted.State.PendingLimitUpdate);
        Assert.Equal(
            10,
            definition.HardLimits.GetEffectiveRange(ControlActuatorKind.Concurrency).Maximum.Value);
        Assert.Equal(8, definition.GetEffectiveRange(ControlActuatorKind.Concurrency).Maximum.Value);

        var point = ApplicationPoint(
            definition,
            accepted.State,
            "safe-point/apply",
            fence: 11,
            accepted.Receipt!.AcceptedAtUtc.AddMilliseconds(1));
        var applied = ControlLimitUpdateReferenceReducer.Apply(
            definition,
            accepted.State,
            point,
            point.ObservedAtUtc);

        Assert.Equal(ControlActuationDisposition.Applied, applied.Disposition);
        Assert.Equal(new ControlRevision("3"), applied.State.Revision);
        Assert.Equal(command.RequestedOperatingPoint, applied.State.OperatingPoint);
        Assert.Null(applied.State.PendingLimitUpdate);
        Assert.Same(applied.Actuation, applied.State.LastLimitUpdateActuation);
        Assert.Same(point, applied.Actuation?.ApplicationPoint);
        Assert.Equal(initial.OperatingPoint, applied.Actuation?.PriorOperatingPoint);
        Assert.Equal(point.Fence, applied.State.LastApplicationFence);
    }

    [Fact]
    public void ExactAndSemanticReplaysReturnRetainedReceiptWithoutMutation()
    {
        var definition = Definition();
        var initial = State(definition);
        var command = Command(definition, initial, Point(concurrency: 6));
        var accepted = ControlLimitUpdateReferenceReducer.Submit(
            definition,
            initial,
            command,
            CreatedAtUtc.AddSeconds(2));

        var exact = ControlLimitUpdateReferenceReducer.Submit(
            definition,
            accepted.State,
            command,
            CreatedAtUtc.AddSeconds(3));
        var semanticReplay = ControlLimitUpdateReferenceReducer.Submit(
            definition,
            accepted.State,
            Command(
                definition,
                initial,
                Point(concurrency: 6),
                commandId: "limit-update/retry",
                idempotencyKey: command.IdempotencyKey.Value,
                issuedAtUtc: command.IssuedAtUtc.AddMilliseconds(1)),
            CreatedAtUtc.AddSeconds(3));

        Assert.Equal(ControlLimitUpdateDecisionDisposition.Replayed, exact.Disposition);
        Assert.Same(accepted.State, exact.State);
        Assert.Same(accepted.Receipt, exact.Receipt);
        Assert.Equal(ControlLimitUpdateDecisionDisposition.Replayed, semanticReplay.Disposition);
        Assert.Same(accepted.State, semanticReplay.State);
        Assert.Same(accepted.Receipt, semanticReplay.Receipt);
    }

    [Fact]
    public void CrossScopeExactAndIdempotentReplaysAreUnauthorizedBeforeReceiptResolution()
    {
        var definition = Definition();
        var initial = State(definition);
        var command = Command(definition, initial, Point(concurrency: 6));
        var accepted = ControlLimitUpdateReferenceReducer.Submit(
            definition,
            initial,
            command,
            CreatedAtUtc.AddSeconds(2));
        var otherScope = Authorization("another-authority");
        var exactCrossScope = Command(
            definition,
            initial,
            Point(concurrency: 6),
            commandId: command.CommandId.Value,
            idempotencyKey: command.IdempotencyKey.Value,
            authorization: otherScope);
        var idempotentCrossScope = Command(
            definition,
            initial,
            Point(concurrency: 6),
            commandId: "limit-update/cross-scope-retry",
            idempotencyKey: command.IdempotencyKey.Value,
            authorization: otherScope);

        var exact = ControlLimitUpdateReferenceReducer.Submit(
            definition,
            accepted.State,
            exactCrossScope,
            CreatedAtUtc.AddSeconds(3));
        var idempotent = ControlLimitUpdateReferenceReducer.Submit(
            definition,
            accepted.State,
            idempotentCrossScope,
            CreatedAtUtc.AddSeconds(3));

        Assert.Equal(ControlLimitUpdateDecisionDisposition.Unauthorized, exact.Disposition);
        Assert.Null(exact.Receipt);
        Assert.Same(accepted.State, exact.State);
        Assert.Equal(ControlLimitUpdateDecisionDisposition.Unauthorized, idempotent.Disposition);
        Assert.Null(idempotent.Receipt);
        Assert.Same(accepted.State, idempotent.State);
    }

    [Fact]
    public void DecisionRejectsMissingAcceptedReceiptAndUnretainedReplayReceiptOnConstructionAndRead()
    {
        var definition = Definition();
        var initial = State(definition);
        var command = Command(definition, initial, Point(concurrency: 6));
        var orphanReceipt = new ControlLimitUpdateReceipt(
            command,
            new ControlRevision("2"),
            CreatedAtUtc.AddSeconds(2));

        Assert.Throws<ArgumentException>(() => new ControlLimitUpdateDecision(
            ControlLoopDefinition.CurrentSchemaVersion,
            ControlLimitUpdateDecisionDisposition.Accepted,
            initial));
        Assert.Throws<ArgumentException>(() => new ControlLimitUpdateDecision(
            ControlLoopDefinition.CurrentSchemaVersion,
            ControlLimitUpdateDecisionDisposition.Replayed,
            initial,
            orphanReceipt));

        var options = ControlJsonSerializer.CreateOptions();
        var rejected = new ControlLimitUpdateDecision(
            ControlLoopDefinition.CurrentSchemaVersion,
            ControlLimitUpdateDecisionDisposition.Invalid,
            initial);
        var missingAcceptedReceipt = Assert.IsType<JsonObject>(
            JsonSerializer.SerializeToNode(rejected, options));
        missingAcceptedReceipt["disposition"] = "accepted";
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ControlLimitUpdateDecision>(
            missingAcceptedReceipt.ToJsonString(),
            options));

        var unretainedReplayReceipt = Assert.IsType<JsonObject>(
            JsonSerializer.SerializeToNode(rejected, options));
        unretainedReplayReceipt["disposition"] = "replayed";
        unretainedReplayReceipt["receipt"] = JsonSerializer.SerializeToNode(orphanReceipt, options);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ControlLimitUpdateDecision>(
            unretainedReplayReceipt.ToJsonString(),
            options));
    }

    [Fact]
    public void ConcurrentCommandWithPriorRevisionIsPreciselyStale()
    {
        var definition = Definition();
        var initial = State(definition);
        var first = Command(
            definition,
            initial,
            Point(concurrency: 6),
            commandId: "limit-update/first",
            idempotencyKey: "limit-update/first");
        var competing = Command(
            definition,
            initial,
            Point(concurrency: 7),
            commandId: "limit-update/competing",
            idempotencyKey: "limit-update/competing");
        var accepted = ControlLimitUpdateReferenceReducer.Submit(
            definition,
            initial,
            first,
            CreatedAtUtc.AddSeconds(2));

        var stale = ControlLimitUpdateReferenceReducer.Submit(
            definition,
            accepted.State,
            competing,
            CreatedAtUtc.AddSeconds(3));

        Assert.Equal(ControlLimitUpdateDecisionDisposition.Stale, stale.Disposition);
        Assert.Same(accepted.State, stale.State);
        Assert.Contains(stale.Diagnostics, diagnostic =>
            diagnostic.Code == ControlDiagnosticCodes.LimitUpdateStaleFence
            && diagnostic.Location == "/expectedRevision");
    }

    [Fact]
    public void StableIdentityAndIdempotencyConflictsAreDistinct()
    {
        var definition = Definition();
        var initial = State(definition);
        var original = Command(definition, initial, Point(concurrency: 6));
        var accepted = ControlLimitUpdateReferenceReducer.Submit(
            definition,
            initial,
            original,
            CreatedAtUtc.AddSeconds(2));

        var identityConflict = ControlLimitUpdateReferenceReducer.Submit(
            definition,
            accepted.State,
            Command(
                definition,
                initial,
                Point(concurrency: 7),
                commandId: original.CommandId.Value,
                idempotencyKey: "limit-update/different-idempotency"),
            CreatedAtUtc.AddSeconds(3));
        var idempotencyConflict = ControlLimitUpdateReferenceReducer.Submit(
            definition,
            accepted.State,
            Command(
                definition,
                initial,
                Point(concurrency: 7),
                commandId: "limit-update/different-command",
                idempotencyKey: original.IdempotencyKey.Value),
            CreatedAtUtc.AddSeconds(3));

        Assert.Equal(ControlLimitUpdateDecisionDisposition.IdentityConflict, identityConflict.Disposition);
        Assert.Contains(identityConflict.Diagnostics, diagnostic =>
            diagnostic.Code == ControlDiagnosticCodes.LimitUpdateIdentityConflict);
        Assert.Equal(ControlLimitUpdateDecisionDisposition.IdempotencyConflict, idempotencyConflict.Disposition);
        Assert.Contains(idempotencyConflict.Diagnostics, diagnostic =>
            diagnostic.Code == ControlDiagnosticCodes.LimitUpdateIdempotencyConflict);
    }

    [Fact]
    public void ImmutableBoundsAndAuthorityRejectWithoutMutation()
    {
        var definition = Definition();
        var initial = State(definition);
        var outsideBudget = Command(definition, initial, Point(concurrency: 9));
        var wrongAuthority = Command(
            definition,
            initial,
            Point(concurrency: 6),
            authorization: Authorization("another-authority"));

        var bounded = ControlLimitUpdateReferenceReducer.Submit(
            definition,
            initial,
            outsideBudget,
            CreatedAtUtc.AddSeconds(2));
        var unauthorized = ControlLimitUpdateReferenceReducer.Submit(
            definition,
            initial,
            wrongAuthority,
            CreatedAtUtc.AddSeconds(2));

        Assert.Equal(ControlLimitUpdateDecisionDisposition.OutOfBounds, bounded.Disposition);
        Assert.Same(initial, bounded.State);
        Assert.Contains(bounded.Diagnostics, diagnostic =>
            diagnostic.Code == ControlDiagnosticCodes.WorkloadBudgetExceeded);
        Assert.Equal(ControlLimitUpdateDecisionDisposition.Unauthorized, unauthorized.Disposition);
        Assert.Same(initial, unauthorized.State);
        Assert.Contains(unauthorized.Diagnostics, diagnostic =>
            diagnostic.Code == ControlDiagnosticCodes.LimitUpdateUnauthorized);
        Assert.Throws<InvalidOperationException>(() =>
            ControlLimitUpdateResult.FromDecision(unauthorized));
        Assert.Throws<InvalidOperationException>(() =>
            ControlLimitUpdateResult.FromAuthorizedDecision(unauthorized));
    }

    [Fact]
    public void ApplicationRequiresExactLaterFenceAndReplaysExactReceipt()
    {
        var definition = Definition();
        var initial = State(definition);
        var accepted = ControlLimitUpdateReferenceReducer.Submit(
            definition,
            initial,
            Command(definition, initial, Point(concurrency: 6)),
            CreatedAtUtc.AddSeconds(2));
        var tooEarly = ApplicationPoint(
            definition,
            accepted.State,
            "safe-point/too-early",
            fence: 1,
            accepted.Receipt!.AcceptedAtUtc);

        var rejected = ControlLimitUpdateReferenceReducer.Apply(
            definition,
            accepted.State,
            tooEarly,
            tooEarly.ObservedAtUtc);

        Assert.Equal(ControlActuationDisposition.Rejected, rejected.Disposition);
        Assert.Same(accepted.State, rejected.State);
        Assert.Contains(rejected.Diagnostics, diagnostic =>
            diagnostic.Code == ControlDiagnosticCodes.ApplicationPointInvalid);

        var point = ApplicationPoint(
            definition,
            accepted.State,
            "safe-point/exact",
            fence: 2,
            accepted.Receipt.AcceptedAtUtc.AddMilliseconds(1));
        var applied = ControlLimitUpdateReferenceReducer.Apply(
            definition,
            accepted.State,
            point,
            point.ObservedAtUtc);
        var replayed = ControlLimitUpdateReferenceReducer.Apply(
            definition,
            applied.State,
            point,
            point.ObservedAtUtc.AddMilliseconds(1));

        Assert.Equal(ControlActuationDisposition.Replayed, replayed.Disposition);
        Assert.Same(applied.State, replayed.State);
        Assert.Same(applied.Actuation, replayed.Actuation);
    }

    [Fact]
    public void DurableActuationLedgerReplaysAndConflictsOnAnyPriorApplicationPoint()
    {
        var definition = Definition();
        var initial = State(definition);
        var firstAccepted = ControlLimitUpdateReferenceReducer.Submit(
            definition,
            initial,
            Command(
                definition,
                initial,
                Point(concurrency: 6),
                commandId: "limit-update/first",
                idempotencyKey: "limit-update/first"),
            CreatedAtUtc.AddSeconds(2));
        var firstPoint = ApplicationPoint(
            definition,
            firstAccepted.State,
            "safe-point/first",
            fence: 10,
            firstAccepted.Receipt!.AcceptedAtUtc.AddMilliseconds(1));
        var firstApplied = ControlLimitUpdateReferenceReducer.Apply(
            definition,
            firstAccepted.State,
            firstPoint,
            firstPoint.ObservedAtUtc);

        var secondCommand = Command(
            definition,
            firstApplied.State,
            Point(concurrency: 7),
            commandId: "limit-update/second",
            idempotencyKey: "limit-update/second",
            issuedAtUtc: CreatedAtUtc.AddSeconds(3));
        var secondAccepted = ControlLimitUpdateReferenceReducer.Submit(
            definition,
            firstApplied.State,
            secondCommand,
            CreatedAtUtc.AddSeconds(4));
        var secondPoint = ApplicationPoint(
            definition,
            secondAccepted.State,
            "safe-point/second",
            fence: 20,
            secondAccepted.Receipt!.AcceptedAtUtc.AddMilliseconds(1));
        var secondApplied = ControlLimitUpdateReferenceReducer.Apply(
            definition,
            secondAccepted.State,
            secondPoint,
            secondPoint.ObservedAtUtc);

        var replayed = ControlLimitUpdateReferenceReducer.Apply(
            definition,
            secondApplied.State,
            firstPoint,
            secondPoint.ObservedAtUtc.AddMilliseconds(1));
        var alteredReuse = ApplicationPoint(
            definition,
            firstAccepted.State,
            firstPoint.Id.Value,
            fence: 11,
            firstPoint.ObservedAtUtc);
        var conflicted = ControlLimitUpdateReferenceReducer.Apply(
            definition,
            secondApplied.State,
            alteredReuse,
            secondPoint.ObservedAtUtc.AddMilliseconds(1));

        Assert.Equal(2, secondApplied.State.LimitUpdateActuations.Length);
        Assert.Same(firstApplied.Actuation, secondApplied.State.LimitUpdateActuations[0]);
        Assert.Same(secondApplied.Actuation, secondApplied.State.LimitUpdateActuations[1]);
        Assert.Same(secondApplied.Actuation, secondApplied.State.LastLimitUpdateActuation);
        Assert.Equal(ControlActuationDisposition.Replayed, replayed.Disposition);
        Assert.Same(secondApplied.State, replayed.State);
        Assert.Same(firstApplied.Actuation, replayed.Actuation);
        Assert.Equal(ControlActuationDisposition.Rejected, conflicted.Disposition);
        Assert.Same(secondApplied.State, conflicted.State);
        Assert.Contains(conflicted.Diagnostics, diagnostic =>
            diagnostic.Code == ControlDiagnosticCodes.LimitUpdateApplicationPointConflict);

        var structuralClone = new ControlLoopState(
            secondApplied.State.SchemaVersion,
            secondApplied.State.LoopId,
            secondApplied.State.Target,
            secondApplied.State.Epoch,
            secondApplied.State.Revision,
            secondApplied.State.DefinitionFingerprint,
            secondApplied.State.OperatingPoint,
            healthyObservationCount: 0,
            secondApplied.State.CreatedAtUtc,
            secondApplied.State.UpdatedAtUtc,
            lastApplicationFence: secondApplied.State.LastApplicationFence,
            authorityScope: secondApplied.State.AuthorityScope,
            limitUpdateActuations: secondApplied.State.LimitUpdateActuations,
            pendingLimitUpdate: secondApplied.State.PendingLimitUpdate);
        Assert.Equal(secondApplied.State, structuralClone);
        Assert.Equal(secondApplied.State.GetHashCode(), structuralClone.GetHashCode());

        var json = JsonSerializer.Serialize(secondApplied.State, ControlJsonSerializer.CreateOptions());
        Assert.Contains("\"limitUpdateActuations\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"limitUpdateReceipts\"", json, StringComparison.Ordinal);
        Assert.Contains("\"lastActuation\":null", json, StringComparison.Ordinal);
        Assert.Equal(
            secondApplied.State,
            JsonSerializer.Deserialize<ControlLoopState>(
                json,
                ControlJsonSerializer.CreateOptions()));

        Assert.Throws<ArgumentException>(() => new ControlLoopState(
            secondApplied.State.SchemaVersion,
            secondApplied.State.LoopId,
            secondApplied.State.Target,
            secondApplied.State.Epoch,
            secondApplied.State.Revision,
            secondApplied.State.DefinitionFingerprint,
            secondApplied.State.OperatingPoint,
            healthyObservationCount: 0,
            secondApplied.State.CreatedAtUtc,
            secondApplied.State.UpdatedAtUtc,
            lastApplicationFence: secondApplied.State.LastApplicationFence,
            authorityScope: secondApplied.State.AuthorityScope,
            limitUpdateActuations: [secondApplied.State.LimitUpdateActuations[1]]));
    }

    [Fact]
    public void TransportProjectionRedactsOperatingPointsByDefault()
    {
        var definition = Definition();
        var initial = State(definition);
        var accepted = ControlLimitUpdateReferenceReducer.Submit(
            definition,
            initial,
            Command(definition, initial, Point(concurrency: 6)),
            CreatedAtUtc.AddSeconds(2));

        var redacted = ControlLimitUpdateResult.FromDecision(accepted);
        var authorized = ControlLimitUpdateResult.FromAuthorizedDecision(accepted);

        Assert.Equal(ControlLimitUpdateResultDisclosure.Redacted, redacted.Disclosure);
        Assert.Null(redacted.RequestedOperatingPoint);
        Assert.Null(redacted.EffectiveOperatingPoint);
        Assert.Equal(ControlLimitUpdateResultDisclosure.Authorized, authorized.Disclosure);
        Assert.Same(accepted.Receipt!.Command.RequestedOperatingPoint, authorized.RequestedOperatingPoint);
        Assert.Same(accepted.State.OperatingPoint, authorized.EffectiveOperatingPoint);
    }

    [Fact]
    public void OneAtomicUpdateCannotSpanDifferentSafePointKinds()
    {
        var definition = Definition();
        var initial = State(definition);
        var command = Command(
            definition,
            initial,
            ControlTestFixture.Point(
                (ControlActuatorKind.Concurrency, 6),
                (ControlActuatorKind.BatchItems, 30)));

        var decision = ControlLimitUpdateReferenceReducer.Submit(
            definition,
            initial,
            command,
            CreatedAtUtc.AddSeconds(2));

        Assert.Equal(ControlLimitUpdateDecisionDisposition.Invalid, decision.Disposition);
        Assert.Same(initial, decision.State);
        Assert.Contains(decision.Diagnostics, diagnostic =>
            diagnostic.Code == ControlDiagnosticCodes.LimitUpdateInvalid);
    }

    [Fact]
    public void AcceptedOperatorOverrideSupersedesAdaptiveAdviceAndFreshEvidenceMayResumeAfterApply()
    {
        var definition = Definition();
        var initial = State(definition);
        var congestion = ControlRegulatorFixture.Observation(
            initial,
            id: "observation/congested-before-override",
            value: 300,
            observedAtUtc: CreatedAtUtc.AddMinutes(1),
            metric: ControlMetricKind.Latency);
        var adaptive = AimdControlReferenceRegulator.Evaluate(
            definition,
            initial,
            congestion,
            congestion.ObservedAtUtc);
        Assert.Equal(ControlDecisionDisposition.Recommended, adaptive.Disposition);

        var staleAdaptivePoint = ControlRegulatorFixture.ApplicationPoint(
            adaptive.State,
            id: "safe-point/stale-adaptive",
            fence: 1,
            observedAtUtc: adaptive.EvaluatedAtUtc.AddMilliseconds(1),
            authority: definition.ApplicationAuthority);
        var accepted = ControlLimitUpdateReferenceReducer.Submit(
            definition,
            adaptive.State,
            Command(
                definition,
                adaptive.State,
                Point(concurrency: 6),
                issuedAtUtc: adaptive.EvaluatedAtUtc.AddMilliseconds(1)),
            adaptive.EvaluatedAtUtc.AddMilliseconds(2));

        Assert.Equal(ControlLimitUpdateDecisionDisposition.Accepted, accepted.Disposition);
        Assert.Null(accepted.State.PendingRecommendation);
        Assert.Equal(0, accepted.State.HealthyObservationCount);
        Assert.Equal(adaptive.State.Revision.Ordinal + 1, accepted.State.Revision.Ordinal);
        Assert.Equal(adaptive.State.OperatingPoint, accepted.State.OperatingPoint);

        var staleApplication = AimdControlReferenceRegulator.Apply(
            definition,
            accepted.State,
            staleAdaptivePoint,
            accepted.Receipt!.AcceptedAtUtc.AddMilliseconds(1));
        Assert.Equal(ControlActuationDisposition.Rejected, staleApplication.Disposition);
        Assert.Same(accepted.State, staleApplication.State);
        Assert.Contains(staleApplication.Diagnostics, diagnostic =>
            diagnostic.Code == ControlDiagnosticCodes.LimitUpdatePending);

        var resumedPending = ControlJsonSerializer.DeserializeState(
            ControlJsonSerializer.Serialize(accepted.State),
            definition);
        Assert.Equal(accepted.State, resumedPending);

        var operatorPoint = ApplicationPoint(
            definition,
            resumedPending,
            id: "safe-point/operator",
            fence: 2,
            observedAtUtc: accepted.Receipt.AcceptedAtUtc.AddMilliseconds(1));
        var applied = ControlLimitUpdateReferenceReducer.Apply(
            definition,
            resumedPending,
            operatorPoint,
            operatorPoint.ObservedAtUtc);
        Assert.Equal(ControlActuationDisposition.Applied, applied.Disposition);
        Assert.Equal(Point(concurrency: 6), applied.State.OperatingPoint);
        Assert.Equal(applied.Actuation!.Id, applied.State.LastAppliedActuationId);

        var freshObservedAt = applied.Actuation.AppliedAtUtc.AddMilliseconds(
            definition.Policy.MinimumDwellMilliseconds + 1);
        var fresh = ControlRegulatorFixture.Observation(
            applied.State,
            id: "observation/fresh-after-override",
            value: 50,
            observedAtUtc: freshObservedAt,
            metric: ControlMetricKind.Latency);
        var resumedAdaptive = AimdControlReferenceRegulator.Evaluate(
            definition,
            applied.State,
            fresh,
            fresh.ObservedAtUtc);

        Assert.NotEqual(ControlDecisionDisposition.Rejected, resumedAdaptive.Disposition);
        Assert.Equal(applied.State.Revision.Ordinal + 1, resumedAdaptive.State.Revision.Ordinal);
        Assert.Equal(applied.State.OperatingPoint, resumedAdaptive.State.OperatingPoint);
        Assert.Equal(1, resumedAdaptive.State.HealthyObservationCount);
    }

    [Fact]
    public void PendingOperatorOverrideBlocksNewAutomaticObservationUntilItsExactSafePoint()
    {
        var definition = Definition();
        var initial = State(definition);
        var accepted = ControlLimitUpdateReferenceReducer.Submit(
            definition,
            initial,
            Command(definition, initial, Point(concurrency: 6)),
            CreatedAtUtc.AddSeconds(2));
        var blockedObservation = ControlRegulatorFixture.Observation(
            accepted.State,
            id: "observation/while-operator-pending",
            value: 300,
            observedAtUtc: CreatedAtUtc.AddSeconds(3),
            metric: ControlMetricKind.Latency);

        var blocked = AimdControlReferenceRegulator.Evaluate(
            definition,
            accepted.State,
            blockedObservation,
            blockedObservation.ObservedAtUtc);

        Assert.Equal(ControlDecisionDisposition.Rejected, blocked.Disposition);
        Assert.Same(accepted.State, blocked.State);
        Assert.Contains(blocked.Diagnostics, diagnostic =>
            diagnostic.Code == ControlDiagnosticCodes.LimitUpdatePending);

        var point = ApplicationPoint(
            definition,
            accepted.State,
            id: "safe-point/operator-before-adaptive",
            fence: 1,
            observedAtUtc: CreatedAtUtc.AddSeconds(4));
        var applied = ControlLimitUpdateReferenceReducer.Apply(
            definition,
            accepted.State,
            point,
            point.ObservedAtUtc);
        var fresh = ControlRegulatorFixture.Observation(
            applied.State,
            id: "observation/after-operator",
            value: 50,
            observedAtUtc: point.ObservedAtUtc.AddMilliseconds(
                definition.Policy.MinimumDwellMilliseconds + 1),
            metric: ControlMetricKind.Latency);
        var resumed = AimdControlReferenceRegulator.Evaluate(
            definition,
            applied.State,
            fresh,
            fresh.ObservedAtUtc);

        Assert.Equal(ControlActuationDisposition.Applied, applied.Disposition);
        Assert.NotEqual(ControlDecisionDisposition.Rejected, resumed.Disposition);
        Assert.Equal(applied.State.Revision.Ordinal + 1, resumed.State.Revision.Ordinal);
    }

    static ControlLoopDefinition Definition() =>
        ControlTestFixture.Definition(
            ControlTestFixture.Limits(
                ControlTestFixture.Limit(
                    ControlActuatorKind.Concurrency,
                    minimum: 1,
                    maximum: 10,
                    ControlHardLimitOrigin.Adapter,
                    "adapter/concurrency"),
                ControlTestFixture.Limit(
                    ControlActuatorKind.BatchItems,
                    minimum: 1,
                    maximum: 100,
                    ControlHardLimitOrigin.Semantic,
                    "process/batch")),
            ControlTestFixture.Point(
                (ControlActuatorKind.Concurrency, 4),
                (ControlActuatorKind.BatchItems, 20)),
            [ControlTestFixture.Budget(
                ControlActuatorKind.Concurrency,
                capacity: 10,
                reserved: 2)]);

    static ControlOperatingPoint Point(long concurrency) =>
        ControlTestFixture.Point(
            (ControlActuatorKind.Concurrency, concurrency),
            (ControlActuatorKind.BatchItems, 20));

    static ControlLoopState State(ControlLoopDefinition definition) =>
        ControlLoopState.Create(
            definition,
            new ControlEpochId("generation-1"),
            Authorization().AuthorityScope,
            CreatedAtUtc);

    static ControlLimitUpdateCommand Command(
        ControlLoopDefinition definition,
        ControlLoopState state,
        ControlOperatingPoint point,
        string commandId = "limit-update/1",
        string idempotencyKey = "limit-update/logical-1",
        ProcessControlAuthorizationContext? authorization = null,
        DateTimeOffset? issuedAtUtc = null) =>
        new(
            ControlLoopDefinition.CurrentSchemaVersion,
            new EmissionId(commandId),
            new InteractionIdempotencyKey(idempotencyKey),
            state.LoopId,
            definition.Fingerprint,
            state.Target,
            state.Epoch,
            state.Revision,
            point,
            authorization ?? Authorization(),
            issuedAtUtc ?? CreatedAtUtc.AddSeconds(1),
            ControlTestFixture.Provenance());

    static ProcessControlAuthorizationContext Authorization(
        string authority = "cohesive/control") =>
        new(
            "operator/1",
            new InteractionAuthorityScope(authority, "tenant-a"),
            "policy-decision/allow-1");

    static ControlApplicationPoint ApplicationPoint(
        ControlLoopDefinition definition,
        ControlLoopState state,
        string id,
        long fence,
        DateTimeOffset observedAtUtc) =>
        new(
            ControlLoopDefinition.CurrentSchemaVersion,
            new ControlApplicationPointId(id),
            state.LoopId,
            state.DefinitionFingerprint,
            state.Target,
            state.Epoch,
            state.Revision,
            new ControlApplicationFence(fence.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ControlApplicationPointKind.WorkAdmissionBoundary,
            observedAtUtc,
            definition.ApplicationAuthority,
            $"process:{id}");
}
