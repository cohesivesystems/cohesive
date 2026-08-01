using System.Text.Json;
using Cohesive.Control;

namespace Cohesive.Tests.Storage.Control;

public sealed class AimdControlReferenceRegulatorTests
{
    [Fact]
    public void Evaluate_IsPureAndDeterministicForIdenticalInputs()
    {
        var definition = ControlRegulatorFixture.Definition();
        var state = ControlRegulatorFixture.InitialState(definition);
        var observation = ControlRegulatorFixture.Observation(state, "observation-1");
        var evaluatedAt = observation.ObservedAtUtc.AddMilliseconds(1);

        var first = AimdControlReferenceRegulator.Evaluate(definition, state, observation, evaluatedAt);
        var second = AimdControlReferenceRegulator.Evaluate(definition, state, observation, evaluatedAt);

        Assert.Equal(first, second);
        Assert.Equal(
            ControlJsonSerializer.GetCanonicalBytes(first),
            ControlJsonSerializer.GetCanonicalBytes(second));
        Assert.Equal(ControlRevision.Initial, state.Revision);
        Assert.Null(state.LastObservation);
    }

    [Fact]
    public void HealthyEvidence_RequiresConsecutiveThresholdThenProposesAdditiveIncrease()
    {
        var definition = ControlRegulatorFixture.Definition();
        var initial = ControlRegulatorFixture.InitialState(definition);
        var firstObservation = ControlRegulatorFixture.Observation(initial, "healthy-1", value: 6_000);

        var first = AimdControlReferenceRegulator.Evaluate(
            definition,
            initial,
            firstObservation,
            firstObservation.ObservedAtUtc);

        Assert.Equal(ControlDecisionDisposition.Held, first.Disposition);
        Assert.Equal(ControlPressureClassification.Healthy, first.State.LastClassification);
        Assert.Equal(1, first.State.HealthyObservationCount);
        Assert.Equal(6, Concurrency(first.State));

        var secondObservation = ControlRegulatorFixture.Observation(
            first.State,
            "healthy-2",
            value: 5_999);
        var second = AimdControlReferenceRegulator.Evaluate(
            definition,
            first.State,
            secondObservation,
            secondObservation.ObservedAtUtc);

        Assert.Equal(ControlDecisionDisposition.Recommended, second.Disposition);
        Assert.Equal(ControlRecommendationDirection.Increase, second.Recommendation?.Direction);
        Assert.Equal(6, Concurrency(second.State));
        Assert.Equal(9, Concurrency(second.Recommendation!.ProposedOperatingPoint));
        Assert.Same(second.Recommendation, second.State.PendingRecommendation);
        Assert.Equal(2, second.State.HealthyObservationCount);
        Assert.Equal(2, second.Recommendation.AuthorizingHealthyObservationCount);
    }

    [Fact]
    public void CongestionEvidence_ImmediatelyProposesMultiplicativeDecrease()
    {
        var definition = ControlRegulatorFixture.Definition();
        var state = ControlRegulatorFixture.InitialState(definition);
        var observation = ControlRegulatorFixture.Observation(state, "congested", value: 8_000);

        var decision = AimdControlReferenceRegulator.Evaluate(
            definition,
            state,
            observation,
            observation.ObservedAtUtc);

        Assert.Equal(ControlDecisionDisposition.Recommended, decision.Disposition);
        Assert.Equal(ControlPressureClassification.Congested, decision.State.LastClassification);
        Assert.Equal(ControlRecommendationDirection.Decrease, decision.Recommendation?.Direction);
        Assert.Equal(3, Concurrency(decision.Recommendation!.ProposedOperatingPoint));
        Assert.Equal(6, Concurrency(decision.State));
    }

    [Fact]
    public void LowerIsCongestedThroughputObjective_UsesExplicitPolarity()
    {
        var definition = ControlRegulatorFixture.Definition(
            objectiveMetric: ControlMetricKind.ItemThroughput,
            objectiveDirection: ControlObjectiveDirection.LowerIsCongested,
            recoveryBoundary: 100,
            congestionBoundary: 50);
        var state = ControlRegulatorFixture.InitialState(definition);
        var observation = ControlRegulatorFixture.Observation(
            state,
            "low-throughput",
            value: 40,
            metric: ControlMetricKind.ItemThroughput);

        var decision = AimdControlReferenceRegulator.Evaluate(
            definition,
            state,
            observation,
            observation.ObservedAtUtc);

        Assert.Equal(ControlPressureClassification.Congested, decision.State.LastClassification);
        Assert.Equal(ControlRecommendationDirection.Decrease, decision.Recommendation?.Direction);
    }

