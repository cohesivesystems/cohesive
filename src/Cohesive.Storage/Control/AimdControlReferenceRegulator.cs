using System.Collections.Immutable;
using System.Numerics;
using Cohesive.Model.Serialization;

namespace Cohesive.Control;

/// <summary>
/// Pure deterministic reference interpretation of bounded AIMD evaluation and fenced safe-point actuation.
/// </summary>
/// <remarks>
/// This type owns no clock, timer, sampler, queue, worker, retry policy, transport, or target SDK. Callers supply
/// explicit state, observations, times, and safe-point evidence and persist returned state atomically with their
    /// own runtime records. The bounded state retains only the latest observation and actuation for exact replay at
    /// the current revision. Recommendation lineage references the latest preceding actuation by identity and
    /// post-actuation revision. Once that receipt rolls out of bounded state, the runtime's durable ledger must prove
    /// the exact immutable latest-predecessor relationship and also owns arbitrary-history replay identities.
/// </remarks>
public static class AimdControlReferenceRegulator
{
    /// <summary>Evaluates one typed observation against explicit durable controller state.</summary>
    /// <param name="definition">Canonical bounded loop definition.</param>
    /// <param name="state">Complete durable state before evaluation.</param>
    /// <param name="observation">Typed revision-fenced observation window.</param>
    /// <param name="evaluatedAtUtc">Explicit UTC evaluation time.</param>
    /// <returns>
    /// A pure decision containing unchanged state on rejection/replay or the complete next state after accepting
    /// the observation. A recommendation remains non-authoritative until <see cref="Apply"/> succeeds.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="definition"/>, <paramref name="state"/>, or <paramref name="observation"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="evaluatedAtUtc"/> is not UTC.</exception>
    public static ControlDecision Evaluate(
        ControlLoopDefinition definition,
        ControlLoopState state,
        ControlObservation observation,
        DateTimeOffset evaluatedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(observation);
        ControlObservation.RequireUtc(evaluatedAtUtc, nameof(evaluatedAtUtc));

        var stateValidation = ValidateState(definition, state);
        if (!stateValidation.IsValid)
            return Rejected(state, evaluatedAtUtc, stateValidation.Diagnostics);

        if (state.LastObservation is { } lastObservation
            && HasSameObservationScope(lastObservation, observation))
        {
            if (lastObservation != observation)
            {
                return Rejected(
                    state,
                    evaluatedAtUtc,
                    Diagnostic(
                        ControlDiagnosticCodes.ObservationConflict,
                        "Observation identity was reused with different measurement or fence evidence.",
                        "/observation/id",
                        expected: lastObservation.Id.Value,
                        observed: observation.Id.Value));
            }

            return new(
                ControlLoopDefinition.CurrentSchemaVersion,
                ControlDecisionDisposition.Replayed,
                evaluatedAtUtc,
                state,
                state.PendingRecommendation);
        }
        if (state.PendingLimitUpdate is not null)
        {
            return Rejected(
                state,
                evaluatedAtUtc,
                Diagnostic(
                    ControlDiagnosticCodes.LimitUpdatePending,
                    "Automatic observations cannot advance while an accepted operator override awaits its safe point.",
                    "/state/pendingLimitUpdate"));
        }
        if (state.Revision.Ordinal == long.MaxValue)
            return Rejected(state, evaluatedAtUtc, RevisionExhaustedDiagnostic(state));

        var observationDiagnostics = ValidateObservation(definition, state, observation, evaluatedAtUtc);
        if (!observationDiagnostics.IsDefaultOrEmpty)
            return Rejected(state, evaluatedAtUtc, observationDiagnostics);

        var classification = Classify(definition, observation);
        if (state.PendingRecommendation is { } pending
            && (pending.Direction != ControlRecommendationDirection.Increase
                || classification == ControlPressureClassification.Healthy))
        {
            return Rejected(
                state,
                evaluatedAtUtc,
                Diagnostic(
                    ControlDiagnosticCodes.RecommendationPending,
                    "Only newer non-healthy evidence may supersede a pending additive increase.",
                    "/state/pendingRecommendation",
                    expected: pending.Id.Value,
                    observed: observation.Id.Value));
        }

        var healthyCount = classification == ControlPressureClassification.Healthy
            ? state.HealthyObservationCount
            : 0;
        ControlRecommendation? recommendation = null;
        var currentValue = state.OperatingPoint.Get(definition.Policy.Actuator);
        var range = definition.GetEffectiveRange(definition.Policy.Actuator);
        var dwellSatisfied = state.LastAppliedAtUtc is not { } lastAppliedAtUtc
            || ElapsedMilliseconds(lastAppliedAtUtc, observation.WindowEndedAtUtc)
                >= definition.Policy.MinimumDwellMilliseconds;

        switch (classification)
        {
            case ControlPressureClassification.Congested:
                healthyCount = 0;
                if (dwellSatisfied && currentValue.Quantity.Value > range.Minimum.Value)
                {
                    var proposedValue = Decrease(
                        currentValue.Quantity.Value,
                        definition.Policy.MultiplicativeDecreaseBasisPoints,
                        range.Minimum.Value);
                    recommendation = CreateRecommendation(
                        definition,
                        state,
                        observation,
                        state.Revision.Next(),
                        ControlRecommendationDirection.Decrease,
                        authorizingHealthyObservationCount: 0,
                        proposedValue,
                        evaluatedAtUtc);
                }
                break;
            case ControlPressureClassification.Healthy:
                if (state.CooldownUntilUtc is { } cooldown && observation.WindowEndedAtUtc < cooldown)
                {
                    healthyCount = 0;
                    break;
                }

                healthyCount = state.HealthyObservationCount == ControlQuantity.MaximumPortableValue
                    ? state.HealthyObservationCount
                    : state.HealthyObservationCount + 1;
                if (healthyCount >= definition.Policy.HealthyObservationCount
                    && dwellSatisfied
                    && currentValue.Quantity.Value < range.Maximum.Value)
                {
                    var proposedValue = Increase(
                        currentValue.Quantity.Value,
                        definition.Policy.AdditiveIncrease.Value,
                        range.Maximum.Value);
                    recommendation = CreateRecommendation(
                        definition,
                        state,
                        observation,
                        state.Revision.Next(),
                        ControlRecommendationDirection.Increase,
                        healthyCount,
                        proposedValue,
                        evaluatedAtUtc);
                }
                break;
            case ControlPressureClassification.Hysteresis:
                healthyCount = 0;
                break;
            default:
                throw new InvalidOperationException($"Unsupported pressure classification '{classification}'.");
        }

        var nextRevision = state.Revision.Next();
        var nextState = new ControlLoopState(
            ControlLoopDefinition.CurrentSchemaVersion,
            state.LoopId,
            state.Target,
            state.Epoch,
            nextRevision,
            state.DefinitionFingerprint,
            state.OperatingPoint,
            healthyCount,
            state.CreatedAtUtc,
            updatedAtUtc: evaluatedAtUtc,
            lastEvaluatedAtUtc: evaluatedAtUtc,
            lastClassification: classification,
            cooldownUntilUtc: state.CooldownUntilUtc,
            lastObservation: observation,
            pendingRecommendation: recommendation,
            lastActuation: state.LastActuation,
            lastApplicationFence: state.LastApplicationFence,
            authorityScope: state.AuthorityScope,
            limitUpdateActuations: state.LimitUpdateActuations,
            pendingLimitUpdate: state.PendingLimitUpdate);

        return new(
            ControlLoopDefinition.CurrentSchemaVersion,
            recommendation is null
                ? ControlDecisionDisposition.Held
                : ControlDecisionDisposition.Recommended,
            evaluatedAtUtc,
            nextState,
            recommendation);
    }

