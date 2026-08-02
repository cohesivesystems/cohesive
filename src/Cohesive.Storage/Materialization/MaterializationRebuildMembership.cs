using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Cohesive.Execution;
using Cohesive.Model.Serialization;

namespace Cohesive.Storage.Materialization;

/// <summary>Whether membership evidence proves the complete selected subject set at its authority cut.</summary>
[JsonConverter(typeof(StrictStringEnumJsonConverterFactory))]
public enum MaterializationRebuildMembershipCompleteness
{
    /// <summary>The observation is explicitly incomplete and cannot authorize a rebuild plan set.</summary>
    Incomplete = 0,

    /// <summary>Omission from the observed set authoritatively means the subject was not selected at the exact cut.</summary>
    Complete = 1
}

/// <summary>Authority, revision, cut, and completeness proof for one finite membership observation.</summary>
public sealed record MaterializationRebuildMembershipAuthority
{
    /// <summary>Creates one portable membership-authority observation.</summary>
    /// <param name="authority">Stable identity of the membership authority.</param>
    /// <param name="revision">Exact authority revision under which the observation was produced.</param>
    /// <param name="cut">Exact logical read or transaction cut shared by the complete observation.</param>
    /// <param name="completeness">Whether omission from the observed set is authoritative.</param>
    /// <param name="evidenceReferences">Attributable references proving revision, cut, and completeness.</param>
    /// <exception cref="ArgumentNullException">A required string is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A required identity is absent or ill-formed Unicode, or an evidence reference is absent or duplicated.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="completeness"/> is unsupported.</exception>
    [JsonConstructor]
    public MaterializationRebuildMembershipAuthority(
        string authority,
        string revision,
        string cut,
        MaterializationRebuildMembershipCompleteness completeness,
        ImmutableArray<string> evidenceReferences)
    {
        Authority = MaterializationContract.RequireUnicodeIdentity(authority, nameof(authority));
        Revision = MaterializationContract.RequireUnicodeIdentity(revision, nameof(revision));
        Cut = MaterializationContract.RequireUnicodeIdentity(cut, nameof(cut));
        if (!Enum.IsDefined(completeness))
            throw new ArgumentOutOfRangeException(nameof(completeness), completeness, "Unsupported membership completeness.");
        Completeness = completeness;
        EvidenceReferences = MaterializationCapabilityOrdering.NormalizeStrings(
            evidenceReferences.IsDefault ? [] : evidenceReferences,
            nameof(evidenceReferences),
            requireNonEmpty: true);
    }

    /// <summary>Stable identity of the membership authority.</summary>
    public string Authority { get; }

    /// <summary>Exact authority revision under which the observation was produced.</summary>
    public string Revision { get; }

    /// <summary>Exact logical read or transaction cut shared by the complete observation.</summary>
    public string Cut { get; }

    /// <summary>Whether omission from the observed set is authoritative.</summary>
    public MaterializationRebuildMembershipCompleteness Completeness { get; }

    /// <summary>Attributable proof references in canonical ordinal order.</summary>
    public ImmutableArray<string> EvidenceReferences { get; }

    /// <summary>Compares authority evidence structurally, including canonical proof references.</summary>
    /// <param name="other">Authority evidence to compare.</param>
    /// <returns><see langword="true"/> when every authority and proof field is equal.</returns>
    public bool Equals(MaterializationRebuildMembershipAuthority? other) =>
        ReferenceEquals(this, other)
        || other is not null
        && string.Equals(Authority, other.Authority, StringComparison.Ordinal)
        && string.Equals(Revision, other.Revision, StringComparison.Ordinal)
        && string.Equals(Cut, other.Cut, StringComparison.Ordinal)
        && Completeness == other.Completeness
        && EvidenceReferences.SequenceEqual(other.EvidenceReferences);

    /// <summary>Returns a structural hash code for all authority and proof fields.</summary>
    /// <returns>A hash code consistent with structural equality.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Authority, StringComparer.Ordinal);
        hash.Add(Revision, StringComparer.Ordinal);
        hash.Add(Cut, StringComparer.Ordinal);
        hash.Add(Completeness);
        foreach (var reference in EvidenceReferences)
            hash.Add(reference, StringComparer.Ordinal);
        return hash.ToHashCode();
    }
}

/// <summary>Finite, frozen placement membership at one authoritative selector evaluation cut.</summary>
public sealed class MaterializationRebuildMembershipEvidence : IEquatable<MaterializationRebuildMembershipEvidence>
{
    /// <summary>Current portable membership-evidence schema.</summary>
    public const string CurrentSchemaVersion = "cohesive-materialization-rebuild-membership/v1";

