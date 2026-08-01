using System.Collections.Immutable;
using Cohesive.Control;

namespace Cohesive.Tests.Storage.Control;

public sealed class ControlSafePointActuationTests
{
    [Fact]
    public void ExactLaterSafePoint_AppliesCompleteRecommendationAtomically()
    {
        var definition = ControlRegulatorFixture.Definition(healthyObservationCount: 1);
        var pending = Recommend(definition, value: 5_000);
        var point = ControlRegulatorFixture.ApplicationPoint(
            pending,
            "safe-point-1",
            fence: 41,
            pending.UpdatedAtUtc.AddMilliseconds(1));

        var result = AimdControlReferenceRegulator.Apply(
            definition,
            pending,
            point,
            point.ObservedAtUtc);

        Assert.Equal(ControlActuationDisposition.Applied, result.Disposition);
        Assert.Equal(pending.Revision, result.Actuation?.PriorRevision);
        Assert.Equal(result.State.Revision, result.Actuation?.Revision);
        Assert.Equal(pending.PendingRecommendation, result.Actuation?.Recommendation);
        Assert.Same(point, result.Actuation?.ApplicationPoint);
        Assert.Equal(pending.PendingRecommendation!.ProposedOperatingPoint, result.State.OperatingPoint);
        Assert.Null(result.State.PendingRecommendation);
        Assert.Equal(point.Fence, result.State.LastApplicationFence);
        Assert.Same(result.Actuation, result.State.LastActuation);
    }

