using System.Collections.Immutable;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Observability;

namespace Cohesive.Relations.Realization;

/// <summary>
/// Projects a demand-scoped compiled plan and deterministically matches every semantic requirement to one target.
/// </summary>
public static class RelationQueryRealizationCompiler
{
    /// <summary>Projects and matches one compiled relation/query plan to a target capability profile.</summary>
    /// <param name="plan">Demand-scoped compiled semantic plan to realize.</param>
    /// <param name="targetProfile">Typed capabilities, guarantees, and boundaries advertised by the target.</param>
    /// <param name="policy">Explicit compiler policy, composition rules, and local overrides.</param>
    /// <returns>
    /// A deterministic derived report containing exactly one final decision for every demanded requirement.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="plan"/>, <paramref name="targetProfile"/>, or <paramref name="policy"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The compiled execution slice and input contract contain inconsistent provenance, or a shape snapshot cannot
    /// be represented by compiled-plan canonicalization.
    /// </exception>
    /// <exception cref="System.Text.Json.JsonException">
    /// A shape snapshot cannot be serialized as canonical JSON.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// A shape snapshot contains a runtime type unsupported by its JSON serializer.
    /// </exception>
    /// <remarks>
    /// This compatibility overload requires <see cref="RelationQueryResultObservability.ExactContributors"/>.
    /// </remarks>
    public static RelationQueryRealizationReport Compile(
        CompiledRelationQueryPlan plan,
        RelationQueryTargetCapabilityProfile targetProfile,
        RelationQueryRealizationPolicy policy)
        => Compile(
            plan,
            targetProfile,
            policy,
            RelationQueryResultObservability.ExactContributors);

