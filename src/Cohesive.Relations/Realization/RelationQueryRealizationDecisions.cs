using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Cohesive.Model.Serialization;

namespace Cohesive.Relations.Realization;

/// <summary>Final classification selected for one demanded realization requirement.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RelationQueryRealizationDecisionKind
{
    /// <summary>The target preserves the requirement directly.</summary>
    Native = 0,

    /// <summary>A declared rule composes exact support from target facilities.</summary>
    Composed = 1,

    /// <summary>Exact support is available only inside validated operating boundaries.</summary>
    Constrained = 2,

    /// <summary>An explicit local override supplies exact support.</summary>
    Override = 3,

    /// <summary>No permitted strategy preserves the requirement.</summary>
    Unavailable = 4
}

/// <summary>Reason that a demanded realization requirement has no permitted exact strategy.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RelationQueryUnavailableReason
{
    /// <summary>The target does not advertise the required capability.</summary>
    CapabilityNotAdvertised = 0,

    /// <summary>The target does not support the plan's canonical schema or compiler profile.</summary>
    ProfileVersionUnsupported = 1,

    /// <summary>A required operating boundary was not declared.</summary>
    OperatingBoundaryMissing = 2,

    /// <summary>A declared operating boundary could not be validated.</summary>
    OperatingBoundaryInvalid = 3,

    /// <summary>No declared exact composition rule can prove support.</summary>
    CompositionUnavailable = 4,

    /// <summary>Several equally preferred strategies remain ambiguous.</summary>
    AmbiguousStrategy = 5,

    /// <summary>Capability evidence conflicts or is incomplete.</summary>
    CapabilityEvidenceInvalid = 6,

    /// <summary>An explicit override is missing, stale, or invalid.</summary>
    OverrideInvalid = 7,

    /// <summary>Compiler policy rejects the otherwise available strategy.</summary>
    PolicyRejected = 8,

    /// <summary>
    /// The requirement was not examined because an attributable prerequisite adapter decision already failed.
    /// </summary>
    PrerequisiteBlocked = 9
}

/// <summary>How an operating boundary used by a constrained realization was validated.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RelationQueryOperatingBoundaryValidationKind
{
    /// <summary>The compiler proved the boundary from immutable demand-scoped plan facts.</summary>
    StaticPlanFact = 0,

    /// <summary>The target explicitly advertises attributable enforcement of the boundary at execution.</summary>
    TargetEnforced = 1
}

/// <summary>Attributable proof that one constrained operating boundary will hold.</summary>
public sealed record RelationQueryOperatingBoundaryValidation
{
    /// <summary>Creates an operating-boundary validation.</summary>
    /// <param name="boundary">Validated operating boundary.</param>
    /// <param name="kind">How the boundary is established.</param>
    /// <param name="capabilityEvidence">
    /// Target enforcement evidence for <see cref="RelationQueryOperatingBoundaryValidationKind.TargetEnforced"/>;
    /// otherwise <see langword="null"/>.
    /// </param>
    /// <param name="measuredValue">
    /// Measured non-negative plan value for a numeric static boundary; otherwise <see langword="null"/>.
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="boundary"/> is default; target enforcement omits evidence or supplies a static measurement;
    /// or static validation supplies target evidence.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="kind"/> is unsupported, or <paramref name="measuredValue"/> is negative.
    /// </exception>
    [JsonConstructor]
    public RelationQueryOperatingBoundaryValidation(
        RelationQueryOperatingBoundaryId boundary,
        RelationQueryOperatingBoundaryValidationKind kind,
        RelationQueryTargetCapabilityEvidenceId? capabilityEvidence = null,
        long? measuredValue = null)
    {
        if (string.IsNullOrWhiteSpace(boundary.Value))
            throw new ArgumentException("An operating-boundary validation requires a boundary identity.", nameof(boundary));
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported boundary-validation kind.");
        if (kind == RelationQueryOperatingBoundaryValidationKind.TargetEnforced
            && (capabilityEvidence is not { } evidence || string.IsNullOrWhiteSpace(evidence.Value)))
        {
            throw new ArgumentException("Target-enforced validation requires capability evidence.", nameof(capabilityEvidence));
        }
        if (kind == RelationQueryOperatingBoundaryValidationKind.StaticPlanFact && capabilityEvidence is not null)
            throw new ArgumentException("Static plan validation cannot reference target evidence.", nameof(capabilityEvidence));
        if (kind == RelationQueryOperatingBoundaryValidationKind.TargetEnforced && measuredValue is not null)
            throw new ArgumentException("Target-enforced validation cannot carry a static measured value.", nameof(measuredValue));
        if (measuredValue is < 0)
            throw new ArgumentOutOfRangeException(nameof(measuredValue), measuredValue, "A measured boundary value cannot be negative.");

        Boundary = boundary;
        Kind = kind;
        CapabilityEvidence = capabilityEvidence;
        MeasuredValue = measuredValue;
    }

