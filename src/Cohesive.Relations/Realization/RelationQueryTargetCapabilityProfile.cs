using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Serialization;

namespace Cohesive.Relations.Realization;

/// <summary>Typed operating constraint under which a target capability can preserve semantics.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RelationQueryOperatingBoundaryKind
{
    /// <summary>Limits the number of input rows participating in one realization.</summary>
    MaximumInputRows = 0,

    /// <summary>Limits the number of output rows produced by one realization.</summary>
    MaximumOutputRows = 1,

    /// <summary>Limits relationship or join fan-out for one input row.</summary>
    MaximumFanOut = 2,

    /// <summary>Limits the number of rows requested by one page.</summary>
    MaximumPageSize = 3,

    /// <summary>Limits structural field-path depth.</summary>
    MaximumFieldPathDepth = 4,

    /// <summary>Limits portable expression-tree depth.</summary>
    MaximumExpressionDepth = 5,

    /// <summary>Limits the number of keys or values in one batch.</summary>
    MaximumBatchSize = 6,

    /// <summary>Requires every participating source to use one physical source.</summary>
    SingleSource = 7,

    /// <summary>Requires every participating value to reside in one physical partition.</summary>
    SinglePartition = 8,

    /// <summary>Requires all logical inputs to be materialized before realization.</summary>
    MaterializedInputs = 9,

    /// <summary>Requires authoritative complete input evidence.</summary>
    CompleteInputEvidence = 10,

    /// <summary>Requires every participating operand to be non-null and non-missing.</summary>
    NonNullOperands = 11,

    /// <summary>Requires every participating operand to be scalar.</summary>
    ScalarOperands = 12,

    /// <summary>Requires all temporal operands to use one exact domain.</summary>
    HomogeneousTemporalDomain = 13,

    /// <summary>Requires finite temporal interval endpoints.</summary>
    FiniteTemporalBounds = 14,

    /// <summary>Requires a unique deterministic ordering key.</summary>
    StableUniqueOrdering = 15,

    /// <summary>Requires the provider to return deterministic results for equivalent inputs.</summary>
    DeterministicProvider = 16,

    /// <summary>Requires explicit evidence that numeric aggregate intermediates and results preserve canonical semantics.</summary>
    ExactNumericAggregateDomain = 17,

    /// <summary>Requires explicit evidence that physical temporal precision and range preserve canonical values.</summary>
    ExactTemporalDomain = 18,

    /// <summary>Limits rooted relation execution to one explicitly supplied root occurrence per invocation.</summary>
    SuppliedRelationRoot = 19
}

/// <summary>One explicit, inspectable target operating boundary.</summary>
/// <remarks>
/// The declaration preserves unknown kinds and invalid kind/limit combinations so realization matching can return
/// structured diagnostics. Construction validates object shape; the compiler validates semantic admissibility.
/// </remarks>
public sealed record RelationQueryOperatingBoundary
{
    /// <summary>Creates an operating boundary.</summary>
    /// <remarks>
    /// Unknown kinds and invalid kind/limit combinations are retained so the realization compiler can diagnose
    /// imported or future declarations without losing their attribution.
    /// </remarks>
    /// <param name="id">Stable boundary identity.</param>
    /// <param name="kind">Typed boundary constraint.</param>
    /// <param name="limit">Declared numeric limit, or <see langword="null"/> when no limit was supplied.</param>
    /// <param name="description">Optional descriptive metadata that does not affect semantic matching.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="id"/> is default or <paramref name="description"/> is empty.
    /// </exception>
    [JsonConstructor]
    public RelationQueryOperatingBoundary(
        RelationQueryOperatingBoundaryId id,
        RelationQueryOperatingBoundaryKind kind,
        long? limit = null,
        string? description = null)
    {
        if (string.IsNullOrWhiteSpace(id.Value))
            throw new ArgumentException("An operating boundary requires a stable identity.", nameof(id));
        if (description is not null && string.IsNullOrWhiteSpace(description))
            throw new ArgumentException("An operating-boundary description cannot be empty.", nameof(description));

        Id = id;
        Kind = kind;
        Limit = limit;
        Description = description;
    }

