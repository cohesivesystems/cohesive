using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Cohesive.Relations.Realization;

/// <summary>Deterministic strategy preference when several exact realizations are available.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RelationQueryRealizationPreference
{
    /// <summary>Prefer direct native support before an exact composition.</summary>
    PreferNative = 0,

    /// <summary>Prefer an exact declared composition before direct native support.</summary>
    PreferComposed = 1
}

/// <summary>Policy governing exact strategies that require a validated operating boundary.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RelationQueryConstrainedRealizationPolicy
{
    /// <summary>Reject strategies that depend on a constrained operating boundary.</summary>
    Reject = 0,

    /// <summary>Permit a constrained strategy only after every declared boundary is validated.</summary>
    AllowValidated = 1
}

/// <summary>Selects one preferred composition rule for an exact demanded capability.</summary>
public sealed record RelationQueryCompositionRuleSelection
{
    /// <summary>Creates a preferred composition-rule selection.</summary>
    /// <param name="capability">Exact capability for which the rule is preferred.</param>
    /// <param name="rule">Preferred composition rule whose provided capability must match <paramref name="capability"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="capability"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="rule"/> is default.</exception>
    [JsonConstructor]
    public RelationQueryCompositionRuleSelection(
        RelationQueryCapability capability,
        RelationQueryCompositionRuleId rule)
    {
        Capability = Guard.RequireNotNull(capability);
        if (string.IsNullOrWhiteSpace(rule.Value))
            throw new ArgumentException("A composition-rule selection requires a rule identity.", nameof(rule));
        Rule = rule;
    }

    /// <summary>Exact capability for which the rule is preferred.</summary>
    public RelationQueryCapability Capability { get; }

    /// <summary>Preferred composition-rule identity.</summary>
    public RelationQueryCompositionRuleId Rule { get; }
}

/// <summary>Explicit local realization supplied for one exact requirement.</summary>
public sealed record RelationQueryRealizationOverride
{
    /// <summary>Creates an explicit realization override.</summary>
    /// <param name="id">Stable override identity.</param>
    /// <param name="requirement">Exact demand-scoped requirement addressed by the override.</param>
    /// <param name="expectedCapability">
    /// Capability expected at <paramref name="requirement"/>; used to diagnose stale requirement identifiers.
    /// </param>
    /// <param name="capabilityEvidence">Target capability assertions used by the override.</param>
    /// <param name="operatingBoundaries">Validated operating boundaries required by the override.</param>
    /// <param name="preservedGuarantees">Guarantees explicitly preserved by the override.</param>
    /// <param name="justification">Required inspectable explanation for using the override.</param>
    /// <exception cref="ArgumentException">
    /// An identity or <paramref name="justification"/> is empty, or an evidence or boundary collection contains a
    /// default or duplicate identity.
    /// </exception>
    /// <exception cref="ArgumentNullException"><paramref name="expectedCapability"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="preservedGuarantees"/> contains an unsupported guarantee.
    /// </exception>
    [JsonConstructor]
    public RelationQueryRealizationOverride(
        RelationQueryRealizationOverrideId id,
        RelationQueryRealizationRequirementId requirement,
        RelationQueryCapability expectedCapability,
        ImmutableArray<RelationQueryTargetCapabilityEvidenceId> capabilityEvidence = default,
        ImmutableArray<RelationQueryOperatingBoundaryId> operatingBoundaries = default,
        ImmutableArray<RelationQueryGuaranteeCapabilityKind> preservedGuarantees = default,
        string justification = "")
    {
        if (string.IsNullOrWhiteSpace(id.Value))
            throw new ArgumentException("A realization override requires a stable identity.", nameof(id));
        if (string.IsNullOrWhiteSpace(requirement.Value))
            throw new ArgumentException("A realization override requires a requirement identity.", nameof(requirement));
        if (string.IsNullOrWhiteSpace(justification))
            throw new ArgumentException("A realization override requires an inspectable justification.", nameof(justification));
        Id = id;
        Requirement = requirement;
        ExpectedCapability = Guard.RequireNotNull(expectedCapability);
        CapabilityEvidence = NormalizeEvidenceIds(capabilityEvidence, nameof(capabilityEvidence));
        OperatingBoundaries = RelationQueryRealizationOrdering.NormalizeBoundaryIds(
            operatingBoundaries,
            nameof(operatingBoundaries));
        PreservedGuarantees = RelationQueryRealizationOrdering.NormalizeGuarantees(
            preservedGuarantees,
            nameof(preservedGuarantees));
        Justification = justification;
    }

    /// <summary>Stable override identity.</summary>
    public RelationQueryRealizationOverrideId Id { get; }

    /// <summary>Exact demand-scoped requirement addressed by the override.</summary>
    public RelationQueryRealizationRequirementId Requirement { get; }

