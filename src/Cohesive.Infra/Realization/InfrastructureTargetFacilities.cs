using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Infra.Realization;

/// <summary>Deterministic fingerprint of one complete target-facility manifest.</summary>
public sealed record InfrastructureTargetFacilityManifestFingerprint
{
    /// <summary>Digest algorithm used by the current manifest fingerprint.</summary>
    public const string CurrentAlgorithm = "sha256";

    /// <summary>Canonicalization profile used by the current manifest fingerprint.</summary>
    public const string CurrentCanonicalization = "cohesive-infra-target-facilities/v1-c14n/v1";

    /// <summary>Creates target-facility manifest fingerprint metadata.</summary>
    /// <param name="algorithm">Stable digest algorithm identity.</param>
    /// <param name="canonicalization">Stable canonicalization-profile identity.</param>
    /// <param name="value">Lowercase hexadecimal digest.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Any argument is empty or white-space.</exception>
    [JsonConstructor]
    public InfrastructureTargetFacilityManifestFingerprint(string algorithm, string canonicalization, string value)
    {
        Algorithm = Guard.RequireNotNullOrWhiteSpace(algorithm);
        Canonicalization = Guard.RequireNotNullOrWhiteSpace(canonicalization);
        Value = Guard.RequireNotNullOrWhiteSpace(value);
    }

    /// <summary>Stable digest algorithm identity.</summary>
    public string Algorithm { get; }

    /// <summary>Stable canonicalization-profile identity.</summary>
    public string Canonicalization { get; }

    /// <summary>Lowercase hexadecimal digest.</summary>
    public string Value { get; }
}

/// <summary>Exact payload-free reference to one target-facility manifest.</summary>
public sealed record InfrastructureTargetFacilityManifestReference
{
    /// <summary>Creates an exact target-facility manifest reference.</summary>
    /// <param name="schemaVersion">Exact persisted manifest schema version.</param>
    /// <param name="id">Stable versioned manifest identity.</param>
    /// <param name="profile">Exact target capability profile referenced by the manifest.</param>
    /// <param name="target">Exact interpretation target described by the manifest.</param>
    /// <param name="variant">Coherent profile variant described by the manifest.</param>
    /// <param name="fingerprint">Exact canonical manifest fingerprint.</param>
    /// <exception cref="ArgumentNullException">A reference argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An identity or schema version is empty or default.</exception>
    [JsonConstructor]
    public InfrastructureTargetFacilityManifestReference(
        string schemaVersion,
        InfrastructureTargetFacilityManifestId id,
        InfrastructureCapabilityProfileReference profile,
        InfrastructureTargetId target,
        InfrastructureCapabilityVariantId variant,
        InfrastructureTargetFacilityManifestFingerprint fingerprint)
    {
        SchemaVersion = Guard.RequireNotNullOrWhiteSpace(schemaVersion);
        if (string.IsNullOrWhiteSpace(id.Value))
            throw new ArgumentException("A target-facility manifest reference requires an identity.", nameof(id));
        if (string.IsNullOrWhiteSpace(target.Value))
            throw new ArgumentException("A target-facility manifest reference requires a target.", nameof(target));
        if (string.IsNullOrWhiteSpace(variant.Value))
            throw new ArgumentException("A target-facility manifest reference requires a coherent variant.", nameof(variant));

        Id = id;
        Profile = Guard.RequireNotNull(profile);
        Target = target;
        Variant = variant;
        Fingerprint = Guard.RequireNotNull(fingerprint);
    }

    /// <summary>Exact persisted manifest schema version.</summary>
    public string SchemaVersion { get; }

    /// <summary>Stable versioned manifest identity.</summary>
    public InfrastructureTargetFacilityManifestId Id { get; }

    /// <summary>Exact target capability profile referenced by the manifest.</summary>
    public InfrastructureCapabilityProfileReference Profile { get; }

    /// <summary>Exact interpretation target described by the manifest.</summary>
    public InfrastructureTargetId Target { get; }

    /// <summary>Coherent profile variant described by the manifest.</summary>
    public InfrastructureCapabilityVariantId Variant { get; }

    /// <summary>Exact canonical manifest fingerprint.</summary>
    public InfrastructureTargetFacilityManifestFingerprint Fingerprint { get; }
}