    /// <summary>Applies a pending recommendation at an exact invariant-preserving safe point.</summary>
    /// <param name="definition">Canonical bounded loop definition.</param>
    /// <param name="state">Complete durable state containing the pending recommendation.</param>
    /// <param name="applicationPoint">Generic Process or materialization safe-point evidence.</param>
    /// <param name="appliedAtUtc">Explicit UTC time at which the complete operating point was applied.</param>
    /// <returns>An applied/replayed receipt or an unchanged deferred/rejected state with diagnostics.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="definition"/>, <paramref name="state"/>, or <paramref name="applicationPoint"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="appliedAtUtc"/> is not UTC.</exception>
    public static ControlActuationResult Apply(
        ControlLoopDefinition definition,
        ControlLoopState state,
        ControlApplicationPoint applicationPoint,
        DateTimeOffset appliedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(applicationPoint);
        ControlObservation.RequireUtc(appliedAtUtc, nameof(appliedAtUtc));

        var stateValidation = ValidateState(definition, state);
        if (!stateValidation.IsValid)
            return RejectedActuation(state, stateValidation.Diagnostics);

        if (state.LastActuation is { } lastActuation
            && HasSameApplicationPointScope(lastActuation.ApplicationPoint, applicationPoint))
        {
            if (lastActuation.ApplicationPoint != applicationPoint)
            {
                return RejectedActuation(
                    state,
                    Diagnostic(
                        ControlDiagnosticCodes.ApplicationPointConflict,
                        "Application-point identity was reused with different safe-point evidence.",
                        "/applicationPoint/id",
                        expected: lastActuation.ApplicationPoint.Id.Value,
                        observed: applicationPoint.Id.Value));
            }

            return new(
                ControlLoopDefinition.CurrentSchemaVersion,
                ControlActuationDisposition.Replayed,
                state,
                lastActuation);
        }
        if (state.PendingLimitUpdate is not null)
        {
            return RejectedActuation(
                state,
                Diagnostic(
                    ControlDiagnosticCodes.LimitUpdatePending,
                    "Adaptive advice cannot be actuated while an accepted operator override awaits its safe point.",
                    "/state/pendingLimitUpdate"));
        }
        if (state.Revision.Ordinal == long.MaxValue)
            return RejectedActuation(state, RevisionExhaustedDiagnostic(state));

        var recommendation = state.PendingRecommendation;
        if (recommendation is null)
        {
            return new(
                ControlLoopDefinition.CurrentSchemaVersion,
                ControlActuationDisposition.Deferred,
                state,
                diagnostics: Diagnostic(
                    ControlDiagnosticCodes.RecommendationAbsent,
                    "No operating-point recommendation is awaiting a safe point.",
                    "/state/pendingRecommendation",
                    DiagnosticSeverity.Info));
        }

        List<DocumentValidationDiagnostic> diagnostics = [];
        if (applicationPoint.SchemaVersion != ControlLoopDefinition.CurrentSchemaVersion
            || applicationPoint.LoopId != state.LoopId
            || applicationPoint.DefinitionFingerprint != state.DefinitionFingerprint
            || applicationPoint.Epoch != state.Epoch
            || !string.Equals(applicationPoint.Target, state.Target, StringComparison.Ordinal)
            || applicationPoint.ExpectedRevision != state.Revision
            || recommendation.ExpectedRevision != state.Revision
            || !string.Equals(applicationPoint.Authority, definition.ApplicationAuthority, StringComparison.Ordinal)
            || applicationPoint.Kind != ControlApplicationPointCatalog.ForActuator(recommendation.Actuator))
        {
            diagnostics.Add(CreateDiagnostic(
                ControlDiagnosticCodes.ApplicationFenceMismatch,
                "Application point does not address the pending recommendation's exact loop, target, epoch, and revision.",
                "/applicationPoint",
                expected: $"{state.LoopId.Value}/{state.Target}/{state.Epoch.Value}/{state.Revision.Value}/{definition.ApplicationAuthority}/{ControlApplicationPointCatalog.ForActuator(recommendation.Actuator)}",
                observed: $"{applicationPoint.LoopId.Value}/{applicationPoint.Target}/{applicationPoint.Epoch.Value}/{applicationPoint.ExpectedRevision.Value}/{applicationPoint.Authority}/{applicationPoint.Kind}"));
        }
        if (state.LastApplicationFence is { } lastFence && applicationPoint.Fence.Ordinal <= lastFence.Ordinal)
        {
            diagnostics.Add(CreateDiagnostic(
                ControlDiagnosticCodes.ApplicationFenceMismatch,
                "Application fence must be strictly later than the last applied runtime fence.",
                "/applicationPoint/fence",
                expected: $"> {lastFence.Value}",
                observed: applicationPoint.Fence.Value));
        }
        if (applicationPoint.ObservedAtUtc < recommendation.IssuedAtUtc
            || appliedAtUtc < applicationPoint.ObservedAtUtc
            || appliedAtUtc < state.UpdatedAtUtc
            || ElapsedExceeds(
                state.LastObservation!.WindowEndedAtUtc,
                appliedAtUtc,
                definition.Policy.MaximumObservationAgeMilliseconds))
        {
            diagnostics.Add(CreateDiagnostic(
                ControlDiagnosticCodes.ApplicationPointInvalid,
                "Actuation must follow evaluation at a safe point while its supporting measurement window remains fresh.",
                "/applicationPoint/observedAtUtc",
                expected: $">= {recommendation.IssuedAtUtc:O}",
                observed: applicationPoint.ObservedAtUtc.ToString("O")));
        }
        if (recommendation.Actuator != definition.Policy.Actuator
            || recommendation.PriorOperatingPoint != state.OperatingPoint)
        {
            diagnostics.Add(CreateDiagnostic(
                ControlDiagnosticCodes.RecommendationInvalid,
                "Pending recommendation does not change the definition's controlled actuator from the current point.",
                "/state/pendingRecommendation",
                expected: definition.Policy.Actuator.ToString(),
                observed: recommendation.Actuator.ToString()));
        }

        var pointValidation = definition.ValidateOperatingPoint(recommendation.ProposedOperatingPoint);
        diagnostics.AddRange(pointValidation.Diagnostics);
        if (diagnostics.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
            return RejectedActuation(state, [.. diagnostics]);

        var nextRevision = state.Revision.Next();
        var actuation = new ControlActuation(
            ControlDerivedIdentity.Actuation(recommendation, applicationPoint),
            recommendation,
            state.LastObservation!,
            applicationPoint,
            state.Revision,
            nextRevision,
            appliedAtUtc);
        DateTimeOffset? cooldown = recommendation.Direction == ControlRecommendationDirection.Decrease
            ? AddMillisecondsSaturating(appliedAtUtc, definition.Policy.RecoveryCooldownMilliseconds)
            : null;
        var nextState = new ControlLoopState(
            ControlLoopDefinition.CurrentSchemaVersion,
            state.LoopId,
            state.Target,
            state.Epoch,
            nextRevision,
            state.DefinitionFingerprint,
            recommendation.ProposedOperatingPoint,
            healthyObservationCount: 0,
            state.CreatedAtUtc,
            updatedAtUtc: appliedAtUtc,
            lastEvaluatedAtUtc: state.LastEvaluatedAtUtc,
            lastClassification: state.LastClassification,
            cooldownUntilUtc: cooldown,
            lastObservation: state.LastObservation,
            pendingRecommendation: null,
            lastActuation: actuation,
            lastApplicationFence: applicationPoint.Fence,
            authorityScope: state.AuthorityScope,
            limitUpdateActuations: state.LimitUpdateActuations,
            pendingLimitUpdate: state.PendingLimitUpdate);

        return new(
            ControlLoopDefinition.CurrentSchemaVersion,
            ControlActuationDisposition.Applied,
            nextState,
            actuation);
    }

    /// <summary>Validates complete state against a canonical definition without mutating it.</summary>
    /// <param name="definition">Canonical bounded loop definition.</param>
    /// <param name="state">State to validate.</param>
    /// <returns>Structured validation diagnostics; an empty result authorizes evaluation or application.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> or <paramref name="state"/> is <see langword="null"/>.</exception>
    public static DocumentValidationResult ValidateState(
        ControlLoopDefinition definition,
        ControlLoopState state)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(state);
        List<DocumentValidationDiagnostic> diagnostics = [];
        if (definition.SchemaVersion != ControlLoopDefinition.CurrentSchemaVersion
            || state.SchemaVersion != ControlLoopDefinition.CurrentSchemaVersion
            || state.LoopId != definition.Id
            || state.DefinitionFingerprint != definition.Fingerprint
            || !string.Equals(state.Target, definition.Target, StringComparison.Ordinal))
        {
            diagnostics.Add(CreateDiagnostic(
                state.DefinitionFingerprint != definition.Fingerprint
                    ? ControlDiagnosticCodes.DefinitionFingerprintMismatch
                    : ControlDiagnosticCodes.ObservationFenceMismatch,
                "Controller state does not belong to the supplied current-version loop definition.",
                "/state",
                expected: $"{definition.Id.Value}/{definition.Target}/{definition.Fingerprint.Value}/{ControlLoopDefinition.CurrentSchemaVersion.Value}",
                observed: $"{state.LoopId.Value}/{state.Target}/{state.DefinitionFingerprint.Value}/{state.SchemaVersion.Value}"));
        }

        var statePointValidation = definition.ValidateOperatingPoint(state.OperatingPoint);
        diagnostics.AddRange(statePointValidation.Diagnostics);
        AppendLimitUpdateDiagnostics(definition, state, diagnostics);

        var expectedOperatingPoint = state.LastAppliedOperatingPoint ?? definition.InitialOperatingPoint;
        if (state.OperatingPoint != expectedOperatingPoint)
        {
            diagnostics.Add(CreateDiagnostic(
                ControlDiagnosticCodes.StateInvalid,
                "The effective operating point must be the definition's initial point or the latest adaptive/operator actuation's exact point.",
                "/state/operatingPoint"));
        }

        var observation = state.LastObservation;
        if (observation is null)
        {
            if (state.HealthyObservationCount != 0
                || state.LastEvaluatedAtUtc is not null
                || state.LastClassification is not null
                || state.CooldownUntilUtc is not null
                || state.PendingRecommendation is not null
                || state.LastActuation is not null)
            {
                diagnostics.Add(CreateDiagnostic(
                    ControlDiagnosticCodes.StateInvalid,
                    "A state without adaptive observation evidence cannot retain adaptive transition evidence.",
                    "/state",
                    expected: "no adaptive transition evidence",
                    observed: $"revision {state.Revision.Value}"));
            }

            ValidateLatestTransition(state, diagnostics);

            return DocumentValidationResult.FromDiagnostics(diagnostics);
        }

        if (observation.SchemaVersion != ControlLoopDefinition.CurrentSchemaVersion
            || observation.LoopId != state.LoopId
            || observation.DefinitionFingerprint != state.DefinitionFingerprint
            || observation.Epoch != state.Epoch
            || !string.Equals(observation.Target, state.Target, StringComparison.Ordinal))
        {
            diagnostics.Add(CreateDiagnostic(
                observation.DefinitionFingerprint != state.DefinitionFingerprint
                    ? ControlDiagnosticCodes.DefinitionFingerprintMismatch
                    : ControlDiagnosticCodes.ObservationFenceMismatch,
                "Retained observation evidence does not belong to the durable state.",
                "/state/lastObservation",
                expected: $"{state.LoopId.Value}/{state.Target}/{state.Epoch.Value}/{state.DefinitionFingerprint.Value}",
                observed: $"{observation.LoopId.Value}/{observation.Target}/{observation.Epoch.Value}/{observation.DefinitionFingerprint.Value}"));
        }

        var measurementDiagnosticCount = diagnostics.Count;
        AppendObjectiveMeasurementDiagnostics(definition, observation, diagnostics);
        var lastEvaluatedAtUtc = state.LastEvaluatedAtUtc!.Value;
        if (lastEvaluatedAtUtc < observation.ObservedAtUtc
            || ElapsedExceeds(
                observation.WindowEndedAtUtc,
                lastEvaluatedAtUtc,
                definition.Policy.MaximumObservationAgeMilliseconds))
        {
            diagnostics.Add(CreateDiagnostic(
                ControlDiagnosticCodes.ObservationTimeInvalid,
                "Retained observation evidence must have been fresh when its evaluation was accepted.",
                "/state/lastObservation/windowEndedAtUtc",
                expected: $"age <= {definition.Policy.MaximumObservationAgeMilliseconds} ms",
                observed: $"age {ElapsedMilliseconds(observation.WindowEndedAtUtc, lastEvaluatedAtUtc)} ms"));
        }
        ControlPressureClassification? classification = null;
        if (diagnostics.Count == measurementDiagnosticCount)
        {
            classification = Classify(definition, observation);
            if (state.LastClassification != classification)
            {
                diagnostics.Add(CreateDiagnostic(
                    ControlDiagnosticCodes.StateInvalid,
                    "Retained pressure classification does not match the retained objective measurements.",
                    "/state/lastClassification",
                    expected: classification.ToString(),
                    observed: state.LastClassification?.ToString() ?? "null"));
            }
        }

        var retainedActuation = state.LastActuation;
        var latestActuation = retainedActuation is not null
            && retainedActuation.Revision == state.Revision;
        var observationEvaluationRevision = observation.ExpectedRevision.Ordinal == long.MaxValue
            ? long.MaxValue
            : observation.ExpectedRevision.Ordinal + 1;
        var latestOperatorTransitionRevision = state.PendingLimitUpdate?.AcceptedRevision
            ?? state.LastLimitUpdateActuation?.Revision;
        var operatorTransitionAfterObservation = latestOperatorTransitionRevision is { } operatorRevision
            && operatorRevision.Ordinal > observationEvaluationRevision;
        if (operatorTransitionAfterObservation)
        {
            if (state.PendingRecommendation is not null || state.HealthyObservationCount != 0)
            {
                diagnostics.Add(CreateDiagnostic(
                    ControlDiagnosticCodes.StateInvalid,
                    "A later operator transition must supersede adaptive advice and reset its recovery streak.",
                    "/state/pendingRecommendation"));
            }
            ValidateLatestTransition(state, diagnostics);
        }
        else
        {
            var revisionDistance = latestActuation ? 2L : 1L;
            if (state.Revision.Ordinal <= revisionDistance
                || observation.ExpectedRevision.Ordinal != state.Revision.Ordinal - revisionDistance)
            {
                diagnostics.Add(CreateDiagnostic(
                    ControlDiagnosticCodes.StateInvalid,
                    "Retained transition evidence does not account for the durable state revision.",
                    "/state/revision",
                    expected: latestActuation
                        ? $"observation revision {state.Revision.Ordinal - 2} before evaluation and actuation"
                        : $"observation revision {state.Revision.Ordinal - 1} before evaluation",
                    observed: observation.ExpectedRevision.Value));
            }

            if (latestActuation)
            {
                if (state.PendingRecommendation is not null
                    || retainedActuation!.Observation != observation
                    || state.UpdatedAtUtc != retainedActuation.AppliedAtUtc
                    || state.LastEvaluatedAtUtc != retainedActuation.Recommendation.IssuedAtUtc)
                {
                    diagnostics.Add(CreateDiagnostic(
                        ControlDiagnosticCodes.StateInvalid,
                        "A latest actuation must retain its generating observation and exact evaluation/application chronology.",
                        "/state/lastActuation"));
                }
            }
            else if (state.UpdatedAtUtc != state.LastEvaluatedAtUtc
                || state.LastActuation is { } earlierActuation
                    && (earlierActuation.Revision.Ordinal >= state.Revision.Ordinal
                        || earlierActuation.AppliedAtUtc > state.LastEvaluatedAtUtc))
            {
                diagnostics.Add(CreateDiagnostic(
                    ControlDiagnosticCodes.StateInvalid,
                    "A state whose latest transition was evaluation must be updated at that evaluation after any retained actuation.",
                    "/state/updatedAtUtc"));
            }
        }

        if (state.LastActuation is { } actuation)
        {
            AppendActuationDiagnostics(definition, actuation, diagnostics, "/state/lastActuation");
            DateTimeOffset? expectedCooldown = state.LastAppliedActuationRevision == actuation.Revision
                && actuation.Recommendation.Direction == ControlRecommendationDirection.Decrease
                ? AddMillisecondsSaturating(actuation.AppliedAtUtc, definition.Policy.RecoveryCooldownMilliseconds)
                : null;
            if (state.CooldownUntilUtc != expectedCooldown)
            {
                diagnostics.Add(CreateDiagnostic(
                    ControlDiagnosticCodes.StateInvalid,
                    "Recovery cooldown must be the exact policy duration derived from the retained actuation.",
                    "/state/cooldownUntilUtc",
                    expected: expectedCooldown?.ToString("O") ?? "null",
                    observed: state.CooldownUntilUtc?.ToString("O") ?? "null"));
            }
        }
        else if (state.CooldownUntilUtc is not null)
        {
            diagnostics.Add(CreateDiagnostic(
                ControlDiagnosticCodes.StateInvalid,
                "A recovery cooldown requires retained decrease-actuation evidence.",
                "/state/cooldownUntilUtc"));
        }

        var healthyCountBaseline = state.LastAppliedActuationRevision ?? ControlRevision.Initial;
        var maximumReachableHealthyObservationCount = state.Revision.Ordinal - healthyCountBaseline.Ordinal;
        var healthyInsideCooldown = classification == ControlPressureClassification.Healthy
            && state.CooldownUntilUtc is { } cooldown
            && observation.WindowEndedAtUtc < cooldown;
        if (state.HealthyObservationCount > maximumReachableHealthyObservationCount
            || (latestActuation || operatorTransitionAfterObservation) && state.HealthyObservationCount != 0
            || healthyInsideCooldown && state.HealthyObservationCount != 0
            || classification is not null and not ControlPressureClassification.Healthy
                && state.HealthyObservationCount != 0)
        {
            diagnostics.Add(CreateDiagnostic(
                ControlDiagnosticCodes.StateInvalid,
                "Healthy-evidence count is inconsistent with the retained classification and revision history.",
                "/state/healthyObservationCount"));
        }

        if (state.PendingRecommendation is { } pending)
        {
            var pendingFenceValid = pending.LoopId == state.LoopId
                && pending.DefinitionFingerprint == definition.Fingerprint
                && string.Equals(pending.Target, state.Target, StringComparison.Ordinal)
                && pending.Epoch == state.Epoch
                && pending.Actuator == definition.Policy.Actuator
                && pending.PriorOperatingPoint == state.OperatingPoint
                && pending.ExpectedRevision == state.Revision
                && pending.ObservationId == observation.Id
                && pending.PriorActuationId == state.LastAppliedActuationId
                && pending.PriorActuationRevision == state.LastAppliedActuationRevision
                && state.PendingLimitUpdate is null;
            if (!pendingFenceValid)
            {
                diagnostics.Add(CreateDiagnostic(
                    ControlDiagnosticCodes.RecommendationInvalid,
                    "Pending recommendation does not match the current loop policy, point, evidence, and revision.",
                    "/state/pendingRecommendation",
                    expected: $"{definition.Policy.Actuator}/{state.Revision.Value}",
                    observed: $"{pending.Actuator}/{pending.ExpectedRevision.Value}"));
            }

            var proposedPointValidation = definition.ValidateOperatingPoint(pending.ProposedOperatingPoint);
            diagnostics.AddRange(proposedPointValidation.Diagnostics);
            if (pendingFenceValid && statePointValidation.IsValid && proposedPointValidation.IsValid)
                ValidatePendingRecommendation(definition, state, pending, diagnostics);
        }
        else if (!latestActuation
            && !operatorTransitionAfterObservation
            && classification == ControlPressureClassification.Healthy
            && (state.CooldownUntilUtc is null || observation.WindowEndedAtUtc >= state.CooldownUntilUtc))
        {
            if (state.HealthyObservationCount == 0)
            {
                diagnostics.Add(CreateDiagnostic(
                    ControlDiagnosticCodes.StateInvalid,
                    "An accepted healthy observation outside cooldown must advance the healthy-evidence count.",
                    "/state/healthyObservationCount"));
            }
            else
            {
                var range = definition.GetEffectiveRange(definition.Policy.Actuator);
                var current = state.OperatingPoint.Get(definition.Policy.Actuator).Quantity.Value;
                var dwellSatisfied = state.LastAppliedAtUtc is not { } lastAppliedAtUtc
                    || ElapsedMilliseconds(lastAppliedAtUtc, observation.WindowEndedAtUtc)
                        >= definition.Policy.MinimumDwellMilliseconds;
                if (state.HealthyObservationCount >= definition.Policy.HealthyObservationCount
                    && dwellSatisfied
                    && current < range.Maximum.Value)
                {
                    diagnostics.Add(CreateDiagnostic(
                        ControlDiagnosticCodes.StateInvalid,
                        "A threshold-satisfying healthy evaluation with available headroom must retain its recommendation.",
                        "/state/pendingRecommendation",
                        expected: "increase recommendation",
                        observed: "null"));
                }
            }
        }

        return DocumentValidationResult.FromDiagnostics(diagnostics);
    }