    /// <summary>Creates and verifies canonical frozen membership evidence.</summary>
    /// <param name="schemaVersion">Exact portable membership schema.</param>
    /// <param name="materialization">Exact materialization definition selected for rebuild.</param>
    /// <param name="selector">Fingerprint of the exact explicit or Relations selector.</param>
    /// <param name="members">Exact finite selected members.</param>
    /// <param name="authority">Authoritative revision, cut, completeness, and proof references.</param>
    /// <param name="provenance">Producer attribution for selector evaluation and membership freezing.</param>
    /// <param name="fingerprint">Persisted membership fingerprint to verify, or <see langword="null"/> to compute it.</param>
    /// <exception cref="ArgumentNullException">A required artifact is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The schema, member set, or fingerprint is invalid.</exception>
    /// <exception cref="System.Text.Json.JsonException">Canonical membership content cannot be serialized.</exception>
    /// <exception cref="NotSupportedException">Canonical membership content contains an unsupported value.</exception>
    /// <exception cref="InvalidOperationException">Canonical membership content has no portable representation.</exception>
    [JsonConstructor]
    public MaterializationRebuildMembershipEvidence(
        string schemaVersion,
        MaterializationDefinitionReference materialization,
        MaterializationPlacementSelectionFingerprint selector,
        ImmutableArray<MaterializationPlacementSubjectId> members,
        MaterializationRebuildMembershipAuthority authority,
        ExecutionProvenance provenance,
        MaterializationRebuildMembershipFingerprint? fingerprint = null)
    {
        SchemaVersion = Guard.RequireNotNullOrWhiteSpace(schemaVersion);
        if (!string.Equals(schemaVersion, CurrentSchemaVersion, StringComparison.Ordinal))
            throw new ArgumentException($"Rebuild-membership schema '{schemaVersion}' is unsupported.", nameof(schemaVersion));
        Materialization = materialization ?? throw new ArgumentNullException(nameof(materialization));
        Selector = selector ?? throw new ArgumentNullException(nameof(selector));
        Members = MaterializationRebuildPlanningContract.NormalizeSubjects(
            members.IsDefault ? [] : members,
            nameof(members),
            allowEmpty: true);
        Authority = authority ?? throw new ArgumentNullException(nameof(authority));
        Provenance = provenance ?? throw new ArgumentNullException(nameof(provenance));

        var computed = MaterializationRebuildPlanningFingerprinters.ComputeMembership(this);
        if (fingerprint is not null && fingerprint != computed)
            throw new ArgumentException("The rebuild-membership fingerprint does not match canonical content.", nameof(fingerprint));
        Fingerprint = computed;
    }

    /// <summary>Exact portable membership-evidence schema.</summary>
    public string SchemaVersion { get; }

    /// <summary>Exact materialization definition selected for rebuild.</summary>
    public MaterializationDefinitionReference Materialization { get; }

    /// <summary>Fingerprint of the exact explicit or Relations selector.</summary>
    public MaterializationPlacementSelectionFingerprint Selector { get; }

    /// <summary>Exact finite selected members in canonical ordinal identity order.</summary>
    public ImmutableArray<MaterializationPlacementSubjectId> Members { get; }

    /// <summary>Authoritative revision, cut, completeness, and proof references.</summary>
    public MaterializationRebuildMembershipAuthority Authority { get; }

    /// <summary>Producer attribution for selector evaluation and membership freezing.</summary>
    public ExecutionProvenance Provenance { get; }

    /// <summary>Deterministic fingerprint of the complete frozen membership and evidence.</summary>
    public MaterializationRebuildMembershipFingerprint Fingerprint { get; }

    /// <summary>Compares membership evidence by its constructor-verified canonical fingerprint.</summary>
    /// <param name="other">Membership evidence to compare.</param>
    /// <returns><see langword="true"/> when both artifacts contain identical frozen evidence.</returns>
    public bool Equals(MaterializationRebuildMembershipEvidence? other) =>
        ReferenceEquals(this, other) || other is not null && Fingerprint == other.Fingerprint;

    /// <summary>Compares an object with this evidence by canonical fingerprint.</summary>
    /// <param name="obj">Object to compare.</param>
    /// <returns><see langword="true"/> when <paramref name="obj"/> is canonically equal membership evidence.</returns>
    public override bool Equals(object? obj) =>
        obj is MaterializationRebuildMembershipEvidence other && Equals(other);

    /// <summary>Returns a hash code derived from the canonical membership fingerprint.</summary>
    /// <returns>A stable hash for canonically equal membership evidence.</returns>
    public override int GetHashCode() => Fingerprint.GetHashCode();
}