    /// <summary>Capability expected at <see cref="Requirement"/> so stale identities can be diagnosed.</summary>
    public RelationQueryCapability ExpectedCapability { get; }

    /// <summary>Target capability assertions used by the override.</summary>
    public ImmutableArray<RelationQueryTargetCapabilityEvidenceId> CapabilityEvidence { get; }

    /// <summary>Validated operating boundaries required by the override.</summary>
    public ImmutableArray<RelationQueryOperatingBoundaryId> OperatingBoundaries { get; }

    /// <summary>Guarantees explicitly preserved by the override.</summary>
    public ImmutableArray<RelationQueryGuaranteeCapabilityKind> PreservedGuarantees { get; }

    /// <summary>Inspectable explanation for using the override.</summary>
    public string Justification { get; }

    internal static ImmutableArray<RelationQueryTargetCapabilityEvidenceId> NormalizeEvidenceIds(
        ImmutableArray<RelationQueryTargetCapabilityEvidenceId> evidence,
        string parameterName)
    {
        var normalized = evidence.IsDefault ? [] : evidence;
        if (normalized.Any(static item => string.IsNullOrWhiteSpace(item.Value)))
            throw new ArgumentException("Capability-evidence identities cannot be empty.", parameterName);
        if (normalized.Distinct().Count() != normalized.Length)
            throw new ArgumentException("Capability-evidence identities cannot be duplicated.", parameterName);
        return [.. normalized.OrderBy(static item => item.Value, StringComparer.Ordinal)];
    }
}

/// <summary>Policy override for one stable realization diagnostic code.</summary>
public sealed record RelationQueryRealizationDiagnosticSeverityOverride
{
    /// <summary>Creates a diagnostic-severity override.</summary>
    /// <param name="code">Stable diagnostic code whose default severity is replaced.</param>
    /// <param name="severity">Configured diagnostic severity.</param>
    /// <exception cref="ArgumentNullException"><paramref name="code"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="code"/> is empty or white space.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="severity"/> is unsupported.</exception>
    [JsonConstructor]
    public RelationQueryRealizationDiagnosticSeverityOverride(string code, DiagnosticSeverity severity)
    {
        Code = Guard.RequireNotNullOrWhiteSpace(code);
        if (!Enum.IsDefined(severity))
            throw new ArgumentOutOfRangeException(nameof(severity), severity, "Unsupported diagnostic severity.");
        Severity = severity;
    }

    /// <summary>Stable diagnostic code whose default severity is replaced.</summary>
    public string Code { get; }

    /// <summary>Configured diagnostic severity.</summary>
    public DiagnosticSeverity Severity { get; }
}

/// <summary>Explicit compiler policy controlling deterministic relation/query realization selection.</summary>
public sealed class RelationQueryRealizationPolicy
{
    /// <summary>Creates realization compiler policy.</summary>
    /// <param name="id">Stable, versioned policy identity.</param>
    /// <param name="conventionSetVersion">Stable convention-set version contributing default decisions.</param>
    /// <param name="preference">Preference between exact native and composed strategies.</param>
    /// <param name="constrainedRealizations">Whether validated constrained strategies are permitted.</param>
    /// <param name="compositionRules">Versioned exact capability-composition rules.</param>
    /// <param name="compositionRuleSelections">Preferred rules for exact capabilities with several equivalent compositions.</param>
    /// <param name="overrides">Explicit local requirement overrides.</param>
    /// <param name="diagnosticSeverityOverrides">Explicit diagnostic reporting severity changes.</param>
    /// <exception cref="ArgumentException">
    /// An identity or convention version is empty; a collection contains null entries or duplicate identities; or
    /// composition-rule selections repeat a capability; or diagnostic severity overrides repeat a code.
    /// </exception>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="conventionSetVersion"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="preference"/> or <paramref name="constrainedRealizations"/> is unsupported.
    /// </exception>
    [JsonConstructor]
    public RelationQueryRealizationPolicy(
        RelationQueryRealizationPolicyId id,
        string conventionSetVersion,
        RelationQueryRealizationPreference preference = RelationQueryRealizationPreference.PreferNative,
        RelationQueryConstrainedRealizationPolicy constrainedRealizations = RelationQueryConstrainedRealizationPolicy.Reject,
        ImmutableArray<RelationQueryCompositionRule> compositionRules = default,
        ImmutableArray<RelationQueryCompositionRuleSelection> compositionRuleSelections = default,
        ImmutableArray<RelationQueryRealizationOverride> overrides = default,
        ImmutableArray<RelationQueryRealizationDiagnosticSeverityOverride> diagnosticSeverityOverrides = default)
    {
        if (string.IsNullOrWhiteSpace(id.Value))
            throw new ArgumentException("Realization policy requires a stable identity.", nameof(id));
        Id = id;
        ConventionSetVersion = Guard.RequireNotNullOrWhiteSpace(conventionSetVersion);
        if (!Enum.IsDefined(preference))
            throw new ArgumentOutOfRangeException(nameof(preference), preference, "Unsupported realization preference.");
        if (!Enum.IsDefined(constrainedRealizations))
        {
            throw new ArgumentOutOfRangeException(
                nameof(constrainedRealizations),
                constrainedRealizations,
                "Unsupported constrained-realization policy.");
        }
        Preference = preference;
        ConstrainedRealizations = constrainedRealizations;
        CompositionRules = NormalizeCompositionRules(compositionRules);
        CompositionRuleSelections = NormalizeCompositionRuleSelections(compositionRuleSelections);
        Overrides = NormalizeOverrides(overrides);
        DiagnosticSeverityOverrides = NormalizeSeverityOverrides(diagnosticSeverityOverrides);
    }