    /// <summary>Validates a recommendation's definition ownership, identity, bounds, step, and authorization count.</summary>
    /// <param name="definition">Canonical bounded loop definition.</param>
    /// <param name="recommendation">Recommendation shape to validate.</param>
    /// <returns>
    /// Structured diagnostics; an empty result proves local recommendation invariants. Observation classification
    /// and freshness require the complete state or actuation validation overloads. Historical prior-actuation
    /// existence, latest-predecessor ordering, revision, and operating-point linkage require current-state validation
    /// or resolution through the runtime's durable receipt ledger.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="definition"/> or <paramref name="recommendation"/> is <see langword="null"/>.
    /// </exception>
    public static DocumentValidationResult ValidateRecommendation(
        ControlLoopDefinition definition,
        ControlRecommendation recommendation)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(recommendation);
        List<DocumentValidationDiagnostic> diagnostics = [];
        AppendRecommendationShapeDiagnostics(definition, recommendation, diagnostics, "/recommendation");
        return DocumentValidationResult.FromDiagnostics(diagnostics);
    }

    /// <summary>Validates a durable actuation receipt against its exact bounded loop definition.</summary>
    /// <param name="definition">Canonical bounded loop definition.</param>
    /// <param name="actuation">Receipt with its generating observation, recommendation, and current safe-point evidence.</param>
    /// <returns>
    /// Structured diagnostics; an empty result proves the receipt's local definition-aware invariants. Historical
    /// prior-actuation existence, latest-predecessor ordering, revision, and operating-point linkage require
    /// current-state validation or resolution through the runtime's durable receipt ledger.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="definition"/> or <paramref name="actuation"/> is <see langword="null"/>.
    /// </exception>
    public static DocumentValidationResult ValidateActuation(
        ControlLoopDefinition definition,
        ControlActuation actuation)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(actuation);
        List<DocumentValidationDiagnostic> diagnostics = [];
        AppendActuationDiagnostics(definition, actuation, diagnostics, "/actuation");
        return DocumentValidationResult.FromDiagnostics(diagnostics);
    }

    static void AppendLimitUpdateDiagnostics(
        ControlLoopDefinition definition,
        ControlLoopState state,
        ICollection<DocumentValidationDiagnostic> diagnostics)
    {
        for (var index = 0; index < state.LimitUpdateActuations.Length; index++)
        {
            var actuation = state.LimitUpdateActuations[index];
            var command = actuation.Receipt.Command;
            var priorValidation = definition.ValidateOperatingPoint(actuation.PriorOperatingPoint);
            var requestedValidation = definition.ValidateOperatingPoint(command.RequestedOperatingPoint);
            foreach (var diagnostic in priorValidation.Diagnostics)
                diagnostics.Add(diagnostic);
            foreach (var diagnostic in requestedValidation.Diagnostics)
                diagnostics.Add(diagnostic);

            var transitionValid = ControlLimitUpdateReferenceReducer.TryGetApplicationKind(
                actuation.PriorOperatingPoint,
                command.RequestedOperatingPoint,
                out var expectedKind,
                out _);
            if (!transitionValid
                || actuation.Id != ControlDerivedIdentity.LimitUpdateActuation(
                    actuation.Receipt,
                    actuation.ApplicationPoint)
                || !string.Equals(
                    actuation.ApplicationPoint.Authority,
                    definition.ApplicationAuthority,
                    StringComparison.Ordinal)
                || transitionValid && actuation.ApplicationPoint.Kind != expectedKind)
            {
                diagnostics.Add(CreateDiagnostic(
                    ControlDiagnosticCodes.ActuationInvalid,
                    "Retained operator actuation is not the exact bounded transition authorized at its safe point.",
                    $"/state/limitUpdateActuations/{index}"));
            }
        }

        if (state.PendingLimitUpdate is { } pending)
        {
            var requestedValidation = definition.ValidateOperatingPoint(pending.Command.RequestedOperatingPoint);
            foreach (var diagnostic in requestedValidation.Diagnostics)
                diagnostics.Add(diagnostic);
            if (!ControlLimitUpdateReferenceReducer.TryGetApplicationKind(
                state.OperatingPoint,
                pending.Command.RequestedOperatingPoint,
                out _,
                out _))
            {
                diagnostics.Add(CreateDiagnostic(
                    ControlDiagnosticCodes.LimitUpdateInvalid,
                    "Pending operator override must be a non-empty bounded transition from the current point.",
                    "/state/pendingLimitUpdate/command/requestedOperatingPoint"));
            }
        }
    }

    static void ValidateLatestTransition(
        ControlLoopState state,
        ICollection<DocumentValidationDiagnostic> diagnostics)
    {
        var expectedRevision = ControlRevision.Initial;
        var expectedUpdatedAtUtc = state.CreatedAtUtc;

        void Consider(ControlRevision revision, DateTimeOffset atUtc)
        {
            if (revision.Ordinal > expectedRevision.Ordinal)
            {
                expectedRevision = revision;
                expectedUpdatedAtUtc = atUtc;
            }
        }

        if (state.LastObservation is { } observation
            && observation.ExpectedRevision.Ordinal < long.MaxValue
            && state.LastEvaluatedAtUtc is { } evaluatedAtUtc)
        {
            Consider(observation.ExpectedRevision.Next(), evaluatedAtUtc);
        }
        if (state.LastActuation is { } adaptive)
            Consider(adaptive.Revision, adaptive.AppliedAtUtc);
        if (state.LastLimitUpdateActuation is { } manual)
            Consider(manual.Revision, manual.AppliedAtUtc);
        if (state.PendingLimitUpdate is { } pending)
            Consider(pending.AcceptedRevision, pending.AcceptedAtUtc);

        if (state.Revision != expectedRevision || state.UpdatedAtUtc != expectedUpdatedAtUtc)
        {
            diagnostics.Add(CreateDiagnostic(
                ControlDiagnosticCodes.StateInvalid,
                "The shared revision and update time must identify the latest retained adaptive or operator transition.",
                "/state/revision",
                expected: $"{expectedRevision.Value}/{expectedUpdatedAtUtc:O}",
                observed: $"{state.Revision.Value}/{state.UpdatedAtUtc:O}"));
        }

        if (state.PendingLimitUpdate is not null && state.PendingRecommendation is not null)
        {
            diagnostics.Add(CreateDiagnostic(
                ControlDiagnosticCodes.StateInvalid,
                "Adaptive advice and an accepted operator override cannot be pending together.",
                "/state/pendingRecommendation"));
        }
    }

    static ImmutableArray<DocumentValidationDiagnostic> ValidateObservation(
        ControlLoopDefinition definition,
        ControlLoopState state,
        ControlObservation observation,
        DateTimeOffset evaluatedAtUtc)
    {
        List<DocumentValidationDiagnostic> diagnostics = [];
        if (observation.SchemaVersion != ControlLoopDefinition.CurrentSchemaVersion
            || observation.LoopId != state.LoopId
            || observation.DefinitionFingerprint != state.DefinitionFingerprint
            || observation.Epoch != state.Epoch
            || !string.Equals(observation.Target, state.Target, StringComparison.Ordinal)
            || observation.ExpectedRevision != state.Revision)
        {
            diagnostics.Add(CreateDiagnostic(
                observation.DefinitionFingerprint != state.DefinitionFingerprint
                    ? ControlDiagnosticCodes.DefinitionFingerprintMismatch
                    : ControlDiagnosticCodes.ObservationFenceMismatch,
                "Observation does not address the controller's exact loop, target, epoch, and revision.",
                "/observation",
                expected: $"{state.LoopId.Value}/{state.Target}/{state.Epoch.Value}/{state.Revision.Value}",
                observed: $"{observation.LoopId.Value}/{observation.Target}/{observation.Epoch.Value}/{observation.ExpectedRevision.Value}"));
        }

        var age = ElapsedMilliseconds(observation.WindowEndedAtUtc, evaluatedAtUtc);
        if (evaluatedAtUtc < observation.ObservedAtUtc
            || evaluatedAtUtc < state.UpdatedAtUtc
            || state.LastObservation is { } prior
                && (observation.ObservedAtUtc < prior.ObservedAtUtc
                    || observation.WindowEndedAtUtc < prior.WindowEndedAtUtc)
            || ElapsedExceeds(
                observation.WindowEndedAtUtc,
                evaluatedAtUtc,
                definition.Policy.MaximumObservationAgeMilliseconds))
        {
            diagnostics.Add(CreateDiagnostic(
                ControlDiagnosticCodes.ObservationTimeInvalid,
                "Observation and measurement window must be chronological, no later than evaluation, and within the configured age bound.",
                "/observation/observedAtUtc",
                expected: $"age <= {definition.Policy.MaximumObservationAgeMilliseconds} ms",
                observed: $"age {age} ms"));
        }

        AppendObjectiveMeasurementDiagnostics(definition, observation, diagnostics);

        return [.. diagnostics];
    }

    static void ValidatePendingRecommendation(
        ControlLoopDefinition definition,
        ControlLoopState state,
        ControlRecommendation recommendation,
        ICollection<DocumentValidationDiagnostic> diagnostics)
    {
        var observation = state.LastObservation!;
        var diagnosticCount = diagnostics.Count;
        AppendRecommendationDiagnostics(
            definition,
            recommendation,
            observation,
            diagnostics,
            "/state/pendingRecommendation");
        if (diagnostics.Count != diagnosticCount)
            return;

        var dwellSatisfied = state.LastAppliedAtUtc is not { } lastAppliedAtUtc
            || ElapsedMilliseconds(lastAppliedAtUtc, observation.WindowEndedAtUtc)
                >= definition.Policy.MinimumDwellMilliseconds;
        var cooldownSatisfied = recommendation.Direction != ControlRecommendationDirection.Increase
            || state.CooldownUntilUtc is null
            || observation.WindowEndedAtUtc >= state.CooldownUntilUtc;
        var countValid = recommendation.Direction == ControlRecommendationDirection.Increase
            ? state.HealthyObservationCount == recommendation.AuthorizingHealthyObservationCount
                && state.HealthyObservationCount >= definition.Policy.HealthyObservationCount
            : state.HealthyObservationCount == 0
                && recommendation.AuthorizingHealthyObservationCount == 0;
        if (recommendation.IssuedAtUtc != state.LastEvaluatedAtUtc
            || !countValid
            || !dwellSatisfied
            || !cooldownSatisfied)
        {
            diagnostics.Add(CreateDiagnostic(
                ControlDiagnosticCodes.RecommendationInvalid,
                "Pending recommendation is not the exact AIMD step authorized by its retained observation and policy state.",
                "/state/pendingRecommendation",
                expected: $"count >= {definition.Policy.HealthyObservationCount}; dwell/cooldown satisfied",
                observed: $"count {state.HealthyObservationCount}; direction {recommendation.Direction}"));
        }
    }

    static void AppendRecommendationDiagnostics(
        ControlLoopDefinition definition,
        ControlRecommendation recommendation,
        ControlObservation observation,
        ICollection<DocumentValidationDiagnostic> diagnostics,
        string location)
    {
        var shapeDiagnosticCount = diagnostics.Count;
        AppendRecommendationShapeDiagnostics(definition, recommendation, diagnostics, location);
        if (diagnostics.Count != shapeDiagnosticCount)
            return;

        if (observation.SchemaVersion != ControlLoopDefinition.CurrentSchemaVersion
            || observation.LoopId != recommendation.LoopId
            || observation.DefinitionFingerprint != recommendation.DefinitionFingerprint
            || !string.Equals(observation.Target, recommendation.Target, StringComparison.Ordinal)
            || observation.Epoch != recommendation.Epoch
            || observation.Id != recommendation.ObservationId
            || observation.ExpectedRevision.Ordinal != recommendation.ExpectedRevision.Ordinal - 1)
        {
            diagnostics.Add(CreateDiagnostic(
                ControlDiagnosticCodes.RecommendationInvalid,
                "Recommendation and observation do not share one exact definition, loop, target, epoch, and evaluation revision.",
                location));
            return;
        }

        var measurementDiagnosticCount = diagnostics.Count;
        AppendObjectiveMeasurementDiagnostics(definition, observation, diagnostics);
        if (diagnostics.Count != measurementDiagnosticCount)
            return;

        var classification = Classify(definition, observation);
        var expectedDirection = classification switch
        {
            ControlPressureClassification.Healthy => ControlRecommendationDirection.Increase,
            ControlPressureClassification.Congested => ControlRecommendationDirection.Decrease,
            _ => (ControlRecommendationDirection?)null
        };
        if (recommendation.Direction != expectedDirection
            || recommendation.IssuedAtUtc < observation.ObservedAtUtc
            || ElapsedExceeds(
                observation.WindowEndedAtUtc,
                recommendation.IssuedAtUtc,
                definition.Policy.MaximumObservationAgeMilliseconds))
        {
            diagnostics.Add(CreateDiagnostic(
                ControlDiagnosticCodes.RecommendationInvalid,
                "Recommendation direction and freshness are not authorized by its typed observation evidence.",
                location,
                expected: $"{classification}/{expectedDirection}",
                observed: recommendation.Direction.ToString()));
        }
    }

    static void AppendRecommendationShapeDiagnostics(
        ControlLoopDefinition definition,
        ControlRecommendation recommendation,
        ICollection<DocumentValidationDiagnostic> diagnostics,
        string location)
    {
        if (recommendation.LoopId != definition.Id
            || recommendation.DefinitionFingerprint != definition.Fingerprint
            || !string.Equals(recommendation.Target, definition.Target, StringComparison.Ordinal)
            || recommendation.Actuator != definition.Policy.Actuator)
        {
            diagnostics.Add(CreateDiagnostic(
                ControlDiagnosticCodes.RecommendationInvalid,
                "Recommendation does not belong to the supplied exact loop definition.",
                location));
            return;
        }

        var expectedId = ControlDerivedIdentity.Recommendation(
            recommendation.LoopId,
            recommendation.Target,
            recommendation.Epoch,
            recommendation.DefinitionFingerprint,
            recommendation.ExpectedRevision,
            recommendation.ObservationId,
            recommendation.PriorActuationId,
            recommendation.PriorActuationRevision);
        if (recommendation.Id != expectedId)
        {
            diagnostics.Add(CreateDiagnostic(
                ControlDiagnosticCodes.RecommendationInvalid,
                "Recommendation identity is not the canonical scoped identity derived from its evidence.",
                $"{location}/id",
                expected: expectedId.Value,
                observed: recommendation.Id.Value));
        }

        var priorActuationEvidencePaired = (recommendation.PriorActuationId is null)
            == (recommendation.PriorActuationRevision is null);
        var firstActuationRevisionOrdinal = ControlRevision.Initial.Ordinal + 2;
        var priorActuationRevisionPlausible = recommendation.PriorActuationRevision is not { } priorActuationRevision
            || priorActuationRevision.Ordinal >= firstActuationRevisionOrdinal
                && priorActuationRevision.Ordinal < recommendation.ExpectedRevision.Ordinal;
        var initialPointWithoutPriorActuation = recommendation.PriorActuationId is not null
            || recommendation.PriorOperatingPoint == definition.InitialOperatingPoint;
        if (!priorActuationEvidencePaired
            || !priorActuationRevisionPlausible
            || !initialPointWithoutPriorActuation)
        {
            diagnostics.Add(CreateDiagnostic(
                ControlDiagnosticCodes.RecommendationInvalid,
                "Recommendation prior-actuation evidence does not establish a reachable prior operating point and revision.",
                $"{location}/priorActuationId"));
        }

        var priorValidation = definition.ValidateOperatingPoint(recommendation.PriorOperatingPoint);
        foreach (var diagnostic in priorValidation.Diagnostics)
            diagnostics.Add(diagnostic);
        var proposedValidation = definition.ValidateOperatingPoint(recommendation.ProposedOperatingPoint);
        foreach (var diagnostic in proposedValidation.Diagnostics)
            diagnostics.Add(diagnostic);
        if (!priorValidation.IsValid || !proposedValidation.IsValid)
            return;

        var range = definition.GetEffectiveRange(definition.Policy.Actuator);
        var current = recommendation.PriorOperatingPoint.Get(definition.Policy.Actuator).Quantity.Value;
        var expectedValue = recommendation.Direction switch
        {
            ControlRecommendationDirection.Increase => Increase(
                current,
                definition.Policy.AdditiveIncrease.Value,
                range.Maximum.Value),
            ControlRecommendationDirection.Decrease => Decrease(
                current,
                definition.Policy.MultiplicativeDecreaseBasisPoints,
                range.Minimum.Value),
            _ => current
        };
        var proposed = recommendation.ProposedOperatingPoint.Get(definition.Policy.Actuator).Quantity.Value;
        var streakBaselineRevisionOrdinal = recommendation.PriorActuationRevision?.Ordinal
            ?? ControlRevision.Initial.Ordinal;
        var revisionAfterStreakBaseline = recommendation.ExpectedRevision.Ordinal > streakBaselineRevisionOrdinal;
        var maximumReachableHealthyObservationCount = revisionAfterStreakBaseline
            ? recommendation.ExpectedRevision.Ordinal - streakBaselineRevisionOrdinal
            : 0;
        var countValid = revisionAfterStreakBaseline
            && recommendation.AuthorizingHealthyObservationCount <= maximumReachableHealthyObservationCount
            && (recommendation.Direction == ControlRecommendationDirection.Increase
                ? recommendation.AuthorizingHealthyObservationCount >= definition.Policy.HealthyObservationCount
                : recommendation.AuthorizingHealthyObservationCount == 0);
        if (proposed != expectedValue || proposed == current || !countValid)
        {
            diagnostics.Add(CreateDiagnostic(
                ControlDiagnosticCodes.RecommendationInvalid,
                "Recommendation is not the exact bounded AIMD step with sufficient authorization evidence.",
                location,
                expected: $"{recommendation.Direction}/{expectedValue}",
                observed: $"{recommendation.Direction}/{proposed}"));
        }
    }

    static void AppendActuationDiagnostics(
        ControlLoopDefinition definition,
        ControlActuation actuation,
        ICollection<DocumentValidationDiagnostic> diagnostics,
        string location)
    {
        var recommendation = actuation.Recommendation;
        var observation = actuation.Observation;
        var applicationPoint = actuation.ApplicationPoint;
        AppendRecommendationDiagnostics(definition, recommendation, observation, diagnostics, $"{location}/recommendation");

        var expectedId = ControlDerivedIdentity.Actuation(recommendation, applicationPoint);
        if (actuation.Id != expectedId
            || applicationPoint.SchemaVersion != ControlLoopDefinition.CurrentSchemaVersion
            || applicationPoint.LoopId != definition.Id
            || applicationPoint.DefinitionFingerprint != definition.Fingerprint
            || !string.Equals(applicationPoint.Target, definition.Target, StringComparison.Ordinal)
            || applicationPoint.Epoch != recommendation.Epoch
            || applicationPoint.ExpectedRevision != recommendation.ExpectedRevision
            || !string.Equals(applicationPoint.Authority, definition.ApplicationAuthority, StringComparison.Ordinal)
            || applicationPoint.Kind != ControlApplicationPointCatalog.ForActuator(recommendation.Actuator)
            || applicationPoint.ObservedAtUtc < recommendation.IssuedAtUtc
            || actuation.AppliedAtUtc < applicationPoint.ObservedAtUtc
            || ElapsedExceeds(
                observation.WindowEndedAtUtc,
                actuation.AppliedAtUtc,
                definition.Policy.MaximumObservationAgeMilliseconds))
        {
            diagnostics.Add(CreateDiagnostic(
                ControlDiagnosticCodes.ActuationInvalid,
                "Actuation is not the exact fresh recommendation application authorized by the definition's safe-point contract.",
                location,
                expected: expectedId.Value,
                observed: actuation.Id.Value));
        }
    }

    static void AppendObjectiveMeasurementDiagnostics(
        ControlLoopDefinition definition,
        ControlObservation observation,
        ICollection<DocumentValidationDiagnostic> diagnostics)
    {
        foreach (var objective in definition.Objectives)
        {
            var measurement = observation.Find(objective.Metric, objective.Statistic);
            if (measurement is null)
            {
                diagnostics.Add(CreateDiagnostic(
                    ControlDiagnosticCodes.MeasurementMissing,
                    $"Required measurement '{objective.Metric}/{objective.Statistic}' is missing.",
                    "/observation/measurements",
                    expected: $"{objective.Metric}/{objective.Statistic}",
                    observed: "missing"));
                continue;
            }
            if (measurement.Availability == ControlMeasurementAvailability.Unavailable)
            {
                diagnostics.Add(CreateDiagnostic(
                    ControlDiagnosticCodes.MeasurementUnavailable,
                    $"Required measurement '{objective.Metric}/{objective.Statistic}' is unavailable.",
                    "/observation/measurements",
                    expected: "available",
                    observed: measurement.FailureCode ?? "unavailable"));
                continue;
            }
            if (measurement.SampleCount < definition.Policy.MinimumSampleCount)
            {
                diagnostics.Add(CreateDiagnostic(
                    ControlDiagnosticCodes.MeasurementInsufficient,
                    $"Required measurement '{objective.Metric}/{objective.Statistic}' has too few samples.",
                    "/observation/measurements",
                    expected: $">= {definition.Policy.MinimumSampleCount}",
                    observed: measurement.SampleCount.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            }
        }
    }

    static ControlPressureClassification Classify(
        ControlLoopDefinition definition,
        ControlObservation observation)
    {
        var allHealthy = true;
        foreach (var objective in definition.Objectives)
        {
            var measurement = observation.Find(objective.Metric, objective.Statistic)
                ?? throw new InvalidOperationException("Validated objective measurement is missing.");
            var value = measurement.Value
                ?? throw new InvalidOperationException("Validated objective measurement is unavailable.");
            var congested = objective.Direction == ControlObjectiveDirection.HigherIsCongested
                ? value.Value >= objective.CongestionBoundary.Value
                : value.Value <= objective.CongestionBoundary.Value;
            if (congested)
                return ControlPressureClassification.Congested;
            var healthy = objective.Direction == ControlObjectiveDirection.HigherIsCongested
                ? value.Value <= objective.RecoveryBoundary.Value
                : value.Value >= objective.RecoveryBoundary.Value;
            if (!healthy)
                allHealthy = false;
        }

        return allHealthy
            ? ControlPressureClassification.Healthy
            : ControlPressureClassification.Hysteresis;
    }

    static ControlRecommendation CreateRecommendation(
        ControlLoopDefinition definition,
        ControlLoopState state,
        ControlObservation observation,
        ControlRevision expectedRevision,
        ControlRecommendationDirection direction,
        long authorizingHealthyObservationCount,
        long proposedValue,
        DateTimeOffset issuedAtUtc)
    {
        var unit = ControlUnitCatalog.ForActuator(definition.Policy.Actuator);
        var proposed = state.OperatingPoint.With(
            new(definition.Policy.Actuator, new(proposedValue, unit)));
        var priorActuationId = state.LastAppliedActuationId;
        var priorActuationRevision = state.LastAppliedActuationRevision;
        return new(
            ControlDerivedIdentity.Recommendation(
                state.LoopId,
                state.Target,
                state.Epoch,
                state.DefinitionFingerprint,
                expectedRevision,
                observation.Id,
                priorActuationId,
                priorActuationRevision),
            state.LoopId,
            state.DefinitionFingerprint,
            state.Target,
            state.Epoch,
            expectedRevision,
            observation.Id,
            definition.Policy.Actuator,
            direction,
            authorizingHealthyObservationCount,
            state.OperatingPoint,
            proposed,
            issuedAtUtc,
            priorActuationId,
            priorActuationRevision);
    }

    static long Increase(long current, long additiveIncrease, long maximum) =>
        additiveIncrease >= maximum - current
            ? maximum
            : current + additiveIncrease;

    static long Decrease(long current, long factorBasisPoints, long minimum)
    {
        var candidate = (long)((BigInteger)current * factorBasisPoints / 10_000);
        if (candidate >= current)
            candidate = current - 1;
        return Math.Max(candidate, minimum);
    }

    static long ElapsedMilliseconds(DateTimeOffset startedAtUtc, DateTimeOffset endedAtUtc)
    {
        var ticks = endedAtUtc.UtcDateTime.Ticks - startedAtUtc.UtcDateTime.Ticks;
        return ticks / TimeSpan.TicksPerMillisecond;
    }

    static bool ElapsedExceeds(
        DateTimeOffset startedAtUtc,
        DateTimeOffset endedAtUtc,
        long maximumMilliseconds)
    {
        if (endedAtUtc < startedAtUtc)
            return false;
        var elapsedTicks = endedAtUtc.UtcDateTime.Ticks - startedAtUtc.UtcDateTime.Ticks;
        return (BigInteger)elapsedTicks > (BigInteger)maximumMilliseconds * TimeSpan.TicksPerMillisecond;
    }

    static DateTimeOffset AddMillisecondsSaturating(DateTimeOffset value, long milliseconds)
    {
        var maximumTicks = DateTimeOffset.MaxValue.UtcDateTime.Ticks - value.UtcDateTime.Ticks;
        if (milliseconds > maximumTicks / TimeSpan.TicksPerMillisecond)
            return new(DateTimeOffset.MaxValue.UtcDateTime, TimeSpan.Zero);
        return value.AddMilliseconds(milliseconds);
    }

    static bool HasSameObservationScope(ControlObservation first, ControlObservation second) =>
        first.Id == second.Id
        && first.LoopId == second.LoopId
        && first.DefinitionFingerprint == second.DefinitionFingerprint
        && string.Equals(first.Target, second.Target, StringComparison.Ordinal)
        && first.Epoch == second.Epoch
        && first.ExpectedRevision == second.ExpectedRevision;

    static bool HasSameApplicationPointScope(
        ControlApplicationPoint first,
        ControlApplicationPoint second) =>
        first.Id == second.Id
        && first.LoopId == second.LoopId
        && first.DefinitionFingerprint == second.DefinitionFingerprint
        && string.Equals(first.Target, second.Target, StringComparison.Ordinal)
        && first.Epoch == second.Epoch
        && first.ExpectedRevision == second.ExpectedRevision;

    static ControlDecision Rejected(
        ControlLoopState state,
        DateTimeOffset evaluatedAtUtc,
        ImmutableArray<DocumentValidationDiagnostic> diagnostics) =>
        new(
            ControlLoopDefinition.CurrentSchemaVersion,
            ControlDecisionDisposition.Rejected,
            evaluatedAtUtc,
            state,
            diagnostics: diagnostics);

    static ControlActuationResult RejectedActuation(
        ControlLoopState state,
        ImmutableArray<DocumentValidationDiagnostic> diagnostics) =>
        new(
            ControlLoopDefinition.CurrentSchemaVersion,
            ControlActuationDisposition.Rejected,
            state,
            diagnostics: diagnostics);

    static ImmutableArray<DocumentValidationDiagnostic> Diagnostic(
        string code,
        string message,
        string location,
        string? expected = null,
        string? observed = null) =>
        [CreateDiagnostic(code, message, location, expected, observed)];

    static ImmutableArray<DocumentValidationDiagnostic> Diagnostic(
        string code,
        string message,
        string location,
        DiagnosticSeverity severity) =>
        [CreateDiagnostic(code, message, location, severity: severity)];

    static ImmutableArray<DocumentValidationDiagnostic> RevisionExhaustedDiagnostic(ControlLoopState state) =>
        Diagnostic(
            ControlDiagnosticCodes.RevisionExhausted,
            "The control revision space is exhausted; begin a new control epoch before accepting more evidence.",
            "/state/revision",
            expected: $"< {long.MaxValue}",
            observed: state.Revision.Value);

    static DocumentValidationDiagnostic CreateDiagnostic(
        string code,
        string message,
        string location,
        string? expected = null,
        string? observed = null,
        DiagnosticSeverity severity = DiagnosticSeverity.Error) =>
        new(
            code,
            severity,
            message,
            location,
            Evidence: new(
                stage: "control-reference-regulator",
                expected: expected,
                observed: observed));
}