    /// <summary>
    /// Projects and matches one compiled relation/query plan to a target capability profile under an explicit
    /// result-observability contract.
    /// </summary>
    /// <param name="plan">Demand-scoped compiled semantic plan to realize.</param>
    /// <param name="targetProfile">Typed capabilities, guarantees, and boundaries advertised by the target.</param>
    /// <param name="policy">Explicit compiler policy, composition rules, and local overrides.</param>
    /// <param name="observability">Runtime result observability required from the interpretation.</param>
    /// <returns>
    /// A deterministic derived report containing exactly one final decision for every demanded requirement.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="plan"/>, <paramref name="targetProfile"/>, or <paramref name="policy"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The compiled execution slice and input contract contain inconsistent provenance, or a shape snapshot cannot
    /// be represented by compiled-plan canonicalization.
    /// </exception>
    /// <exception cref="System.Text.Json.JsonException">
    /// A shape snapshot cannot be serialized as canonical JSON.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// A shape snapshot contains a runtime type unsupported by its JSON serializer.
    /// </exception>
    public static RelationQueryRealizationReport Compile(
        CompiledRelationQueryPlan plan,
        RelationQueryTargetCapabilityProfile targetProfile,
        RelationQueryRealizationPolicy policy,
        RelationQueryResultObservability observability)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return RelationQueryTelemetryRuntime.IsOperationEnabled
            ? CompileObserved(plan, targetProfile, policy, observability)
            : CompileCore(plan, targetProfile, policy, observability);
    }

    static RelationQueryRealizationReport CompileCore(
        CompiledRelationQueryPlan plan,
        RelationQueryTargetCapabilityProfile targetProfile,
        RelationQueryRealizationPolicy policy,
        RelationQueryResultObservability observability) => MatchCore(
            RelationQueryCompiledPlanReference.From(plan),
            RelationQueryRealizationRequirementProjector.Project(plan, observability),
            targetProfile,
            policy,
            observability);

    static RelationQueryRealizationReport CompileObserved(
        CompiledRelationQueryPlan plan,
        RelationQueryTargetCapabilityProfile targetProfile,
        RelationQueryRealizationPolicy policy,
        RelationQueryResultObservability observability)
    {
        var activity = RelationQueryTelemetryRuntime.StartActivity(
            RelationQueryTelemetry.ProfileFeasibilityActivityName);
        var started = RelationQueryTelemetryRuntime.StartTimer();
        Exception? failure = null;
        RelationQueryRealizationReport? result = null;
        try
        {
            result = CompileCore(plan, targetProfile, policy, observability);
            if (activity?.IsAllDataRequested == true)
            {
                RelationQueryTelemetry.TrySetFingerprintTag(
                    activity,
                    RelationQueryTelemetry.PlanFingerprintTagName,
                    RelationQueryCompiledPlanReferenceFingerprinter.Compute(result.Plan).Value);
                activity.SetTag(RelationQueryTelemetry.TargetTagName, targetProfile.Target.Value);
                RelationQueryTelemetry.TrySetFingerprintTag(
                    activity,
                    RelationQueryTelemetry.RealizationFingerprintTagName,
                    result.Fingerprint.Value);
                activity.SetTag(RelationQueryTelemetry.DiagnosticCountTagName, result.Diagnostics.Length);
                foreach (var diagnostic in result.Diagnostics)
                {
                    RelationQueryTelemetry.AddDiagnosticEvent(
                        activity,
                        diagnostic.Code,
                        diagnostic.Severity);
                }
            }
            return result;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException
                                          and not StackOverflowException
                                          and not AccessViolationException)
        {
            failure = exception;
            throw;
        }
        finally
        {
            RelationQueryTelemetryRuntime.CompleteOperation(
                activity,
                started,
                RelationQueryTelemetry.ProfileFeasibilityActivityName,
                failure is not null || result is null
                    ? RelationQueryTelemetry.ExceptionStatus
                    : RelationQueryTelemetry.GetStatusTagValue(result.Status),
                exception: failure);
        }
    }

    /// <summary>Matches an already projected requirement set to one target capability profile.</summary>
    /// <param name="plan">Portable provenance for the exact compiled plan that produced the requirements.</param>
    /// <param name="requirements">Complete demand-scoped requirements projected from that plan.</param>
    /// <param name="targetProfile">Typed capabilities, guarantees, and boundaries advertised by the target.</param>
    /// <param name="policy">Explicit compiler policy, composition rules, and local overrides.</param>
    /// <returns>
    /// A deterministic derived report containing exactly one final decision for every supplied requirement.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="plan"/>, <paramref name="targetProfile"/>, or <paramref name="policy"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="requirements"/> is empty, contains a <see langword="null"/> entry, or repeats an identity.
    /// </exception>
    internal static RelationQueryRealizationReport Match(
        RelationQueryCompiledPlanReference plan,
        ImmutableArray<RelationQueryRealizationRequirement> requirements,
        RelationQueryTargetCapabilityProfile targetProfile,
        RelationQueryRealizationPolicy policy)
    {
        return MatchCore(
            plan,
            requirements,
            targetProfile,
            policy,
            RelationQueryResultObservability.ExactContributors);
    }

    internal static RelationQueryRealizationReport Match(
        RelationQueryCompiledPlanReference plan,
        ImmutableArray<RelationQueryRealizationRequirement> requirements,
        RelationQueryTargetCapabilityProfile targetProfile,
        RelationQueryRealizationPolicy policy,
        RelationQueryResultObservability observability)
    {
        return MatchCore(plan, requirements, targetProfile, policy, observability);
    }

    static RelationQueryRealizationReport MatchCore(
        RelationQueryCompiledPlanReference plan,
        ImmutableArray<RelationQueryRealizationRequirement> requirements,
        RelationQueryTargetCapabilityProfile targetProfile,
        RelationQueryRealizationPolicy policy,
        RelationQueryResultObservability observability)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(targetProfile);
        ArgumentNullException.ThrowIfNull(policy);

        var normalizedRequirements = NormalizeRequirements(requirements);
        Matcher matcher = new(plan, normalizedRequirements, targetProfile, policy, observability);
        return matcher.Match();
    }

    static ImmutableArray<RelationQueryRealizationRequirement> NormalizeRequirements(
        ImmutableArray<RelationQueryRealizationRequirement> requirements)
    {
        var normalized = requirements.IsDefault ? [] : requirements;
        if (normalized.IsDefaultOrEmpty)
            throw new ArgumentException("At least one realization requirement is required.", nameof(requirements));
        if (normalized.Any(static requirement => requirement is null))
            throw new ArgumentException("Realization requirements cannot contain null entries.", nameof(requirements));
        if (normalized.GroupBy(static requirement => requirement.Id).Any(static group => group.Count() > 1))
            throw new ArgumentException("Realization requirements cannot repeat an identity.", nameof(requirements));
        return [.. normalized.OrderBy(static requirement => requirement.Id.Value, StringComparer.Ordinal)];
    }

    sealed class Matcher
    {
        readonly RelationQueryCompiledPlanReference plan;
        readonly ImmutableArray<RelationQueryRealizationRequirement> requirements;
        readonly RelationQueryTargetCapabilityProfile profile;
        readonly RelationQueryRealizationPolicy policy;
        readonly RelationQueryResultObservability observability;
        readonly RelationQueryTargetCapabilityProfileAnalysis profileAnalysis;
        readonly ImmutableArray<RelationQueryTargetCapabilityEvidence> capabilityEvidence;
        readonly Dictionary<RelationQueryCapability, ImmutableArray<Proof>> proofs = [];
        readonly Dictionary<RelationQueryCapability, ImmutableArray<RelationQueryCapability>> missing = [];
        readonly HashSet<RelationQueryCompositionRuleId> invalidRules = [];
        readonly ImmutableHashSet<RelationQueryOperatingBoundaryId> boundaryIds;
        readonly ImmutableDictionary<RelationQueryTargetCapabilityEvidenceId, RelationQueryTargetCapabilityEvidence>
            evidenceById;
        readonly ImmutableDictionary<RelationQueryCapability, RelationQueryCompositionRuleSelection> ruleSelections;
        readonly ImmutableDictionary<RelationQueryCompositionRuleId, RelationQueryCompositionRule> rulesById;
        readonly ImmutableDictionary<string, DiagnosticSeverity> severityOverrides;
        readonly ImmutableArray<RelationQueryRealizationDiagnostic>.Builder diagnostics =
            ImmutableArray.CreateBuilder<RelationQueryRealizationDiagnostic>();
        readonly Dictionary<RelationQueryRealizationRequirementId, ImmutableArray<StrategyFailure>> strategyFailures = [];
        bool invalid;

        public Matcher(
            RelationQueryCompiledPlanReference plan,
            ImmutableArray<RelationQueryRealizationRequirement> requirements,
            RelationQueryTargetCapabilityProfile profile,
            RelationQueryRealizationPolicy policy,
            RelationQueryResultObservability observability)
        {
            this.plan = plan;
            this.requirements = requirements;
            this.profile = profile;
            this.policy = policy;
            this.observability = observability;
            profileAnalysis = RelationQueryTargetCapabilityProfileAnalysis.Analyze(profile);
            capabilityEvidence =
            [
                .. profileAnalysis.Evidence.Values
                    .OrderBy(static evidence => evidence.Id.Value, StringComparer.Ordinal)
            ];
            boundaryIds = profileAnalysis.Boundaries.Keys.ToImmutableHashSet();
            evidenceById = profileAnalysis.Evidence;
            ruleSelections = policy.CompositionRuleSelections.ToImmutableDictionary(static selection => selection.Capability);
            rulesById = policy.CompositionRules.ToImmutableDictionary(static rule => rule.Id);
            severityOverrides = policy.DiagnosticSeverityOverrides.ToImmutableDictionary(
                static item => item.Code,
                static item => item.Severity,
                StringComparer.Ordinal);
        }

        public RelationQueryRealizationReport Match()
        {
            foreach (var issue in profileAnalysis.Issues)
            {
                invalid = true;
                AddDiagnostic(
                    issue.Code,
                    issue.Message,
                    capabilityEvidence: issue.CapabilityEvidence,
                    operatingBoundary: issue.OperatingBoundary,
                    fatal: true);
            }
            ValidateInputDeclarations();
            if (!profileAnalysis.Issues.IsDefaultOrEmpty)
            {
                return CreateReport(
                    [
                        .. requirements.Select(static requirement =>
                            (RelationQueryRealizationDecision)new UnavailableRelationQueryRealizationDecision(
                                requirement.Id,
                                RelationQueryUnavailableReason.CapabilityEvidenceInvalid,
                                [requirement.Capability]))
                    ],
                    RelationQueryRealizationStatus.Invalid);
            }

            var relevantCapabilities = FindRelevantCapabilities();
            ValidatePolicyReferences(relevantCapabilities);
            ValidateCompositionCycles(relevantCapabilities);

            var supportsPlan = profile.SupportedDefinitionSchemaVersions.Contains(
                                   plan.DefinitionSchemaVersion,
                                   StringComparer.Ordinal)
                               && profile.SupportedCompilerProfiles.Contains(
                                   plan.CompilerProfile,
                                   StringComparer.Ordinal);

            ImmutableArray<RelationQueryRealizationDecision>.Builder decisions =
                ImmutableArray.CreateBuilder<RelationQueryRealizationDecision>(requirements.Length);
            foreach (var requirement in requirements)
            {
                decisions.Add(supportsPlan
                    ? Decide(requirement)
                    : UnsupportedProfile(requirement));
            }

            var normalizedDecisions = decisions.ToImmutable();
            var status = invalid
                ? RelationQueryRealizationStatus.Invalid
                : normalizedDecisions.Any(static decision =>
                    decision.Kind == CapabilityRealizationKind.Unavailable)
                    ? RelationQueryRealizationStatus.NotRealizable
                    : RelationQueryRealizationStatus.Realizable;
            return CreateReport(normalizedDecisions, status);
        }

        RelationQueryRealizationReport CreateReport(
            ImmutableArray<RelationQueryRealizationDecision> decisions,
            RelationQueryRealizationStatus status)
        {
            var normalizedDiagnostics = diagnostics.ToImmutable();
            var fingerprint = RelationQueryRealizationFingerprinter.Compute(
                plan,
                profile,
                policy,
                observability,
                requirements,
                decisions,
                normalizedDiagnostics,
                status);
            return new(
                plan,
                profile,
                policy,
                requirements,
                decisions,
                normalizedDiagnostics,
                status,
                fingerprint,
                observability);
        }

        void ValidateInputDeclarations()
        {
            foreach (var requirement in requirements)
            {
                if (RelationQueryTargetCapabilityProfileAnalysis.GetCapabilityProblem(requirement.Capability)
                    is not { } problem)
                {
                    continue;
                }

                invalid = true;
                AddDiagnostic(
                    RelationQueryRealizationDiagnosticCodes.RequirementInvalid,
                    $"Requirement '{requirement.Id.Value}' contains an invalid capability: {problem}.",
                    requirement,
                    fatal: true);
            }

            foreach (var rule in policy.CompositionRules)
            {
                var problem = RelationQueryTargetCapabilityProfileAnalysis.GetCapabilityProblem(
                                  rule.ProvidedCapability)
                              ?? rule.RequiredCapabilities
                                  .Select(RelationQueryTargetCapabilityProfileAnalysis.GetCapabilityProblem)
                                  .FirstOrDefault(static candidate => candidate is not null);
                if (problem is null)
                    continue;

                invalid = true;
                invalidRules.Add(rule.Id);
                AddDiagnostic(
                    RelationQueryRealizationDiagnosticCodes.CompositionRuleInvalid,
                    $"Composition rule '{rule.Id.Value}' contains an invalid capability: {problem}.",
                    compositionRule: rule.Id,
                    fatal: true);
            }

            foreach (var selection in policy.CompositionRuleSelections)
            {
                if (RelationQueryTargetCapabilityProfileAnalysis.GetCapabilityProblem(selection.Capability)
                    is not { } problem)
                {
                    continue;
                }

                invalid = true;
                AddDiagnostic(
                    RelationQueryRealizationDiagnosticCodes.PolicyInvalid,
                    $"Composition-rule selection contains an invalid capability: {problem}.",
                    compositionRule: selection.Rule,
                    fatal: true);
            }

            foreach (var @override in policy.Overrides)
            {
                if (RelationQueryTargetCapabilityProfileAnalysis.GetCapabilityProblem(@override.ExpectedCapability)
                    is not { } problem)
                {
                    continue;
                }

                invalid = true;
                AddDiagnostic(
                    RelationQueryRealizationDiagnosticCodes.OverrideInvalid,
                    $"Override '{@override.Id.Value}' contains an invalid capability: {problem}.",
                    @override: @override.Id,
                    fatal: true);
            }
        }

        RelationQueryRealizationDecision Decide(RelationQueryRealizationRequirement requirement)
        {
            var overrides = policy.Overrides
                .Where(item => item.Requirement == requirement.Id)
                .ToImmutableArray();
            if (!overrides.IsDefaultOrEmpty)
                return DecideOverride(requirement, overrides);

            var resolved = Resolve(requirement.Capability, []);
            if (policy.ConstrainedRealizations == RelationQueryConstrainedRealizationPolicy.Reject)
            {
                var permitted = resolved.Proofs
                    .Where(static proof => proof.Boundaries.IsDefaultOrEmpty)
                    .ToImmutableArray();
                if (permitted.IsDefaultOrEmpty)
                    return Unavailable(requirement);
                resolved = new(permitted, resolved.Missing);
            }

            ImmutableArray<ValidatedProof>.Builder candidates = ImmutableArray.CreateBuilder<ValidatedProof>();
            ImmutableArray<StrategyFailure>.Builder failures = ImmutableArray.CreateBuilder<StrategyFailure>();
            foreach (var proof in resolved.Proofs)
            {
                var guaranteed = ValidateGuarantees(proof, requirement);
                if (guaranteed.Failure is { } guaranteeFailure)
                {
                    failures.Add(guaranteeFailure);
                    continue;
                }

                foreach (var guaranteedProof in guaranteed.Proofs)
                {
                    var bounded = ValidateBoundaries(
                        guaranteedProof,
                        requirement,
                        allowAutomaticTargetEnforcement: true);
                    if (bounded.Failure is { } boundaryFailure)
                    {
                        failures.Add(boundaryFailure);
                        continue;
                    }
                    candidates.Add(new(bounded.Proof!, bounded.Validations));
                }
            }

            if (candidates.Count == 0)
            {
                strategyFailures[requirement.Id] = failures
                    .Distinct()
                    .OrderBy(static failure => failure.Code, StringComparer.Ordinal)
                    .ThenBy(static failure => failure.Boundary?.Value ?? string.Empty, StringComparer.Ordinal)
                    .ToImmutableArray();
                return Unavailable(requirement);
            }

            var preferred = SelectPreferred(candidates.ToImmutable());
            if (preferred.Length != 1)
            {
                invalid = true;
                AddDiagnostic(
                    RelationQueryRealizationDiagnosticCodes.StrategyAmbiguous,
                    $"Requirement '{requirement.Id.Value}' has {preferred.Length} equally preferred exact realization strategies.",
                    requirement,
                    fatal: true);
                return new UnavailableRelationQueryRealizationDecision(
                    requirement.Id,
                    RelationQueryUnavailableReason.AmbiguousStrategy);
            }

            return preferred[0].ToDecision(requirement.Id);
        }

        RelationQueryRealizationDecision DecideOverride(
            RelationQueryRealizationRequirement requirement,
            ImmutableArray<RelationQueryRealizationOverride> overrides)
        {
            if (overrides.Length != 1)
            {
                invalid = true;
                AddDiagnostic(
                    RelationQueryRealizationDiagnosticCodes.OverrideInvalid,
                    $"Requirement '{requirement.Id.Value}' has several explicit realization overrides.",
                    requirement,
                    @override: overrides[0].Id,
                    fatal: true);
                return new UnavailableRelationQueryRealizationDecision(
                    requirement.Id,
                    RelationQueryUnavailableReason.OverrideInvalid);
            }

            var selected = overrides[0];
            var selectedBoundaryIds = selected.OperatingBoundaries.ToHashSet();
            var valid = Equals(selected.ExpectedCapability, requirement.Capability)
                        && selected.CapabilityEvidence.All(evidenceById.ContainsKey)
                        && selected.OperatingBoundaries.All(boundaryIds.Contains)
                        && selected.CapabilityEvidence
                            .Select(item => evidenceById[item])
                            .SelectMany(static evidence => evidence.OperatingBoundaries)
                            .All(selectedBoundaryIds.Contains);
            if (!valid)
            {
                invalid = true;
                AddDiagnostic(
                    RelationQueryRealizationDiagnosticCodes.OverrideInvalid,
                    $"Override '{selected.Id.Value}' is stale, references unavailable target evidence or boundaries, or omits a boundary required by its selected evidence in target profile '{profile.Id.Value}'.",
                    requirement,
                    @override: selected.Id,
                    fatal: true);
                return new UnavailableRelationQueryRealizationDecision(
                    requirement.Id,
                    RelationQueryUnavailableReason.OverrideInvalid);
            }

            var missingGuarantees = requirement.RequiredGuarantees
                .Except(selected.PreservedGuarantees)
                .ToImmutableArray();
            if (!missingGuarantees.IsDefaultOrEmpty)
            {
                invalid = true;
                AddDiagnostic(
                    RelationQueryRealizationDiagnosticCodes.OverrideInvalid,
                    $"Override '{selected.Id.Value}' does not preserve every guarantee required by '{requirement.Id.Value}'.",
                    requirement,
                    @override: selected.Id,
                    fatal: true);
                return new UnavailableRelationQueryRealizationDecision(
                    requirement.Id,
                    RelationQueryUnavailableReason.OverrideInvalid,
                    [.. missingGuarantees.Select(static guarantee =>
                        new GuaranteeRelationQueryCapability(guarantee))]);
            }

            var proof = new Proof(
                selected.CapabilityEvidence,
                selected.OperatingBoundaries,
                [],
                selected.PreservedGuarantees);
            var bounded = ValidateBoundaries(proof, requirement, allowAutomaticTargetEnforcement: false);
            if (bounded.Failure is { } failure)
            {
                strategyFailures[requirement.Id] = [failure];
                AddFailureDiagnostics(requirement, [failure]);
                return new UnavailableRelationQueryRealizationDecision(
                    requirement.Id,
                    failure.Reason,
                    failure.MissingCapabilities);
            }

            return new OverrideRelationQueryRealizationDecision(
                requirement.Id,
                selected.Id,
                selected.CapabilityEvidence,
                bounded.Validations,
                selected.PreservedGuarantees);
        }

        RelationQueryRealizationDecision UnsupportedProfile(RelationQueryRealizationRequirement requirement)
        {
            AddDiagnostic(
                RelationQueryRealizationDiagnosticCodes.TargetProfileVersionUnsupported,
                $"Target profile '{profile.Id.Value}' does not support definition schema '{plan.DefinitionSchemaVersion}' and compiler profile '{plan.CompilerProfile}'.",
                requirement);
            return new UnavailableRelationQueryRealizationDecision(
                requirement.Id,
                RelationQueryUnavailableReason.ProfileVersionUnsupported,
                [requirement.Capability]);
        }

        RelationQueryRealizationDecision Unavailable(RelationQueryRealizationRequirement requirement)
        {
            var resolution = Resolve(requirement.Capability, []);
            if (strategyFailures.TryGetValue(requirement.Id, out var failures) && !failures.IsDefaultOrEmpty)
            {
                AddFailureDiagnostics(requirement, failures);
                var failure = failures[0];
                return new UnavailableRelationQueryRealizationDecision(
                    requirement.Id,
                    failure.Reason,
                    failure.MissingCapabilities);
            }

            var hasEvidence = profile.Capabilities.Any(item => Equals(item.Capability, requirement.Capability));
            var hasRule = policy.CompositionRules.Any(rule =>
                Equals(rule.ProvidedCapability, requirement.Capability) && !invalidRules.Contains(rule.Id));
            var rejectedConstraint = resolution.Proofs.Any(static proof => !proof.Boundaries.IsDefaultOrEmpty)
                                     && policy.ConstrainedRealizations == RelationQueryConstrainedRealizationPolicy.Reject;
            var reason = rejectedConstraint
                ? RelationQueryUnavailableReason.PolicyRejected
                : hasRule
                    ? RelationQueryUnavailableReason.CompositionUnavailable
                    : hasEvidence
                        ? RelationQueryUnavailableReason.CapabilityEvidenceInvalid
                        : RelationQueryUnavailableReason.CapabilityNotAdvertised;
            var code = rejectedConstraint
                ? RelationQueryRealizationDiagnosticCodes.PolicyInvalid
                : hasEvidence
                    ? RelationQueryRealizationDiagnosticCodes.CapabilityEvidenceInvalid
                : RelationQueryRealizationDiagnosticCodes.RequirementUnavailable;
            var missingCapabilities = rejectedConstraint
                ? ImmutableArray<RelationQueryCapability>.Empty
                : resolution.Missing.IsDefaultOrEmpty
                    ? ImmutableArray.Create(requirement.Capability)
                    : resolution.Missing;
            AddDiagnostic(
                code,
                $"Target '{profile.Target.Value}' has no permitted exact realization for requirement '{requirement.Id.Value}'.",
                requirement);
            return new UnavailableRelationQueryRealizationDecision(
                requirement.Id,
                reason,
                missingCapabilities);
        }

        void AddFailureDiagnostics(
            RelationQueryRealizationRequirement requirement,
            ImmutableArray<StrategyFailure> failures)
        {
            foreach (var failure in failures)
            {
                AddDiagnostic(
                    failure.Code,
                    failure.Message,
                    requirement,
                    operatingBoundary: failure.Boundary);
            }
        }

        ImmutableArray<ValidatedProof> SelectPreferred(ImmutableArray<ValidatedProof> candidates)
        {
            var minimumBoundaryRank = candidates.Min(static candidate => candidate.Validations.IsDefaultOrEmpty ? 0 : 1);
            var withinBoundaryRank = candidates
                .Where(candidate => (candidate.Validations.IsDefaultOrEmpty ? 0 : 1) == minimumBoundaryRank)
                .ToImmutableArray();
            var preferredRuleCount = policy.Preference == RelationQueryRealizationPreference.PreferNative
                ? withinBoundaryRank.Min(static candidate => candidate.Proof.Rules.Length)
                : withinBoundaryRank.Max(static candidate => candidate.Proof.Rules.Length);
            return
            [
                .. withinBoundaryRank
                    .Where(candidate => candidate.Proof.Rules.Length == preferredRuleCount)
                    .OrderBy(static candidate => candidate.Key, StringComparer.Ordinal)
            ];
        }

        GuaranteeValidation ValidateGuarantees(
            Proof proof,
            RelationQueryRealizationRequirement requirement)
        {
            if (requirement.RequiredGuarantees.IsDefaultOrEmpty)
                return new([proof], null);

            var missingGuarantees = requirement.RequiredGuarantees
                .Except(proof.Guarantees)
                .ToImmutableArray();
            if (missingGuarantees.IsDefaultOrEmpty)
                return new([proof], null);

            if (!proof.Rules.IsDefaultOrEmpty)
            {
                return new(
                    [],
                    new(
                        RelationQueryUnavailableReason.CompositionUnavailable,
                        RelationQueryRealizationDiagnosticCodes.RequirementUnavailable,
                        $"Composed strategy for requirement '{requirement.Id.Value}' does not preserve guarantees: "
                        + string.Join(", ", missingGuarantees),
                        Boundary: null,
                        [.. missingGuarantees.Select(static guarantee =>
                            new GuaranteeRelationQueryCapability(guarantee))]));
            }

            var guaranteed = ImmutableArray.Create(proof);
            foreach (var guarantee in missingGuarantees)
            {
                var capability = new GuaranteeRelationQueryCapability(guarantee);
                var guaranteeProofs = capabilityEvidence
                    .Where(item => Equals(item.Capability, capability))
                    .GroupBy(
                        static item => BoundaryKey(item.OperatingBoundaries),
                        StringComparer.Ordinal)
                    .OrderBy(static group => group.Key, StringComparer.Ordinal)
                    .Select(static group => new Proof(
                        [.. group.Select(static item => item.Id)],
                        group.First().OperatingBoundaries,
                        [],
                        [((GuaranteeRelationQueryCapability)group.First().Capability).Kind]))
                    .ToImmutableArray();
                if (guaranteeProofs.IsDefaultOrEmpty)
                {
                    return new(
                        [],
                        new(
                            RelationQueryUnavailableReason.CapabilityNotAdvertised,
                            RelationQueryRealizationDiagnosticCodes.RequirementUnavailable,
                            $"Native strategy for requirement '{requirement.Id.Value}' does not prove guarantee '{guarantee}'.",
                            Boundary: null,
                            [capability]));
                }

                guaranteed = Combine(guaranteed, guaranteeProofs);
            }

            return new(guaranteed, null);
        }

        ProofValidation ValidateBoundaries(
            Proof proof,
            RelationQueryRealizationRequirement requirement,
            bool allowAutomaticTargetEnforcement)
        {
            if (proof.Boundaries.IsDefaultOrEmpty)
                return new(proof, [], null);
            if (policy.ConstrainedRealizations == RelationQueryConstrainedRealizationPolicy.Reject)
            {
                return new(
                    null,
                    [],
                    new(
                        RelationQueryUnavailableReason.PolicyRejected,
                        RelationQueryRealizationDiagnosticCodes.PolicyInvalid,
                        $"Policy '{policy.Id.Value}' rejects constrained realization of requirement '{requirement.Id.Value}'.",
                        Boundary: null,
                        []));
            }

            var validatedProof = proof;
            ImmutableArray<RelationQueryOperatingBoundaryValidation>.Builder validations =
                ImmutableArray.CreateBuilder<RelationQueryOperatingBoundaryValidation>(proof.Boundaries.Length);
            foreach (var boundaryId in proof.Boundaries)
            {
                if (!profileAnalysis.Boundaries.TryGetValue(boundaryId, out var boundary))
                {
                    return new(
                        null,
                        [],
                        new(
                            RelationQueryUnavailableReason.OperatingBoundaryMissing,
                            RelationQueryRealizationDiagnosticCodes.OperatingBoundaryMissing,
                            $"Requirement '{requirement.Id.Value}' references undeclared boundary '{boundaryId.Value}'.",
                            boundaryId,
                            []));
                }

                var fact = EvaluateStaticBoundary(boundary, requirement);
                if (fact.Status == BoundaryFactStatus.Violated)
                {
                    return new(
                        null,
                        [],
                        new(
                            RelationQueryUnavailableReason.OperatingBoundaryInvalid,
                            RelationQueryRealizationDiagnosticCodes.OperatingBoundaryInvalid,
                            $"Requirement '{requirement.Id.Value}' exceeds operating boundary '{boundary.Id.Value}'"
                            + (fact.MeasuredValue is { } measured && boundary.Limit is { } limit
                                ? $" ({measured} > {limit})."
                                : "."),
                            boundary.Id,
                            []));
                }

                if (fact.Status == BoundaryFactStatus.Satisfied)
                {
                    validations.Add(new(
                        boundary.Id,
                        RelationQueryOperatingBoundaryValidationKind.StaticPlanFact,
                        measuredValue: fact.MeasuredValue));
                    continue;
                }

                var validatorCapability = new OperatingBoundaryValidationRelationQueryCapability(boundary.Id);
                var validatorEvidence = capabilityEvidence
                    .Where(item => Equals(item.Capability, validatorCapability)
                                   && item.OperatingBoundaries.IsDefaultOrEmpty)
                    .Select(static item => item.Id)
                    .ToImmutableArray();
                if (!allowAutomaticTargetEnforcement)
                    validatorEvidence = [.. validatorEvidence.Where(proof.Evidence.Contains)];

                if (validatorEvidence.IsDefaultOrEmpty)
                {
                    return new(
                        null,
                        [],
                        new(
                            RelationQueryUnavailableReason.OperatingBoundaryInvalid,
                            RelationQueryRealizationDiagnosticCodes.OperatingBoundaryInvalid,
                            $"Operating boundary '{boundary.Id.Value}' for requirement '{requirement.Id.Value}' "
                            + "cannot be proved statically and has no attributable target enforcement evidence.",
                            boundary.Id,
                            [validatorCapability]));
                }

                validations.Add(new(
                    boundary.Id,
                    RelationQueryOperatingBoundaryValidationKind.TargetEnforced,
                    validatorEvidence[0]));
                if (allowAutomaticTargetEnforcement)
                {
                    validatedProof = validatedProof.Merge(new([validatorEvidence[0]], [], [], []));
                }
            }

            return new(validatedProof, validations.ToImmutable(), null);
        }

        BoundaryFact EvaluateStaticBoundary(
            RelationQueryOperatingBoundary boundary,
            RelationQueryRealizationRequirement requirement)
        {
            long? measured = boundary.Kind switch
            {
                RelationQueryOperatingBoundaryKind.MaximumFieldPathDepth =>
                    StaticFact(requirement, RelationQueryRealizationStaticFactKind.FieldPathDepth),
                RelationQueryOperatingBoundaryKind.MaximumExpressionDepth =>
                    StaticFact(requirement, RelationQueryRealizationStaticFactKind.ExpressionDepth),
                RelationQueryOperatingBoundaryKind.MaximumPageSize =>
                    StaticFact(requirement, RelationQueryRealizationStaticFactKind.PageSize),
                _ => null
            };
            if (measured is null || boundary.Limit is null)
                return new(BoundaryFactStatus.Unknown, null);
            return measured <= boundary.Limit
                ? new(BoundaryFactStatus.Satisfied, measured)
                : new(BoundaryFactStatus.Violated, measured);
        }

        static long? StaticFact(
            RelationQueryRealizationRequirement requirement,
            RelationQueryRealizationStaticFactKind kind) =>
            requirement.StaticFacts.FirstOrDefault(fact => fact.Kind == kind)?.Value;

        Resolution Resolve(RelationQueryCapability capability, ImmutableHashSet<RelationQueryCapability> stack)
        {
            if (proofs.TryGetValue(capability, out var cachedProofs))
                return new(cachedProofs, missing.GetValueOrDefault(capability, []));
            if (stack.Contains(capability))
                return new([], [capability]);

            var nextStack = stack.Add(capability);
            ImmutableArray<Proof>.Builder candidates = ImmutableArray.CreateBuilder<Proof>();
            ImmutableArray<RelationQueryCapability>.Builder missingCapabilities =
                ImmutableArray.CreateBuilder<RelationQueryCapability>();

            foreach (var group in capabilityEvidence
                         .Where(item => Equals(item.Capability, capability))
                         .GroupBy(
                             static item => BoundaryKey(item.OperatingBoundaries),
                             StringComparer.Ordinal)
                         .OrderBy(static group => group.Key, StringComparer.Ordinal))
            {
                var first = group.First();
                candidates.Add(new(
                    [.. group.Select(static item => item.Id)],
                    first.OperatingBoundaries,
                    [],
                    []));
            }

            ruleSelections.TryGetValue(capability, out var selectedRule);
            foreach (var rule in policy.CompositionRules.Where(rule =>
                         Equals(rule.ProvidedCapability, capability)
                         && !invalidRules.Contains(rule.Id)
                         && (selectedRule is null || rule.Id == selectedRule.Rule)))
            {
                var combinations = ImmutableArray.Create(Proof.Empty);
                var complete = true;
                foreach (var required in rule.RequiredCapabilities)
                {
                    var child = Resolve(required, nextStack);
                    if (child.Proofs.IsDefaultOrEmpty)
                    {
                        complete = false;
                        missingCapabilities.AddRange(child.Missing.IsDefaultOrEmpty ? [required] : child.Missing);
                        continue;
                    }

                    combinations = Combine(combinations, child.Proofs);
                }

                if (!complete)
                    continue;
                foreach (var combination in combinations)
                {
                    candidates.Add(combination.With(rule));
                }
            }

            var normalizedProofs = candidates
                .DistinctBy(static candidate => candidate.Key, StringComparer.Ordinal)
                .OrderBy(static candidate => candidate.Key, StringComparer.Ordinal)
                .ToImmutableArray();
            var normalizedMissing = RelationQueryRealizationOrdering.NormalizeCapabilities(
                missingCapabilities.ToImmutable(),
                nameof(missingCapabilities));
            proofs[capability] = normalizedProofs;
            missing[capability] = normalizedMissing;
            return new(normalizedProofs, normalizedMissing);
        }

        ImmutableArray<Proof> Combine(ImmutableArray<Proof> left, ImmutableArray<Proof> right)
        {
            ImmutableArray<Proof>.Builder combined = ImmutableArray.CreateBuilder<Proof>(left.Length * right.Length);
            foreach (var leftProof in left)
            {
                foreach (var rightProof in right)
                {
                    if (HaveCompatibleRuleChoices(leftProof, rightProof))
                        combined.Add(leftProof.Merge(rightProof));
                }
            }
            return combined.ToImmutable();
        }

        bool HaveCompatibleRuleChoices(Proof left, Proof right) =>
            left.Rules
                .Concat(right.Rules)
                .Distinct()
                .Select(rule => rulesById[rule])
                .GroupBy(static rule => rule.ProvidedCapability)
                .All(static group => group.Count() == 1);

        ImmutableHashSet<RelationQueryCapability> FindRelevantCapabilities()
        {
            HashSet<RelationQueryCapability> relevant = [];
            Queue<RelationQueryCapability> pending = new(
                requirements.Select(static requirement => requirement.Capability).Distinct());
            while (pending.TryDequeue(out var capability))
            {
                if (!relevant.Add(capability))
                    continue;

                ruleSelections.TryGetValue(capability, out var selection);
                foreach (var required in policy.CompositionRules
                             .Where(rule => Equals(rule.ProvidedCapability, capability)
                                            && (selection is null || rule.Id == selection.Rule))
                             .SelectMany(static rule => rule.RequiredCapabilities))
                {
                    pending.Enqueue(required);
                }
            }

            return relevant.ToImmutableHashSet();
        }

        void ValidatePolicyReferences(ImmutableHashSet<RelationQueryCapability> relevantCapabilities)
        {
            foreach (var selection in policy.CompositionRuleSelections.Where(selection =>
                         relevantCapabilities.Contains(selection.Capability)))
            {
                var selected = policy.CompositionRules.SingleOrDefault(rule => rule.Id == selection.Rule);
                if (selected is not null && Equals(selected.ProvidedCapability, selection.Capability))
                    continue;

                invalid = true;
                AddDiagnostic(
                    RelationQueryRealizationDiagnosticCodes.PolicyInvalid,
                    $"Composition-rule selection for capability '{RelationQueryRealizationOrdering.CapabilityKey(selection.Capability)}' references missing or mismatched rule '{selection.Rule.Value}'.",
                    compositionRule: selection.Rule,
                    fatal: true);
            }

            foreach (var rule in policy.CompositionRules.Where(rule =>
                         relevantCapabilities.Contains(rule.ProvidedCapability)
                         && (!ruleSelections.TryGetValue(rule.ProvidedCapability, out var selection)
                             || rule.Id == selection.Rule)))
            {
                foreach (var boundary in rule.RequiredOperatingBoundaries.Where(boundary => !boundaryIds.Contains(boundary)))
                {
                    invalid = true;
                    invalidRules.Add(rule.Id);
                    AddDiagnostic(
                        RelationQueryRealizationDiagnosticCodes.OperatingBoundaryMissing,
                        $"Composition rule '{rule.Id.Value}' references operating boundary '{boundary.Value}', which target profile '{profile.Id.Value}' does not declare.",
                        compositionRule: rule.Id,
                        operatingBoundary: boundary,
                        fatal: true);
                }
            }

            var requirementIds = requirements.Select(static requirement => requirement.Id).ToHashSet();
            foreach (var @override in policy.Overrides.Where(item => requirementIds.Contains(item.Requirement)))
            {
                if (@override.CapabilityEvidence.All(evidenceById.ContainsKey)
                    && @override.OperatingBoundaries.All(boundaryIds.Contains))
                {
                    continue;
                }

                invalid = true;
                AddDiagnostic(
                    RelationQueryRealizationDiagnosticCodes.OverrideInvalid,
                    $"Override '{@override.Id.Value}' references evidence absent from target profile '{profile.Id.Value}'.",
                    @override: @override.Id,
                    fatal: true);
            }
        }

        void ValidateCompositionCycles(ImmutableHashSet<RelationQueryCapability> relevantCapabilities)
        {
            var relevantRules = policy.CompositionRules
                .Where(rule => relevantCapabilities.Contains(rule.ProvidedCapability)
                               && (!ruleSelections.TryGetValue(rule.ProvidedCapability, out var selection)
                                   || rule.Id == selection.Rule))
                .ToImmutableArray();
            var rulesByCapability = relevantRules
                .GroupBy(static rule => rule.ProvidedCapability)
                .ToDictionary(static group => group.Key, static group => group.ToImmutableArray());
            foreach (var rule in relevantRules)
            {
                if (rule.RequiredCapabilities.Any(required => CanReach(
                        required,
                        rule.ProvidedCapability,
                        rulesByCapability,
                        [])))
                {
                    invalid = true;
                    invalidRules.Add(rule.Id);
                    AddDiagnostic(
                        RelationQueryRealizationDiagnosticCodes.CompositionRuleInvalid,
                        $"Composition rule '{rule.Id.Value}' participates in a capability cycle.",
                        compositionRule: rule.Id,
                        fatal: true);
                }
            }
        }

        static bool CanReach(
            RelationQueryCapability capability,
            RelationQueryCapability target,
            IReadOnlyDictionary<RelationQueryCapability, ImmutableArray<RelationQueryCompositionRule>> rules)
            => CanReach(capability, target, rules, []);

        static bool CanReach(
            RelationQueryCapability capability,
            RelationQueryCapability target,
            IReadOnlyDictionary<RelationQueryCapability, ImmutableArray<RelationQueryCompositionRule>> rules,
            ImmutableHashSet<RelationQueryCapability> visited)
        {
            if (Equals(capability, target))
                return true;
            if (visited.Contains(capability))
                return false;
            var next = visited.Add(capability);
            if (!rules.TryGetValue(capability, out var providers))
                return false;
            return providers.Any(provider => provider.RequiredCapabilities.Any(required =>
                CanReach(required, target, rules, next)));
        }

        void AddDiagnostic(
            string code,
            string message,
            RelationQueryRealizationRequirement? requirement = null,
            RelationQueryTargetCapabilityEvidenceId? capabilityEvidence = null,
            RelationQueryCompositionRuleId? compositionRule = null,
            RelationQueryOperatingBoundaryId? operatingBoundary = null,
            RelationQueryRealizationOverrideId? @override = null,
            bool fatal = false)
        {
            var severity = fatal
                ? DiagnosticSeverity.Error
                : severityOverrides.GetValueOrDefault(code, DiagnosticSeverity.Error);
            diagnostics.Add(new(
                code,
                severity,
                message,
                requirement?.Id,
                capabilityEvidence,
                compositionRule,
                operatingBoundary,
                @override,
                requirement?.Origin?.Node,
                requirement?.Origin?.SemanticSite));
        }

        static string BoundaryKey(ImmutableArray<RelationQueryOperatingBoundaryId> boundaries) =>
            RelationQueryRealizationOrdering.SequenceKey(
                boundaries.Select(static boundary => boundary.Value));
    }

    readonly record struct Resolution(
        ImmutableArray<Proof> Proofs,
        ImmutableArray<RelationQueryCapability> Missing);

    readonly record struct ProofValidation(
        Proof? Proof,
        ImmutableArray<RelationQueryOperatingBoundaryValidation> Validations,
        StrategyFailure? Failure);

    readonly record struct GuaranteeValidation(
        ImmutableArray<Proof> Proofs,
        StrategyFailure? Failure);

    readonly record struct StrategyFailure(
        RelationQueryUnavailableReason Reason,
        string Code,
        string Message,
        RelationQueryOperatingBoundaryId? Boundary,
        ImmutableArray<RelationQueryCapability> MissingCapabilities);

    enum BoundaryFactStatus
    {
        Unknown = 0,
        Satisfied = 1,
        Violated = 2
    }

    readonly record struct BoundaryFact(BoundaryFactStatus Status, long? MeasuredValue);

    sealed record ValidatedProof
    {
        public ValidatedProof(
            Proof proof,
            ImmutableArray<RelationQueryOperatingBoundaryValidation> validations)
        {
            Proof = proof;
            Validations = validations.IsDefault ? [] : validations;
            Key = RelationQueryRealizationOrdering.SequenceKey(
            [
                proof.Key,
                RelationQueryRealizationOrdering.SequenceKey(Validations.Select(static validation =>
                    RelationQueryRealizationOrdering.SequenceKey(
                    [
                        validation.Boundary.Value,
                        ((int)validation.Kind).ToString("D4", System.Globalization.CultureInfo.InvariantCulture),
                        validation.CapabilityEvidence?.Value,
                        validation.MeasuredValue?.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    ])))
            ]);
        }

        public Proof Proof { get; }

        public ImmutableArray<RelationQueryOperatingBoundaryValidation> Validations { get; }

        public string Key { get; }

        public RelationQueryRealizationDecision ToDecision(RelationQueryRealizationRequirementId requirement) =>
            !Validations.IsDefaultOrEmpty
                ? new ConstrainedRelationQueryRealizationDecision(
                    requirement,
                    Proof.Evidence,
                    Validations,
                    Proof.Rules,
                    Proof.Guarantees)
                : !Proof.Rules.IsDefaultOrEmpty
                    ? new ComposedRelationQueryRealizationDecision(
                        requirement,
                        Proof.Rules,
                        Proof.Evidence,
                        Proof.Guarantees)
                    : new NativeRelationQueryRealizationDecision(requirement, Proof.Evidence, Proof.Guarantees);
    }

    sealed record Proof
    {
        public static Proof Empty { get; } = new([], [], [], []);

        public Proof(
            ImmutableArray<RelationQueryTargetCapabilityEvidenceId> evidence,
            ImmutableArray<RelationQueryOperatingBoundaryId> boundaries,
            ImmutableArray<RelationQueryCompositionRuleId> rules,
            ImmutableArray<RelationQueryGuaranteeCapabilityKind> guarantees)
        {
            Evidence = [.. evidence.Distinct().OrderBy(static item => item.Value, StringComparer.Ordinal)];
            Boundaries = [.. boundaries.Distinct().OrderBy(static item => item.Value, StringComparer.Ordinal)];
            Rules = [.. rules.Distinct().OrderBy(static item => item.Value, StringComparer.Ordinal)];
            Guarantees = [.. guarantees.Distinct().OrderBy(static item => (int)item)];
            Key = RelationQueryRealizationOrdering.SequenceKey(
            [
                RelationQueryRealizationOrdering.SequenceKey(Evidence.Select(static item => item.Value)),
                RelationQueryRealizationOrdering.SequenceKey(Boundaries.Select(static item => item.Value)),
                RelationQueryRealizationOrdering.SequenceKey(Rules.Select(static item => item.Value)),
                RelationQueryRealizationOrdering.SequenceKey(Guarantees.Select(static item =>
                    ((int)item).ToString("D4", System.Globalization.CultureInfo.InvariantCulture)))
            ]);
        }

        public ImmutableArray<RelationQueryTargetCapabilityEvidenceId> Evidence { get; }

        public ImmutableArray<RelationQueryOperatingBoundaryId> Boundaries { get; }

        public ImmutableArray<RelationQueryCompositionRuleId> Rules { get; }

        public ImmutableArray<RelationQueryGuaranteeCapabilityKind> Guarantees { get; }

        public string Key { get; }

        public Proof Merge(Proof other) => new(
            [.. Evidence, .. other.Evidence],
            [.. Boundaries, .. other.Boundaries],
            [.. Rules, .. other.Rules],
            [.. Guarantees, .. other.Guarantees]);

        public Proof With(RelationQueryCompositionRule rule) => new(
            Evidence,
            [.. Boundaries, .. rule.RequiredOperatingBoundaries],
            [.. Rules, rule.Id],
            rule.PreservedGuarantees);
    }
}