    /// <summary>Stable, versioned policy identity.</summary>
    public RelationQueryRealizationPolicyId Id { get; }

    /// <summary>Stable convention-set version contributing default decisions.</summary>
    public string ConventionSetVersion { get; }

    /// <summary>Preference between exact native and composed strategies.</summary>
    public RelationQueryRealizationPreference Preference { get; }

    /// <summary>Whether validated constrained strategies are permitted.</summary>
    public RelationQueryConstrainedRealizationPolicy ConstrainedRealizations { get; }

    /// <summary>Versioned exact capability-composition rules in deterministic identity order.</summary>
    public ImmutableArray<RelationQueryCompositionRule> CompositionRules { get; }

    /// <summary>Preferred composition rules in deterministic capability order.</summary>
    public ImmutableArray<RelationQueryCompositionRuleSelection> CompositionRuleSelections { get; }

    /// <summary>Explicit local requirement overrides in deterministic identity order.</summary>
    public ImmutableArray<RelationQueryRealizationOverride> Overrides { get; }

    /// <summary>Explicit diagnostic reporting severity changes in deterministic code order.</summary>
    public ImmutableArray<RelationQueryRealizationDiagnosticSeverityOverride> DiagnosticSeverityOverrides { get; }

    static ImmutableArray<RelationQueryCompositionRule> NormalizeCompositionRules(
        ImmutableArray<RelationQueryCompositionRule> rules)
    {
        var normalized = rules.IsDefault ? [] : rules;
        if (normalized.Any(static rule => rule is null))
            throw new ArgumentException("Composition rules cannot contain null entries.", nameof(rules));
        if (normalized.GroupBy(static rule => rule.Id).Any(static group => group.Count() > 1))
            throw new ArgumentException("Composition rules cannot repeat an identity.", nameof(rules));
        return [.. normalized.OrderBy(static rule => rule.Id.Value, StringComparer.Ordinal)];
    }

    static ImmutableArray<RelationQueryCompositionRuleSelection> NormalizeCompositionRuleSelections(
        ImmutableArray<RelationQueryCompositionRuleSelection> selections)
    {
        var normalized = selections.IsDefault ? [] : selections;
        if (normalized.Any(static selection => selection is null))
            throw new ArgumentException("Composition-rule selections cannot contain null entries.", nameof(selections));
        if (normalized.GroupBy(static selection => selection.Capability).Any(static group => group.Count() > 1))
            throw new ArgumentException("Composition-rule selections cannot repeat a capability.", nameof(selections));
        return
        [
            .. normalized.OrderBy(
                static selection => RelationQueryRealizationOrdering.CapabilityKey(selection.Capability),
                StringComparer.Ordinal)
        ];
    }

    static ImmutableArray<RelationQueryRealizationOverride> NormalizeOverrides(
        ImmutableArray<RelationQueryRealizationOverride> overrides)
    {
        var normalized = overrides.IsDefault ? [] : overrides;
        if (normalized.Any(static item => item is null))
            throw new ArgumentException("Realization overrides cannot contain null entries.", nameof(overrides));
        if (normalized.GroupBy(static item => item.Id).Any(static group => group.Count() > 1))
            throw new ArgumentException("Realization overrides cannot repeat an identity.", nameof(overrides));
        return [.. normalized.OrderBy(static item => item.Id.Value, StringComparer.Ordinal)];
    }

    static ImmutableArray<RelationQueryRealizationDiagnosticSeverityOverride> NormalizeSeverityOverrides(
        ImmutableArray<RelationQueryRealizationDiagnosticSeverityOverride> overrides)
    {
        var normalized = overrides.IsDefault ? [] : overrides;
        if (normalized.Any(static item => item is null))
            throw new ArgumentException("Diagnostic severity overrides cannot contain null entries.", nameof(overrides));
        if (normalized.GroupBy(static item => item.Code, StringComparer.Ordinal).Any(static group => group.Count() > 1))
            throw new ArgumentException("Diagnostic severity overrides cannot repeat a code.", nameof(overrides));
        return [.. normalized.OrderBy(static item => item.Code, StringComparer.Ordinal)];
    }
}