    /// <summary>Validated operating boundary.</summary>
    public RelationQueryOperatingBoundaryId Boundary { get; }

    /// <summary>How the boundary is established.</summary>
    public RelationQueryOperatingBoundaryValidationKind Kind { get; }

    /// <summary>Target enforcement evidence, or <see langword="null"/> for a static plan fact.</summary>
    public RelationQueryTargetCapabilityEvidenceId? CapabilityEvidence { get; }

    /// <summary>
    /// Measured plan value for a numeric static boundary, or <see langword="null"/>. Portable JSON encodes
    /// a supplied value as a canonical decimal string.
    /// </summary>
    [JsonConverter(typeof(StringEncodedInt64JsonConverter))]
    public long? MeasuredValue { get; }
}

/// <summary>Closed portable final decision for one realization requirement.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = RelationQueryRealizationWireNames.DecisionDiscriminator)]
[JsonDerivedType(typeof(NativeRelationQueryRealizationDecision), RelationQueryRealizationWireNames.NativeDecision)]
[JsonDerivedType(typeof(ComposedRelationQueryRealizationDecision), RelationQueryRealizationWireNames.ComposedDecision)]
[JsonDerivedType(typeof(ConstrainedRelationQueryRealizationDecision), RelationQueryRealizationWireNames.ConstrainedDecision)]
[JsonDerivedType(typeof(OverrideRelationQueryRealizationDecision), RelationQueryRealizationWireNames.OverrideDecision)]
[JsonDerivedType(typeof(UnavailableRelationQueryRealizationDecision), RelationQueryRealizationWireNames.UnavailableDecision)]
public abstract record RelationQueryRealizationDecision
{
    /// <summary>Creates a final decision for one requirement.</summary>
    /// <param name="requirement">Demand-scoped requirement receiving the decision.</param>
    /// <exception cref="ArgumentException"><paramref name="requirement"/> is default.</exception>
    private protected RelationQueryRealizationDecision(RelationQueryRealizationRequirementId requirement)
    {
        if (string.IsNullOrWhiteSpace(requirement.Value))
            throw new ArgumentException("A realization decision requires a requirement identity.", nameof(requirement));
        Requirement = requirement;
    }

    /// <summary>Demand-scoped requirement receiving the decision.</summary>
    public RelationQueryRealizationRequirementId Requirement { get; }

    /// <summary>Final realization classification.</summary>
    [JsonIgnore]
    public abstract RelationQueryRealizationDecisionKind Kind { get; }

    /// <summary>Gets the target capability evidence retained by this final decision.</summary>
    /// <returns>
    /// Canonically ordered capability-evidence identities, or an empty array for an unavailable decision or an
    /// override that does not rely on target evidence.
    /// </returns>
    public ImmutableArray<RelationQueryTargetCapabilityEvidenceId> GetCapabilityEvidence() => this switch
    {
        NativeRelationQueryRealizationDecision native => native.CapabilityEvidence,
        ComposedRelationQueryRealizationDecision composed => composed.CapabilityEvidence,
        ConstrainedRelationQueryRealizationDecision constrained => constrained.CapabilityEvidence,
        OverrideRelationQueryRealizationDecision overridden => overridden.CapabilityEvidence,
        UnavailableRelationQueryRealizationDecision => [],
        _ => throw new InvalidOperationException(
            $"Unsupported realization decision '{GetType().Name}'.")
    };