    [Fact]
    public void HysteresisBoundaries_AreInclusiveAndBandEvidenceBreaksHealthyStreaks()
    {
        var definition = ControlRegulatorFixture.Definition(healthyObservationCount: 2);
        var state = ControlRegulatorFixture.InitialState(definition);

        var recoveryBoundary = Evaluate(definition, state, "recovery-boundary", value: 6_000);
        Assert.Equal(ControlPressureClassification.Healthy, recoveryBoundary.State.LastClassification);
        Assert.Equal(1, recoveryBoundary.State.HealthyObservationCount);

        var band = Evaluate(definition, recoveryBoundary.State, "hysteresis", value: 6_001);
        Assert.Equal(ControlDecisionDisposition.Held, band.Disposition);
        Assert.Equal(ControlPressureClassification.Hysteresis, band.State.LastClassification);
        Assert.Equal(0, band.State.HealthyObservationCount);

        var healthyAgain = Evaluate(definition, band.State, "healthy-again", value: 5_000);
        Assert.Equal(ControlDecisionDisposition.Held, healthyAgain.Disposition);
        Assert.Equal(1, healthyAgain.State.HealthyObservationCount);

        var congestionBoundary = Evaluate(definition, state, "congestion-boundary", value: 8_000);
        Assert.Equal(ControlDecisionDisposition.Recommended, congestionBoundary.Disposition);
        Assert.Equal(ControlPressureClassification.Congested, congestionBoundary.State.LastClassification);
    }

    [Fact]
    public void AlternatingHealthyAndHysteresisEvidence_CannotCauseOscillatingRecommendations()
    {
        var definition = ControlRegulatorFixture.Definition(healthyObservationCount: 2);
        var state = ControlRegulatorFixture.InitialState(definition);

        for (var index = 0; index < 20; index++)
        {
            var value = index % 2 == 0 ? 6_000 : 7_000;
            var decision = Evaluate(definition, state, $"oscillation-{index}", value);

            Assert.Equal(ControlDecisionDisposition.Held, decision.Disposition);
            Assert.Null(decision.Recommendation);
            Assert.InRange(decision.State.HealthyObservationCount, 0, 1);
            state = decision.State;
        }

        Assert.Equal(6, Concurrency(state));
    }

    [Fact]
    public void CooldownAndMinimumDwell_BlockRecoveryUntilBothBoundariesAreSatisfied()
    {
        var definition = ControlRegulatorFixture.Definition(
            healthyObservationCount: 1,
            recoveryCooldownMilliseconds: 10_000,
            minimumDwellMilliseconds: 5_000);
        var initial = ControlRegulatorFixture.InitialState(definition);
        var decreased = RecommendAndApplyDecrease(definition, initial);
        var appliedAt = decreased.LastActuation!.AppliedAtUtc;

        var duringCooldown = Evaluate(
            definition,
            decreased,
            "healthy-during-cooldown",
            value: 5_000,
            observedAtUtc: appliedAt.AddMilliseconds(9_999));
        Assert.Equal(ControlDecisionDisposition.Held, duringCooldown.Disposition);
        Assert.Equal(0, duringCooldown.State.HealthyObservationCount);

        var atCooldownBoundary = Evaluate(
            definition,
            duringCooldown.State,
            "healthy-at-cooldown",
            value: 5_000,
            observedAtUtc: appliedAt.AddMilliseconds(10_001));
        Assert.Equal(ControlDecisionDisposition.Recommended, atCooldownBoundary.Disposition);
        Assert.Equal(ControlRecommendationDirection.Increase, atCooldownBoundary.Recommendation?.Direction);

        var secondInitial = ControlRegulatorFixture.InitialState(definition, "generation-2");
        var secondDecrease = RecommendAndApplyDecrease(definition, secondInitial, fence: 2);
        var tooSoon = Evaluate(
            definition,
            secondDecrease,
            "congested-before-dwell",
            value: 9_000,
            observedAtUtc: secondDecrease.LastActuation!.AppliedAtUtc.AddMilliseconds(4_999));
        Assert.Equal(ControlDecisionDisposition.Held, tooSoon.Disposition);

        var atDwell = Evaluate(
            definition,
            tooSoon.State,
            "congested-at-dwell",
            value: 9_000,
            observedAtUtc: secondDecrease.LastActuation.AppliedAtUtc.AddMilliseconds(5_001));
        Assert.Equal(ControlDecisionDisposition.Recommended, atDwell.Disposition);
        Assert.Equal(ControlRecommendationDirection.Decrease, atDwell.Recommendation?.Direction);
    }

    [Fact]
    public void ArithmeticSaturatesAtPortableBoundsWithoutOverflow()
    {
        var maximum = ControlQuantity.MaximumPortableValue;
        var increaseDefinition = ControlRegulatorFixture.Definition(
            initial: maximum - 1,
            minimum: 1,
            maximum: maximum,
            additiveIncrease: maximum,
            healthyObservationCount: 1);
        var increaseState = ControlRegulatorFixture.InitialState(increaseDefinition);

        var increase = Evaluate(increaseDefinition, increaseState, "increase", value: 5_000);

        Assert.Equal(maximum, Concurrency(increase.Recommendation!.ProposedOperatingPoint));

        var decreaseDefinition = ControlRegulatorFixture.Definition(
            initial: maximum,
            minimum: 1,
            maximum: maximum,
            healthyObservationCount: 1);
        var decreaseState = ControlRegulatorFixture.InitialState(decreaseDefinition);

        var decrease = Evaluate(decreaseDefinition, decreaseState, "decrease", value: 9_000);

        Assert.Equal(maximum / 2, Concurrency(decrease.Recommendation!.ProposedOperatingPoint));
    }

