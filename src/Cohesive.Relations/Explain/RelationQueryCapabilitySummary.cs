using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Cohesive.Relations.Realization;

namespace Cohesive.Relations.Explain;

/// <summary>
/// Machine-readable index entry for one exact canonical relation/query capability.
/// </summary>
/// <remarks>
/// The entry contains only stable references into the target profile and optional realization reports from which it
/// was projected. Detailed declarations, decisions, and contextual assessments remain authoritative in those source
/// artifacts.
/// </remarks>
public sealed record RelationQueryCapabilitySummaryEntry
{
    /// <summary>Creates a normalized capability-summary entry.</summary>
    /// <param name="capability">Exact canonical capability indexed by the entry.</param>
    /// <param name="requirements">Requirements that directly demand <paramref name="capability"/>.</param>
    /// <param name="missingForRequirements">
    /// Requirements whose unavailable decisions identify <paramref name="capability"/> as missing.
    /// </param>
    /// <param name="capabilityEvidence">
    /// Resolvable target-profile evidence declarations associated with the capability or its realization proof.
    /// </param>
    /// <param name="operatingBoundaries">
    /// Resolvable target-profile operating boundaries associated with the capability or its realization proof.
    /// </param>
    /// <param name="contextEvidence">
    /// Resolvable bound-realization contextual assessments for requirements that directly demand the capability.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="capability"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// An identity collection contains a default or repeated value.
    /// </exception>
    [JsonConstructor]
    public RelationQueryCapabilitySummaryEntry(
        RelationQueryCapability capability,
        ImmutableArray<RelationQueryRealizationRequirementId> requirements = default,
        ImmutableArray<RelationQueryRealizationRequirementId> missingForRequirements = default,
        ImmutableArray<RelationQueryTargetCapabilityEvidenceId> capabilityEvidence = default,
        ImmutableArray<RelationQueryOperatingBoundaryId> operatingBoundaries = default,
        ImmutableArray<RelationQueryContextEvidenceId> contextEvidence = default)
    {
        Capability = Guard.RequireNotNull(capability);
        Requirements = Normalize(requirements, static value => value.Value, nameof(requirements));
        MissingForRequirements = Normalize(
            missingForRequirements,
            static value => value.Value,
            nameof(missingForRequirements));
        CapabilityEvidence = Normalize(
            capabilityEvidence,
            static value => value.Value,
            nameof(capabilityEvidence));
        OperatingBoundaries = Normalize(
            operatingBoundaries,
            static value => value.Value,
            nameof(operatingBoundaries));
        ContextEvidence = Normalize(contextEvidence, static value => value.Value, nameof(contextEvidence));
    }

    /// <summary>Exact canonical capability indexed by the entry.</summary>
    public RelationQueryCapability Capability { get; }

    /// <summary>Requirements that directly demand the capability, in stable identity order.</summary>
    public ImmutableArray<RelationQueryRealizationRequirementId> Requirements { get; }

    /// <summary>Requirements whose unavailable decisions identify the capability as missing.</summary>
    public ImmutableArray<RelationQueryRealizationRequirementId> MissingForRequirements { get; }

    /// <summary>Resolvable target-profile capability-evidence identities in stable order.</summary>
    public ImmutableArray<RelationQueryTargetCapabilityEvidenceId> CapabilityEvidence { get; }

    /// <summary>Resolvable target-profile operating-boundary identities in stable order.</summary>
    public ImmutableArray<RelationQueryOperatingBoundaryId> OperatingBoundaries { get; }

    /// <summary>Resolvable contextual-assessment identities in stable order.</summary>
    public ImmutableArray<RelationQueryContextEvidenceId> ContextEvidence { get; }