    /// <summary>Gets the composition-rule closure retained by this final decision.</summary>
    /// <returns>
    /// Canonically ordered composition-rule identities, or an empty array when the decision is not composed.
    /// </returns>
    public ImmutableArray<RelationQueryCompositionRuleId> GetCompositionRules() => this switch
    {
        ComposedRelationQueryRealizationDecision composed => composed.CompositionRules,
        ConstrainedRelationQueryRealizationDecision constrained => constrained.CompositionRules,
        NativeRelationQueryRealizationDecision or OverrideRelationQueryRealizationDecision
            or UnavailableRelationQueryRealizationDecision => [],
        _ => throw new InvalidOperationException(
            $"Unsupported realization decision '{GetType().Name}'.")
    };

    /// <summary>Gets every attributable operating-boundary validation retained by this final decision.</summary>
    /// <returns>
    /// Canonically ordered boundary validations, or an empty array when the decision is not boundary-constrained.
    /// </returns>
    public ImmutableArray<RelationQueryOperatingBoundaryValidation> GetBoundaryValidations() => this switch
    {
        ConstrainedRelationQueryRealizationDecision constrained => constrained.BoundaryValidations,
        OverrideRelationQueryRealizationDecision overridden => overridden.BoundaryValidations,
        NativeRelationQueryRealizationDecision or ComposedRelationQueryRealizationDecision
            or UnavailableRelationQueryRealizationDecision => [],
        _ => throw new InvalidOperationException(
            $"Unsupported realization decision '{GetType().Name}'.")
    };

    /// <summary>Gets operating boundaries whose exact enforcement must be re-established by a target adapter.</summary>
    /// <returns>Target-enforced operating-boundary identities in canonical order.</returns>
    public ImmutableArray<RelationQueryOperatingBoundaryId> GetTargetEnforcedBoundaries() =>
    [
        .. GetBoundaryValidations()
            .Where(static validation =>
                validation.Kind == RelationQueryOperatingBoundaryValidationKind.TargetEnforced)
            .Select(static validation => validation.Boundary)
    ];

    /// <summary>Gets the guarantees retained by this final decision.</summary>
    /// <returns>
    /// Canonically ordered preserved guarantees, or an empty array when the decision is unavailable.
    /// </returns>
    public ImmutableArray<RelationQueryGuaranteeCapabilityKind> GetPreservedGuarantees() => this switch
    {
        NativeRelationQueryRealizationDecision native => native.PreservedGuarantees,
        ComposedRelationQueryRealizationDecision composed => composed.PreservedGuarantees,
        ConstrainedRelationQueryRealizationDecision constrained => constrained.PreservedGuarantees,
        OverrideRelationQueryRealizationDecision overridden => overridden.PreservedGuarantees,
        UnavailableRelationQueryRealizationDecision => [],
        _ => throw new InvalidOperationException(
            $"Unsupported realization decision '{GetType().Name}'.")
    };
}

/// <summary>Decision proving that a target preserves a requirement directly.</summary>
public sealed record NativeRelationQueryRealizationDecision : RelationQueryRealizationDecision
{
    /// <summary>Creates a native realization decision.</summary>
    /// <param name="requirement">Demand-scoped requirement receiving the decision.</param>
    /// <param name="capabilityEvidence">Non-empty target evidence proving direct support.</param>
    /// <param name="preservedGuarantees">Guarantees explicitly preserved by the native strategy.</param>
    /// <exception cref="ArgumentException">
    /// An identity is default, <paramref name="capabilityEvidence"/> is empty, or an evidence identity is duplicated.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="preservedGuarantees"/> contains an unsupported guarantee.
    /// </exception>
    [JsonConstructor]
    public NativeRelationQueryRealizationDecision(
        RelationQueryRealizationRequirementId requirement,
        ImmutableArray<RelationQueryTargetCapabilityEvidenceId> capabilityEvidence,
        ImmutableArray<RelationQueryGuaranteeCapabilityKind> preservedGuarantees = default)
        : base(requirement)
    {
        CapabilityEvidence = NormalizeRequiredEvidence(capabilityEvidence, nameof(capabilityEvidence));
        PreservedGuarantees = RelationQueryRealizationOrdering.NormalizeGuarantees(
            preservedGuarantees,
            nameof(preservedGuarantees));
    }

    /// <inheritdoc />
    public override RelationQueryRealizationDecisionKind Kind => RelationQueryRealizationDecisionKind.Native;