    /// <summary>Stable boundary identity.</summary>
    public RelationQueryOperatingBoundaryId Id { get; }

    /// <summary>Typed boundary constraint.</summary>
    [JsonConverter(typeof(DiagnosticPreservingStringEnumJsonConverter<RelationQueryOperatingBoundaryKind>))]
    public RelationQueryOperatingBoundaryKind Kind { get; }

    /// <summary>
    /// Declared numeric limit, or <see langword="null"/> when none was supplied. Portable JSON encodes the
    /// value as a canonical decimal string.
    /// </summary>
    [JsonConverter(typeof(StringEncodedInt64JsonConverter))]
    public long? Limit { get; }

    /// <summary>Optional descriptive metadata that does not affect semantic matching.</summary>
    public string? Description { get; }

    internal static bool KindRequiresLimit(RelationQueryOperatingBoundaryKind kind) => kind is
        RelationQueryOperatingBoundaryKind.MaximumInputRows
        or RelationQueryOperatingBoundaryKind.MaximumOutputRows
        or RelationQueryOperatingBoundaryKind.MaximumFanOut
        or RelationQueryOperatingBoundaryKind.MaximumPageSize
        or RelationQueryOperatingBoundaryKind.MaximumFieldPathDepth
        or RelationQueryOperatingBoundaryKind.MaximumExpressionDepth
        or RelationQueryOperatingBoundaryKind.MaximumBatchSize;
}

/// <summary>One attributable assertion that a target provides a capability.</summary>
/// <remarks>
/// Boundary references are retained exactly, including repetitions, so incomplete imported evidence can be
/// diagnosed by the realization compiler instead of failing before a report can be produced.
/// </remarks>
public sealed record RelationQueryTargetCapabilityEvidence
{
    /// <summary>Creates target capability evidence.</summary>
    /// <remarks>
    /// Repeated or semantically invalid boundary references are retained for attributable compiler diagnostics.
    /// </remarks>
    /// <param name="id">Stable evidence identity.</param>
    /// <param name="capability">Capability asserted by the evidence.</param>
    /// <param name="operatingBoundaries">Boundaries under which the capability assertion is valid.</param>
    /// <param name="description">Optional descriptive metadata that does not affect semantic matching.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="id"/> is default, <paramref name="operatingBoundaries"/> contains a default identity, or
    /// <paramref name="description"/> is empty.
    /// </exception>
    /// <exception cref="ArgumentNullException"><paramref name="capability"/> is <see langword="null"/>.</exception>
    [JsonConstructor]
    public RelationQueryTargetCapabilityEvidence(
        RelationQueryTargetCapabilityEvidenceId id,
        RelationQueryCapability capability,
        ImmutableArray<RelationQueryOperatingBoundaryId> operatingBoundaries = default,
        string? description = null
        )
    {
        if (string.IsNullOrWhiteSpace(id.Value))
            throw new ArgumentException("Target capability evidence requires a stable identity.", nameof(id));
        Id = id;
        Capability = Guard.RequireNotNull(capability);
        var normalizedBoundaries = operatingBoundaries.IsDefault ? [] : operatingBoundaries;
        if (normalizedBoundaries.Any(static boundary => string.IsNullOrWhiteSpace(boundary.Value)))
        {
            throw new ArgumentException(
                "Operating-boundary identities cannot be empty.",
                nameof(operatingBoundaries));
        }
        OperatingBoundaries =
        [
            .. normalizedBoundaries.OrderBy(static boundary => boundary.Value, StringComparer.Ordinal)
        ];
        if (description is not null && string.IsNullOrWhiteSpace(description))
            throw new ArgumentException("A capability-evidence description cannot be empty.", nameof(description));
        Description = description;
    }

    /// <summary>Stable evidence identity.</summary>
    public RelationQueryTargetCapabilityEvidenceId Id { get; }

    /// <summary>Capability asserted by the evidence.</summary>
    public RelationQueryCapability Capability { get; }

    /// <summary>Boundaries under which the capability assertion is valid.</summary>
    public ImmutableArray<RelationQueryOperatingBoundaryId> OperatingBoundaries { get; }