    /// <summary>Compares the normalized capability and source-artifact references by value.</summary>
    /// <param name="other">Capability-summary entry to compare.</param>
    /// <returns>
    /// <see langword="true"/> when both entries index the same capability and exact source references; otherwise
    /// <see langword="false"/>.
    /// </returns>
    public bool HasSameSemantics(RelationQueryCapabilitySummaryEntry? other) =>
        other is not null
        && Equals(Capability, other.Capability)
        && Requirements.SequenceEqual(other.Requirements)
        && MissingForRequirements.SequenceEqual(other.MissingForRequirements)
        && CapabilityEvidence.SequenceEqual(other.CapabilityEvidence)
        && OperatingBoundaries.SequenceEqual(other.OperatingBoundaries)
        && ContextEvidence.SequenceEqual(other.ContextEvidence);

    static ImmutableArray<T> Normalize<T>(
        ImmutableArray<T> values,
        Func<T, string> key,
        string parameterName)
        where T : struct
    {
        var normalized = values.IsDefault ? [] : values;
        if (normalized.Any(value => string.IsNullOrWhiteSpace(key(value))))
            throw new ArgumentException("Identity collections cannot contain default values.", parameterName);
        if (normalized.GroupBy(key, StringComparer.Ordinal).Any(static group => group.Count() > 1))
            throw new ArgumentException("Identity collections cannot contain repeated values.", parameterName);
        return [.. normalized.OrderBy(key, StringComparer.Ordinal)];
    }
}

/// <summary>
/// Deterministic machine-readable index of target, profile, and contextual evidence by canonical capability.
/// </summary>
/// <remarks>
/// This summary is a derived projection. The target capability profile and optional realization reports remain the
/// semantic authorities for declarations, decisions, diagnostics, and contextual outcomes.
/// </remarks>
public sealed record RelationQueryCapabilitySummary
{
    /// <summary>Creates a normalized capability summary.</summary>
    /// <param name="target">Interpretation target whose capabilities are summarized.</param>
    /// <param name="targetProfile">Exact target capability-profile identity.</param>
    /// <param name="policy">Realization policy identity, or <see langword="null"/> for a profile-only summary.</param>
    /// <param name="profileFeasibility">
    /// Profile-feasibility fingerprint, or <see langword="null"/> for a profile-only summary.
    /// </param>
    /// <param name="boundRealization">
    /// Bound-realization fingerprint, or <see langword="null"/> when no contextual report was projected.
    /// </param>
    /// <param name="operatingBoundaries">Every resolvable boundary declared by the target profile.</param>
    /// <param name="entries">Capability index entries in any input order.</param>
    /// <exception cref="ArgumentException">
    /// A target or profile identity is default; optional report attribution is incomplete; an operating-boundary
    /// identity is default or repeated; entries contain a null or repeated capability; or an entry references an
    /// operating boundary absent from <paramref name="operatingBoundaries"/>.
    /// </exception>
    [JsonConstructor]
    public RelationQueryCapabilitySummary(
        RelationQueryTargetId target,
        RelationQueryTargetProfileId targetProfile,
        RelationQueryRealizationPolicyId? policy = null,
        RelationQueryRealizationFingerprint? profileFeasibility = null,
        RelationQueryBoundRealizationFingerprint? boundRealization = null,
        ImmutableArray<RelationQueryOperatingBoundaryId> operatingBoundaries = default,
        ImmutableArray<RelationQueryCapabilitySummaryEntry> entries = default)
    {
        if (string.IsNullOrWhiteSpace(target.Value))
            throw new ArgumentException("A capability summary requires a target identity.", nameof(target));
        if (string.IsNullOrWhiteSpace(targetProfile.Value))
            throw new ArgumentException("A capability summary requires a target-profile identity.", nameof(targetProfile));
        if (policy is { } policyId && string.IsNullOrWhiteSpace(policyId.Value))
            throw new ArgumentException("A supplied policy identity cannot be default.", nameof(policy));
        if ((policy is null) != (profileFeasibility is null))
        {
            throw new ArgumentException(
                "Policy and profile-feasibility attribution must be supplied together.",
                nameof(profileFeasibility));
        }
        if (boundRealization is not null && profileFeasibility is null)
        {
            throw new ArgumentException(
                "Bound-realization attribution requires profile-feasibility attribution.",
                nameof(boundRealization));
        }

        var normalizedBoundaries = operatingBoundaries.IsDefault ? [] : operatingBoundaries;
        if (normalizedBoundaries.Any(static boundary => string.IsNullOrWhiteSpace(boundary.Value)))
            throw new ArgumentException("Operating-boundary identities cannot be default.", nameof(operatingBoundaries));
        if (normalizedBoundaries.Distinct().Count() != normalizedBoundaries.Length)
            throw new ArgumentException("Operating-boundary identities cannot be repeated.", nameof(operatingBoundaries));
        OperatingBoundaries =
        [
            .. normalizedBoundaries.OrderBy(static boundary => boundary.Value, StringComparer.Ordinal)
        ];

        var normalizedEntries = entries.IsDefault ? [] : entries;
        if (normalizedEntries.Any(static entry => entry is null))
            throw new ArgumentException("Capability-summary entries cannot contain null values.", nameof(entries));
        if (normalizedEntries.GroupBy(static entry => entry.Capability).Any(static group => group.Count() > 1))
            throw new ArgumentException("Capability-summary entries cannot repeat a capability.", nameof(entries));
        var boundarySet = OperatingBoundaries.ToHashSet();
        if (normalizedEntries.Any(entry => entry.OperatingBoundaries.Any(boundary => !boundarySet.Contains(boundary))))
        {
            throw new ArgumentException(
                "Capability-summary entries can reference only declared operating boundaries.",
                nameof(entries));
        }

        Target = target;
        TargetProfile = targetProfile;
        Policy = policy;
        ProfileFeasibility = profileFeasibility;
        BoundRealization = boundRealization;
        Entries =
        [
            .. normalizedEntries.OrderBy(
                static entry => RelationQueryRealizationOrdering.CapabilityKey(entry.Capability),
                StringComparer.Ordinal)
        ];
    }