    [Fact]
    public void StateAndRecommendationCannotResumeUnderChangedDefinitionContent()
    {
        var original = ControlRegulatorFixture.Definition(
            additiveIncrease: 3,
            healthyObservationCount: 1);
        var changed = ControlRegulatorFixture.Definition(
            additiveIncrease: 2,
            healthyObservationCount: 1);
        var pending = Recommend(original, value: 5_000);
        var point = ControlRegulatorFixture.ApplicationPoint(
            pending,
            "changed-definition",
            fence: 1,
            pending.UpdatedAtUtc.AddMilliseconds(1));

        var result = AimdControlReferenceRegulator.Apply(
            changed,
            pending,
            point,
            point.ObservedAtUtc);

        Assert.NotEqual(original.Fingerprint, changed.Fingerprint);
        Assert.Equal(ControlActuationDisposition.Rejected, result.Disposition);
        Assert.Same(pending, result.State);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == ControlDiagnosticCodes.DefinitionFingerprintMismatch);
    }

    [Fact]
    public void SafePointBeforeRecommendation_IsRejectedWithoutMutation()
    {
        var definition = ControlRegulatorFixture.Definition(healthyObservationCount: 1);
        var pending = Recommend(definition, value: 5_000);
        var point = ControlRegulatorFixture.ApplicationPoint(
            pending,
            "too-early",
            fence: 1,
            pending.PendingRecommendation!.IssuedAtUtc.AddMilliseconds(-1));

        var result = AimdControlReferenceRegulator.Apply(
            definition,
            pending,
            point,
            pending.UpdatedAtUtc);

        Assert.Equal(ControlActuationDisposition.Rejected, result.Disposition);
        Assert.Same(pending, result.State);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == ControlDiagnosticCodes.ApplicationPointInvalid);
    }

    [Theory]
    [InlineData("revision")]
    [InlineData("epoch")]
    public void StaleApplicationFence_IsRejectedWithoutMutation(string mismatch)
    {
        var definition = ControlRegulatorFixture.Definition(healthyObservationCount: 1);
        var pending = Recommend(definition, value: 5_000);
        var observedAt = pending.UpdatedAtUtc.AddMilliseconds(1);
        var point = mismatch switch
        {
            "revision" => ControlRegulatorFixture.ApplicationPoint(
                pending,
                "stale-revision",
                fence: 1,
                observedAt,
                expectedRevision: ControlRevision.Initial),
            "epoch" => ControlRegulatorFixture.ApplicationPoint(
                pending,
                "stale-epoch",
                fence: 1,
                observedAt,
                epoch: new ControlEpochId("generation-old")),
            _ => throw new InvalidOperationException($"Unknown mismatch '{mismatch}'.")
        };

        var result = AimdControlReferenceRegulator.Apply(
            definition,
            pending,
            point,
            observedAt);

        Assert.Equal(ControlActuationDisposition.Rejected, result.Disposition);
        Assert.Same(pending, result.State);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == ControlDiagnosticCodes.ApplicationFenceMismatch);
    }

    [Theory]
    [InlineData("authority")]
    [InlineData("kind")]
    public void SafePointMustCarryTheDefinitionsExactAuthorityAndActuatorCut(string mismatch)
    {
        var definition = ControlRegulatorFixture.Definition(healthyObservationCount: 1);
        var pending = Recommend(definition, value: 5_000);
        var observedAtUtc = pending.UpdatedAtUtc.AddMilliseconds(1);
        var point = ControlRegulatorFixture.ApplicationPoint(
            pending,
            $"wrong-{mismatch}",
            fence: 1,
            observedAtUtc,
            authority: mismatch == "authority" ? "untrusted/runtime" : definition.ApplicationAuthority,
            kind: mismatch == "kind"
                ? ControlApplicationPointKind.BatchBoundary
                : ControlApplicationPointKind.WorkAdmissionBoundary);

        var result = AimdControlReferenceRegulator.Apply(
            definition,
            pending,
            point,
            observedAtUtc);

        Assert.Equal(ControlActuationDisposition.Rejected, result.Disposition);
        Assert.Same(pending, result.State);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == ControlDiagnosticCodes.ApplicationFenceMismatch);
    }

    [Fact]
    public void NonIncreasingRuntimeFence_IsRejectedAfterPriorActuation()
    {
        var definition = ControlRegulatorFixture.Definition(
            healthyObservationCount: 1,
            minimumDwellMilliseconds: 0);
        var firstPending = Recommend(definition, value: 9_000);
        var firstApplied = Apply(definition, firstPending, "safe-point-1", fence: 10);
        var secondObservation = ControlRegulatorFixture.Observation(
            firstApplied,
            "second-congestion",
            value: 9_000,
            observedAtUtc: firstApplied.UpdatedAtUtc.AddMilliseconds(1));
        var secondPending = AimdControlReferenceRegulator.Evaluate(
            definition,
            firstApplied,
            secondObservation,
            secondObservation.ObservedAtUtc).State;
        var stalePoint = ControlRegulatorFixture.ApplicationPoint(
            secondPending,
            "safe-point-stale",
            fence: 10,
            secondPending.UpdatedAtUtc.AddMilliseconds(1));

        var result = AimdControlReferenceRegulator.Apply(
            definition,
            secondPending,
            stalePoint,
            stalePoint.ObservedAtUtc);

        Assert.Equal(ControlActuationDisposition.Rejected, result.Disposition);
        Assert.Same(secondPending, result.State);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == ControlDiagnosticCodes.ApplicationFenceMismatch);
    }

    [Fact]
    public void ExactActuationReplay_ReturnsSameReceiptAndState()
    {
        var definition = ControlRegulatorFixture.Definition(healthyObservationCount: 1);
        var pending = Recommend(definition, value: 5_000);
        var point = ControlRegulatorFixture.ApplicationPoint(
            pending,
            "safe-point-replay",
            fence: 1,
            pending.UpdatedAtUtc.AddMilliseconds(1));
        var applied = AimdControlReferenceRegulator.Apply(
            definition,
            pending,
            point,
            point.ObservedAtUtc);

        var replay = AimdControlReferenceRegulator.Apply(
            definition,
            applied.State,
            point,
            point.ObservedAtUtc.AddSeconds(1));

        Assert.Equal(ControlActuationDisposition.Replayed, replay.Disposition);
        Assert.Same(applied.State, replay.State);
        Assert.Same(applied.Actuation, replay.Actuation);
        Assert.Empty(replay.Diagnostics);
    }

    [Fact]
    public void DerivedActuationIdentities_AreUnambiguousWhenComponentIdsContainDelimiters()
    {
        var definition = ControlRegulatorFixture.Definition(healthyObservationCount: 1);
        var firstInitial = ControlRegulatorFixture.InitialState(definition, epoch: "identity-history-1");
        var firstObservation = ControlRegulatorFixture.Observation(firstInitial, "a", value: 5_000);
        var firstPending = AimdControlReferenceRegulator.Evaluate(
            definition,
            firstInitial,
            firstObservation,
            firstObservation.ObservedAtUtc).State;
        var firstPoint = ControlRegulatorFixture.ApplicationPoint(
            firstPending,
            "2@b",
            fence: 1,
            firstPending.UpdatedAtUtc.AddMilliseconds(1));
        var first = AimdControlReferenceRegulator.Apply(
            definition,
            firstPending,
            firstPoint,
            firstPoint.ObservedAtUtc);

        var secondInitial = ControlRegulatorFixture.InitialState(definition, epoch: "identity-history-2");
        var secondObservation = ControlRegulatorFixture.Observation(secondInitial, "a@2", value: 5_000);
        var secondPending = AimdControlReferenceRegulator.Evaluate(
            definition,
            secondInitial,
            secondObservation,
            secondObservation.ObservedAtUtc).State;
        var secondPoint = ControlRegulatorFixture.ApplicationPoint(
            secondPending,
            "b",
            fence: 1,
            secondPending.UpdatedAtUtc.AddMilliseconds(1));
        var second = AimdControlReferenceRegulator.Apply(
            definition,
            secondPending,
            secondPoint,
            secondPoint.ObservedAtUtc);

        Assert.Equal(ControlActuationDisposition.Applied, first.Disposition);
        Assert.Equal(ControlActuationDisposition.Applied, second.Disposition);
        Assert.NotEqual(firstPending.PendingRecommendation?.Id, secondPending.PendingRecommendation?.Id);
        Assert.NotEqual(first.Actuation?.Id, second.Actuation?.Id);
    }

    [Fact]
    public void ReusedApplicationPointIdentityWithDifferentEvidence_IsRejected()
    {
        var definition = ControlRegulatorFixture.Definition(healthyObservationCount: 1);
        var pending = Recommend(definition, value: 5_000);
        var point = ControlRegulatorFixture.ApplicationPoint(
            pending,
            "safe-point-conflict",
            fence: 1,
            pending.UpdatedAtUtc.AddMilliseconds(1));
        var applied = AimdControlReferenceRegulator.Apply(
            definition,
            pending,
            point,
            point.ObservedAtUtc);
        var conflictingPoint = ControlRegulatorFixture.ApplicationPoint(
            pending,
            "safe-point-conflict",
            fence: 1,
            point.ObservedAtUtc,
            sourceReference: "process:different-cut");

        var conflict = AimdControlReferenceRegulator.Apply(
            definition,
            applied.State,
            conflictingPoint,
            point.ObservedAtUtc.AddSeconds(1));

        Assert.Equal(ControlActuationDisposition.Rejected, conflict.Disposition);
        Assert.Same(applied.State, conflict.State);
        Assert.Contains(conflict.Diagnostics, diagnostic =>
            diagnostic.Code == ControlDiagnosticCodes.ApplicationPointConflict);
    }

    [Fact]
    public void RevisionScopedApplicationPointIdentity_CanBeReusedByALaterActuation()
    {
        var definition = ControlRegulatorFixture.Definition(
            healthyObservationCount: 1,
            minimumDwellMilliseconds: 0);
        var firstPending = Recommend(definition, value: 9_000);
        var firstApplied = Apply(definition, firstPending, "local-cut", fence: 1);
        var secondObservation = ControlRegulatorFixture.Observation(
            firstApplied,
            "second-pressure",
            value: 9_000);
        var secondPending = AimdControlReferenceRegulator.Evaluate(
            definition,
            firstApplied,
            secondObservation,
            secondObservation.ObservedAtUtc).State;
        var secondPoint = ControlRegulatorFixture.ApplicationPoint(
            secondPending,
            "local-cut",
            fence: 2,
            secondPending.UpdatedAtUtc.AddMilliseconds(1));

        var secondApplied = AimdControlReferenceRegulator.Apply(
            definition,
            secondPending,
            secondPoint,
            secondPoint.ObservedAtUtc);

        Assert.Equal(ControlActuationDisposition.Applied, secondApplied.Disposition);
        Assert.Same(secondPoint, secondApplied.Actuation?.ApplicationPoint);
    }

    [Fact]
    public void ApplicationPointEvidence_IsFencedToExactDefinitionContent()
    {
        var priorDefinition = ControlRegulatorFixture.Definition(
            additiveIncrease: 2,
            healthyObservationCount: 1);
        var currentDefinition = ControlRegulatorFixture.Definition(
            additiveIncrease: 3,
            healthyObservationCount: 1);
        var pending = Recommend(currentDefinition, value: 5_000);
        var point = ControlRegulatorFixture.ApplicationPoint(
            pending,
            "prior-definition-cut",
            fence: 1,
            pending.UpdatedAtUtc.AddMilliseconds(1),
            definitionFingerprint: priorDefinition.Fingerprint);

        var result = AimdControlReferenceRegulator.Apply(
            currentDefinition,
            pending,
            point,
            point.ObservedAtUtc);

        Assert.Equal(ControlActuationDisposition.Rejected, result.Disposition);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == ControlDiagnosticCodes.ApplicationFenceMismatch);
    }

    [Fact]
    public void RetainedActuation_IsRevalidatedAgainstDefinitionSafePointAuthority()
    {
        var definition = ControlRegulatorFixture.Definition(healthyObservationCount: 1);
        var pending = Recommend(definition, value: 5_000);
        var recommendation = Assert.IsType<ControlRecommendation>(pending.PendingRecommendation);
        var observedAtUtc = pending.UpdatedAtUtc.AddMilliseconds(1);
        var unauthorizedPoint = ControlRegulatorFixture.ApplicationPoint(
            pending,
            "unauthorized-retained-cut",
            fence: 1,
            observedAtUtc,
            authority: "untrusted/runtime");
        var nextRevision = new ControlRevision(
            (long.Parse(pending.Revision.Value, System.Globalization.CultureInfo.InvariantCulture) + 1)
                .ToString(System.Globalization.CultureInfo.InvariantCulture));
        var forgedActuation = new ControlActuation(
            new("forged-actuation"),
            recommendation,
            pending.LastObservation!,
            unauthorizedPoint,
            pending.Revision,
            nextRevision,
            observedAtUtc);
        var forgedState = new ControlLoopState(
            pending.SchemaVersion,
            pending.LoopId,
            pending.Target,
            pending.Epoch,
            nextRevision,
            pending.DefinitionFingerprint,
            recommendation.ProposedOperatingPoint,
            healthyObservationCount: 0,
            pending.CreatedAtUtc,
            updatedAtUtc: observedAtUtc,
            lastEvaluatedAtUtc: pending.LastEvaluatedAtUtc,
            lastClassification: pending.LastClassification,
            lastObservation: pending.LastObservation,
            lastActuation: forgedActuation,
            lastApplicationFence: unauthorizedPoint.Fence);

        var validation = AimdControlReferenceRegulator.ValidateState(definition, forgedState);

        Assert.Contains(validation.Diagnostics, diagnostic =>
            diagnostic.Code == ControlDiagnosticCodes.ActuationInvalid);
    }

    [Theory]
    [InlineData(11L, null, ControlDiagnosticCodes.HardLimitExceeded)]
    [InlineData(8L, 7L, ControlDiagnosticCodes.WorkloadBudgetExceeded)]
    public void Apply_RevalidatesHardBoundsAndWorkloadBudgets(
        long proposedConcurrency,
        long? availableBudget,
        string expectedCode)
    {
        var definition = ControlRegulatorFixture.Definition(
            availableBudget: availableBudget,
            healthyObservationCount: 1);
        var initial = ControlRegulatorFixture.InitialState(definition);
        var observedAt = initial.UpdatedAtUtc.AddSeconds(1);
        var observation = ControlRegulatorFixture.Observation(
            initial,
            "tampered-recommendation",
            value: 5_000,
            observedAtUtc: observedAt);
        var revisionAfterEvaluation = new ControlRevision("2");
        var recommendation = new ControlRecommendation(
            new ControlRecommendationId("tampered@1"),
            initial.LoopId,
            definition.Fingerprint,
            initial.Target,
            initial.Epoch,
            revisionAfterEvaluation,
            observation.Id,
            ControlActuatorKind.Concurrency,
            ControlRecommendationDirection.Increase,
            authorizingHealthyObservationCount: 1,
            initial.OperatingPoint,
            ControlRegulatorFixture.Point(proposedConcurrency),
            observedAt);
        var state = new ControlLoopState(
            ControlLoopDefinition.CurrentSchemaVersion,
            initial.LoopId,
            initial.Target,
            initial.Epoch,
            revisionAfterEvaluation,
            definition.Fingerprint,
            initial.OperatingPoint,
            healthyObservationCount: 1,
            initial.CreatedAtUtc,
            updatedAtUtc: observedAt,
            lastEvaluatedAtUtc: observedAt,
            lastClassification: ControlPressureClassification.Healthy,
            lastObservation: observation,
            pendingRecommendation: recommendation);
        var point = ControlRegulatorFixture.ApplicationPoint(
            state,
            "safe-point-tampered",
            fence: 1,
            observedAt.AddMilliseconds(1));

        var result = AimdControlReferenceRegulator.Apply(
            definition,
            state,
            point,
            point.ObservedAtUtc);

        Assert.Equal(ControlActuationDisposition.Rejected, result.Disposition);
        Assert.Same(state, result.State);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == expectedCode);
    }

    [Fact]
    public void ApplyRejectsAnInBoundsRecommendationThatBypassesTheExactAimdStep()
    {
        var definition = ControlRegulatorFixture.Definition(
            additiveIncrease: 3,
            healthyObservationCount: 1);
        var initial = ControlRegulatorFixture.InitialState(definition);
        var observedAtUtc = initial.UpdatedAtUtc.AddSeconds(1);
        var observation = ControlRegulatorFixture.Observation(
            initial,
            "forged-step",
            value: 5_000,
            observedAtUtc);
        var revisionAfterEvaluation = new ControlRevision("2");
        var forged = new ControlRecommendation(
            new("forged-step@2"),
            initial.LoopId,
            definition.Fingerprint,
            initial.Target,
            initial.Epoch,
            revisionAfterEvaluation,
            observation.Id,
            ControlActuatorKind.Concurrency,
            ControlRecommendationDirection.Increase,
            authorizingHealthyObservationCount: 1,
            initial.OperatingPoint,
            ControlRegulatorFixture.Point(concurrency: 7),
            observedAtUtc);
        var state = new ControlLoopState(
            ControlLoopDefinition.CurrentSchemaVersion,
            initial.LoopId,
            initial.Target,
            initial.Epoch,
            revisionAfterEvaluation,
            definition.Fingerprint,
            initial.OperatingPoint,
            healthyObservationCount: 0,
            initial.CreatedAtUtc,
            updatedAtUtc: observedAtUtc,
            lastEvaluatedAtUtc: observedAtUtc,
            lastClassification: ControlPressureClassification.Healthy,
            lastObservation: observation,
            pendingRecommendation: forged);
        var point = ControlRegulatorFixture.ApplicationPoint(
            state,
            "forged-step-cut",
            fence: 1,
            observedAtUtc.AddMilliseconds(1));

        var result = AimdControlReferenceRegulator.Apply(
            definition,
            state,
            point,
            point.ObservedAtUtc);

        Assert.Equal(ControlActuationDisposition.Rejected, result.Disposition);
        Assert.Same(state, result.State);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == ControlDiagnosticCodes.RecommendationInvalid);
    }

    [Fact]
    public void DurableActuationValidation_RechecksTheAppliedAimdStep()
    {
        var definition = ControlRegulatorFixture.Definition(
            additiveIncrease: 3,
            healthyObservationCount: 1);
        var pending = Recommend(definition, value: 5_000);
        var observation = Assert.IsType<ControlObservation>(pending.LastObservation);
        var issuedAtUtc = Assert.IsType<DateTimeOffset>(pending.LastEvaluatedAtUtc);
        var forgedRecommendation = new ControlRecommendation(
            new("forged-applied-step"),
            pending.LoopId,
            pending.DefinitionFingerprint,
            pending.Target,
            pending.Epoch,
            pending.Revision,
            observation.Id,
            ControlActuatorKind.Concurrency,
            ControlRecommendationDirection.Increase,
            authorizingHealthyObservationCount: 1,
            pending.OperatingPoint,
            ControlRegulatorFixture.Point(concurrency: 7),
            issuedAtUtc);
        var applicationPoint = ControlRegulatorFixture.ApplicationPoint(
            pending,
            "forged-applied-step-cut",
            fence: 1,
            issuedAtUtc.AddMilliseconds(1));
        var receipt = new ControlActuation(
            new("forged-applied-receipt"),
            forgedRecommendation,
            observation,
            applicationPoint,
            pending.Revision,
            new("3"),
            applicationPoint.ObservedAtUtc);

        var validation = AimdControlReferenceRegulator.ValidateActuation(definition, receipt);

        Assert.Contains(validation.Diagnostics, diagnostic =>
            diagnostic.Code == ControlDiagnosticCodes.RecommendationInvalid);
    }

    [Fact]
    public void StateValidation_RecomputesExactRecoveryCooldownFromRetainedActuation()
    {
        var definition = ControlRegulatorFixture.Definition(
            healthyObservationCount: 1,
            recoveryCooldownMilliseconds: 10_000);
        var pending = Recommend(definition, value: 9_000);
        var applied = Apply(definition, pending, "cooldown-cut", fence: 1);
        var forged = new ControlLoopState(
            applied.SchemaVersion,
            applied.LoopId,
            applied.Target,
            applied.Epoch,
            applied.Revision,
            applied.DefinitionFingerprint,
            applied.OperatingPoint,
            applied.HealthyObservationCount,
            applied.CreatedAtUtc,
            applied.UpdatedAtUtc,
            applied.LastEvaluatedAtUtc,
            applied.LastClassification,
            applied.CooldownUntilUtc!.Value.AddMilliseconds(1),
            applied.LastObservation,
            applied.PendingRecommendation,
            applied.LastActuation,
            applied.LastApplicationFence);

        var validation = AimdControlReferenceRegulator.ValidateState(definition, forged);

        Assert.Contains(validation.Diagnostics, diagnostic =>
            diagnostic.Code == ControlDiagnosticCodes.StateInvalid);
    }

    [Fact]
    public void RecommendationExpiresWhenItsMeasurementWindowIsNoLongerFresh()
    {
        var definition = ControlRegulatorFixture.Definition(
            healthyObservationCount: 1,
            maximumObservationAgeMilliseconds: 1_000);
        var pending = Recommend(definition, value: 5_000);
        var point = ControlRegulatorFixture.ApplicationPoint(
            pending,
            "expired-recommendation",
            fence: 1,
            pending.LastObservation!.WindowEndedAtUtc.AddMilliseconds(1_001));

        var result = AimdControlReferenceRegulator.Apply(
            definition,
            pending,
            point,
            point.ObservedAtUtc);

        Assert.Equal(ControlActuationDisposition.Rejected, result.Disposition);
        Assert.Same(pending, result.State);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == ControlDiagnosticCodes.ApplicationPointInvalid);
    }

    [Fact]
    public void RecommendationCannotBeAppliedAfterFreshSafePointEvidenceHasExpired()
    {
        var definition = ControlRegulatorFixture.Definition(
            healthyObservationCount: 1,
            maximumObservationAgeMilliseconds: 1_000);
        var pending = Recommend(definition, value: 5_000);
        var windowEndedAtUtc = pending.LastObservation!.WindowEndedAtUtc;
        var point = ControlRegulatorFixture.ApplicationPoint(
            pending,
            "fresh-cut-delayed-application",
            fence: 1,
            windowEndedAtUtc.AddMilliseconds(500));

        var result = AimdControlReferenceRegulator.Apply(
            definition,
            pending,
            point,
            appliedAtUtc: windowEndedAtUtc.AddMilliseconds(1_001));

        Assert.Equal(ControlActuationDisposition.Rejected, result.Disposition);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == ControlDiagnosticCodes.ApplicationPointInvalid);
    }

    [Fact]
    public void AppliedResult_CannotClaimALaterEvaluatedState()
    {
        var definition = ControlRegulatorFixture.Definition(healthyObservationCount: 1);
        var pending = Recommend(definition, value: 5_000);
        var point = ControlRegulatorFixture.ApplicationPoint(
            pending,
            "applied-result-cut",
            fence: 1,
            pending.UpdatedAtUtc.AddMilliseconds(1));
        var applied = AimdControlReferenceRegulator.Apply(
            definition,
            pending,
            point,
            point.ObservedAtUtc);
        var laterObservation = ControlRegulatorFixture.Observation(
            applied.State,
            "later-evaluation",
            value: 7_000);
        var later = AimdControlReferenceRegulator.Evaluate(
            definition,
            applied.State,
            laterObservation,
            laterObservation.ObservedAtUtc);

        Assert.Throws<ArgumentException>(() => new ControlActuationResult(
            ControlLoopDefinition.CurrentSchemaVersion,
            ControlActuationDisposition.Applied,
            later.State,
            applied.Actuation));
    }

    [Fact]
    public void NoPendingRecommendation_DefersWithoutMutation()
    {
        var definition = ControlRegulatorFixture.Definition();
        var state = ControlRegulatorFixture.InitialState(definition);
        var point = ControlRegulatorFixture.ApplicationPoint(
            state,
            "unused-safe-point",
            fence: 1,
            state.UpdatedAtUtc.AddSeconds(1));

        var result = AimdControlReferenceRegulator.Apply(
            definition,
            state,
            point,
            point.ObservedAtUtc);

        Assert.Equal(ControlActuationDisposition.Deferred, result.Disposition);
        Assert.Same(state, result.State);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == ControlDiagnosticCodes.RecommendationAbsent);
    }

    static ControlLoopState Recommend(ControlLoopDefinition definition, long value)
    {
        var state = ControlRegulatorFixture.InitialState(definition);
        var observation = ControlRegulatorFixture.Observation(state, "recommendation", value);
        var decision = AimdControlReferenceRegulator.Evaluate(
            definition,
            state,
            observation,
            observation.ObservedAtUtc);
        Assert.Equal(ControlDecisionDisposition.Recommended, decision.Disposition);
        return decision.State;
    }

    static ControlLoopState Apply(
        ControlLoopDefinition definition,
        ControlLoopState pending,
        string id,
        long fence)
    {
        var point = ControlRegulatorFixture.ApplicationPoint(
            pending,
            id,
            fence,
            pending.UpdatedAtUtc.AddMilliseconds(1));
        var result = AimdControlReferenceRegulator.Apply(
            definition,
            pending,
            point,
            point.ObservedAtUtc);
        Assert.Equal(ControlActuationDisposition.Applied, result.Disposition);
        return result.State;
    }
}