    /// <summary>Optional descriptive metadata that does not affect semantic matching.</summary>
    public string? Description { get; }
}

/// <summary>Portable, versioned capabilities, constraints, and guarantees of one interpretation target.</summary>
/// <remarks>
/// Profile construction normalizes declaration order but retains conflicting identities and semantically invalid
/// declarations. Matching validates them globally and produces a fail-closed invalid report with structured,
/// attributable diagnostics.
/// </remarks>
public sealed class RelationQueryTargetCapabilityProfile
{
    /// <summary>Creates a target capability profile.</summary>
    /// <remarks>
    /// Conflicting identities and other semantically malformed declarations are retained and validated by the
    /// realization compiler; this constructor enforces only the portable object's structural contract.
    /// </remarks>
    /// <param name="target">Stable interpretation-target identity.</param>
    /// <param name="id">Stable, versioned profile identity.</param>
    /// <param name="supportedDefinitionSchemaVersions">Canonical relation/query schema versions understood by the target.</param>
    /// <param name="supportedCompilerProfiles">Static compiler profiles understood by the target.</param>
    /// <param name="capabilities">Attributable primitive, semantic, and guarantee capability evidence.</param>
    /// <param name="operatingBoundaries">Declared operating boundaries referenced by capability evidence.</param>
    /// <param name="description">Optional descriptive metadata that does not affect semantic matching or identity.</param>
    /// <exception cref="ArgumentException">
    /// An identity is default; a supported-version collection is empty or contains invalid values; a collection
    /// contains null entries; or <paramref name="description"/> is empty.
    /// </exception>
    [JsonConstructor]
    public RelationQueryTargetCapabilityProfile(
        RelationQueryTargetId target,
        RelationQueryTargetProfileId id,
        ImmutableArray<string> supportedDefinitionSchemaVersions,
        ImmutableArray<string> supportedCompilerProfiles,
        ImmutableArray<RelationQueryTargetCapabilityEvidence> capabilities = default,
        ImmutableArray<RelationQueryOperatingBoundary> operatingBoundaries = default,
        string? description = null
        )
    {
        if (string.IsNullOrWhiteSpace(target.Value))
            throw new ArgumentException("A target capability profile requires a target identity.", nameof(target));
        if (string.IsNullOrWhiteSpace(id.Value))
            throw new ArgumentException("A target capability profile requires a profile identity.", nameof(id));

        Target = target;
        Id = id;
        SupportedDefinitionSchemaVersions = RelationQueryRealizationOrdering.NormalizeStrings(
            supportedDefinitionSchemaVersions,
            nameof(supportedDefinitionSchemaVersions),
            requireNonEmpty: true);
        SupportedCompilerProfiles = RelationQueryRealizationOrdering.NormalizeStrings(
            supportedCompilerProfiles,
            nameof(supportedCompilerProfiles),
            requireNonEmpty: true);
        OperatingBoundaries = NormalizeBoundaries(operatingBoundaries);
        Capabilities = NormalizeCapabilityEvidence(capabilities);

        if (description is not null && string.IsNullOrWhiteSpace(description))
            throw new ArgumentException("A target-profile description cannot be empty.", nameof(description));
        Description = description;
    }

    /// <summary>Stable interpretation-target identity.</summary>
    public RelationQueryTargetId Target { get; }

    /// <summary>Stable, versioned profile identity.</summary>
    public RelationQueryTargetProfileId Id { get; }

    /// <summary>Canonical relation/query schema versions understood by the target.</summary>
    public ImmutableArray<string> SupportedDefinitionSchemaVersions { get; }

    /// <summary>Static compiler profiles understood by the target.</summary>
    public ImmutableArray<string> SupportedCompilerProfiles { get; }

    /// <summary>Attributable primitive, semantic, and guarantee capability evidence.</summary>
    public ImmutableArray<RelationQueryTargetCapabilityEvidence> Capabilities { get; }

    /// <summary>Declared operating boundaries referenced by capability evidence.</summary>
    public ImmutableArray<RelationQueryOperatingBoundary> OperatingBoundaries { get; }

    /// <summary>Optional descriptive metadata that does not affect semantic matching or identity.</summary>
    public string? Description { get; }