    /// <summary>Interpretation target whose capabilities are summarized.</summary>
    public RelationQueryTargetId Target { get; }

    /// <summary>Exact target capability-profile identity.</summary>
    public RelationQueryTargetProfileId TargetProfile { get; }

    /// <summary>Realization policy identity, or <see langword="null"/> for a profile-only summary.</summary>
    public RelationQueryRealizationPolicyId? Policy { get; }

    /// <summary>Profile-feasibility fingerprint, or <see langword="null"/> for a profile-only summary.</summary>
    public RelationQueryRealizationFingerprint? ProfileFeasibility { get; }

    /// <summary>Bound-realization fingerprint, or <see langword="null"/> without contextual evidence.</summary>
    public RelationQueryBoundRealizationFingerprint? BoundRealization { get; }

    /// <summary>Every resolvable target-profile boundary identity in stable order.</summary>
    public ImmutableArray<RelationQueryOperatingBoundaryId> OperatingBoundaries { get; }

    /// <summary>Capability index entries in canonical capability order.</summary>
    public ImmutableArray<RelationQueryCapabilitySummaryEntry> Entries { get; }

    /// <summary>Compares normalized summary identity and index content by value.</summary>
    /// <param name="other">Capability summary to compare.</param>
    /// <returns>
    /// <see langword="true"/> when both summaries identify the same source artifacts and capability references;
    /// otherwise <see langword="false"/>.
    /// </returns>
    public bool HasSameSemantics(RelationQueryCapabilitySummary? other) =>
        other is not null
        && Target == other.Target
        && TargetProfile == other.TargetProfile
        && Policy == other.Policy
        && Equals(ProfileFeasibility, other.ProfileFeasibility)
        && Equals(BoundRealization, other.BoundRealization)
        && OperatingBoundaries.SequenceEqual(other.OperatingBoundaries)
        && Entries.Length == other.Entries.Length
        && Entries.Zip(other.Entries).All(static pair => pair.First.HasSameSemantics(pair.Second));
}