    /// <summary>Target evidence proving direct support.</summary>
    public ImmutableArray<RelationQueryTargetCapabilityEvidenceId> CapabilityEvidence { get; }

    /// <summary>Guarantees explicitly preserved by the native strategy.</summary>
    public ImmutableArray<RelationQueryGuaranteeCapabilityKind> PreservedGuarantees { get; }

    internal static ImmutableArray<RelationQueryTargetCapabilityEvidenceId> NormalizeRequiredEvidence(
        ImmutableArray<RelationQueryTargetCapabilityEvidenceId> evidence,
        string parameterName)
    {
        var normalized = RelationQueryRealizationOverride.NormalizeEvidenceIds(evidence, parameterName);
        if (normalized.IsDefaultOrEmpty)
            throw new ArgumentException("At least one capability-evidence identity is required.", parameterName);
        return normalized;
    }
}

/// <summary>Decision proving exact support through one declared composition rule.</summary>
public sealed record ComposedRelationQueryRealizationDecision : RelationQueryRealizationDecision
{
    /// <summary>Creates a composed realization decision.</summary>
    /// <param name="requirement">Demand-scoped requirement receiving the decision.</param>
    /// <param name="compositionRules">Non-empty ordered rule closure proving exact support.</param>
    /// <param name="capabilityEvidence">Target evidence satisfying every primitive rule input.</param>
    /// <param name="preservedGuarantees">Guarantees explicitly preserved by the composition.</param>
    /// <exception cref="ArgumentException">
    /// An identity is default; <paramref name="compositionRules"/> or <paramref name="capabilityEvidence"/> is
    /// empty; or a rule or evidence identity is duplicated.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="preservedGuarantees"/> contains an unsupported guarantee.
    /// </exception>
    [JsonConstructor]
    public ComposedRelationQueryRealizationDecision(
        RelationQueryRealizationRequirementId requirement,
        ImmutableArray<RelationQueryCompositionRuleId> compositionRules,
        ImmutableArray<RelationQueryTargetCapabilityEvidenceId> capabilityEvidence,
        ImmutableArray<RelationQueryGuaranteeCapabilityKind> preservedGuarantees = default)
        : base(requirement)
    {
        CompositionRules = NormalizeRuleIds(compositionRules, nameof(compositionRules), requireNonEmpty: true);
        CapabilityEvidence = NativeRelationQueryRealizationDecision.NormalizeRequiredEvidence(
            capabilityEvidence,
            nameof(capabilityEvidence));
        PreservedGuarantees = RelationQueryRealizationOrdering.NormalizeGuarantees(
            preservedGuarantees,
            nameof(preservedGuarantees));
    }

    /// <inheritdoc />
    public override RelationQueryRealizationDecisionKind Kind => RelationQueryRealizationDecisionKind.Composed;

    /// <summary>Ordered versioned rule closure proving exact support.</summary>
    public ImmutableArray<RelationQueryCompositionRuleId> CompositionRules { get; }

    /// <summary>Target evidence satisfying every primitive rule input.</summary>
    public ImmutableArray<RelationQueryTargetCapabilityEvidenceId> CapabilityEvidence { get; }

    /// <summary>Guarantees explicitly preserved by the composition.</summary>
    public ImmutableArray<RelationQueryGuaranteeCapabilityKind> PreservedGuarantees { get; }

    internal static ImmutableArray<RelationQueryCompositionRuleId> NormalizeRuleIds(
        ImmutableArray<RelationQueryCompositionRuleId> rules,
        string parameterName,
        bool requireNonEmpty)
    {
        var normalized = rules.IsDefault ? [] : rules;
        if (normalized.Any(static rule => string.IsNullOrWhiteSpace(rule.Value)))
            throw new ArgumentException("Composition-rule identities cannot be empty.", parameterName);
        if (normalized.Distinct().Count() != normalized.Length)
            throw new ArgumentException("Composition-rule identities cannot be duplicated.", parameterName);
        var result = normalized.OrderBy(static rule => rule.Value, StringComparer.Ordinal).ToImmutableArray();
        if (requireNonEmpty && result.IsDefaultOrEmpty)
            throw new ArgumentException("At least one composition-rule identity is required.", parameterName);
        return result;
    }
}