    /// <summary>Determines whether another profile carries the same normalized capability snapshot.</summary>
    /// <param name="other">Profile snapshot to compare.</param>
    /// <returns>
    /// <see langword="true"/> when target identity, supported versions, operating boundaries, and capability
    /// evidence are equivalent; otherwise <see langword="false"/>. Non-semantic descriptions are ignored.
    /// </returns>
    public bool HasSameSemantics(RelationQueryTargetCapabilityProfile? other) =>
        other is not null
        && Target == other.Target
        && Id == other.Id
        && SupportedDefinitionSchemaVersions.SequenceEqual(
            other.SupportedDefinitionSchemaVersions,
            StringComparer.Ordinal)
        && SupportedCompilerProfiles.SequenceEqual(
            other.SupportedCompilerProfiles,
            StringComparer.Ordinal)
        && OperatingBoundaries.Length == other.OperatingBoundaries.Length
        && OperatingBoundaries.Zip(other.OperatingBoundaries).All(static pair =>
            pair.First.Id == pair.Second.Id
            && pair.First.Kind == pair.Second.Kind
            && pair.First.Limit == pair.Second.Limit)
        && Capabilities.Length == other.Capabilities.Length
        && Capabilities.Zip(other.Capabilities).All(static pair =>
            pair.First.Id == pair.Second.Id
            && Equals(pair.First.Capability, pair.Second.Capability)
            && pair.First.OperatingBoundaries.SequenceEqual(pair.Second.OperatingBoundaries));

    static ImmutableArray<RelationQueryOperatingBoundary> NormalizeBoundaries(
        ImmutableArray<RelationQueryOperatingBoundary> boundaries)
    {
        var normalized = boundaries.IsDefault ? [] : boundaries;
        if (normalized.Any(static boundary => boundary is null))
            throw new ArgumentException("Operating boundaries cannot contain null entries.", nameof(boundaries));
        return
        [
            .. normalized
                .OrderBy(static boundary => boundary.Id.Value, StringComparer.Ordinal)
                .ThenBy(static boundary => (int)boundary.Kind)
                .ThenBy(static boundary => boundary.Limit)
                .ThenBy(static boundary => boundary.Description ?? string.Empty, StringComparer.Ordinal)
        ];
    }

    static ImmutableArray<RelationQueryTargetCapabilityEvidence> NormalizeCapabilityEvidence(
        ImmutableArray<RelationQueryTargetCapabilityEvidence> capabilities)
    {
        var normalized = capabilities.IsDefault ? [] : capabilities;
        if (normalized.Any(static evidence => evidence is null))
            throw new ArgumentException("Target capability evidence cannot contain null entries.", nameof(capabilities));
        return
        [
            .. normalized
                .OrderBy(static evidence => evidence.Id.Value, StringComparer.Ordinal)
                .ThenBy(static evidence => RelationQueryRealizationOrdering.CapabilityKey(evidence.Capability), StringComparer.Ordinal)
                .ThenBy(
                    static evidence => RelationQueryRealizationOrdering.SequenceKey(
                        evidence.OperatingBoundaries.Select(static boundary => boundary.Value)),
                    StringComparer.Ordinal)
                .ThenBy(static evidence => evidence.Description ?? string.Empty, StringComparer.Ordinal)
        ];
    }
}

internal sealed record RelationQueryTargetCapabilityProfileIssue(
    string Code,
    string Message,
    RelationQueryTargetCapabilityEvidenceId? CapabilityEvidence = null,
    RelationQueryOperatingBoundaryId? OperatingBoundary = null);

internal sealed class RelationQueryTargetCapabilityProfileAnalysis
{
    RelationQueryTargetCapabilityProfileAnalysis(
        ImmutableDictionary<RelationQueryOperatingBoundaryId, RelationQueryOperatingBoundary> boundaries,
        ImmutableDictionary<RelationQueryTargetCapabilityEvidenceId, RelationQueryTargetCapabilityEvidence> evidence,
        ImmutableArray<RelationQueryTargetCapabilityProfileIssue> issues)
    {
        Boundaries = boundaries;
        Evidence = evidence;
        Issues = issues;
    }