/// <summary>
/// One selectable target facility, expressed by the exact capability evidence it can attach to a logical node.
/// </summary>
public sealed record InfrastructureTargetFacility
{
    /// <summary>Creates a selectable target facility.</summary>
    /// <param name="id">Stable target-local facility identity.</param>
    /// <param name="nodeKind">Logical node family this facility can realize.</param>
    /// <param name="evidence">Exact capability-evidence identities supplied by this facility.</param>
    /// <exception cref="ArgumentException">The identity is default, or evidence is empty, default, or duplicated.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="nodeKind"/> is unsupported.</exception>
    [JsonConstructor]
    public InfrastructureTargetFacility(
        InfrastructureTargetFacilityId id,
        InfrastructureNodeKind nodeKind,
        ImmutableArray<InfrastructureCapabilityEvidenceId> evidence)
    {
        if (string.IsNullOrWhiteSpace(id.Value))
            throw new ArgumentException("An infrastructure target facility requires a stable identity.", nameof(id));
        if (!Enum.IsDefined(nodeKind))
            throw new ArgumentOutOfRangeException(nameof(nodeKind), nodeKind, "Unsupported infrastructure node kind.");
        if (evidence.IsDefaultOrEmpty)
            throw new ArgumentException("An infrastructure target facility requires capability evidence.", nameof(evidence));

        Id = id;
        NodeKind = nodeKind;
        Evidence = InfrastructureCapabilityCollections.IdentitySet(
            evidence,
            static identity => identity.Value,
            nameof(evidence),
            requireNonEmpty: true);
    }

    /// <summary>Stable target-local facility identity.</summary>
    public InfrastructureTargetFacilityId Id { get; }

    /// <summary>Logical node family this facility can realize.</summary>
    public InfrastructureNodeKind NodeKind { get; }

    /// <summary>Exact leaf capability-evidence identities in stable order.</summary>
    public ImmutableArray<InfrastructureCapabilityEvidenceId> Evidence { get; }

    /// <summary>Compares target facilities structurally.</summary>
    /// <param name="other">Other target facility.</param>
    /// <returns><see langword="true"/> when identity, node kind, and evidence are equal.</returns>
    public bool Equals(InfrastructureTargetFacility? other) =>
        ReferenceEquals(this, other)
        || other is not null
        && Id == other.Id
        && NodeKind == other.NodeKind
        && Evidence.SequenceEqual(other.Evidence);

    /// <summary>Returns a structural hash code for this facility.</summary>
    /// <returns>A hash code derived from every field.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Id);
        hash.Add(NodeKind);
        foreach (var item in Evidence)
            hash.Add(item);
        return hash.ToHashCode();
    }
}

/// <summary>
/// Portable target manifest that groups existing capability evidence into selectable workload and resource facilities.
/// </summary>
/// <remarks>
/// The capability profile remains the authority for target guarantees and composition. A facility only attributes
/// leaf evidence to one physical construction family so generic planning can select it without a provider-specific
/// switch over application nodes.
/// </remarks>
public sealed record InfrastructureTargetFacilityManifest
{
    /// <summary>Current persisted target-facility manifest schema version.</summary>
    public const string CurrentSchemaVersion = "cohesive.infra.target-facilities/1";