/// <summary>Decision proving exact support only within explicit validated operating boundaries.</summary>
public sealed record ConstrainedRelationQueryRealizationDecision : RelationQueryRealizationDecision
{
    /// <summary>Creates a constrained realization decision.</summary>
    /// <param name="requirement">Demand-scoped requirement receiving the decision.</param>
    /// <param name="capabilityEvidence">Target evidence supporting the constrained strategy.</param>
    /// <param name="boundaryValidations">Non-empty attributable validations under which semantics are preserved.</param>
    /// <param name="compositionRules">Ordered composition-rule closure used by a constrained composed strategy.</param>
    /// <param name="preservedGuarantees">Guarantees explicitly preserved inside the boundary.</param>
    /// <exception cref="ArgumentException">
    /// An identity is default; evidence or boundary validations are empty; a boundary validation is
    /// <see langword="null"/>; or an evidence, boundary, or rule identity is duplicated.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="preservedGuarantees"/> contains an unsupported guarantee.
    /// </exception>
    [JsonConstructor]
    public ConstrainedRelationQueryRealizationDecision(
        RelationQueryRealizationRequirementId requirement,
        ImmutableArray<RelationQueryTargetCapabilityEvidenceId> capabilityEvidence,
        ImmutableArray<RelationQueryOperatingBoundaryValidation> boundaryValidations,
        ImmutableArray<RelationQueryCompositionRuleId> compositionRules = default,
        ImmutableArray<RelationQueryGuaranteeCapabilityKind> preservedGuarantees = default)
        : base(requirement)
    {
        CapabilityEvidence = NativeRelationQueryRealizationDecision.NormalizeRequiredEvidence(
            capabilityEvidence,
            nameof(capabilityEvidence));
        BoundaryValidations = NormalizeBoundaryValidations(boundaryValidations, nameof(boundaryValidations));
        if (BoundaryValidations.IsDefaultOrEmpty)
            throw new ArgumentException("A constrained decision requires at least one boundary validation.", nameof(boundaryValidations));
        CompositionRules = ComposedRelationQueryRealizationDecision.NormalizeRuleIds(
            compositionRules,
            nameof(compositionRules),
            requireNonEmpty: false);
        PreservedGuarantees = RelationQueryRealizationOrdering.NormalizeGuarantees(
            preservedGuarantees,
            nameof(preservedGuarantees));
    }

    /// <inheritdoc />
    public override RelationQueryRealizationDecisionKind Kind => RelationQueryRealizationDecisionKind.Constrained;

    /// <summary>Target evidence supporting the constrained strategy.</summary>
    public ImmutableArray<RelationQueryTargetCapabilityEvidenceId> CapabilityEvidence { get; }

    /// <summary>Attributable validations under which semantics are preserved.</summary>
    public ImmutableArray<RelationQueryOperatingBoundaryValidation> BoundaryValidations { get; }

    /// <summary>Ordered composition-rule closure used by a constrained composed strategy.</summary>
    public ImmutableArray<RelationQueryCompositionRuleId> CompositionRules { get; }

    /// <summary>Guarantees explicitly preserved inside the boundary.</summary>
    public ImmutableArray<RelationQueryGuaranteeCapabilityKind> PreservedGuarantees { get; }

    internal static ImmutableArray<RelationQueryOperatingBoundaryValidation> NormalizeBoundaryValidations(
        ImmutableArray<RelationQueryOperatingBoundaryValidation> validations,
        string parameterName)
    {
        var normalized = validations.IsDefault ? [] : validations;
        if (normalized.Any(static validation => validation is null))
            throw new ArgumentException("Boundary validations cannot contain null entries.", parameterName);
        if (normalized.GroupBy(static validation => validation.Boundary).Any(static group => group.Count() > 1))
            throw new ArgumentException("Boundary validations cannot repeat a boundary identity.", parameterName);
        return [.. normalized.OrderBy(static validation => validation.Boundary.Value, StringComparer.Ordinal)];
    }
}