    public ImmutableDictionary<RelationQueryOperatingBoundaryId, RelationQueryOperatingBoundary> Boundaries { get; }

    public ImmutableDictionary<RelationQueryTargetCapabilityEvidenceId, RelationQueryTargetCapabilityEvidence> Evidence { get; }

    public ImmutableArray<RelationQueryTargetCapabilityProfileIssue> Issues { get; }

    public static RelationQueryTargetCapabilityProfileAnalysis Analyze(
        RelationQueryTargetCapabilityProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        ImmutableArray<RelationQueryTargetCapabilityProfileIssue>.Builder issues =
            ImmutableArray.CreateBuilder<RelationQueryTargetCapabilityProfileIssue>();
        var validBoundaries = ImmutableDictionary.CreateBuilder<
            RelationQueryOperatingBoundaryId,
            RelationQueryOperatingBoundary>();
        foreach (var group in profile.OperatingBoundaries.GroupBy(static boundary => boundary.Id))
        {
            var declarations = group.ToImmutableArray();
            if (declarations.Length != 1)
            {
                issues.Add(new(
                    RelationQueryRealizationDiagnosticCodes.OperatingBoundaryInvalid,
                    $"Operating boundary identity '{group.Key.Value}' has conflicting declarations.",
                    OperatingBoundary: group.Key));
                continue;
            }

            var boundary = declarations[0];
            var problem = BoundaryProblem(boundary);
            if (problem is null)
            {
                validBoundaries.Add(boundary.Id, boundary);
                continue;
            }

            issues.Add(new(
                RelationQueryRealizationDiagnosticCodes.OperatingBoundaryInvalid,
                problem,
                OperatingBoundary: boundary.Id));
        }

        var declaredBoundaryIds = profile.OperatingBoundaries
            .Select(static boundary => boundary.Id)
            .ToHashSet();
        var validEvidence = ImmutableDictionary.CreateBuilder<
            RelationQueryTargetCapabilityEvidenceId,
            RelationQueryTargetCapabilityEvidence>();
        foreach (var group in profile.Capabilities.GroupBy(static evidence => evidence.Id))
        {
            var declarations = group.ToImmutableArray();
            if (declarations.Length != 1)
            {
                issues.Add(new(
                    RelationQueryRealizationDiagnosticCodes.CapabilityEvidenceConflict,
                    $"Capability evidence identity '{group.Key.Value}' has conflicting declarations.",
                    group.Key));
                continue;
            }

            var evidence = declarations[0];
            var evidenceIsValid = true;
            if (GetCapabilityProblem(evidence.Capability) is { } capabilityProblem)
            {
                issues.Add(new(
                    RelationQueryRealizationDiagnosticCodes.CapabilityEvidenceInvalid,
                    $"Capability evidence '{evidence.Id.Value}' is invalid: {capabilityProblem}",
                    evidence.Id));
                evidenceIsValid = false;
            }

            foreach (var duplicate in evidence.OperatingBoundaries
                         .GroupBy(static boundary => boundary)
                         .Where(static group => group.Count() > 1))
            {
                issues.Add(new(
                    RelationQueryRealizationDiagnosticCodes.CapabilityEvidenceInvalid,
                    $"Capability evidence '{evidence.Id.Value}' repeats operating boundary '{duplicate.Key.Value}'.",
                    evidence.Id,
                    duplicate.Key));
                evidenceIsValid = false;
            }

            foreach (var boundaryId in evidence.OperatingBoundaries.Distinct())
            {
                var problem = !declaredBoundaryIds.Contains(boundaryId)
                    ? $"references undeclared operating boundary '{boundaryId.Value}'"
                    : !validBoundaries.ContainsKey(boundaryId)
                        ? $"references invalid operating boundary '{boundaryId.Value}'"
                        : null;
                if (problem is null)
                    continue;

                issues.Add(new(
                    RelationQueryRealizationDiagnosticCodes.CapabilityEvidenceInvalid,
                    $"Capability evidence '{evidence.Id.Value}' {problem}.",
                    evidence.Id,
                    boundaryId));
                evidenceIsValid = false;
            }

            if (evidence.Capability is OperatingBoundaryValidationRelationQueryCapability validation)
            {
                if (!declaredBoundaryIds.Contains(validation.Boundary))
                {
                    issues.Add(new(
                        RelationQueryRealizationDiagnosticCodes.CapabilityEvidenceInvalid,
                        $"Boundary-enforcement evidence '{evidence.Id.Value}' references undeclared operating boundary '{validation.Boundary.Value}'.",
                        evidence.Id,
                        validation.Boundary));
                    evidenceIsValid = false;
                }
                else if (!validBoundaries.ContainsKey(validation.Boundary))
                {
                    issues.Add(new(
                        RelationQueryRealizationDiagnosticCodes.CapabilityEvidenceInvalid,
                        $"Boundary-enforcement evidence '{evidence.Id.Value}' references invalid operating boundary '{validation.Boundary.Value}'.",
                        evidence.Id,
                        validation.Boundary));
                    evidenceIsValid = false;
                }

                if (!evidence.OperatingBoundaries.IsDefaultOrEmpty)
                {
                    issues.Add(new(
                        RelationQueryRealizationDiagnosticCodes.CapabilityEvidenceInvalid,
                        $"Boundary-enforcement evidence '{evidence.Id.Value}' must be unconditional.",
                        evidence.Id,
                        validation.Boundary));
                    evidenceIsValid = false;
                }
            }

            if (evidenceIsValid)
                validEvidence.Add(evidence.Id, evidence);
        }

        return new(
            validBoundaries.ToImmutable(),
            validEvidence.ToImmutable(),
            [
                .. issues
                    .OrderBy(static issue => issue.CapabilityEvidence?.Value ?? string.Empty, StringComparer.Ordinal)
                    .ThenBy(static issue => issue.OperatingBoundary?.Value ?? string.Empty, StringComparer.Ordinal)
                    .ThenBy(static issue => issue.Code, StringComparer.Ordinal)
                    .ThenBy(static issue => issue.Message, StringComparer.Ordinal)
            ]);
    }