    /// <summary>Creates or restores an exactly fingerprinted target-facility manifest.</summary>
    /// <param name="schemaVersion">Exact persisted manifest schema version.</param>
    /// <param name="id">Stable versioned manifest identity.</param>
    /// <param name="profile">Complete target capability profile.</param>
    /// <param name="variant">Coherent profile variant whose evidence is grouped into facilities.</param>
    /// <param name="facilities">Selectable target facilities.</param>
    /// <param name="fingerprint">Persisted exact fingerprint, or <see langword="null"/> to compute it.</param>
    /// <exception cref="ArgumentNullException">A reference argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// The schema, identity, variant, facility collection, evidence ownership, or fingerprint is invalid.
    /// </exception>
    [JsonConstructor]
    public InfrastructureTargetFacilityManifest(
        string schemaVersion,
        InfrastructureTargetFacilityManifestId id,
        InfrastructureCapabilityProfile profile,
        InfrastructureCapabilityVariantId variant,
        ImmutableArray<InfrastructureTargetFacility> facilities,
        InfrastructureTargetFacilityManifestFingerprint? fingerprint = null)
    {
        SchemaVersion = Guard.RequireNotNullOrWhiteSpace(schemaVersion);
        if (!string.Equals(SchemaVersion, CurrentSchemaVersion, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Target-facility manifest schema '{SchemaVersion}' is unsupported; expected '{CurrentSchemaVersion}'.",
                nameof(schemaVersion));
        }
        if (string.IsNullOrWhiteSpace(id.Value))
            throw new ArgumentException("A target-facility manifest requires a stable identity.", nameof(id));
        if (string.IsNullOrWhiteSpace(variant.Value))
            throw new ArgumentException("A target-facility manifest requires a coherent variant.", nameof(variant));

        Id = id;
        Profile = Guard.RequireNotNull(profile);
        Variant = variant;
        Facilities = NormalizeFacilities(facilities);
        ValidateEvidenceOwnership();

        var computed = InfrastructureTargetFacilityManifestFingerprinting.Compute(
            SchemaVersion,
            Id,
            Profile.ToReference(),
            Variant,
            Facilities);
        if (fingerprint is not null && fingerprint != computed)
        {
            throw new ArgumentException(
                "The supplied target-facility manifest fingerprint is not canonical.",
                nameof(fingerprint));
        }
        Fingerprint = computed;
    }

    /// <summary>Exact persisted manifest schema version.</summary>
    public string SchemaVersion { get; }

    /// <summary>Stable versioned manifest identity.</summary>
    public InfrastructureTargetFacilityManifestId Id { get; }

    /// <summary>Complete target capability profile.</summary>
    public InfrastructureCapabilityProfile Profile { get; }

    /// <summary>Coherent profile variant described by this manifest.</summary>
    public InfrastructureCapabilityVariantId Variant { get; }

    /// <summary>Selectable target facilities in stable identity order.</summary>
    public ImmutableArray<InfrastructureTargetFacility> Facilities { get; }

    /// <summary>Deterministic fingerprint of the exact profile fence and facility grouping.</summary>
    public InfrastructureTargetFacilityManifestFingerprint Fingerprint { get; }

    /// <summary>Creates an exact payload-free reference to this manifest.</summary>
    /// <returns>The schema, identity, profile, variant, and fingerprint fence.</returns>
    public InfrastructureTargetFacilityManifestReference ToReference() =>
        new(SchemaVersion, Id, Profile.ToReference(), Profile.Target, Variant, Fingerprint);

    /// <summary>Compares target-facility manifests structurally.</summary>
    /// <param name="other">Other target-facility manifest.</param>
    /// <returns><see langword="true"/> when every semantic field is equal.</returns>
    public bool Equals(InfrastructureTargetFacilityManifest? other) =>
        ReferenceEquals(this, other)
        || other is not null
        && string.Equals(SchemaVersion, other.SchemaVersion, StringComparison.Ordinal)
        && Id == other.Id
        && Profile == other.Profile
        && Variant == other.Variant
        && Facilities.SequenceEqual(other.Facilities)
        && Fingerprint == other.Fingerprint;