    [Theory]
    [InlineData("missing", ControlDiagnosticCodes.MeasurementMissing)]
    [InlineData("unavailable", ControlDiagnosticCodes.MeasurementUnavailable)]
    [InlineData("insufficient", ControlDiagnosticCodes.MeasurementInsufficient)]
    [InlineData("stale", ControlDiagnosticCodes.ObservationTimeInvalid)]
    [InlineData("future", ControlDiagnosticCodes.ObservationTimeInvalid)]
    [InlineData("wrong-epoch", ControlDiagnosticCodes.ObservationFenceMismatch)]
    [InlineData("wrong-revision", ControlDiagnosticCodes.ObservationFenceMismatch)]
    public void InvalidEvidence_IsRejectedWithoutStateMutation(string scenario, string expectedCode)
    {
        var definition = ControlRegulatorFixture.Definition();
        var state = ControlRegulatorFixture.InitialState(definition);
        var evaluationTime = state.UpdatedAtUtc.AddMinutes(2);
        var observation = scenario switch
        {
            "missing" => ControlRegulatorFixture.Observation(
                state,
                scenario,
                observedAtUtc: evaluationTime,
                includeObjective: false),
            "unavailable" => ControlRegulatorFixture.Observation(
                state,
                scenario,
                observedAtUtc: evaluationTime,
                availability: ControlMeasurementAvailability.Unavailable),
            "insufficient" => ControlRegulatorFixture.Observation(
                state,
                scenario,
                observedAtUtc: evaluationTime,
                sampleCount: 2),
            "stale" => ControlRegulatorFixture.Observation(
                state,
                scenario,
                observedAtUtc: evaluationTime.AddMilliseconds(-60_001)),
            "future" => ControlRegulatorFixture.Observation(
                state,
                scenario,
                observedAtUtc: evaluationTime.AddMilliseconds(1)),
            "wrong-epoch" => ControlRegulatorFixture.Observation(
                state,
                scenario,
                observedAtUtc: evaluationTime,
                epoch: new ControlEpochId("generation-other")),
            "wrong-revision" => ControlRegulatorFixture.Observation(
                state,
                scenario,
                observedAtUtc: evaluationTime,
                expectedRevision: new ControlRevision("99")),
            _ => throw new InvalidOperationException($"Unknown test scenario '{scenario}'.")
        };

        var decision = AimdControlReferenceRegulator.Evaluate(
            definition,
            state,
            observation,
            evaluationTime);

        Assert.Equal(ControlDecisionDisposition.Rejected, decision.Disposition);
        Assert.Same(state, decision.State);
        Assert.Null(decision.Recommendation);
        Assert.Contains(decision.Diagnostics, diagnostic => diagnostic.Code == expectedCode);
    }

    [Fact]
    public void FreshEnvelopeCannotHideAStaleMeasurementWindow()
    {
        var definition = ControlRegulatorFixture.Definition();
        var state = ControlRegulatorFixture.InitialState(definition);
        var evaluatedAtUtc = state.UpdatedAtUtc.AddMinutes(2);
        var observation = ControlRegulatorFixture.Observation(
            state,
            "stale-window",
            observedAtUtc: evaluatedAtUtc,
            windowEndedAtUtc: evaluatedAtUtc.AddMinutes(-2));

        var decision = AimdControlReferenceRegulator.Evaluate(
            definition,
            state,
            observation,
            evaluatedAtUtc);

        Assert.Equal(ControlDecisionDisposition.Rejected, decision.Disposition);
        Assert.Same(state, decision.State);
        Assert.Contains(decision.Diagnostics, diagnostic =>
            diagnostic.Code == ControlDiagnosticCodes.ObservationTimeInvalid);
    }

    [Fact]
    public void ExactObservationReplay_ReturnsMaterializedOutcomeWithoutMutation()
    {
        var definition = ControlRegulatorFixture.Definition(healthyObservationCount: 1);
        var initial = ControlRegulatorFixture.InitialState(definition);
        var observation = ControlRegulatorFixture.Observation(initial, "replay", value: 5_000);
        var first = AimdControlReferenceRegulator.Evaluate(
            definition,
            initial,
            observation,
            observation.ObservedAtUtc);

        var replay = AimdControlReferenceRegulator.Evaluate(
            definition,
            first.State,
            observation,
            observation.ObservedAtUtc.AddSeconds(1));

        Assert.Equal(ControlDecisionDisposition.Replayed, replay.Disposition);
        Assert.Same(first.State, replay.State);
        Assert.Same(first.State.PendingRecommendation, replay.Recommendation);
        Assert.Empty(replay.Diagnostics);
    }