    static string? BoundaryProblem(RelationQueryOperatingBoundary boundary)
    {
        if (!Enum.IsDefined(boundary.Kind))
        {
            return $"Operating boundary '{boundary.Id.Value}' uses unsupported kind "
                   + $"'{((int)boundary.Kind).ToString(CultureInfo.InvariantCulture)}'.";
        }

        if (RelationQueryOperatingBoundary.KindRequiresLimit(boundary.Kind))
        {
            if (boundary.Limit is not > 0)
                return $"Maximum operating boundary '{boundary.Id.Value}' requires a positive limit.";
        }
        else if (boundary.Limit is not null)
        {
            return $"Non-maximum operating boundary '{boundary.Id.Value}' cannot declare a numeric limit.";
        }

        return null;
    }

    internal static string? GetCapabilityProblem(RelationQueryCapability capability) => capability switch
    {
        LogicalRelationQueryCapability logical when !Enum.IsDefined(logical.Kind) =>
            $"unsupported logical kind '{((int)logical.Kind).ToString(CultureInfo.InvariantCulture)}'",
        ExpressionRelationQueryCapability expression when !Enum.IsDefined(expression.RequirementKind) =>
            $"unsupported expression requirement kind '{((int)expression.RequirementKind).ToString(CultureInfo.InvariantCulture)}'",
        TemporalRelationQueryCapability temporal when !Enum.IsDefined(temporal.Capability) =>
            $"unsupported temporal kind '{((int)temporal.Capability).ToString(CultureInfo.InvariantCulture)}'",
        StructuralRelationQueryCapability structural when !Enum.IsDefined(structural.Role) =>
            $"unsupported structural role '{((int)structural.Role).ToString(CultureInfo.InvariantCulture)}'",
        StructuralRelationQueryCapability structural when !Enum.IsDefined(structural.PathKind) =>
            $"unsupported structural path kind '{((int)structural.PathKind).ToString(CultureInfo.InvariantCulture)}'",
        GuaranteeRelationQueryCapability guarantee when !Enum.IsDefined(guarantee.Kind) =>
            $"unsupported guarantee kind '{((int)guarantee.Kind).ToString(CultureInfo.InvariantCulture)}'",
        PrimitiveRelationQueryCapability primitive when !Enum.IsDefined(primitive.Kind) =>
            $"unsupported primitive kind '{((int)primitive.Kind).ToString(CultureInfo.InvariantCulture)}'",
        LogicalRelationQueryCapability
            or ExpressionRelationQueryCapability
            or TemporalRelationQueryCapability
            or StructuralRelationQueryCapability
            or GuaranteeRelationQueryCapability
            or OperatingBoundaryValidationRelationQueryCapability
            or PrimitiveRelationQueryCapability => null,
        _ => $"unsupported capability variant '{capability.GetType().Name}'"
    };
}