/// <summary>Decision proving exact support through one explicit local override.</summary>
public sealed record OverrideRelationQueryRealizationDecision : RelationQueryRealizationDecision
{
    /// <summary>Creates an override realization decision.</summary>
    /// <param name="requirement">Demand-scoped requirement receiving the decision.</param>
    /// <param name="override">Explicit override supplying the realization.</param>
    /// <param name="capabilityEvidence">Target evidence used by the override.</param>
    /// <param name="boundaryValidations">Attributable validations for operating boundaries required by the override.</param>
    /// <param name="preservedGuarantees">Guarantees explicitly preserved by the override.</param>
    /// <exception cref="ArgumentException">
    /// An identity is default; a boundary validation is <see langword="null"/>; or an evidence or boundary identity
    /// is duplicated.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="preservedGuarantees"/> contains an unsupported guarantee.
    /// </exception>
    [JsonConstructor]
    public OverrideRelationQueryRealizationDecision(
        RelationQueryRealizationRequirementId requirement,
        RelationQueryRealizationOverrideId @override,
        ImmutableArray<RelationQueryTargetCapabilityEvidenceId> capabilityEvidence = default,
        ImmutableArray<RelationQueryOperatingBoundaryValidation> boundaryValidations = default,
        ImmutableArray<RelationQueryGuaranteeCapabilityKind> preservedGuarantees = default)
        : base(requirement)
    {
        if (string.IsNullOrWhiteSpace(@override.Value))
            throw new ArgumentException("An override decision requires an override identity.", nameof(@override));
        Override = @override;
        CapabilityEvidence = RelationQueryRealizationOverride.NormalizeEvidenceIds(
            capabilityEvidence,
            nameof(capabilityEvidence));
        BoundaryValidations = ConstrainedRelationQueryRealizationDecision.NormalizeBoundaryValidations(
            boundaryValidations,
            nameof(boundaryValidations));
        PreservedGuarantees = RelationQueryRealizationOrdering.NormalizeGuarantees(
            preservedGuarantees,
            nameof(preservedGuarantees));
    }

    /// <inheritdoc />
    public override RelationQueryRealizationDecisionKind Kind => RelationQueryRealizationDecisionKind.Override;

    /// <summary>Explicit override supplying the realization.</summary>
    public RelationQueryRealizationOverrideId Override { get; }

    /// <summary>Target evidence used by the override.</summary>
    public ImmutableArray<RelationQueryTargetCapabilityEvidenceId> CapabilityEvidence { get; }

    /// <summary>Attributable validations for operating boundaries required by the override.</summary>
    public ImmutableArray<RelationQueryOperatingBoundaryValidation> BoundaryValidations { get; }

    /// <summary>Guarantees explicitly preserved by the override.</summary>
    public ImmutableArray<RelationQueryGuaranteeCapabilityKind> PreservedGuarantees { get; }
}

/// <summary>Decision recording that no permitted strategy preserves a demanded requirement.</summary>
public sealed record UnavailableRelationQueryRealizationDecision : RelationQueryRealizationDecision
{
    /// <summary>Creates an unavailable realization decision.</summary>
    /// <param name="requirement">Demand-scoped requirement receiving the decision.</param>
    /// <param name="reason">Typed reason no permitted exact strategy exists.</param>
    /// <param name="missingCapabilities">Capabilities whose absence contributed to unavailability.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="requirement"/> is default or <paramref name="missingCapabilities"/> contains null entries.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="reason"/> is unsupported.</exception>
    [JsonConstructor]
    public UnavailableRelationQueryRealizationDecision(
        RelationQueryRealizationRequirementId requirement,
        RelationQueryUnavailableReason reason,
        ImmutableArray<RelationQueryCapability> missingCapabilities = default)
        : base(requirement)
    {
        if (!Enum.IsDefined(reason))
            throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unsupported unavailable-reason kind.");
        Reason = reason;
        MissingCapabilities = RelationQueryRealizationOrdering.NormalizeCapabilities(
            missingCapabilities,
            nameof(missingCapabilities));
    }

    /// <inheritdoc />
    public override RelationQueryRealizationDecisionKind Kind => RelationQueryRealizationDecisionKind.Unavailable;

    /// <summary>Typed reason no permitted exact strategy exists.</summary>
    public RelationQueryUnavailableReason Reason { get; }

    /// <summary>Capabilities whose absence contributed to unavailability.</summary>
    public ImmutableArray<RelationQueryCapability> MissingCapabilities { get; }
}
