using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.IR;
using Cohesive.Relations.Physical;

namespace Cohesive.Relations.Realization;

/// <summary>Outcome of examining one exact adapter fact for one branch-scoped realization requirement.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RelationQueryBoundAssessmentStatus
{
    /// <summary>The examined physical fact preserves the requirement within the attributed boundaries.</summary>
    Available = 0,

    /// <summary>The physical fact is valid but cannot preserve the requested semantics.</summary>
    Unavailable = 1,

    /// <summary>The fact is stale, malformed, contradictory, or cannot be trusted.</summary>
    Invalid = 2,

    /// <summary>
    /// The requirement was not examined because a prior unavailable or invalid adapter decision blocks it.
    /// </summary>
    Blocked = 3
}

/// <summary>Stable adapter-owned identity of one contextual realization assessment.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct RelationQueryContextEvidenceId
{
    /// <summary>Creates a contextual-evidence identity.</summary>
    /// <param name="value">Stable adapter-owned identity value.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white space.</exception>
    [JsonConstructor]
    public RelationQueryContextEvidenceId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Stable adapter-owned identity value.</summary>
    public string Value { get; }

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>Stable adapter-owned code identifying one contextual realization decision.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct RelationQueryAdapterDecisionCode
{
    /// <summary>Creates an adapter decision code.</summary>
    /// <param name="value">Stable adapter-owned decision code.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white space.</exception>
    [JsonConstructor]
    public RelationQueryAdapterDecisionCode(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Stable adapter-owned decision code.</summary>
    public string Value { get; }

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>Target-neutral projection of an adapter storage-binding fingerprint.</summary>
public sealed record RelationQueryAdapterBindingFingerprint
{
    /// <summary>Creates an adapter-binding fingerprint projection.</summary>
    /// <param name="algorithm">Hash algorithm identity.</param>
    /// <param name="canonicalization">Binding canonicalization profile.</param>
    /// <param name="value">Canonical lowercase hexadecimal hash value.</param>
    /// <exception cref="ArgumentNullException">A parameter is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A parameter is empty or white space.</exception>
    [JsonConstructor]
    public RelationQueryAdapterBindingFingerprint(string algorithm, string canonicalization, string value)
    {
        Algorithm = Guard.RequireNotNullOrWhiteSpace(algorithm);
        Canonicalization = Guard.RequireNotNullOrWhiteSpace(canonicalization);
        Value = Guard.RequireNotNullOrWhiteSpace(value);
    }

    /// <summary>Hash algorithm identity.</summary>
    public string Algorithm { get; }

    /// <summary>Binding canonicalization profile.</summary>
    public string Canonicalization { get; }

    /// <summary>Canonical lowercase hexadecimal hash value.</summary>
    public string Value { get; }
}

/// <summary>
/// Target-neutral reference to the exact adapter-owned storage binding examined by contextual realization.
/// </summary>
/// <remarks>
/// The adapter binding remains the authority for physical facts. This reference retains only identity, affinity,
/// and configuration provenance required to verify and explain a bound realization.
/// </remarks>
public sealed record RelationQueryAdapterBindingReference
{
    /// <summary>Creates an exact adapter-binding reference.</summary>
    /// <param name="schemaVersion">Persisted adapter-binding schema version.</param>
    /// <param name="bindingId">Stable adapter-binding identity.</param>
    /// <param name="target">Interpretation target identity.</param>
    /// <param name="targetProfile">Exact target capability-profile identity.</param>
    /// <param name="fingerprint">Deterministic fingerprint of the complete adapter binding.</param>
    /// <param name="compiledPlanFingerprint">
    /// Exact compiled-plan affinity, or <see langword="null"/> when the low-level binding is unverified.
    /// </param>
    /// <param name="placementFingerprint">
    /// Exact source-placement affinity, or <see langword="null"/> when the low-level binding is unverified.
    /// </param>
    /// <param name="sources">Physical source instances referenced by the binding.</param>
    /// <param name="placementBindings">Plan-scoped placement bindings referenced by the binding.</param>
    /// <param name="configurationDecisions">Attribution for effective adapter-binding settings.</param>
    /// <exception cref="ArgumentNullException">
    /// A required reference or string parameter is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// An identity is empty; plan and placement affinity are not both supplied or both omitted; or a collection
    /// contains a null, default, or repeated entry.
    /// </exception>
    [JsonConstructor]
    public RelationQueryAdapterBindingReference(
        string schemaVersion,
        string bindingId,
        RelationQueryTargetId target,
        RelationQueryTargetProfileId targetProfile,
        RelationQueryAdapterBindingFingerprint fingerprint,
        RelationQueryPlanComponentFingerprint? compiledPlanFingerprint = null,
        RelationQuerySourcePlacementFingerprint? placementFingerprint = null,
        ImmutableArray<RelationQuerySourceInstanceId> sources = default,
        ImmutableArray<RelationQuerySourcePlacementBindingId> placementBindings = default,
        ImmutableArray<RelationQueryConfigurationDecision> configurationDecisions = default)
    {
        SchemaVersion = Guard.RequireNotNullOrWhiteSpace(schemaVersion);
        BindingId = Guard.RequireNotNullOrWhiteSpace(bindingId);
        if (string.IsNullOrWhiteSpace(target.Value))
            throw new ArgumentException("An adapter-binding reference requires a target identity.", nameof(target));
        if (string.IsNullOrWhiteSpace(targetProfile.Value))
            throw new ArgumentException("An adapter-binding reference requires a target-profile identity.", nameof(targetProfile));
        if ((compiledPlanFingerprint is null) != (placementFingerprint is null))
        {
            throw new ArgumentException(
                "Adapter-binding plan and placement affinity must be supplied together or both omitted.",
                nameof(compiledPlanFingerprint));
        }

        Target = target;
        TargetProfile = targetProfile;
        Fingerprint = Guard.RequireNotNull(fingerprint);
        CompiledPlanFingerprint = compiledPlanFingerprint;
        PlacementFingerprint = placementFingerprint;
        Sources = RelationQueryRealizationOrdering.NormalizeIdentityValues(
            sources,
            static value => value.Value,
            nameof(sources));
        PlacementBindings = RelationQueryRealizationOrdering.NormalizeIdentityValues(
            placementBindings,
            static value => value.Value,
            nameof(placementBindings));
        ConfigurationDecisions = NormalizeConfiguration(configurationDecisions);
    }

    /// <summary>Persisted adapter-binding schema version.</summary>
    public string SchemaVersion { get; }

    /// <summary>Stable adapter-binding identity.</summary>
    public string BindingId { get; }

    /// <summary>Interpretation target identity.</summary>
    public RelationQueryTargetId Target { get; }

    /// <summary>Exact target capability-profile identity.</summary>
    public RelationQueryTargetProfileId TargetProfile { get; }

    /// <summary>Deterministic fingerprint of the complete adapter binding.</summary>
    public RelationQueryAdapterBindingFingerprint Fingerprint { get; }

    /// <summary>Exact compiled-plan affinity, or <see langword="null"/> for an unverified low-level binding.</summary>
    public RelationQueryPlanComponentFingerprint? CompiledPlanFingerprint { get; }

    /// <summary>Exact source-placement affinity, or <see langword="null"/> for an unverified low-level binding.</summary>
    public RelationQuerySourcePlacementFingerprint? PlacementFingerprint { get; }

    /// <summary>Physical source instances referenced by the binding in stable identity order.</summary>
    public ImmutableArray<RelationQuerySourceInstanceId> Sources { get; }

    /// <summary>Plan-scoped placement bindings referenced by the binding in stable identity order.</summary>
    public ImmutableArray<RelationQuerySourcePlacementBindingId> PlacementBindings { get; }

    /// <summary>Effective adapter-binding configuration attribution in stable setting order.</summary>
    public ImmutableArray<RelationQueryConfigurationDecision> ConfigurationDecisions { get; }

    /// <summary>Determines whether another reference describes the same exact binding evidence.</summary>
    /// <param name="other">Binding reference to compare, or <see langword="null"/>.</param>
    /// <returns>
    /// <see langword="true"/> when every scalar affinity value and every canonically ordered evidence collection
    /// is equal; otherwise, <see langword="false"/>.
    /// </returns>
    public bool HasSameSemantics(RelationQueryAdapterBindingReference? other) =>
        other is not null
        && string.Equals(SchemaVersion, other.SchemaVersion, StringComparison.Ordinal)
        && string.Equals(BindingId, other.BindingId, StringComparison.Ordinal)
        && Target == other.Target
        && TargetProfile == other.TargetProfile
        && Equals(Fingerprint, other.Fingerprint)
        && Equals(CompiledPlanFingerprint, other.CompiledPlanFingerprint)
        && Equals(PlacementFingerprint, other.PlacementFingerprint)
        && Sources.SequenceEqual(other.Sources)
        && PlacementBindings.SequenceEqual(other.PlacementBindings)
        && ConfigurationDecisions.SequenceEqual(other.ConfigurationDecisions);

    static ImmutableArray<RelationQueryConfigurationDecision> NormalizeConfiguration(
        ImmutableArray<RelationQueryConfigurationDecision> decisions)
    {
        var normalized = decisions.IsDefault ? [] : decisions;
        if (normalized.Any(static decision => decision is null))
            throw new ArgumentException("Configuration decisions cannot contain null entries.", nameof(decisions));
        if (normalized.GroupBy(static decision => decision.Setting, StringComparer.Ordinal)
            .Any(static group => group.Count() > 1))
        {
            throw new ArgumentException("Configuration decisions cannot repeat a setting.", nameof(decisions));
        }

        return [.. normalized.OrderBy(static decision => decision.Setting, StringComparer.Ordinal)];
    }
}

/// <summary>One adapter-projected contextual assessment of a branch-scoped semantic requirement.</summary>
public sealed record RelationQueryBoundRequirementAssessment
{
    /// <summary>Creates one contextual requirement assessment.</summary>
    /// <param name="id">Stable adapter-owned evidence identity.</param>
    /// <param name="branch">Selected result branch affected by the assessment.</param>
    /// <param name="requirement">Demand-scoped semantic requirement being assessed.</param>
    /// <param name="status">Available, unavailable, invalid, or prerequisite-blocked contextual outcome.</param>
    /// <param name="origin">Configuration-precedence tier that supplied the examined fact.</param>
    /// <param name="authority">Stable declaration, profile, convention, or adapter authority.</param>
    /// <param name="capabilityEvidence">Target capability evidence retained by the assessment.</param>
    /// <param name="operatingBoundaries">Operating boundaries validated by the examined facts.</param>
    /// <param name="preservedGuarantees">Semantic guarantees preserved in this exact context.</param>
    /// <param name="unavailableReason">Typed failure reason for unavailable, invalid, or blocked assessments.</param>
    /// <param name="node">Affected logical node, or <see langword="null"/>.</param>
    /// <param name="input">Affected compiled input, or <see langword="null"/>.</param>
    /// <param name="field">Affected semantic field path, or <see langword="null"/>.</param>
    /// <param name="placementBinding">Affected source-placement binding, or <see langword="null"/>.</param>
    /// <param name="configurationSetting">Affected adapter-binding setting, or <see langword="null"/>.</param>
    /// <param name="message">Human-readable explanation of the adapter decision.</param>
    /// <param name="resolution">Actionable resolution for an unavailable, invalid, or blocked decision.</param>
    /// <param name="adapterDecisionCode">
    /// Stable adapter-owned decision code for an unavailable, invalid, or blocked assessment; otherwise
    /// <see langword="null"/>.
    /// </param>
    /// <param name="missingCapabilityEvidence">
    /// Capability-evidence identities whose absence contributed to an unavailable or invalid decision.
    /// </param>
    /// <param name="failedOperatingBoundary">
    /// Operating boundary that was missing or failed validation, or <see langword="null"/>.
    /// </param>
    /// <param name="failedConfigurationSetting">
    /// Expected configuration setting that was absent or invalid, or <see langword="null"/>. Unlike
    /// <paramref name="configurationSetting"/>, this value need not occur among effective binding decisions.
    /// </param>
    /// <param name="blockedBy">
    /// Contextual assessment containing the prior unavailable or invalid adapter decision that prevented this
    /// requirement from being examined, or <see langword="null"/> when <paramref name="status"/> is not blocked.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="authority"/> or <paramref name="message"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// An identity or supplied string is empty; a supplied path is empty; an evidence or boundary identity is
    /// repeated; or failure metadata conflicts with <paramref name="status"/>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="status"/>, <paramref name="origin"/>, <paramref name="unavailableReason"/>, or a guarantee
    /// value is unsupported.
    /// </exception>
    [JsonConstructor]
    public RelationQueryBoundRequirementAssessment(
        RelationQueryContextEvidenceId id,
        RelationQueryNativeResultBranchId branch,
        RelationQueryRealizationRequirementId requirement,
        RelationQueryBoundAssessmentStatus status,
        RelationQueryConfigurationValueOrigin origin,
        string authority,
        ImmutableArray<RelationQueryTargetCapabilityEvidenceId> capabilityEvidence = default,
        ImmutableArray<RelationQueryOperatingBoundaryId> operatingBoundaries = default,
        ImmutableArray<RelationQueryGuaranteeCapabilityKind> preservedGuarantees = default,
        RelationQueryUnavailableReason? unavailableReason = null,
        QueryNodeId? node = null,
        RelationQueryInputId? input = null,
        FieldPath? field = null,
        RelationQuerySourcePlacementBindingId? placementBinding = null,
        string? configurationSetting = null,
        string message = "Contextual evidence evaluated.",
        string? resolution = null,
        RelationQueryAdapterDecisionCode? adapterDecisionCode = null,
        ImmutableArray<RelationQueryTargetCapabilityEvidenceId> missingCapabilityEvidence = default,
        RelationQueryOperatingBoundaryId? failedOperatingBoundary = null,
        string? failedConfigurationSetting = null,
        RelationQueryContextEvidenceId? blockedBy = null)
    {
        if (string.IsNullOrWhiteSpace(id.Value))
            throw new ArgumentException("A contextual assessment requires an evidence identity.", nameof(id));
        if (string.IsNullOrWhiteSpace(branch.Value))
            throw new ArgumentException("A contextual assessment requires a branch identity.", nameof(branch));
        if (string.IsNullOrWhiteSpace(requirement.Value))
            throw new ArgumentException("A contextual assessment requires a requirement identity.", nameof(requirement));
        if (!Enum.IsDefined(status))
            throw new ArgumentOutOfRangeException(nameof(status), status, "Unsupported contextual assessment status.");
        if (!Enum.IsDefined(origin))
            throw new ArgumentOutOfRangeException(nameof(origin), origin, "Unsupported configuration-value origin.");
        if (unavailableReason is { } reason && !Enum.IsDefined(reason))
            throw new ArgumentOutOfRangeException(nameof(unavailableReason), reason, "Unsupported unavailable reason.");
        if (adapterDecisionCode is { } decisionCode && string.IsNullOrWhiteSpace(decisionCode.Value))
            throw new ArgumentException("An adapter decision code cannot be default.", nameof(adapterDecisionCode));
        if (failedOperatingBoundary is { } failedBoundary && string.IsNullOrWhiteSpace(failedBoundary.Value))
            throw new ArgumentException("A failed operating boundary cannot be default.", nameof(failedOperatingBoundary));
        if (failedConfigurationSetting is not null && string.IsNullOrWhiteSpace(failedConfigurationSetting))
            throw new ArgumentException("A failed configuration setting cannot be empty.", nameof(failedConfigurationSetting));
        if (blockedBy is { } blockingAssessment && string.IsNullOrWhiteSpace(blockingAssessment.Value))
            throw new ArgumentException("A blocking contextual assessment cannot be default.", nameof(blockedBy));
        if (blockedBy == id)
            throw new ArgumentException("A contextual assessment cannot block itself.", nameof(blockedBy));

        var normalizedCapabilityEvidence = RelationQueryRealizationOrdering.NormalizeIdentityValues(
            capabilityEvidence,
            static value => value.Value,
            nameof(capabilityEvidence));
        var normalizedOperatingBoundaries = RelationQueryRealizationOrdering.NormalizeIdentityValues(
            operatingBoundaries,
            static value => value.Value,
            nameof(operatingBoundaries));
        var normalizedPreservedGuarantees = RelationQueryRealizationOrdering.NormalizeGuarantees(
            preservedGuarantees,
            nameof(preservedGuarantees));
        var normalizedMissingCapabilityEvidence = RelationQueryRealizationOrdering.NormalizeIdentityValues(
            missingCapabilityEvidence,
            static value => value.Value,
            nameof(missingCapabilityEvidence));

        if (status == RelationQueryBoundAssessmentStatus.Available
            && (unavailableReason is not null
                || resolution is not null
                || adapterDecisionCode is not null
                || !normalizedMissingCapabilityEvidence.IsEmpty
                || failedOperatingBoundary is not null
                || failedConfigurationSetting is not null
                || blockedBy is not null))
        {
            throw new ArgumentException(
                "An available contextual assessment cannot carry failure metadata.",
                nameof(unavailableReason));
        }
        if (status is RelationQueryBoundAssessmentStatus.Unavailable or RelationQueryBoundAssessmentStatus.Invalid
            && (unavailableReason is null
                || unavailableReason == RelationQueryUnavailableReason.PrerequisiteBlocked
                || string.IsNullOrWhiteSpace(resolution)
                || adapterDecisionCode is null
                || blockedBy is not null))
        {
            throw new ArgumentException(
                "An unavailable or invalid contextual assessment requires its own typed failure reason, actionable resolution, and stable adapter decision code.",
                nameof(unavailableReason));
        }
        if (status == RelationQueryBoundAssessmentStatus.Blocked
            && (unavailableReason != RelationQueryUnavailableReason.PrerequisiteBlocked
                || string.IsNullOrWhiteSpace(resolution)
                || adapterDecisionCode is null
                || blockedBy is null))
        {
            throw new ArgumentException(
                "A blocked contextual assessment requires the prerequisite-blocked reason, an actionable resolution, a stable adapter decision code, and the prior adapter decision.",
                nameof(blockedBy));
        }
        if (status == RelationQueryBoundAssessmentStatus.Blocked
            && (!normalizedCapabilityEvidence.IsEmpty
                || !normalizedOperatingBoundaries.IsEmpty
                || !normalizedPreservedGuarantees.IsEmpty
                || !normalizedMissingCapabilityEvidence.IsEmpty
                || failedOperatingBoundary is not null
                || failedConfigurationSetting is not null))
        {
            throw new ArgumentException(
                "A blocked contextual assessment cannot claim examined capability evidence, validated boundaries, guarantees, or direct failure facts.",
                nameof(status));
        }
        if (node is { } nodeId && string.IsNullOrWhiteSpace(nodeId.Value))
            throw new ArgumentException("An assessment node cannot be default.", nameof(node));
        if (input is { } inputId && string.IsNullOrWhiteSpace(inputId.Value))
            throw new ArgumentException("An assessment input cannot be default.", nameof(input));
        if (field is { Segments.IsDefaultOrEmpty: true })
            throw new ArgumentException("An assessment field path cannot be empty.", nameof(field));
        if (placementBinding is { } placementId && string.IsNullOrWhiteSpace(placementId.Value))
            throw new ArgumentException("An assessment placement binding cannot be default.", nameof(placementBinding));
        if (configurationSetting is not null && string.IsNullOrWhiteSpace(configurationSetting))
            throw new ArgumentException("An assessment configuration setting cannot be empty.", nameof(configurationSetting));

        Id = id;
        Branch = branch;
        Requirement = requirement;
        Status = status;
        Origin = origin;
        Authority = Guard.RequireNotNullOrWhiteSpace(authority);
        CapabilityEvidence = normalizedCapabilityEvidence;
        OperatingBoundaries = normalizedOperatingBoundaries;
        PreservedGuarantees = normalizedPreservedGuarantees;
        UnavailableReason = unavailableReason;
        Node = node;
        Input = input;
        Field = field;
        PlacementBinding = placementBinding;
        ConfigurationSetting = configurationSetting;
        Message = Guard.RequireNotNullOrWhiteSpace(message);
        Resolution = resolution;
        AdapterDecisionCode = adapterDecisionCode;
        MissingCapabilityEvidence = normalizedMissingCapabilityEvidence;
        FailedOperatingBoundary = failedOperatingBoundary;
        FailedConfigurationSetting = failedConfigurationSetting;
        BlockedBy = blockedBy;
    }

    /// <summary>Stable adapter-owned evidence identity.</summary>
    public RelationQueryContextEvidenceId Id { get; }

    /// <summary>Selected result branch affected by the assessment.</summary>
    public RelationQueryNativeResultBranchId Branch { get; }

    /// <summary>Demand-scoped semantic requirement being assessed.</summary>
    public RelationQueryRealizationRequirementId Requirement { get; }

    /// <summary>Available, unavailable, invalid, or prerequisite-blocked contextual outcome.</summary>
    public RelationQueryBoundAssessmentStatus Status { get; }

    /// <summary>Configuration-precedence tier that supplied the examined fact.</summary>
    public RelationQueryConfigurationValueOrigin Origin { get; }

    /// <summary>Stable declaration, profile, convention, or adapter authority.</summary>
    public string Authority { get; }

    /// <summary>Target capability evidence retained by the assessment in stable identity order.</summary>
    public ImmutableArray<RelationQueryTargetCapabilityEvidenceId> CapabilityEvidence { get; }

    /// <summary>Operating boundaries validated by the examined facts in stable identity order.</summary>
    public ImmutableArray<RelationQueryOperatingBoundaryId> OperatingBoundaries { get; }

    /// <summary>Semantic guarantees preserved in this exact context.</summary>
    public ImmutableArray<RelationQueryGuaranteeCapabilityKind> PreservedGuarantees { get; }

    /// <summary>Typed failure reason for an unavailable, invalid, or blocked assessment.</summary>
    public RelationQueryUnavailableReason? UnavailableReason { get; }

    /// <summary>Affected logical node, or <see langword="null"/>.</summary>
    public QueryNodeId? Node { get; }

    /// <summary>Affected compiled input, or <see langword="null"/>.</summary>
    public RelationQueryInputId? Input { get; }

    /// <summary>Affected semantic field path, or <see langword="null"/>.</summary>
    public FieldPath? Field { get; }

    /// <summary>Affected source-placement binding, or <see langword="null"/>.</summary>
    public RelationQuerySourcePlacementBindingId? PlacementBinding { get; }

    /// <summary>Affected adapter-binding configuration setting, or <see langword="null"/>.</summary>
    public string? ConfigurationSetting { get; }

    /// <summary>Human-readable explanation of the adapter decision.</summary>
    public string Message { get; }

    /// <summary>Actionable resolution for an unavailable, invalid, or blocked decision.</summary>
    public string? Resolution { get; }

    /// <summary>
    /// Stable adapter-owned decision code for an unavailable, invalid, or blocked assessment; otherwise
    /// <see langword="null"/>.
    /// </summary>
    public RelationQueryAdapterDecisionCode? AdapterDecisionCode { get; }

    /// <summary>
    /// Capability-evidence identities whose absence contributed to an unavailable or invalid decision, in stable
    /// identity order.
    /// </summary>
    public ImmutableArray<RelationQueryTargetCapabilityEvidenceId> MissingCapabilityEvidence { get; }

    /// <summary>Operating boundary that was missing or failed validation, or <see langword="null"/>.</summary>
    public RelationQueryOperatingBoundaryId? FailedOperatingBoundary { get; }

    /// <summary>
    /// Expected configuration setting that was absent or invalid, or <see langword="null"/>. This may name a
    /// missing setting not present among effective binding decisions.
    /// </summary>
    public string? FailedConfigurationSetting { get; }

    /// <summary>
    /// Contextual assessment containing the prior unavailable or invalid adapter decision that blocked examination,
    /// or <see langword="null"/> for an examined assessment.
    /// </summary>
    public RelationQueryContextEvidenceId? BlockedBy { get; }

}

/// <summary>
/// Immutable adapter projection of one storage binding into target-independent contextual realization evidence.
/// </summary>
public sealed class RelationQueryContextualEvidenceProjection
{
    /// <summary>Creates a normalized contextual-evidence projection.</summary>
    /// <param name="binding">Exact target-neutral adapter-binding reference.</param>
    /// <param name="assessments">Adapter assessments in any input order.</param>
    /// <exception cref="ArgumentNullException"><paramref name="binding"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="assessments"/> contains a null entry or repeats an evidence identity; or a blocked assessment
    /// does not reference an unavailable or invalid assessment on the same branch.
    /// </exception>
    [JsonConstructor]
    public RelationQueryContextualEvidenceProjection(
        RelationQueryAdapterBindingReference binding,
        ImmutableArray<RelationQueryBoundRequirementAssessment> assessments)
    {
        Binding = Guard.RequireNotNull(binding);
        var normalized = assessments.IsDefault ? [] : assessments;
        if (normalized.Any(static assessment => assessment is null))
            throw new ArgumentException("Contextual assessments cannot contain null entries.", nameof(assessments));
        Dictionary<RelationQueryContextEvidenceId, RelationQueryBoundRequirementAssessment> assessmentsById =
            new(normalized.Length);
        foreach (var assessment in normalized)
        {
            if (!assessmentsById.TryAdd(assessment.Id, assessment))
                throw new ArgumentException("Contextual assessments cannot repeat an evidence identity.", nameof(assessments));
        }
        foreach (var assessment in normalized)
        {
            if (assessment.Status != RelationQueryBoundAssessmentStatus.Blocked)
                continue;

            if (assessment.BlockedBy is not { } blockingId
                || !assessmentsById.TryGetValue(blockingId, out var blockingAssessment)
                || blockingAssessment.Branch != assessment.Branch
                || blockingAssessment.Status is not (RelationQueryBoundAssessmentStatus.Unavailable
                    or RelationQueryBoundAssessmentStatus.Invalid))
            {
                throw new ArgumentException(
                    "A blocked contextual assessment must reference an unavailable or invalid adapter decision on the same branch.",
                    nameof(assessments));
            }
        }
        Assessments =
        [
            .. normalized
                .OrderBy(static assessment => assessment.Branch.Value, StringComparer.Ordinal)
                .ThenBy(static assessment => assessment.Requirement.Value, StringComparer.Ordinal)
                .ThenBy(static assessment => assessment.Id.Value, StringComparer.Ordinal)
        ];
    }

    /// <summary>Exact target-neutral adapter-binding reference.</summary>
    public RelationQueryAdapterBindingReference Binding { get; }

    /// <summary>Contextual assessments in deterministic branch, requirement, and evidence order.</summary>
    public ImmutableArray<RelationQueryBoundRequirementAssessment> Assessments { get; }
}

/// <summary>Deterministic identity of one exact bound-realization report.</summary>
public sealed record RelationQueryBoundRealizationFingerprint
{
    /// <summary>Creates a bound-realization fingerprint.</summary>
    /// <param name="algorithm">Hash algorithm identity.</param>
    /// <param name="canonicalization">Bound-realization canonicalization profile.</param>
    /// <param name="value">Canonical lowercase hexadecimal hash value.</param>
    /// <exception cref="ArgumentNullException">A parameter is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A parameter is empty or white space.</exception>
    [JsonConstructor]
    public RelationQueryBoundRealizationFingerprint(string algorithm, string canonicalization, string value)
    {
        Algorithm = Guard.RequireNotNullOrWhiteSpace(algorithm);
        Canonicalization = Guard.RequireNotNullOrWhiteSpace(canonicalization);
        Value = Guard.RequireNotNullOrWhiteSpace(value);
    }

    /// <summary>Hash algorithm identity.</summary>
    public string Algorithm { get; }

    /// <summary>Bound-realization canonicalization profile.</summary>
    public string Canonicalization { get; }

    /// <summary>Canonical lowercase hexadecimal hash value.</summary>
    public string Value { get; }
}

/// <summary>
/// Exact prediction of whether one profile-feasible plan can execute under a particular placement and adapter binding.
/// </summary>
/// <remarks>
/// This wrapper reuses the profile report's canonical requirements and decisions. Its assessments qualify those
/// family-level proofs with exact physical evidence instead of defining a second realization vocabulary.
/// </remarks>
public sealed class RelationQueryBoundRealizationReport
{
    /// <summary>Creates a normalized bound-realization report.</summary>
    /// <param name="profileFeasibility">Exact family-level realization report being qualified.</param>
    /// <param name="placement">Exact source-placement fingerprint examined by binding.</param>
    /// <param name="branches">Selected native result branches covered by the report.</param>
    /// <param name="evidence">Adapter-projected binding and contextual assessments.</param>
    /// <param name="diagnostics">Structured contextual realization diagnostics.</param>
    /// <param name="status">Overall exact bound-realization status.</param>
    /// <param name="fingerprint">Deterministic fingerprint to verify.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="profileFeasibility"/>, <paramref name="placement"/>, <paramref name="evidence"/>, or
    /// <paramref name="fingerprint"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="branches"/> is empty or repeats an identity; diagnostics contain null entries; status
    /// conflicts with the profile, assessments, or diagnostics; or <paramref name="fingerprint"/> is stale.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="status"/> is unsupported.</exception>
    [JsonConstructor]
    public RelationQueryBoundRealizationReport(
        RelationQueryRealizationReport profileFeasibility,
        RelationQuerySourcePlacementFingerprint placement,
        ImmutableArray<RelationQueryNativeResultBranchId> branches,
        RelationQueryContextualEvidenceProjection evidence,
        ImmutableArray<RelationQueryRealizationDiagnostic> diagnostics,
        RelationQueryRealizationStatus status,
        RelationQueryBoundRealizationFingerprint fingerprint)
    {
        ProfileFeasibility = Guard.RequireNotNull(profileFeasibility);
        Placement = Guard.RequireNotNull(placement);
        Evidence = Guard.RequireNotNull(evidence);
        Branches = RelationQueryRealizationOrdering.NormalizeIdentityValues(
            branches,
            static branch => branch.Value,
            nameof(branches),
            requireNonEmpty: true);
        Diagnostics = RelationQueryBoundRealizationCompiler.NormalizeDiagnostics(diagnostics);
        if (!Enum.IsDefined(status))
            throw new ArgumentOutOfRangeException(nameof(status), status, "Unsupported bound-realization status.");
        ValidateStatus(status, ProfileFeasibility, Evidence.Assessments, Diagnostics);
        var suppliedFingerprint = Guard.RequireNotNull(fingerprint);
        var expected = RelationQueryBoundRealizationFingerprinter.Compute(
            ProfileFeasibility,
            Placement,
            Branches,
            Evidence,
            Diagnostics,
            status);
        if (!Equals(suppliedFingerprint, expected))
            throw new ArgumentException("The bound-realization fingerprint does not match normalized content.", nameof(fingerprint));

        Status = status;
        Fingerprint = suppliedFingerprint;
    }

    /// <summary>Exact family-level realization report being qualified.</summary>
    public RelationQueryRealizationReport ProfileFeasibility { get; }

    /// <summary>Exact source-placement fingerprint examined by binding.</summary>
    public RelationQuerySourcePlacementFingerprint Placement { get; }

    /// <summary>Selected native result branches in stable identity order.</summary>
    public ImmutableArray<RelationQueryNativeResultBranchId> Branches { get; }

    /// <summary>Adapter-projected binding and contextual assessments.</summary>
    public RelationQueryContextualEvidenceProjection Evidence { get; }

    /// <summary>Structured contextual realization diagnostics in deterministic order.</summary>
    public ImmutableArray<RelationQueryRealizationDiagnostic> Diagnostics { get; }

    /// <summary>Overall exact bound-realization status.</summary>
    public RelationQueryRealizationStatus Status { get; }

    /// <summary>Deterministic fingerprint of the exact bound-realization report.</summary>
    public RelationQueryBoundRealizationFingerprint Fingerprint { get; }

    /// <summary>Whether every selected branch requirement is exactly realizable in the bound context.</summary>
    [JsonIgnore]
    public bool IsRealizable => Status == RelationQueryRealizationStatus.Realizable;

    static void ValidateStatus(
        RelationQueryRealizationStatus status,
        RelationQueryRealizationReport profile,
        ImmutableArray<RelationQueryBoundRequirementAssessment> assessments,
        ImmutableArray<RelationQueryRealizationDiagnostic> diagnostics)
    {
        var hasInvalid = assessments.Any(static item => item.Status == RelationQueryBoundAssessmentStatus.Invalid)
                         || diagnostics.Any(static item => item.Severity == DiagnosticSeverity.Error
                                                           && item.Code is RelationQueryRealizationDiagnosticCodes.ContextInvalid
                                                               or RelationQueryRealizationDiagnosticCodes.ContextEvidenceIncomplete
                                                               or RelationQueryRealizationDiagnosticCodes.ContextAffinityMismatch);
        var hasUnavailable = assessments.Any(static item => item.Status == RelationQueryBoundAssessmentStatus.Unavailable);
        var hasBlocked = assessments.Any(static item => item.Status == RelationQueryBoundAssessmentStatus.Blocked);
        if (status == RelationQueryRealizationStatus.Realizable
            && (!profile.IsRealizable || hasInvalid || hasUnavailable || hasBlocked
                || diagnostics.Any(static item => item.Severity == DiagnosticSeverity.Error)))
        {
            throw new ArgumentException("A realizable bound report requires feasible profile and available evidence.", nameof(status));
        }
        if (status == RelationQueryRealizationStatus.NotRealizable
            && profile.IsRealizable
            && !hasUnavailable)
        {
            throw new ArgumentException("A non-realizable bound report requires unavailable evidence or profile.", nameof(status));
        }
        if (status == RelationQueryRealizationStatus.Invalid && !hasInvalid)
            throw new ArgumentException("An invalid bound report requires invalid contextual evidence.", nameof(status));
    }
}
