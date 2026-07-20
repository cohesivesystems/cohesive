using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Cohesive.Relations.Compilation;

namespace Cohesive.Relations.Realization;

/// <summary>
/// Computes portable identities for the exact realization requirements and guarantees projected from a static plan.
/// </summary>
public static class RelationQueryRealizationRequirementSetFingerprinter
{
    /// <summary>Fingerprint algorithm identifier.</summary>
    public const string Algorithm = "sha256";

    /// <summary>Canonicalization profile identifier.</summary>
    public const string Canonicalization = "relation-query-realization-requirements/v1-c14n/v1";

    /// <summary>Computes an exact plan-affine fingerprint of one normalized realization-requirement set.</summary>
    /// <param name="plan">Compiled plan reference from which the requirements were projected.</param>
    /// <param name="observability">Result-observability contract used during projection.</param>
    /// <param name="requirements">Projected realization requirements.</param>
    /// <returns>A versioned SHA-256 fingerprint over the plan, observability, requirements, and guarantees.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="plan"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="requirements"/> contains a <see langword="null"/> entry.</exception>
    public static RelationQueryPlanComponentFingerprint Compute(
        RelationQueryCompiledPlanReference plan,
        RelationQueryResultObservability observability,
        ImmutableArray<RelationQueryRealizationRequirement> requirements) =>
        RelationQueryRealizationFingerprinter.ComputeRequirements(plan, observability, requirements);
}

/// <summary>Computes deterministic content fingerprints for derived relation/query realization reports.</summary>
/// <remarks>
/// The current profile uses length-prefixed, big-endian binary canonicalization. It includes semantic plan,
/// requirement, target, policy, decision, and diagnostic attribution while excluding human-facing messages,
/// descriptions, justifications, and other metadata that cannot change realization.
/// </remarks>
public static class RelationQueryRealizationFingerprinter
{
    /// <summary>Fingerprint algorithm identifier.</summary>
    public const string Algorithm = "sha256";

    /// <summary>Canonicalization profile identifier.</summary>
    public const string Canonicalization = "relation-query-realization/v1-c14n/v4";

    /// <summary>Computes the deterministic derived-artifact fingerprint of a realization report.</summary>
    /// <param name="report">Normalized realization report to fingerprint.</param>
    /// <returns>A versioned SHA-256 fingerprint of realization-affecting report content.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="report"/> is <see langword="null"/>.</exception>
    public static RelationQueryRealizationFingerprint Compute(RelationQueryRealizationReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return Compute(
            report.Plan,
            report.TargetProfile,
            report.Policy,
            report.Observability,
            report.Requirements,
            report.Decisions,
            report.Diagnostics,
            report.Status);
    }

    internal static RelationQueryRealizationFingerprint Compute(
        RelationQueryCompiledPlanReference plan,
        RelationQueryTargetCapabilityProfile targetProfile,
        RelationQueryRealizationPolicy policy,
        RelationQueryResultObservability observability,
        ImmutableArray<RelationQueryRealizationRequirement> requirements,
        ImmutableArray<RelationQueryRealizationDecision> decisions,
        ImmutableArray<RelationQueryRealizationDiagnostic> diagnostics,
        RelationQueryRealizationStatus status)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(targetProfile);
        ArgumentNullException.ThrowIfNull(policy);
        if (!Enum.IsDefined(status))
            throw new ArgumentOutOfRangeException(nameof(status), status, "Unsupported realization status.");

        var normalizedRequirements = Normalize(
            requirements,
            static requirement => requirement.Id.Value,
            nameof(requirements));
        var normalizedDecisions = Normalize(
            decisions,
            static decision => decision.Requirement.Value,
            nameof(decisions));
        var normalizedDiagnostics = NormalizeDiagnostics(diagnostics);
        var relevant = ResolveRelevantInputs(
            targetProfile,
            policy,
            normalizedRequirements,
            normalizedDecisions,
            normalizedDiagnostics);