/// <summary>Versioned proof rule that composes primitive target facilities into an exact semantic capability.</summary>
public sealed record RelationQueryCompositionRule
{
    /// <summary>Creates an exact composition rule.</summary>
    /// <param name="id">Stable, versioned rule identity.</param>
    /// <param name="providedCapability">Higher-level capability proved by the rule.</param>
    /// <param name="requiredCapabilities">Capabilities that must all be supported to apply the rule.</param>
    /// <param name="requiredOperatingBoundaries">Operating boundaries required by the proof.</param>
    /// <param name="preservedGuarantees">Guarantees explicitly preserved by the composition.</param>
    /// <param name="description">Optional descriptive metadata that does not affect semantic matching.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="id"/> is default; a collection contains null or default values;
    /// <paramref name="requiredOperatingBoundaries"/> contains a duplicate identity;
    /// <paramref name="requiredCapabilities"/> is empty; or <paramref name="description"/> is empty.
    /// </exception>
    /// <exception cref="ArgumentNullException"><paramref name="providedCapability"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="preservedGuarantees"/> contains an unsupported guarantee.
    /// </exception>
    [JsonConstructor]
    public RelationQueryCompositionRule(
        RelationQueryCompositionRuleId id,
        RelationQueryCapability providedCapability,
        ImmutableArray<RelationQueryCapability> requiredCapabilities,
        ImmutableArray<RelationQueryOperatingBoundaryId> requiredOperatingBoundaries = default,
        ImmutableArray<RelationQueryGuaranteeCapabilityKind> preservedGuarantees = default,
        string? description = null)
    {
        if (string.IsNullOrWhiteSpace(id.Value))
            throw new ArgumentException("A composition rule requires a stable identity.", nameof(id));
        Id = id;
        ProvidedCapability = Guard.RequireNotNull(providedCapability);
        RequiredCapabilities = RelationQueryRealizationOrdering.NormalizeCapabilities(
            requiredCapabilities,
            nameof(requiredCapabilities),
            requireNonEmpty: true);
        RequiredOperatingBoundaries = RelationQueryRealizationOrdering.NormalizeBoundaryIds(
            requiredOperatingBoundaries,
            nameof(requiredOperatingBoundaries));
        PreservedGuarantees = RelationQueryRealizationOrdering.NormalizeGuarantees(
            preservedGuarantees,
            nameof(preservedGuarantees));
        if (description is not null && string.IsNullOrWhiteSpace(description))
            throw new ArgumentException("A composition-rule description cannot be empty.", nameof(description));
        Description = description;
    }

    /// <summary>Stable, versioned rule identity.</summary>
    public RelationQueryCompositionRuleId Id { get; }

    /// <summary>Higher-level capability proved by the rule.</summary>
    public RelationQueryCapability ProvidedCapability { get; }

    /// <summary>Capabilities that must all be supported to apply the rule.</summary>
    public ImmutableArray<RelationQueryCapability> RequiredCapabilities { get; }

    /// <summary>Operating boundaries required by the proof.</summary>
    public ImmutableArray<RelationQueryOperatingBoundaryId> RequiredOperatingBoundaries { get; }

    /// <summary>Guarantees explicitly preserved by the composition.</summary>
    public ImmutableArray<RelationQueryGuaranteeCapabilityKind> PreservedGuarantees { get; }

    /// <summary>Optional descriptive metadata that does not affect semantic matching.</summary>
    public string? Description { get; }
}