    [Fact]
    public void ConflictingObservationIdentity_IsRejectedWithoutMutation()
    {
        var definition = ControlRegulatorFixture.Definition();
        var initial = ControlRegulatorFixture.InitialState(definition);
        var first = Evaluate(definition, initial, "same-id", value: 5_000);
        var conflict = ControlRegulatorFixture.Observation(
            first.State,
            "same-id",
            value: 7_000,
            observedAtUtc: first.State.UpdatedAtUtc.AddSeconds(1),
            expectedRevision: initial.Revision);

        var result = AimdControlReferenceRegulator.Evaluate(
            definition,
            first.State,
            conflict,
            conflict.ObservedAtUtc);

        Assert.Equal(ControlDecisionDisposition.Rejected, result.Disposition);
        Assert.Same(first.State, result.State);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == ControlDiagnosticCodes.ObservationConflict);
    }

    [Fact]
    public void RevisionScopedObservationIdentity_CanBeReusedByALaterEvaluation()
    {
        var definition = ControlRegulatorFixture.Definition();
        var initial = ControlRegulatorFixture.InitialState(definition);
        var first = Evaluate(definition, initial, "local-sample", value: 7_000);
        var secondObservation = ControlRegulatorFixture.Observation(
            first.State,
            "local-sample",
            value: 7_001);

        var second = AimdControlReferenceRegulator.Evaluate(
            definition,
            first.State,
            secondObservation,
            secondObservation.ObservedAtUtc);

        Assert.Equal(ControlDecisionDisposition.Held, second.Disposition);
        Assert.Same(secondObservation, second.State.LastObservation);
        Assert.Empty(second.Diagnostics);
    }

    [Fact]
    public void ObservationEvidence_IsFencedToExactDefinitionContent()
    {
        var priorDefinition = ControlRegulatorFixture.Definition(additiveIncrease: 2);
        var currentDefinition = ControlRegulatorFixture.Definition(additiveIncrease: 3);
        var state = ControlRegulatorFixture.InitialState(currentDefinition);
        var observation = ControlRegulatorFixture.Observation(
            state,
            "prior-definition-evidence",
            definitionFingerprint: priorDefinition.Fingerprint);

        var result = AimdControlReferenceRegulator.Evaluate(
            currentDefinition,
            state,
            observation,
            observation.ObservedAtUtc);

        Assert.Equal(ControlDecisionDisposition.Rejected, result.Disposition);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == ControlDiagnosticCodes.DefinitionFingerprintMismatch);
    }

    [Fact]
    public void StateValidation_RejectsUnreachableRevisionAndContradictoryClassification()
    {
        var definition = ControlRegulatorFixture.Definition();
        var initial = ControlRegulatorFixture.InitialState(definition);
        var unreachable = new ControlLoopState(
            ControlLoopDefinition.CurrentSchemaVersion,
            initial.LoopId,
            initial.Target,
            initial.Epoch,
            new("2"),
            definition.Fingerprint,
            initial.OperatingPoint,
            healthyObservationCount: 0,
            initial.CreatedAtUtc,
            initial.UpdatedAtUtc);
        var observedAtUtc = initial.UpdatedAtUtc.AddSeconds(1);
        var congested = ControlRegulatorFixture.Observation(
            initial,
            "contradictory-classification",
            value: 9_000,
            observedAtUtc);
        var contradictory = new ControlLoopState(
            ControlLoopDefinition.CurrentSchemaVersion,
            initial.LoopId,
            initial.Target,
            initial.Epoch,
            new("2"),
            definition.Fingerprint,
            initial.OperatingPoint,
            healthyObservationCount: 1,
            initial.CreatedAtUtc,
            updatedAtUtc: observedAtUtc,
            lastEvaluatedAtUtc: observedAtUtc,
            lastClassification: ControlPressureClassification.Healthy,
            lastObservation: congested);

        var unreachableValidation = AimdControlReferenceRegulator.ValidateState(definition, unreachable);
        var contradictoryValidation = AimdControlReferenceRegulator.ValidateState(definition, contradictory);

        Assert.Contains(unreachableValidation.Diagnostics, diagnostic =>
            diagnostic.Code == ControlDiagnosticCodes.StateInvalid);
        Assert.Contains(contradictoryValidation.Diagnostics, diagnostic =>
            diagnostic.Code == ControlDiagnosticCodes.StateInvalid);
    }

    [Fact]
    public void StateValidation_RequiresOperatingPointProvenanceFromDefinitionOrActuation()
    {
        var definition = ControlRegulatorFixture.Definition(initial: 6);
        var initial = ControlRegulatorFixture.InitialState(definition);
        var forged = new ControlLoopState(
            initial.SchemaVersion,
            initial.LoopId,
            initial.Target,
            initial.Epoch,
            initial.Revision,
            initial.DefinitionFingerprint,
            ControlRegulatorFixture.Point(concurrency: 7),
            healthyObservationCount: 0,
            initial.CreatedAtUtc,
            initial.UpdatedAtUtc);

        var validation = AimdControlReferenceRegulator.ValidateState(definition, forged);

        Assert.Contains(validation.Diagnostics, diagnostic =>
            diagnostic.Code == ControlDiagnosticCodes.StateInvalid
            && diagnostic.Location == "/state/operatingPoint");
        Assert.Throws<JsonException>(() => ControlJsonSerializer.DeserializeState(
            ControlJsonSerializer.Serialize(forged),
            definition));
    }

    [Fact]
    public void StateValidation_RejectsObservationThatWasStaleWhenAccepted()
    {
        var definition = ControlRegulatorFixture.Definition(
            healthyObservationCount: 2,
            maximumObservationAgeMilliseconds: 1_000);
        var initial = ControlRegulatorFixture.InitialState(definition);
        var evaluatedAtUtc = initial.UpdatedAtUtc.AddSeconds(10);
        var observation = ControlRegulatorFixture.Observation(
            initial,
            "forged-stale-accepted-observation",
            value: 5_000,
            observedAtUtc: evaluatedAtUtc,
            windowEndedAtUtc: evaluatedAtUtc.AddMilliseconds(-1_001));
        var forged = new ControlLoopState(
            initial.SchemaVersion,
            initial.LoopId,
            initial.Target,
            initial.Epoch,
            new("2"),
            initial.DefinitionFingerprint,
            initial.OperatingPoint,
            healthyObservationCount: 1,
            initial.CreatedAtUtc,
            updatedAtUtc: evaluatedAtUtc,
            lastEvaluatedAtUtc: evaluatedAtUtc,
            lastClassification: ControlPressureClassification.Healthy,
            lastObservation: observation);

        var validation = AimdControlReferenceRegulator.ValidateState(definition, forged);

        Assert.Contains(validation.Diagnostics, diagnostic =>
            diagnostic.Code == ControlDiagnosticCodes.ObservationTimeInvalid);
        Assert.Throws<JsonException>(() => ControlJsonSerializer.DeserializeState(
            ControlJsonSerializer.Serialize(forged),
            definition));
    }

    [Fact]
    public void StateValidation_RejectsHealthyStreakDuringCooldownOrBeyondPostActuationRevisions()
    {
        var definition = ControlRegulatorFixture.Definition(
            healthyObservationCount: 3,
            recoveryCooldownMilliseconds: 10_000,
            minimumDwellMilliseconds: 0);
        var initial = ControlRegulatorFixture.InitialState(definition);
        var applied = RecommendAndApplyDecrease(definition, initial);
        var cooldown = Assert.IsType<DateTimeOffset>(applied.CooldownUntilUtc);
        var insideCooldownAtUtc = applied.UpdatedAtUtc.AddSeconds(1);
        var insideCooldownObservation = ControlRegulatorFixture.Observation(
            applied,
            "forged-cooldown-streak",
            value: 5_000,
            observedAtUtc: insideCooldownAtUtc);
        var insideCooldown = StateAfterForgedHealthyEvaluation(
            applied,
            insideCooldownObservation,
            healthyObservationCount: 1);
        var afterCooldownAtUtc = cooldown.AddMilliseconds(2);
        var afterCooldownObservation = ControlRegulatorFixture.Observation(
            applied,
            "forged-post-actuation-streak",
            value: 5_000,
            observedAtUtc: afterCooldownAtUtc);
        var beyondReachableRevisions = StateAfterForgedHealthyEvaluation(
            applied,
            afterCooldownObservation,
            healthyObservationCount: 2);

        var insideCooldownValidation = AimdControlReferenceRegulator.ValidateState(definition, insideCooldown);
        var revisionValidation = AimdControlReferenceRegulator.ValidateState(definition, beyondReachableRevisions);

        Assert.Contains(insideCooldownValidation.Diagnostics, diagnostic =>
            diagnostic.Code == ControlDiagnosticCodes.StateInvalid
            && diagnostic.Location == "/state/healthyObservationCount");
        Assert.Contains(revisionValidation.Diagnostics, diagnostic =>
            diagnostic.Code == ControlDiagnosticCodes.StateInvalid
            && diagnostic.Location == "/state/healthyObservationCount");
        Assert.Throws<JsonException>(() => ControlJsonSerializer.DeserializeState(
            ControlJsonSerializer.Serialize(insideCooldown),
            definition));
        Assert.Throws<JsonException>(() => ControlJsonSerializer.DeserializeState(
            ControlJsonSerializer.Serialize(beyondReachableRevisions),
            definition));
    }

    [Fact]
    public void ActuationValidation_RejectsAuthorizationCountUnreachableAtItsRevision()
    {
        var definition = ControlRegulatorFixture.Definition(
            healthyObservationCount: 1,
            minimumDwellMilliseconds: 0);
        var initial = ControlRegulatorFixture.InitialState(definition);
        var observation = ControlRegulatorFixture.Observation(
            initial,
            "forged-actuation-streak",
            value: 5_000);
        var decision = AimdControlReferenceRegulator.Evaluate(
            definition,
            initial,
            observation,
            observation.ObservedAtUtc);
        var recommendation = Assert.IsType<ControlRecommendation>(decision.Recommendation);
        var point = ControlRegulatorFixture.ApplicationPoint(
            decision.State,
            "forged-actuation-streak-cut",
            fence: 1,
            decision.State.UpdatedAtUtc.AddMilliseconds(1));
        var applied = AimdControlReferenceRegulator.Apply(
            definition,
            decision.State,
            point,
            point.ObservedAtUtc);
        var actuation = Assert.IsType<ControlActuation>(applied.Actuation);
        var forgedRecommendation = new ControlRecommendation(
            recommendation.Id,
            recommendation.LoopId,
            recommendation.DefinitionFingerprint,
            recommendation.Target,
            recommendation.Epoch,
            recommendation.ExpectedRevision,
            recommendation.ObservationId,
            recommendation.Actuator,
            recommendation.Direction,
            authorizingHealthyObservationCount: 2,
            recommendation.PriorOperatingPoint,
            recommendation.ProposedOperatingPoint,
            recommendation.IssuedAtUtc);
        var forgedActuation = new ControlActuation(
            actuation.Id,
            forgedRecommendation,
            actuation.Observation,
            actuation.ApplicationPoint,
            actuation.PriorRevision,
            actuation.Revision,
            actuation.AppliedAtUtc);

        var validation = AimdControlReferenceRegulator.ValidateActuation(definition, forgedActuation);

        Assert.Contains(validation.Diagnostics, diagnostic =>
            diagnostic.Code == ControlDiagnosticCodes.RecommendationInvalid);
        Assert.Throws<JsonException>(() => ControlJsonSerializer.DeserializeActuation(
            ControlJsonSerializer.Serialize(forgedActuation),
            definition));
    }

    [Fact]
    public void ActuationValidation_RejectsFirstReceiptFromAnInventedPriorOperatingPoint()
    {
        var definition = ControlRegulatorFixture.Definition(
            initial: 6,
            maximum: 12,
            additiveIncrease: 3,
            healthyObservationCount: 1,
            minimumDwellMilliseconds: 0);
        var initial = ControlRegulatorFixture.InitialState(definition);
        var observation = ControlRegulatorFixture.Observation(
            initial,
            "forged-first-prior-point",
            value: 5_000);
        var decision = AimdControlReferenceRegulator.Evaluate(
            definition,
            initial,
            observation,
            observation.ObservedAtUtc);
        var recommendation = Assert.IsType<ControlRecommendation>(decision.Recommendation);
        var point = ControlRegulatorFixture.ApplicationPoint(
            decision.State,
            "forged-first-prior-point-cut",
            fence: 1,
            decision.State.UpdatedAtUtc.AddMilliseconds(1));
        var applied = AimdControlReferenceRegulator.Apply(
            definition,
            decision.State,
            point,
            point.ObservedAtUtc);
        var actuation = Assert.IsType<ControlActuation>(applied.Actuation);
        var forgedRecommendation = new ControlRecommendation(
            recommendation.Id,
            recommendation.LoopId,
            recommendation.DefinitionFingerprint,
            recommendation.Target,
            recommendation.Epoch,
            recommendation.ExpectedRevision,
            recommendation.ObservationId,
            recommendation.Actuator,
            recommendation.Direction,
            recommendation.AuthorizingHealthyObservationCount,
            ControlRegulatorFixture.Point(concurrency: 7),
            ControlRegulatorFixture.Point(concurrency: 10),
            recommendation.IssuedAtUtc,
            priorActuationId: null,
            priorActuationRevision: null);
        var forgedActuation = new ControlActuation(
            actuation.Id,
            forgedRecommendation,
            actuation.Observation,
            actuation.ApplicationPoint,
            actuation.PriorRevision,
            actuation.Revision,
            actuation.AppliedAtUtc);

        var validation = AimdControlReferenceRegulator.ValidateActuation(definition, forgedActuation);

        Assert.Contains(validation.Diagnostics, diagnostic =>
            diagnostic.Code == ControlDiagnosticCodes.RecommendationInvalid);
        Assert.Throws<JsonException>(() => ControlJsonSerializer.DeserializeActuation(
            ControlJsonSerializer.Serialize(forgedActuation),
            definition));
    }

    [Fact]
    public void ActuationValidation_RejectsPostActuationStreakBeyondItsExplicitPriorFence()
    {
        var definition = ControlRegulatorFixture.Definition(
            healthyObservationCount: 1,
            recoveryCooldownMilliseconds: 0,
            minimumDwellMilliseconds: 0);
        var initial = ControlRegulatorFixture.InitialState(definition);
        var firstApplied = RecommendAndApplyDecrease(definition, initial);
        var priorActuation = Assert.IsType<ControlActuation>(firstApplied.LastActuation);
        var healthyObservation = ControlRegulatorFixture.Observation(
            firstApplied,
            "post-actuation-healthy",
            value: 5_000);
        var decision = AimdControlReferenceRegulator.Evaluate(
            definition,
            firstApplied,
            healthyObservation,
            healthyObservation.ObservedAtUtc);
        var recommendation = Assert.IsType<ControlRecommendation>(decision.Recommendation);
        var point = ControlRegulatorFixture.ApplicationPoint(
            decision.State,
            "post-actuation-healthy-cut",
            fence: 2,
            decision.State.UpdatedAtUtc.AddMilliseconds(1));
        var applied = AimdControlReferenceRegulator.Apply(
            definition,
            decision.State,
            point,
            point.ObservedAtUtc);
        var actuation = Assert.IsType<ControlActuation>(applied.Actuation);
        var forgedRecommendation = new ControlRecommendation(
            recommendation.Id,
            recommendation.LoopId,
            recommendation.DefinitionFingerprint,
            recommendation.Target,
            recommendation.Epoch,
            recommendation.ExpectedRevision,
            recommendation.ObservationId,
            recommendation.Actuator,
            recommendation.Direction,
            authorizingHealthyObservationCount: 2,
            recommendation.PriorOperatingPoint,
            recommendation.ProposedOperatingPoint,
            recommendation.IssuedAtUtc,
            recommendation.PriorActuationId,
            recommendation.PriorActuationRevision);
        var forgedActuation = new ControlActuation(
            actuation.Id,
            forgedRecommendation,
            actuation.Observation,
            actuation.ApplicationPoint,
            actuation.PriorRevision,
            actuation.Revision,
            actuation.AppliedAtUtc);

        var validation = AimdControlReferenceRegulator.ValidateActuation(definition, forgedActuation);

        Assert.Equal(priorActuation.Id, recommendation.PriorActuationId);
        Assert.Equal(priorActuation.Revision, recommendation.PriorActuationRevision);
        Assert.Contains(validation.Diagnostics, diagnostic =>
            diagnostic.Code == ControlDiagnosticCodes.RecommendationInvalid);
        Assert.Throws<JsonException>(() => ControlJsonSerializer.DeserializeActuation(
            ControlJsonSerializer.Serialize(forgedActuation),
            definition));
    }

    [Fact]
    public void PendingIncrease_RetainsAndValidatesItsAuthorizingHealthyStreak()
    {
        var definition = ControlRegulatorFixture.Definition(healthyObservationCount: 3);
        var state = ControlRegulatorFixture.InitialState(definition);
        state = Evaluate(definition, state, "healthy-proof-1", value: 5_000).State;
        state = Evaluate(definition, state, "healthy-proof-2", value: 5_000).State;
        var recommended = Evaluate(definition, state, "healthy-proof-3", value: 5_000).State;
        var forged = new ControlLoopState(
            recommended.SchemaVersion,
            recommended.LoopId,
            recommended.Target,
            recommended.Epoch,
            recommended.Revision,
            recommended.DefinitionFingerprint,
            recommended.OperatingPoint,
            healthyObservationCount: 1,
            recommended.CreatedAtUtc,
            recommended.UpdatedAtUtc,
            recommended.LastEvaluatedAtUtc,
            recommended.LastClassification,
            recommended.CooldownUntilUtc,
            recommended.LastObservation,
            recommended.PendingRecommendation,
            recommended.LastActuation,
            recommended.LastApplicationFence);

        var validation = AimdControlReferenceRegulator.ValidateState(definition, forged);

        Assert.Equal(3, recommended.HealthyObservationCount);
        Assert.Equal(3, recommended.PendingRecommendation?.AuthorizingHealthyObservationCount);
        Assert.Contains(validation.Diagnostics, diagnostic =>
            diagnostic.Code == ControlDiagnosticCodes.RecommendationInvalid);
    }

    [Fact]
    public void PendingIncrease_IsSupersededByNewerCongestionEvidence()
    {
        var definition = ControlRegulatorFixture.Definition(healthyObservationCount: 1);
        var initial = ControlRegulatorFixture.InitialState(definition);
        var recommended = Evaluate(definition, initial, "first", value: 5_000);
        var pending = recommended.State.PendingRecommendation;
        var nextObservation = ControlRegulatorFixture.Observation(
            recommended.State,
            "second",
            value: 9_000);

        var result = AimdControlReferenceRegulator.Evaluate(
            definition,
            recommended.State,
            nextObservation,
            nextObservation.ObservedAtUtc);

        Assert.Equal(ControlDecisionDisposition.Recommended, result.Disposition);
        Assert.NotSame(recommended.State, result.State);
        Assert.NotSame(pending, result.State.PendingRecommendation);
        Assert.Equal(ControlRecommendationDirection.Decrease, result.Recommendation?.Direction);
        Assert.Equal(nextObservation.Id, result.Recommendation?.ObservationId);
    }

    [Fact]
    public void PendingIncrease_RejectsNewHealthyEvidenceWithoutReplacingIt()
    {
        var definition = ControlRegulatorFixture.Definition(healthyObservationCount: 1);
        var initial = ControlRegulatorFixture.InitialState(definition);
        var recommended = Evaluate(definition, initial, "first-healthy", value: 5_000);
        var pending = recommended.State.PendingRecommendation;
        var nextObservation = ControlRegulatorFixture.Observation(
            recommended.State,
            "second-healthy",
            value: 5_000);

        var result = AimdControlReferenceRegulator.Evaluate(
            definition,
            recommended.State,
            nextObservation,
            nextObservation.ObservedAtUtc);

        Assert.Equal(ControlDecisionDisposition.Rejected, result.Disposition);
        Assert.Same(recommended.State, result.State);
        Assert.Same(pending, result.State.PendingRecommendation);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == ControlDiagnosticCodes.RecommendationPending);
    }

    [Fact]
    public void EpochsAreIndependentAndCannotConsumeEachOthersEvidence()
    {
        var definition = ControlRegulatorFixture.Definition();
        var firstEpoch = ControlRegulatorFixture.InitialState(definition, "generation-1");
        var secondEpoch = ControlRegulatorFixture.InitialState(definition, "generation-2");
        var observation = ControlRegulatorFixture.Observation(firstEpoch, "shared-id", value: 5_000);

        var accepted = AimdControlReferenceRegulator.Evaluate(
            definition,
            firstEpoch,
            observation,
            observation.ObservedAtUtc);
        var rejected = AimdControlReferenceRegulator.Evaluate(
            definition,
            secondEpoch,
            observation,
            observation.ObservedAtUtc);

        Assert.Equal(ControlDecisionDisposition.Held, accepted.Disposition);
        Assert.Equal(ControlDecisionDisposition.Rejected, rejected.Disposition);
        Assert.Same(secondEpoch, rejected.State);
        Assert.Contains(rejected.Diagnostics, diagnostic =>
            diagnostic.Code == ControlDiagnosticCodes.ObservationFenceMismatch);
    }

    [Fact]
    public void ExhaustedRevisionRejectsEvaluationAndRequiresANewEpoch()
    {
        var definition = ControlRegulatorFixture.Definition();
        var initial = ControlRegulatorFixture.InitialState(definition);
        var priorRevision = new ControlRevision(
            (long.MaxValue - 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
        var observedAtUtc = initial.UpdatedAtUtc.AddSeconds(1);
        var retainedObservation = ControlRegulatorFixture.Observation(
            initial,
            "revision-max-retained",
            value: 7_000,
            observedAtUtc,
            expectedRevision: priorRevision);
        var exhausted = new ControlLoopState(
            ControlLoopDefinition.CurrentSchemaVersion,
            initial.LoopId,
            initial.Target,
            initial.Epoch,
            new ControlRevision(long.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            definition.Fingerprint,
            initial.OperatingPoint,
            healthyObservationCount: 0,
            initial.CreatedAtUtc,
            updatedAtUtc: observedAtUtc,
            lastEvaluatedAtUtc: observedAtUtc,
            lastClassification: ControlPressureClassification.Hysteresis,
            lastObservation: retainedObservation);
        var observation = ControlRegulatorFixture.Observation(exhausted, "revision-exhausted");

        var replay = AimdControlReferenceRegulator.Evaluate(
            definition,
            exhausted,
            retainedObservation,
            observedAtUtc.AddMilliseconds(1));

        var decision = AimdControlReferenceRegulator.Evaluate(
            definition,
            exhausted,
            observation,
            observation.ObservedAtUtc);

        Assert.Equal(ControlDecisionDisposition.Replayed, replay.Disposition);
        Assert.Same(exhausted, replay.State);
        Assert.Equal(ControlDecisionDisposition.Rejected, decision.Disposition);
        Assert.Same(exhausted, decision.State);
        Assert.Contains(decision.Diagnostics, diagnostic =>
            diagnostic.Code == ControlDiagnosticCodes.RevisionExhausted);
    }

    static ControlDecision Evaluate(
        ControlLoopDefinition definition,
        ControlLoopState state,
        string id,
        long value,
        DateTimeOffset? observedAtUtc = null)
    {
        var observation = ControlRegulatorFixture.Observation(
            state,
            id,
            value,
            observedAtUtc);
        return AimdControlReferenceRegulator.Evaluate(
            definition,
            state,
            observation,
            observation.ObservedAtUtc);
    }

    static ControlLoopState RecommendAndApplyDecrease(
        ControlLoopDefinition definition,
        ControlLoopState state,
        long fence = 1)
    {
        var recommendation = Evaluate(definition, state, $"decrease-{fence}", value: 9_000);
        var safePointTime = recommendation.State.UpdatedAtUtc.AddMilliseconds(1);
        var point = ControlRegulatorFixture.ApplicationPoint(
            recommendation.State,
            $"safe-point-{fence}",
            fence,
            safePointTime);
        var applied = AimdControlReferenceRegulator.Apply(
            definition,
            recommendation.State,
            point,
            safePointTime);
        Assert.Equal(ControlActuationDisposition.Applied, applied.Disposition);
        return applied.State;
    }

    static ControlLoopState StateAfterForgedHealthyEvaluation(
        ControlLoopState state,
        ControlObservation observation,
        long healthyObservationCount) =>
        new(
            state.SchemaVersion,
            state.LoopId,
            state.Target,
            state.Epoch,
            new ControlRevision(
                (long.Parse(state.Revision.Value, System.Globalization.CultureInfo.InvariantCulture) + 1)
                    .ToString(System.Globalization.CultureInfo.InvariantCulture)),
            state.DefinitionFingerprint,
            state.OperatingPoint,
            healthyObservationCount,
            state.CreatedAtUtc,
            updatedAtUtc: observation.ObservedAtUtc,
            lastEvaluatedAtUtc: observation.ObservedAtUtc,
            lastClassification: ControlPressureClassification.Healthy,
            cooldownUntilUtc: state.CooldownUntilUtc,
            lastObservation: observation,
            lastActuation: state.LastActuation,
            lastApplicationFence: state.LastApplicationFence);

    static long Concurrency(ControlLoopState state) => Concurrency(state.OperatingPoint);

    static long Concurrency(ControlOperatingPoint point) =>
        point.Get(ControlActuatorKind.Concurrency).Quantity.Value;
}