        ArrayBufferWriter<byte> canonical = new();
        Append(canonical, Canonicalization);
        AppendPlan(canonical, plan);
        Append(canonical, (int)observability.OccurrenceProvenance);
        AppendRequirements(canonical, normalizedRequirements);
        AppendTarget(canonical, targetProfile, relevant);
        AppendPolicy(canonical, policy, relevant, normalizedDiagnostics);
        AppendDecisions(canonical, normalizedDecisions);
        AppendDiagnostics(canonical, normalizedDiagnostics);
        Append(canonical, (int)status);

        var hash = SHA256.HashData(canonical.WrittenSpan);
        return new(
            Algorithm,
            Canonicalization,
            Convert.ToHexString(hash).ToLowerInvariant());
    }

    internal static RelationQueryPlanComponentFingerprint ComputeRequirements(
        RelationQueryCompiledPlanReference plan,
        RelationQueryResultObservability observability,
        ImmutableArray<RelationQueryRealizationRequirement> requirements)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var normalized = Normalize(
            requirements,
            static requirement => requirement.Id.Value,
            nameof(requirements));

        ArrayBufferWriter<byte> canonical = new();
        Append(canonical, RelationQueryRealizationRequirementSetFingerprinter.Canonicalization);
        AppendPlan(canonical, plan);
        Append(canonical, (int)observability.OccurrenceProvenance);
        AppendRequirements(canonical, normalized);
        var hash = SHA256.HashData(canonical.WrittenSpan);
        return new(
            RelationQueryRealizationRequirementSetFingerprinter.Algorithm,
            RelationQueryRealizationRequirementSetFingerprinter.Canonicalization,
            Convert.ToHexString(hash).ToLowerInvariant());
    }

    static void AppendPlan(ArrayBufferWriter<byte> buffer, RelationQueryCompiledPlanReference plan)
    {
        Append(buffer, plan.DefinitionSchemaVersion);
        Append(buffer, plan.CompilerProfile);
        AppendFingerprint(
            buffer,
            plan.DefinitionFingerprint.Algorithm,
            plan.DefinitionFingerprint.Canonicalization,
            plan.DefinitionFingerprint.Value);
        AppendFingerprint(
            buffer,
            plan.ShapeSnapshotsFingerprint.Algorithm,
            plan.ShapeSnapshotsFingerprint.Canonicalization,
            plan.ShapeSnapshotsFingerprint.Value);
        Append(buffer, plan.RelationshipCatalogFingerprint is not null);
        if (plan.RelationshipCatalogFingerprint is { } catalog)
        {
            AppendFingerprint(buffer, catalog.Algorithm, catalog.Canonicalization, catalog.Value);
        }
        AppendFingerprint(
            buffer,
            plan.DemandFingerprint.Algorithm,
            plan.DemandFingerprint.Canonicalization,
            plan.DemandFingerprint.Value);
        Append(buffer, plan.Inputs.Length);
        foreach (var input in plan.Inputs.OrderBy(static input => input.Value, StringComparer.Ordinal))
            Append(buffer, input.Value);
    }

    static void AppendRequirements(
        ArrayBufferWriter<byte> buffer,
        ImmutableArray<RelationQueryRealizationRequirement> requirements)
    {
        Append(buffer, requirements.Length);
        foreach (var requirement in requirements)
        {
            Append(buffer, requirement.Id.Value);
            AppendCapability(buffer, requirement.Capability);
            Append(buffer, requirement.Origin is not null);
            if (requirement.Origin is { } origin)
                AppendOrigin(buffer, origin);

            AppendGuarantees(buffer, requirement.RequiredGuarantees);
            Append(buffer, requirement.StaticFacts.Length);
            foreach (var fact in requirement.StaticFacts.OrderBy(static fact => (int)fact.Kind))
            {
                Append(buffer, (int)fact.Kind);
                AppendInt64(buffer, fact.Value);
            }

            var uses = requirement.Uses
                .OrderBy(static use => use.Output.Id.Value, StringComparer.Ordinal)
                .ThenBy(static use => (int)use.Effect)
                .ThenBy(static use => (int)use.Requirement)
                .ToArray();
            Append(buffer, uses.Length);
            foreach (var use in uses)
                AppendUse(buffer, use);
        }
    }

    static void AppendOrigin(ArrayBufferWriter<byte> buffer, RelationQueryRealizationRequirementOrigin origin)
    {
        AppendOptional(buffer, origin.Input?.Value);
        AppendOptional(buffer, origin.Node?.Value);
        AppendOptional(buffer, origin.Binding?.Value);
        AppendOptional(buffer, origin.SemanticSite);
        AppendOptional(buffer, origin.ExpressionPath);
        Append(buffer, origin.FieldPath is not null);
        if (origin.FieldPath is { } fieldPath)
            AppendFieldPath(buffer, fieldPath);
    }

    static void AppendUse(ArrayBufferWriter<byte> buffer, RelationQueryRealizationRequirementUse use)
    {
        AppendOutput(buffer, use.Output);
        Append(buffer, (int)use.Effect);
        Append(buffer, (int)use.Requirement);
        var traces = use.Traces
            .OrderBy(RelationQueryRealizationRequirementUse.TraceKey, StringComparer.Ordinal)
            .ToArray();
        Append(buffer, traces.Length);
        foreach (var trace in traces)
        {
            Append(buffer, trace.Steps.Length);
            foreach (var step in trace.Steps)
            {
                Append(buffer, (int)step.Kind);
                Append(buffer, step.Node.Value);
                AppendNullableEnum(buffer, step.SiteKind is { } siteKind ? (int)siteKind : null);
                AppendOptional(buffer, step.ExpressionSite?.Value);
                AppendOptional(buffer, step.Assignment?.Value);
                AppendNullableInt32(buffer, step.Ordinal);
                AppendOptional(buffer, step.InvariantName);
            }
        }
    }

    static void AppendOutput(ArrayBufferWriter<byte> buffer, RelationQueryRealizationOutputReference output)
    {
        Append(buffer, output.Id.Value);
        Append(buffer, (int)output.Kind);
        Append(buffer, output.Node.Value);
        Append(buffer, output.Shape.GraphId.Value);
        Append(buffer, output.Shape.ShapeId.Value);
        AppendOptional(buffer, output.Relation?.Value);
        AppendOptional(buffer, output.QueryResult?.Value);
        Append(buffer, output.Field is not null);
        if (output.Field is { } field)
        {
            Append(buffer, field.Shape.GraphId.Value);
            Append(buffer, field.Shape.ShapeId.Value);
            AppendFieldPath(buffer, field.Path);
        }
    }

    static void AppendTarget(
        ArrayBufferWriter<byte> buffer,
        RelationQueryTargetCapabilityProfile targetProfile,
        RelevantInputs relevant)
    {
        Append(buffer, targetProfile.Target.Value);
        Append(buffer, targetProfile.Id.Value);

        Append(buffer, relevant.Evidence.Length);
        foreach (var evidence in relevant.Evidence)
        {
            Append(buffer, evidence.Id.Value);
            AppendCapability(buffer, evidence.Capability);
            AppendIds(buffer, evidence.OperatingBoundaries.Select(static boundary => boundary.Value));
        }

        Append(buffer, relevant.Boundaries.Length);
        foreach (var boundary in relevant.Boundaries)
        {
            Append(buffer, boundary.Id.Value);
            Append(buffer, (int)boundary.Kind);
            AppendNullableInt64(buffer, boundary.Limit);
        }
    }

    static void AppendPolicy(
        ArrayBufferWriter<byte> buffer,
        RelationQueryRealizationPolicy policy,
        RelevantInputs relevant,
        ImmutableArray<RelationQueryRealizationDiagnostic> diagnostics)
    {
        Append(buffer, policy.Id.Value);
        Append(buffer, policy.ConventionSetVersion);
        Append(buffer, (int)policy.Preference);
        Append(buffer, (int)policy.ConstrainedRealizations);

        Append(buffer, relevant.Rules.Length);
        foreach (var rule in relevant.Rules)
        {
            Append(buffer, rule.Id.Value);
            AppendCapability(buffer, rule.ProvidedCapability);
            AppendCapabilities(buffer, rule.RequiredCapabilities);
            AppendIds(buffer, rule.RequiredOperatingBoundaries.Select(static boundary => boundary.Value));
            AppendGuarantees(buffer, rule.PreservedGuarantees);
        }

        Append(buffer, relevant.RuleSelections.Length);
        foreach (var selection in relevant.RuleSelections)
        {
            AppendCapability(buffer, selection.Capability);
            Append(buffer, selection.Rule.Value);
        }

        Append(buffer, relevant.Overrides.Length);
        foreach (var item in relevant.Overrides)
        {
            Append(buffer, item.Id.Value);
            Append(buffer, item.Requirement.Value);
            AppendCapability(buffer, item.ExpectedCapability);
            AppendIds(buffer, item.CapabilityEvidence.Select(static evidence => evidence.Value));
            AppendIds(buffer, item.OperatingBoundaries.Select(static boundary => boundary.Value));
            AppendGuarantees(buffer, item.PreservedGuarantees);
        }

        var diagnosticCodes = diagnostics.Select(static diagnostic => diagnostic.Code).ToHashSet(StringComparer.Ordinal);
        var severityDecisions = policy.DiagnosticSeverityOverrides
            .Where(item => diagnosticCodes.Contains(item.Code))
            .OrderBy(static item => item.Code, StringComparer.Ordinal)
            .ToArray();
        Append(buffer, severityDecisions.Length);
        foreach (var severity in severityDecisions)
        {
            Append(buffer, severity.Code);
            Append(buffer, (int)severity.Severity);
        }
    }

    static void AppendDecisions(
        ArrayBufferWriter<byte> buffer,
        ImmutableArray<RelationQueryRealizationDecision> decisions)
    {
        Append(buffer, decisions.Length);
        foreach (var decision in decisions)
        {
            Append(buffer, decision.Requirement.Value);
            Append(buffer, (int)decision.Kind);
            switch (decision)
            {
                case NativeRelationQueryRealizationDecision native:
                    AppendIds(buffer, native.CapabilityEvidence.Select(static evidence => evidence.Value));
                    AppendGuarantees(buffer, native.PreservedGuarantees);
                    break;
                case ComposedRelationQueryRealizationDecision composed:
                    AppendIds(buffer, composed.CompositionRules.Select(static rule => rule.Value));
                    AppendIds(buffer, composed.CapabilityEvidence.Select(static evidence => evidence.Value));
                    AppendGuarantees(buffer, composed.PreservedGuarantees);
                    break;
                case ConstrainedRelationQueryRealizationDecision constrained:
                    AppendIds(buffer, constrained.CompositionRules.Select(static rule => rule.Value));
                    AppendIds(buffer, constrained.CapabilityEvidence.Select(static evidence => evidence.Value));
                    AppendBoundaryValidations(buffer, constrained.BoundaryValidations);
                    AppendGuarantees(buffer, constrained.PreservedGuarantees);
                    break;
                case OverrideRelationQueryRealizationDecision overridden:
                    Append(buffer, overridden.Override.Value);
                    AppendIds(buffer, overridden.CapabilityEvidence.Select(static evidence => evidence.Value));
                    AppendBoundaryValidations(buffer, overridden.BoundaryValidations);
                    AppendGuarantees(buffer, overridden.PreservedGuarantees);
                    break;
                case UnavailableRelationQueryRealizationDecision unavailable:
                    Append(buffer, (int)unavailable.Reason);
                    AppendCapabilities(buffer, unavailable.MissingCapabilities);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(decisions),
                        decision,
                        "Unsupported realization decision variant.");
            }
        }
    }

    static void AppendBoundaryValidations(
        ArrayBufferWriter<byte> buffer,
        ImmutableArray<RelationQueryOperatingBoundaryValidation> validations)
    {
        var ordered = validations.OrderBy(static validation => validation.Boundary.Value, StringComparer.Ordinal).ToArray();
        Append(buffer, ordered.Length);
        foreach (var validation in ordered)
        {
            Append(buffer, validation.Boundary.Value);
            Append(buffer, (int)validation.Kind);
            AppendOptional(buffer, validation.CapabilityEvidence?.Value);
            AppendNullableInt64(buffer, validation.MeasuredValue);
        }
    }

    static void AppendDiagnostics(
        ArrayBufferWriter<byte> buffer,
        ImmutableArray<RelationQueryRealizationDiagnostic> diagnostics)
    {
        Append(buffer, diagnostics.Length);
        foreach (var diagnostic in diagnostics)
        {
            Append(buffer, diagnostic.Code);
            Append(buffer, (int)diagnostic.Severity);
            AppendOptional(buffer, diagnostic.Requirement?.Value);
            AppendOptional(buffer, diagnostic.CapabilityEvidence?.Value);
            AppendOptional(buffer, diagnostic.CompositionRule?.Value);
            AppendOptional(buffer, diagnostic.OperatingBoundary?.Value);
            AppendOptional(buffer, diagnostic.Override?.Value);
            AppendOptional(buffer, diagnostic.Node?.Value);
            AppendOptional(buffer, diagnostic.SemanticSite);
            AppendOptional(buffer, diagnostic.ContextEvidence?.Value);
            AppendOptional(buffer, diagnostic.Branch?.Value);
            AppendOptional(buffer, diagnostic.Input?.Value);
            Append(buffer, diagnostic.Field is not null);
            if (diagnostic.Field is { } field)
                AppendFieldPath(buffer, field);
            AppendOptional(buffer, diagnostic.PlacementBinding?.Value);
            AppendOptional(buffer, diagnostic.BindingSetting);
            AppendNullableInt32(
                buffer,
                diagnostic.ConfigurationOrigin is { } origin ? (int)origin : null);
            AppendOptional(buffer, diagnostic.ConfigurationAuthority);
            AppendOptional(buffer, diagnostic.AdapterDecisionCode?.Value);
        }
    }

    static RelevantInputs ResolveRelevantInputs(
        RelationQueryTargetCapabilityProfile targetProfile,
        RelationQueryRealizationPolicy policy,
        ImmutableArray<RelationQueryRealizationRequirement> requirements,
        ImmutableArray<RelationQueryRealizationDecision> decisions,
        ImmutableArray<RelationQueryRealizationDiagnostic> diagnostics)
    {
        HashSet<RelationQueryCapability> relevantCapabilities =
        [
            .. requirements.Select(static requirement => requirement.Capability),
            .. requirements.SelectMany(static requirement => requirement.RequiredGuarantees)
                .Select(static guarantee => new GuaranteeRelationQueryCapability(guarantee)),
            .. decisions.OfType<UnavailableRelationQueryRealizationDecision>()
                .SelectMany(static decision => decision.MissingCapabilities)
        ];
        HashSet<RelationQueryCompositionRuleId> relevantRuleIds =
        [
            .. decisions.OfType<ComposedRelationQueryRealizationDecision>()
                .SelectMany(static decision => decision.CompositionRules),
            .. decisions.OfType<ConstrainedRelationQueryRealizationDecision>()
                .SelectMany(static decision => decision.CompositionRules),
            .. diagnostics.Where(static diagnostic => diagnostic.CompositionRule is not null)
                .Select(static diagnostic => diagnostic.CompositionRule!.Value)
        ];

        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var rule in policy.CompositionRules)
            {
                if (!relevantRuleIds.Contains(rule.Id)
                    && !relevantCapabilities.Contains(rule.ProvidedCapability))
                {
                    continue;
                }

                changed |= relevantRuleIds.Add(rule.Id);
                foreach (var capability in rule.RequiredCapabilities)
                    changed |= relevantCapabilities.Add(capability);
            }
        }

        var relevantRules = policy.CompositionRules
            .Where(rule => relevantRuleIds.Contains(rule.Id))
            .OrderBy(static rule => rule.Id.Value, StringComparer.Ordinal)
            .ToImmutableArray();
        var relevantRuleSelections = policy.CompositionRuleSelections
            .Where(selection => relevantCapabilities.Contains(selection.Capability))
            .OrderBy(
                static selection => RelationQueryRealizationOrdering.CapabilityKey(selection.Capability),
                StringComparer.Ordinal)
            .ToImmutableArray();
        var requirementIds = requirements.Select(static requirement => requirement.Id).ToHashSet();
        HashSet<RelationQueryRealizationOverrideId> relevantOverrideIds =
        [
            .. decisions.OfType<OverrideRelationQueryRealizationDecision>()
                .Select(static decision => decision.Override),
            .. diagnostics.Where(static diagnostic => diagnostic.Override is not null)
                .Select(static diagnostic => diagnostic.Override!.Value)
        ];
        var relevantOverrides = policy.Overrides
            .Where(item => requirementIds.Contains(item.Requirement) || relevantOverrideIds.Contains(item.Id))
            .OrderBy(static item => item.Id.Value, StringComparer.Ordinal)
            .ToImmutableArray();

        HashSet<RelationQueryTargetCapabilityEvidenceId> evidenceIds =
        [
            .. decisions.SelectMany(static decision => decision.GetCapabilityEvidence()),
            .. diagnostics.Where(static diagnostic => diagnostic.CapabilityEvidence is not null)
                .Select(static diagnostic => diagnostic.CapabilityEvidence!.Value),
            .. relevantOverrides.SelectMany(static item => item.CapabilityEvidence)
        ];
        var relevantEvidence = targetProfile.Capabilities
            .Where(evidence => evidenceIds.Contains(evidence.Id)
                               || relevantCapabilities.Contains(evidence.Capability))
            .OrderBy(static evidence => evidence.Id.Value, StringComparer.Ordinal)
            .ThenBy(
                static evidence => RelationQueryRealizationOrdering.CapabilityKey(evidence.Capability),
                StringComparer.Ordinal)
            .ThenBy(
                static evidence => RelationQueryRealizationOrdering.SequenceKey(
                    evidence.OperatingBoundaries.Select(static boundary => boundary.Value)),
                StringComparer.Ordinal)
            .ToImmutableArray();

        HashSet<RelationQueryOperatingBoundaryId> boundaryIds =
        [
            .. decisions.SelectMany(static decision => decision.GetBoundaryValidations())
                .Select(static validation => validation.Boundary),
            .. diagnostics.Where(static diagnostic => diagnostic.OperatingBoundary is not null)
                .Select(static diagnostic => diagnostic.OperatingBoundary!.Value),
            .. relevantEvidence.SelectMany(static evidence => evidence.OperatingBoundaries),
            .. relevantRules.SelectMany(static rule => rule.RequiredOperatingBoundaries),
            .. relevantOverrides.SelectMany(static item => item.OperatingBoundaries)
        ];
        relevantEvidence =
        [
            .. targetProfile.Capabilities
                .Where(evidence => relevantEvidence.Contains(evidence)
                                   || evidence.Capability is OperatingBoundaryValidationRelationQueryCapability validation
                                   && boundaryIds.Contains(validation.Boundary))
                .OrderBy(static evidence => evidence.Id.Value, StringComparer.Ordinal)
                .ThenBy(
                    static evidence => RelationQueryRealizationOrdering.CapabilityKey(evidence.Capability),
                    StringComparer.Ordinal)
                .ThenBy(
                    static evidence => RelationQueryRealizationOrdering.SequenceKey(
                        evidence.OperatingBoundaries.Select(static boundary => boundary.Value)),
                    StringComparer.Ordinal)
        ];
        var relevantBoundaries = targetProfile.OperatingBoundaries
            .Where(boundary => boundaryIds.Contains(boundary.Id))
            .OrderBy(static boundary => boundary.Id.Value, StringComparer.Ordinal)
            .ThenBy(static boundary => (int)boundary.Kind)
            .ThenBy(static boundary => boundary.Limit)
            .ToImmutableArray();

        return new(
            relevantEvidence,
            relevantBoundaries,
            relevantRules,
            relevantRuleSelections,
            relevantOverrides);
    }

    static void AppendCapabilities(
        ArrayBufferWriter<byte> buffer,
        IEnumerable<RelationQueryCapability> capabilities)
    {
        var normalized = capabilities
            .Distinct()
            .OrderBy(RelationQueryRealizationOrdering.CapabilityKey, StringComparer.Ordinal)
            .ToArray();
        Append(buffer, normalized.Length);
        foreach (var capability in normalized)
            AppendCapability(buffer, capability);
    }

    static void AppendCapability(ArrayBufferWriter<byte> buffer, RelationQueryCapability capability)
    {
        switch (capability)
        {
            case LogicalRelationQueryCapability logical:
                Append(buffer, 0);
                Append(buffer, (int)logical.Kind);
                break;
            case ExpressionRelationQueryCapability expression:
                Append(buffer, 1);
                Append(buffer, (int)expression.RequirementKind);
                Append(buffer, expression.Capability.Value);
                break;
            case TemporalRelationQueryCapability temporal:
                Append(buffer, 2);
                Append(buffer, (int)temporal.Capability);
                break;
            case StructuralRelationQueryCapability structural:
                Append(buffer, 3);
                Append(buffer, (int)structural.Role);
                Append(buffer, (int)structural.PathKind);
                break;
            case GuaranteeRelationQueryCapability guarantee:
                Append(buffer, 4);
                Append(buffer, (int)guarantee.Kind);
                break;
            case OperatingBoundaryValidationRelationQueryCapability boundaryValidation:
                Append(buffer, 5);
                Append(buffer, boundaryValidation.Boundary.Value);
                break;
            case PrimitiveRelationQueryCapability primitive:
                Append(buffer, 6);
                Append(buffer, (int)primitive.Kind);
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(capability),
                    capability,
                    "Unsupported relation/query capability variant.");
        }
    }

    static void AppendGuarantees(
        ArrayBufferWriter<byte> buffer,
        IEnumerable<RelationQueryGuaranteeCapabilityKind> guarantees)
    {
        var normalized = guarantees.Distinct().OrderBy(static guarantee => (int)guarantee).ToArray();
        Append(buffer, normalized.Length);
        foreach (var guarantee in normalized)
            Append(buffer, (int)guarantee);
    }

    static void AppendIds(ArrayBufferWriter<byte> buffer, IEnumerable<string> values)
    {
        var normalized = values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        Append(buffer, normalized.Length);
        foreach (var value in normalized)
            Append(buffer, value);
    }

    static void AppendFieldPath(ArrayBufferWriter<byte> buffer, FieldPath path)
    {
        Append(buffer, path.Segments.Length);
        foreach (var segment in path.Segments)
        {
            Append(buffer, (int)segment.Kind);
            AppendOptional(buffer, segment.Segment);
        }
    }

    static void AppendFingerprint(
        ArrayBufferWriter<byte> buffer,
        string algorithm,
        string canonicalization,
        string value)
    {
        Append(buffer, algorithm);
        Append(buffer, canonicalization);
        Append(buffer, value);
    }

    static ImmutableArray<T> Normalize<T>(
        ImmutableArray<T> values,
        Func<T, string> key,
        string parameterName)
        where T : class
    {
        var normalized = values.IsDefault ? [] : values;
        if (normalized.Any(static item => item is null))
            throw new ArgumentException("Realization fingerprint inputs cannot contain null entries.", parameterName);
        return [.. normalized.OrderBy(key, StringComparer.Ordinal)];
    }

    static ImmutableArray<RelationQueryRealizationDiagnostic> NormalizeDiagnostics(
        ImmutableArray<RelationQueryRealizationDiagnostic> diagnostics)
    {
        var normalized = diagnostics.IsDefault ? [] : diagnostics;
        if (normalized.Any(static diagnostic => diagnostic is null))
            throw new ArgumentException("Realization diagnostics cannot contain null entries.", nameof(diagnostics));
        return
        [
            .. normalized
                .OrderBy(static diagnostic => diagnostic.Requirement?.Value ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(static diagnostic => diagnostic.CapabilityEvidence?.Value ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(static diagnostic => diagnostic.CompositionRule?.Value ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(static diagnostic => diagnostic.OperatingBoundary?.Value ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(static diagnostic => diagnostic.Override?.Value ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(static diagnostic => diagnostic.Node?.Value ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(static diagnostic => diagnostic.SemanticSite ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(static diagnostic => diagnostic.Branch?.Value ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(static diagnostic => diagnostic.Input?.Value ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(static diagnostic => diagnostic.Field?.ToString() ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(static diagnostic => diagnostic.PlacementBinding?.Value ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(static diagnostic => diagnostic.BindingSetting ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(static diagnostic => diagnostic.ConfigurationOrigin is { } origin ? (int)origin : -1)
                .ThenBy(static diagnostic => diagnostic.ConfigurationAuthority ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(static diagnostic => diagnostic.AdapterDecisionCode?.Value ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(static diagnostic => diagnostic.ContextEvidence?.Value ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(static diagnostic => diagnostic.Code, StringComparer.Ordinal)
                .ThenBy(static diagnostic => (int)diagnostic.Severity)
        ];
    }

    static void Append(ArrayBufferWriter<byte> buffer, string value)
    {
        var length = Encoding.UTF8.GetByteCount(value);
        Append(buffer, length);
        var destination = buffer.GetSpan(length);
        Encoding.UTF8.GetBytes(value.AsSpan(), destination);
        buffer.Advance(length);
    }

    static void AppendOptional(ArrayBufferWriter<byte> buffer, string? value)
    {
        if (value is null)
        {
            Append(buffer, -1);
            return;
        }

        Append(buffer, value);
    }

    static void Append(ArrayBufferWriter<byte> buffer, bool value)
    {
        var destination = buffer.GetSpan(1);
        destination[0] = value ? (byte)1 : (byte)0;
        buffer.Advance(1);
    }

    static void Append(ArrayBufferWriter<byte> buffer, int value)
    {
        var destination = buffer.GetSpan(sizeof(int));
        BinaryPrimitives.WriteInt32BigEndian(destination, value);
        buffer.Advance(sizeof(int));
    }

    static void AppendNullableEnum(ArrayBufferWriter<byte> buffer, int? value) =>
        AppendNullableInt32(buffer, value);

    static void AppendNullableInt32(ArrayBufferWriter<byte> buffer, int? value)
    {
        Append(buffer, value.HasValue);
        if (value is { } concrete)
            Append(buffer, concrete);
    }

    static void AppendNullableInt64(ArrayBufferWriter<byte> buffer, long? value)
    {
        Append(buffer, value.HasValue);
        if (value is not { } concrete)
            return;
        AppendInt64(buffer, concrete);
    }

    static void AppendInt64(ArrayBufferWriter<byte> buffer, long value)
    {
        var destination = buffer.GetSpan(sizeof(long));
        BinaryPrimitives.WriteInt64BigEndian(destination, value);
        buffer.Advance(sizeof(long));
    }

    sealed record RelevantInputs(
        ImmutableArray<RelationQueryTargetCapabilityEvidence> Evidence,
        ImmutableArray<RelationQueryOperatingBoundary> Boundaries,
        ImmutableArray<RelationQueryCompositionRule> Rules,
        ImmutableArray<RelationQueryCompositionRuleSelection> RuleSelections,
        ImmutableArray<RelationQueryRealizationOverride> Overrides);
}