internal static class RelationQueryRealizationOrdering
{
    public static ImmutableArray<string> NormalizeStrings(
        ImmutableArray<string> values,
        string parameterName,
        bool requireNonEmpty = false)
    {
        var normalized = values.IsDefault ? [] : values;
        if (normalized.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Values cannot contain null, empty, or white-space entries.", parameterName);
        var result = normalized.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToImmutableArray();
        if (requireNonEmpty && result.IsDefaultOrEmpty)
            throw new ArgumentException("At least one value is required.", parameterName);
        return result;
    }

    public static ImmutableArray<RelationQueryCapability> NormalizeCapabilities(
        ImmutableArray<RelationQueryCapability> capabilities,
        string parameterName,
        bool requireNonEmpty = false)
    {
        var normalized = capabilities.IsDefault ? [] : capabilities;
        if (normalized.Any(static capability => capability is null))
            throw new ArgumentException("Capabilities cannot contain null entries.", parameterName);
        var result = normalized
            .Distinct()
            .OrderBy(CapabilityKey, StringComparer.Ordinal)
            .ToImmutableArray();
        if (requireNonEmpty && result.IsDefaultOrEmpty)
            throw new ArgumentException("At least one capability is required.", parameterName);
        return result;
    }

    public static ImmutableArray<RelationQueryOperatingBoundaryId> NormalizeBoundaryIds(
        ImmutableArray<RelationQueryOperatingBoundaryId> boundaries,
        string parameterName)
    {
        var normalized = boundaries.IsDefault ? [] : boundaries;
        if (normalized.Any(static boundary => string.IsNullOrWhiteSpace(boundary.Value)))
            throw new ArgumentException("Operating-boundary identities cannot be empty.", parameterName);
        if (normalized.Distinct().Count() != normalized.Length)
            throw new ArgumentException("Operating-boundary identities cannot be duplicated.", parameterName);
        return [.. normalized.OrderBy(static boundary => boundary.Value, StringComparer.Ordinal)];
    }

    public static ImmutableArray<RelationQueryGuaranteeCapabilityKind> NormalizeGuarantees(
        ImmutableArray<RelationQueryGuaranteeCapabilityKind> guarantees,
        string parameterName)
    {
        var normalized = guarantees.IsDefault ? [] : guarantees;
        if (normalized.Any(static guarantee => !Enum.IsDefined(guarantee)))
            throw new ArgumentOutOfRangeException(parameterName, "Guarantees contain an unsupported value.");
        return [.. normalized.Distinct().OrderBy(static guarantee => (int)guarantee)];
    }

    public static string CapabilityKey(RelationQueryCapability capability) => capability switch
    {
        LogicalRelationQueryCapability logical => $"0/{EnumKey((int)logical.Kind)}",
        ExpressionRelationQueryCapability expression =>
            $"1/{EnumKey((int)expression.RequirementKind)}/{expression.Capability.Value}",
        TemporalRelationQueryCapability temporal => $"2/{EnumKey((int)temporal.Capability)}",
        StructuralRelationQueryCapability structural =>
            $"3/{EnumKey((int)structural.Role)}/{EnumKey((int)structural.PathKind)}",
        GuaranteeRelationQueryCapability guarantee => $"4/{EnumKey((int)guarantee.Kind)}",
        OperatingBoundaryValidationRelationQueryCapability boundary => $"5/{boundary.Boundary.Value}",
        PrimitiveRelationQueryCapability primitive => $"6/{EnumKey((int)primitive.Kind)}",
        _ => throw new ArgumentOutOfRangeException(nameof(capability), capability, "Unsupported relation/query capability variant.")
    };

    public static string SequenceKey(IEnumerable<string?> values)
    {
        StringBuilder key = new();
        foreach (var value in values)
        {
            if (value is null)
            {
                key.Append("-1:");
                continue;
            }

            key.Append(value.Length.ToString(CultureInfo.InvariantCulture));
            key.Append(':');
            key.Append(value);
        }
        return key.ToString();
    }

    static string EnumKey(int value) => value.ToString("D4", CultureInfo.InvariantCulture);
}