    /// <summary>Returns a structural hash code for this manifest.</summary>
    /// <returns>A hash code derived from every semantic field.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(SchemaVersion, StringComparer.Ordinal);
        hash.Add(Id);
        hash.Add(Profile);
        hash.Add(Variant);
        foreach (var facility in Facilities)
            hash.Add(facility);
        hash.Add(Fingerprint);
        return hash.ToHashCode();
    }

    static ImmutableArray<InfrastructureTargetFacility> NormalizeFacilities(
        ImmutableArray<InfrastructureTargetFacility> facilities)
    {
        if (facilities.IsDefaultOrEmpty)
            throw new ArgumentException("A target-facility manifest requires at least one facility.", nameof(facilities));
        if (facilities.Any(static item => item is null))
            throw new ArgumentException("Target facilities cannot contain null.", nameof(facilities));

        var ordered = facilities.Sort(static (left, right) =>
            StringComparer.Ordinal.Compare(left.Id.Value, right.Id.Value));
        for (var index = 1; index < ordered.Length; index++)
        {
            if (ordered[index - 1].Id == ordered[index].Id)
            {
                throw new ArgumentException(
                    $"Target facility '{ordered[index].Id.Value}' is duplicated.",
                    nameof(facilities));
            }
        }
        return ordered;
    }

    void ValidateEvidenceOwnership()
    {
        var selected = Profile.FindVariant(Variant)
            ?? throw new ArgumentException(
                $"Target-facility manifest variant '{Variant.Value}' is unavailable in profile '{Profile.Id.Value}'.",
                nameof(Variant));
        var available = selected.Evidence.ToDictionary(static item => item.Id);
        var owners = new Dictionary<InfrastructureCapabilityEvidenceId, InfrastructureTargetFacilityId>();
        foreach (var facility in Facilities)
        {
            foreach (var evidence in facility.Evidence)
            {
                if (!available.TryGetValue(evidence, out var profiled)
                    || profiled.Realization == CapabilityRealizationKind.Composed)
                {
                    throw new ArgumentException(
                        $"Target facility '{facility.Id.Value}' cites evidence '{evidence.Value}' that is absent or is not leaf evidence in the selected capability variant.",
                        nameof(Facilities));
                }
                if (owners.TryGetValue(evidence, out var owner))
                {
                    throw new ArgumentException(
                        $"Capability evidence '{evidence.Value}' is assigned to both '{owner.Value}' and '{facility.Id.Value}'.",
                        nameof(Facilities));
                }
                owners.Add(evidence, facility.Id);
            }
        }
    }
}

static class InfrastructureTargetFacilityManifestFingerprinting
{
    internal static InfrastructureTargetFacilityManifestFingerprint Compute(
        string schemaVersion,
        InfrastructureTargetFacilityManifestId id,
        InfrastructureCapabilityProfileReference profile,
        InfrastructureCapabilityVariantId variant,
        ImmutableArray<InfrastructureTargetFacility> facilities)
    {
        var canonical = StrictDocumentJson.GetCanonicalBytes(
            new FingerprintInput(schemaVersion, id, profile, variant, facilities),
            StrictDocumentJson.CreateOptions());
        return new(
            InfrastructureTargetFacilityManifestFingerprint.CurrentAlgorithm,
            InfrastructureTargetFacilityManifestFingerprint.CurrentCanonicalization,
            Convert.ToHexStringLower(SHA256.HashData(canonical)));
    }

    sealed record FingerprintInput(
        string SchemaVersion,
        InfrastructureTargetFacilityManifestId Id,
        InfrastructureCapabilityProfileReference Profile,
        InfrastructureCapabilityVariantId Variant,
        ImmutableArray<InfrastructureTargetFacility> Facilities);
}

static class InfrastructureTargetEvidenceSelection
{
    internal static ImmutableArray<InfrastructureCapabilityEvidenceId> Select(
        InfrastructureTargetFacilityManifest manifest,
        ImmutableArray<InfrastructureTargetFacilityDecision> decisions) =>
        Select(manifest, decisions.Select(static decision => decision.Facility));

    internal static ImmutableArray<InfrastructureCapabilityEvidenceId> Select(
        InfrastructureTargetFacilityManifest manifest,
        IEnumerable<InfrastructureTargetFacilityId> facilities)
    {
        var variant = manifest.Profile.FindVariant(manifest.Variant)!;
        var evidenceOwners = manifest.Facilities
            .SelectMany(static facility => facility.Evidence.Select(evidence => (evidence, facility.Id)))
            .ToDictionary(static assignment => assignment.evidence, static assignment => assignment.Id);
        var selectedFacilities = facilities.ToHashSet();
        var selectedEvidence = variant.Evidence
            .Where(evidence => !evidenceOwners.TryGetValue(evidence.Id, out var owner)
                || selectedFacilities.Contains(owner))
            .Select(static evidence => evidence.Id)
            .ToHashSet();

        var evidenceById = variant.Evidence.ToDictionary(static evidence => evidence.Id);
        var pending = new Queue<InfrastructureCapabilityEvidenceId>(selectedEvidence);
        while (pending.TryDequeue(out var evidenceId))
        {
            foreach (var auxiliary in evidenceById[evidenceId].Auxiliaries)
            {
                if (selectedEvidence.Add(auxiliary))
                    pending.Enqueue(auxiliary);
            }
        }

        return variant.Evidence
            .Where(evidence => selectedEvidence.Contains(evidence.Id))
            .Select(static evidence => evidence.Id)
            .ToImmutableArray();
    }
}
