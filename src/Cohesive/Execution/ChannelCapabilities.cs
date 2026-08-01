using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Execution;

/// <summary>Stable, versioned identity of one Channel capability profile.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct ChannelCapabilityProfileId
{
    /// <summary>Creates a Channel capability-profile identity.</summary>
    /// <param name="value">Stable identity that changes when the profile's semantic capability set changes.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white-space.</exception>
    [JsonConstructor]
    public ChannelCapabilityProfileId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw stable profile identity.</summary>
    public string Value { get; }

    /// <summary>Returns the raw stable profile identity.</summary>
    /// <returns>The value supplied at construction.</returns>
    public override string ToString() => Value;
}

/// <summary>Stable identity of one coherent Channel capability variant.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct ChannelCapabilityVariantId
{
    /// <summary>Creates a capability-variant identity.</summary>
    /// <param name="value">Stable profile-local variant identity.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white-space.</exception>
    [JsonConstructor]
    public ChannelCapabilityVariantId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw stable variant identity.</summary>
    public string Value { get; }

    /// <summary>Returns the raw stable variant identity.</summary>
    /// <returns>The value supplied at construction.</returns>
    public override string ToString() => Value;
}

/// <summary>Stable identity of one attributable Channel capability assertion.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct ChannelCapabilityEvidenceId
{
    /// <summary>Creates a capability-evidence identity.</summary>
    /// <param name="value">Stable variant-local evidence identity.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white-space.</exception>
    [JsonConstructor]
    public ChannelCapabilityEvidenceId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw stable evidence identity.</summary>
    public string Value { get; }

    /// <summary>Returns the raw stable evidence identity.</summary>
    /// <returns>The value supplied at construction.</returns>
    public override string ToString() => Value;
}

/// <summary>Deterministic fingerprint of one complete Channel capability profile.</summary>
public sealed record ChannelCapabilityProfileFingerprint
{
    /// <summary>Creates a Channel capability-profile fingerprint.</summary>
    /// <param name="algorithm">Stable digest algorithm identity.</param>
    /// <param name="canonicalization">Stable canonicalization profile identity.</param>
    /// <param name="value">Lowercase hexadecimal digest.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Any argument is empty or white-space.</exception>
    [JsonConstructor]
    public ChannelCapabilityProfileFingerprint(string algorithm, string canonicalization, string value)
    {
        Algorithm = Guard.RequireNotNullOrWhiteSpace(algorithm);
        Canonicalization = Guard.RequireNotNullOrWhiteSpace(canonicalization);
        Value = Guard.RequireNotNullOrWhiteSpace(value);
    }

    /// <summary>Digest algorithm identity.</summary>
    public string Algorithm { get; }

    /// <summary>Canonicalization profile identity.</summary>
    public string Canonicalization { get; }

    /// <summary>Lowercase hexadecimal digest.</summary>
    public string Value { get; }
}

/// <summary>Exact versioned reference to one Channel capability profile.</summary>
public sealed record ChannelCapabilityProfileReference
{
    /// <summary>Creates an exact Channel capability-profile reference.</summary>
    /// <param name="schemaVersion">Exact persisted profile schema version.</param>
    /// <param name="id">Stable versioned profile identity.</param>
    /// <param name="fingerprint">Exact canonical profile fingerprint.</param>
    /// <exception cref="ArgumentNullException"><paramref name="schemaVersion"/> or <paramref name="fingerprint"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="schemaVersion"/> or <paramref name="id"/> is default.</exception>
    [JsonConstructor]
    public ChannelCapabilityProfileReference(
        string schemaVersion,
        ChannelCapabilityProfileId id,
        ChannelCapabilityProfileFingerprint fingerprint)
    {
        SchemaVersion = Guard.RequireNotNullOrWhiteSpace(schemaVersion);
        if (string.IsNullOrWhiteSpace(id.Value))
            throw new ArgumentException("A Channel capability-profile reference requires an identity.", nameof(id));
        Id = id;
        Fingerprint = Guard.RequireNotNull(fingerprint);
    }

    /// <summary>Exact persisted profile schema version.</summary>
    public string SchemaVersion { get; }

    /// <summary>Stable versioned profile identity.</summary>
    public ChannelCapabilityProfileId Id { get; }

    /// <summary>Exact canonical profile fingerprint.</summary>
    public ChannelCapabilityProfileFingerprint Fingerprint { get; }
}

/// <summary>One attributable assertion that a coherent target variant preserves a Channel requirement.</summary>
/// <remarks>
/// <see cref="Capability"/> deliberately reuses the canonical <see cref="ChannelRequirement"/> algebra. Capability
/// profiles therefore cannot create a second feature enum or silently reinterpret a requirement family.
/// </remarks>
public sealed record ChannelCapabilityEvidence
{
    /// <summary>Creates one attributable Channel capability assertion.</summary>
    /// <param name="id">Stable evidence identity.</param>
    /// <param name="capability">Canonical requirement-shaped capability supplied by the target.</param>
    /// <param name="realization">Native, composed, constrained, or explicit-override realization.</param>
    /// <param name="auxiliaries">Evidence composed by this assertion.</param>
    /// <param name="operatingBoundaries">Positive operating boundaries under which the assertion holds.</param>
    /// <param name="configuration">Attribution for effective configuration used by the assertion.</param>
    /// <param name="sourceReferences">Adapter, deployment, compiler, or override evidence references.</param>
    /// <param name="description">Optional non-semantic human-facing explanation.</param>
    /// <exception cref="ArgumentNullException"><paramref name="capability"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// An identity, collection, source reference, description, realization-specific field, or configuration decision
    /// is invalid.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="realization"/> is unavailable or unknown.</exception>
    [JsonConstructor]
    public ChannelCapabilityEvidence(
        ChannelCapabilityEvidenceId id,
        ChannelRequirement capability,
        CapabilityRealizationKind realization,
        ImmutableArray<ChannelCapabilityEvidenceId> auxiliaries = default,
        ImmutableArray<ChannelLimitRequirement> operatingBoundaries = default,
        ImmutableArray<EffectiveConfigurationDecision> configuration = default,
        ImmutableArray<string> sourceReferences = default,
        string? description = null)
    {
        if (string.IsNullOrWhiteSpace(id.Value))
            throw new ArgumentException("Channel capability evidence requires a stable identity.", nameof(id));
        if (!Enum.IsDefined(realization)
            || realization is CapabilityRealizationKind.Unavailable or CapabilityRealizationKind.Unknown)
        {
            throw new ArgumentOutOfRangeException(
                nameof(realization),
                realization,
                "Capability evidence must describe an available exact realization.");
        }

        Id = id;
        Capability = Guard.RequireNotNull(capability);
        capability.EnsureDeclaredVariant();
        Auxiliaries = ChannelCapabilityNormalization.IdentitySet(auxiliaries, nameof(auxiliaries));
        OperatingBoundaries = ChannelCapabilityNormalization.Boundaries(operatingBoundaries, nameof(operatingBoundaries));
        Configuration = ChannelCapabilityNormalization.Configuration(configuration, nameof(configuration));
        SourceReferences = ChannelCapabilityNormalization.StringSet(
            sourceReferences,
            nameof(sourceReferences),
            requireNonEmpty: true);

        if (description is not null && string.IsNullOrWhiteSpace(description))
            throw new ArgumentException("A capability-evidence description cannot be white-space.", nameof(description));
        if (Auxiliaries.Contains(id))
            throw new ArgumentException("Capability evidence cannot compose itself.", nameof(auxiliaries));

        switch (realization)
        {
            case CapabilityRealizationKind.Native when !Auxiliaries.IsDefaultOrEmpty || !OperatingBoundaries.IsDefaultOrEmpty:
                throw new ArgumentException("Native capability evidence cannot declare auxiliaries or operating boundaries.", nameof(realization));
            case CapabilityRealizationKind.Composed when Auxiliaries.IsDefaultOrEmpty || !OperatingBoundaries.IsDefaultOrEmpty:
                throw new ArgumentException("Composed capability evidence requires auxiliaries and cannot claim constrained boundaries.", nameof(realization));
            case CapabilityRealizationKind.Constrained when OperatingBoundaries.IsDefaultOrEmpty:
                throw new ArgumentException("Constrained capability evidence requires an attributable operating boundary.", nameof(realization));
            case CapabilityRealizationKind.Override when !Configuration.Any(static item => item.Origin == EffectiveConfigurationOrigin.Explicit):
                throw new ArgumentException("Override capability evidence requires explicit configuration attribution.", nameof(configuration));
        }

        Realization = realization;
        Description = description;
    }

    /// <summary>Stable evidence identity.</summary>
    public ChannelCapabilityEvidenceId Id { get; }

    /// <summary>Canonical requirement-shaped capability supplied by the target.</summary>
    public ChannelRequirement Capability { get; }

    /// <summary>How this evidence realizes the capability.</summary>
    public CapabilityRealizationKind Realization { get; }

    /// <summary>Evidence composed by this assertion in stable identity order.</summary>
    public ImmutableArray<ChannelCapabilityEvidenceId> Auxiliaries { get; }

    /// <summary>Operating boundaries in deterministic scope and dimension order.</summary>
    public ImmutableArray<ChannelLimitRequirement> OperatingBoundaries { get; }

    /// <summary>Effective configuration attribution in stable setting order.</summary>
    public ImmutableArray<EffectiveConfigurationDecision> Configuration { get; }

    /// <summary>Attributable source references in ordinal order.</summary>
    public ImmutableArray<string> SourceReferences { get; }

    /// <summary>Optional non-semantic human-facing explanation.</summary>
    public string? Description { get; }

    /// <summary>Compares normalized capability evidence structurally.</summary>
    /// <param name="other">Other capability evidence.</param>
    /// <returns><see langword="true"/> when every evidence field is equal.</returns>
    public bool Equals(ChannelCapabilityEvidence? other) =>
        ReferenceEquals(this, other)
        || other is not null
        && Id == other.Id
        && Capability == other.Capability
        && Realization == other.Realization
        && Auxiliaries.SequenceEqual(other.Auxiliaries)
        && OperatingBoundaries.SequenceEqual(other.OperatingBoundaries)
        && Configuration.SequenceEqual(other.Configuration)
        && SourceReferences.SequenceEqual(other.SourceReferences, StringComparer.Ordinal)
        && string.Equals(Description, other.Description, StringComparison.Ordinal);

    /// <summary>Returns a structural hash code for normalized capability evidence.</summary>
    /// <returns>A hash code derived from every evidence field.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Id);
        hash.Add(Capability);
        hash.Add(Realization);
        foreach (var auxiliary in Auxiliaries)
            hash.Add(auxiliary);
        foreach (var boundary in OperatingBoundaries)
            hash.Add(boundary);
        foreach (var decision in Configuration)
            hash.Add(decision);
        foreach (var sourceReference in SourceReferences)
            hash.Add(sourceReference, StringComparer.Ordinal);
        hash.Add(Description, StringComparer.Ordinal);
        return hash.ToHashCode();
    }
}

/// <summary>One complete, internally coherent realization alternative in a Channel capability profile.</summary>
/// <remarks>
/// The realization compiler selects one variant for the complete definition. It never combines evidence from
/// different variants, even when doing so would make individual requirements appear satisfiable.
/// </remarks>
public sealed record ChannelCapabilityVariant
{
    /// <summary>Creates a coherent Channel capability variant.</summary>
    /// <param name="id">Stable profile-local variant identity.</param>
    /// <param name="evidence">Attributable capability evidence owned by the variant.</param>
    /// <param name="description">Optional non-semantic human-facing explanation.</param>
    /// <exception cref="ArgumentException">
    /// The identity is default; evidence is null, duplicated, semantically conflicting, cyclic, or references an
    /// unknown auxiliary; or <paramref name="description"/> is white-space.
    /// </exception>
    [JsonConstructor]
    public ChannelCapabilityVariant(
        ChannelCapabilityVariantId id,
        ImmutableArray<ChannelCapabilityEvidence> evidence,
        string? description = null)
    {
        if (string.IsNullOrWhiteSpace(id.Value))
            throw new ArgumentException("A Channel capability variant requires a stable identity.", nameof(id));
        if (description is not null && string.IsNullOrWhiteSpace(description))
            throw new ArgumentException("A capability-variant description cannot be white-space.", nameof(description));

        Id = id;
        Evidence = NormalizeEvidence(evidence);
        Description = description;
        ValidateAuxiliaryGraph(Evidence);
        ValidateConfigurationCoherence(Evidence);
    }

    /// <summary>Stable profile-local variant identity.</summary>
    public ChannelCapabilityVariantId Id { get; }

    /// <summary>Attributable evidence in stable identity order.</summary>
    public ImmutableArray<ChannelCapabilityEvidence> Evidence { get; }

    /// <summary>Optional non-semantic human-facing explanation.</summary>
    public string? Description { get; }

    /// <summary>Compares normalized coherent variants structurally.</summary>
    /// <param name="other">Other capability variant.</param>
    /// <returns><see langword="true"/> when the identity, evidence, and description are equal.</returns>
    public bool Equals(ChannelCapabilityVariant? other) =>
        ReferenceEquals(this, other)
        || other is not null
        && Id == other.Id
        && Evidence.SequenceEqual(other.Evidence)
        && string.Equals(Description, other.Description, StringComparison.Ordinal);

    /// <summary>Returns a structural hash code for one normalized coherent variant.</summary>
    /// <returns>A hash code derived from the identity, evidence, and description.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Id);
        foreach (var item in Evidence)
            hash.Add(item);
        hash.Add(Description, StringComparer.Ordinal);
        return hash.ToHashCode();
    }

    static ImmutableArray<ChannelCapabilityEvidence> NormalizeEvidence(
        ImmutableArray<ChannelCapabilityEvidence> evidence)
    {
        if (evidence.IsDefaultOrEmpty)
            throw new ArgumentException("A Channel capability variant requires evidence.", nameof(evidence));

        HashSet<ChannelCapabilityEvidenceId> identities = [];
        HashSet<string> slots = new(StringComparer.Ordinal);
        foreach (var item in evidence)
        {
            if (item is null)
                throw new ArgumentException("Capability evidence cannot contain null entries.", nameof(evidence));
            if (!identities.Add(item.Id))
                throw new ArgumentException($"Capability evidence identity '{item.Id.Value}' is duplicated.", nameof(evidence));
            var slot = ChannelRequirementCompatibility.Slot(item.Capability);
            if (!slots.Add(slot))
            {
                throw new ArgumentException(
                    $"A coherent variant cannot declare several realizations for capability slot '{slot}'.",
                    nameof(evidence));
            }
        }
        return CanonicalDocumentCollections.SortIfNeeded(
            evidence,
            static (left, right) => StringComparer.Ordinal.Compare(left.Id.Value, right.Id.Value));
    }

    static void ValidateAuxiliaryGraph(ImmutableArray<ChannelCapabilityEvidence> evidence)
    {
        var byId = evidence.ToDictionary(static item => item.Id);
        foreach (var item in evidence)
        {
            foreach (var auxiliary in item.Auxiliaries)
            {
                if (!byId.ContainsKey(auxiliary))
                {
                    throw new ArgumentException(
                        $"Capability evidence '{item.Id.Value}' references unknown auxiliary '{auxiliary.Value}'.",
                        nameof(evidence));
                }
            }
        }

        Dictionary<ChannelCapabilityEvidenceId, byte> states = [];
        foreach (var item in evidence)
            Visit(item.Id, byId, states);

        static void Visit(
            ChannelCapabilityEvidenceId id,
            IReadOnlyDictionary<ChannelCapabilityEvidenceId, ChannelCapabilityEvidence> byId,
            IDictionary<ChannelCapabilityEvidenceId, byte> states)
        {
            if (states.TryGetValue(id, out var state))
            {
                if (state == 1)
                    throw new ArgumentException($"Capability auxiliary graph contains a cycle through '{id.Value}'.", "evidence");
                return;
            }

            states[id] = 1;
            foreach (var auxiliary in byId[id].Auxiliaries)
                Visit(auxiliary, byId, states);
            states[id] = 2;
        }
    }

    static void ValidateConfigurationCoherence(ImmutableArray<ChannelCapabilityEvidence> evidence)
    {
        Dictionary<string, EffectiveConfigurationDecision> settings = new(StringComparer.Ordinal);
        foreach (var decision in evidence.SelectMany(static item => item.Configuration))
        {
            if (settings.TryGetValue(decision.Setting, out var existing) && existing != decision)
            {
                throw new ArgumentException(
                    $"Capability variant has conflicting attribution for setting '{decision.Setting}'.",
                    nameof(evidence));
            }
            settings[decision.Setting] = decision;
        }
    }
}

/// <summary>Portable, versioned capability snapshot exposed by one exact Channel interpretation target.</summary>
public sealed record ChannelCapabilityProfile
{
    /// <summary>Current Channel capability-profile schema version.</summary>
    public const string CurrentSchemaVersion = "cohesive-channel-capability-profile/v1";

    /// <summary>Creates a current-version fingerprinted Channel capability profile.</summary>
    /// <param name="id">Stable versioned profile identity.</param>
    /// <param name="subject">Stable interpretation-target identity described by the profile.</param>
    /// <param name="variants">Complete coherent realization alternatives.</param>
    /// <param name="provenance">Producer and semantic source attribution.</param>
    /// <param name="description">Optional non-semantic human-facing explanation.</param>
    /// <exception cref="ArgumentNullException"><paramref name="subject"/> or <paramref name="provenance"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An identity, variant, or description is invalid.</exception>
    public ChannelCapabilityProfile(
        ChannelCapabilityProfileId id,
        string subject,
        ImmutableArray<ChannelCapabilityVariant> variants,
        ExecutionProvenance provenance,
        string? description = null)
        : this(
            CurrentSchemaVersion,
            id,
            subject,
            variants,
            provenance,
            fingerprint: null,
            description)
    {
    }

    /// <summary>Creates or deserializes an exactly fingerprinted Channel capability profile.</summary>
    /// <param name="schemaVersion">Exact profile schema version.</param>
    /// <param name="id">Stable versioned profile identity.</param>
    /// <param name="subject">Stable interpretation-target identity described by the profile.</param>
    /// <param name="variants">Complete coherent realization alternatives.</param>
    /// <param name="provenance">Producer and semantic source attribution.</param>
    /// <param name="fingerprint">Persisted exact fingerprint, or <see langword="null"/> to compute it.</param>
    /// <param name="description">Optional non-semantic human-facing explanation.</param>
    /// <exception cref="ArgumentNullException"><paramref name="schemaVersion"/>, <paramref name="subject"/>, or <paramref name="provenance"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An identity, variant, description, or supplied fingerprint is invalid.</exception>
    [JsonConstructor]
    public ChannelCapabilityProfile(
        string schemaVersion,
        ChannelCapabilityProfileId id,
        string subject,
        ImmutableArray<ChannelCapabilityVariant> variants,
        ExecutionProvenance provenance,
        ChannelCapabilityProfileFingerprint? fingerprint,
        string? description = null)
    {
        SchemaVersion = Guard.RequireNotNullOrWhiteSpace(schemaVersion);
        if (string.IsNullOrWhiteSpace(id.Value))
            throw new ArgumentException("A Channel capability profile requires a stable identity.", nameof(id));
        Id = id;
        Subject = Guard.RequireNotNullOrWhiteSpace(subject);
        Variants = NormalizeVariants(variants);
        Provenance = Guard.RequireNotNull(provenance);
        if (description is not null && string.IsNullOrWhiteSpace(description))
            throw new ArgumentException("A capability-profile description cannot be white-space.", nameof(description));
        Description = description;

        var computed = ChannelCapabilityProfileFingerprinter.Compute(
            SchemaVersion,
            Id,
            Subject,
            Variants,
            Provenance);
        if (fingerprint is not null && fingerprint != computed)
            throw new ArgumentException("The supplied Channel capability-profile fingerprint is not canonical.", nameof(fingerprint));
        Fingerprint = computed;
    }

    /// <summary>Exact profile schema version.</summary>
    public string SchemaVersion { get; }

    /// <summary>Stable versioned profile identity.</summary>
    public ChannelCapabilityProfileId Id { get; }

    /// <summary>Stable interpretation-target identity described by the profile.</summary>
    public string Subject { get; }

    /// <summary>Coherent realization alternatives in stable identity order.</summary>
    public ImmutableArray<ChannelCapabilityVariant> Variants { get; }

    /// <summary>Producer and semantic source attribution.</summary>
    public ExecutionProvenance Provenance { get; }

    /// <summary>Deterministic fingerprint of all semantic capability and attribution content.</summary>
    public ChannelCapabilityProfileFingerprint Fingerprint { get; }

    /// <summary>Optional non-semantic human-facing explanation.</summary>
    public string? Description { get; }

    /// <summary>Compares normalized capability profiles structurally.</summary>
    /// <param name="other">Other capability profile.</param>
    /// <returns><see langword="true"/> when every persisted profile field is equal.</returns>
    public bool Equals(ChannelCapabilityProfile? other) =>
        ReferenceEquals(this, other)
        || other is not null
        && string.Equals(SchemaVersion, other.SchemaVersion, StringComparison.Ordinal)
        && Id == other.Id
        && string.Equals(Subject, other.Subject, StringComparison.Ordinal)
        && Variants.SequenceEqual(other.Variants)
        && Provenance == other.Provenance
        && Fingerprint == other.Fingerprint
        && string.Equals(Description, other.Description, StringComparison.Ordinal);

    /// <summary>Returns a structural hash code for the normalized persisted profile.</summary>
    /// <returns>A hash code derived from every persisted profile field.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(SchemaVersion, StringComparer.Ordinal);
        hash.Add(Id);
        hash.Add(Subject, StringComparer.Ordinal);
        foreach (var variant in Variants)
            hash.Add(variant);
        hash.Add(Provenance);
        hash.Add(Fingerprint);
        hash.Add(Description, StringComparer.Ordinal);
        return hash.ToHashCode();
    }

    /// <summary>Creates an exact reference to this profile.</summary>
    /// <returns>The schema, identity, and canonical fingerprint of this profile.</returns>
    public ChannelCapabilityProfileReference ToReference() => new(SchemaVersion, Id, Fingerprint);

    internal static ImmutableArray<ChannelCapabilityVariant> NormalizeVariants(
        ImmutableArray<ChannelCapabilityVariant> variants)
    {
        if (variants.IsDefaultOrEmpty)
            throw new ArgumentException("A Channel capability profile requires at least one coherent variant.", nameof(variants));

        HashSet<ChannelCapabilityVariantId> identities = [];
        foreach (var variant in variants)
        {
            if (variant is null)
                throw new ArgumentException("Capability variants cannot contain null entries.", nameof(variants));
            if (!identities.Add(variant.Id))
                throw new ArgumentException($"Capability variant identity '{variant.Id.Value}' is duplicated.", nameof(variants));
        }
        return CanonicalDocumentCollections.SortIfNeeded(
            variants,
            static (left, right) => StringComparer.Ordinal.Compare(left.Id.Value, right.Id.Value));
    }
}

/// <summary>Deterministic fingerprinting for Channel capability profiles.</summary>
/// <remarks>
/// The fingerprint covers schema, profile identity, target subject, normalized evidence and attribution. Optional
/// profile, variant, and evidence descriptions are intentionally excluded because they do not change capability
/// semantics or target selection.
/// </remarks>
public static class ChannelCapabilityProfileFingerprinter
{
    /// <summary>Digest algorithm used by Channel capability-profile fingerprints.</summary>
    public const string Algorithm = "sha256";

    /// <summary>Canonicalization profile used by Channel capability-profile fingerprints.</summary>
    public const string Canonicalization = "cohesive-channel-capability-profile/v1-c14n/v1";

    /// <summary>Computes an exact profile fingerprint from normalized semantic content.</summary>
    /// <param name="schemaVersion">Exact profile schema version.</param>
    /// <param name="id">Stable profile identity.</param>
    /// <param name="subject">Stable target subject.</param>
    /// <param name="variants">Normalized coherent variants.</param>
    /// <param name="provenance">Producer and source attribution.</param>
    /// <returns>A deterministic SHA-256 fingerprint.</returns>
    /// <exception cref="ArgumentNullException">A required reference is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An identity, subject, or variant collection is invalid.</exception>
    /// <exception cref="System.Text.Json.JsonException">Profile content cannot be serialized canonically.</exception>
    /// <exception cref="NotSupportedException">Profile content contains an unsupported runtime value.</exception>
    /// <exception cref="InvalidOperationException">Profile content has no canonical JSON representation.</exception>
    public static ChannelCapabilityProfileFingerprint Compute(
        string schemaVersion,
        ChannelCapabilityProfileId id,
        string subject,
        ImmutableArray<ChannelCapabilityVariant> variants,
        ExecutionProvenance provenance)
    {
        schemaVersion = Guard.RequireNotNullOrWhiteSpace(schemaVersion);
        if (string.IsNullOrWhiteSpace(id.Value))
            throw new ArgumentException("A capability-profile fingerprint requires a profile identity.", nameof(id));
        subject = Guard.RequireNotNullOrWhiteSpace(subject);
        variants = ChannelCapabilityProfile.NormalizeVariants(variants);
        ArgumentNullException.ThrowIfNull(provenance);

        var semanticVariants = ImmutableArray.CreateBuilder<VariantFingerprintInput>(variants.Length);
        foreach (var variant in variants)
        {
            var evidence = ImmutableArray.CreateBuilder<EvidenceFingerprintInput>(variant.Evidence.Length);
            foreach (var assertion in variant.Evidence)
            {
                evidence.Add(new(
                    assertion.Id,
                    assertion.Capability,
                    assertion.Realization,
                    assertion.Auxiliaries,
                    assertion.OperatingBoundaries,
                    assertion.Configuration,
                    assertion.SourceReferences));
            }
            semanticVariants.Add(new(variant.Id, evidence.MoveToImmutable()));
        }

        var bytes = StrictDocumentJson.GetCanonicalBytes(
            new FingerprintInput(schemaVersion, id, subject, semanticVariants.MoveToImmutable(), provenance),
            StrictDocumentJson.CreateOptions());
        return new(
            Algorithm,
            Canonicalization,
            Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    sealed record FingerprintInput(
        string SchemaVersion,
        ChannelCapabilityProfileId Id,
        string Subject,
        ImmutableArray<VariantFingerprintInput> Variants,
        ExecutionProvenance Provenance);

    sealed record VariantFingerprintInput(
        ChannelCapabilityVariantId Id,
        ImmutableArray<EvidenceFingerprintInput> Evidence);

    sealed record EvidenceFingerprintInput(
        ChannelCapabilityEvidenceId Id,
        ChannelRequirement Capability,
        CapabilityRealizationKind Realization,
        ImmutableArray<ChannelCapabilityEvidenceId> Auxiliaries,
        ImmutableArray<ChannelLimitRequirement> OperatingBoundaries,
        ImmutableArray<EffectiveConfigurationDecision> Configuration,
        ImmutableArray<string> SourceReferences);
}

/// <summary>Semantic compatibility between a demanded Channel requirement and target capability evidence.</summary>
/// <remarks>
/// Requirement family and logical scope are exact. Set-valued security and atomicity capabilities may be supersets;
/// settlement operation/coupling pairs match exactly; minimum capacity and duration evidence must meet or exceed the demand; and maximum in-flight,
/// retransmission, or lifetime bounds may be tighter but never looser. Other closed semantic variants match exactly
/// except where a requirement explicitly declares that no stronger isolation, ordering, replay, or continuity is
/// demanded.
/// </remarks>
public static class ChannelRequirementCompatibility
{
    /// <summary>Returns whether one target capability preserves a demanded requirement without weakening it.</summary>
    /// <param name="required">Exact canonical requirement demanded by a Channel definition.</param>
    /// <param name="available">Requirement-shaped capability advertised by a target variant.</param>
    /// <returns><see langword="true"/> when <paramref name="available"/> satisfies every demanded dimension.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="required"/> or <paramref name="available"/> is <see langword="null"/>.</exception>
    public static bool Satisfies(ChannelRequirement required, ChannelRequirement available)
    {
        ArgumentNullException.ThrowIfNull(required);
        ArgumentNullException.ThrowIfNull(available);
        if (required.GetType() != available.GetType() || required.Scope != available.Scope)
            return false;

        return (required, available) switch
        {
            (ChannelTopologyRequirement demand, ChannelTopologyRequirement supply) =>
                demand.Distribution == supply.Distribution && demand.Interaction == supply.Interaction,
            (ChannelRoutingRequirement demand, ChannelRoutingRequirement supply) =>
                demand.Routing == supply.Routing && IsolationSatisfies(demand.Isolation, supply.Isolation),
            (ChannelFramingRequirement demand, ChannelFramingRequirement supply) =>
                demand.Framing == supply.Framing
                && BoundarySatisfies(demand.Boundaries, demand.Codec, supply.Boundaries, supply.Codec),
            (ChannelPersistenceRequirement demand, ChannelPersistenceRequirement supply) =>
                demand.Retention == supply.Retention
                && (demand.Replay == ChannelReplayKind.None || demand.Replay == supply.Replay)
                && MinimumSatisfies(demand.MinimumRetention, supply.MinimumRetention),
            (ChannelProgressRequirement demand, ChannelProgressRequirement supply) =>
                (demand.Floor == ChannelProgressFloorKind.None || demand.Floor == supply.Floor)
                && (demand.Pending == ChannelPendingProgressKind.None || demand.Pending == supply.Pending),
            (ChannelDeliveryRequirement demand, ChannelDeliveryRequirement supply) =>
                DeliveryGuaranteeSatisfies(demand.Guarantee, supply.Guarantee)
                && (demand.Ordering == ChannelOrderingScopeKind.None || demand.Ordering == supply.Ordering)
                && (demand.Ordering != ChannelOrderingScopeKind.Named
                    || string.Equals(demand.NamedOrderingScope, supply.NamedOrderingScope, StringComparison.Ordinal)),
            (ChannelReliabilityRequirement demand, ChannelReliabilityRequirement supply) =>
                ReliabilitySatisfies(demand, supply),
            (ChannelSettlementRequirement demand, ChannelSettlementRequirement supply) =>
                demand.Operation == supply.Operation && demand.Coupling == supply.Coupling,
            (ChannelFlowRequirement demand, ChannelFlowRequirement supply) =>
                FlowSatisfies(demand, supply),
            (ChannelAtomicityRequirement demand, ChannelAtomicityRequirement supply) =>
                demand.AtomicScope == supply.AtomicScope && IsSuperset(supply.Operations, demand.Operations),
            (ChannelSecurityRequirement demand, ChannelSecurityRequirement supply) =>
                IsSuperset(supply.Properties, demand.Properties),
            (ChannelLimitRequirement demand, ChannelLimitRequirement supply) =>
                demand.Kind == supply.Kind && supply.Value >= demand.Value,
            _ => false
        };
    }

    internal static string Slot(ChannelRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(requirement);
        var discriminator = requirement switch
        {
            ChannelLimitRequirement limit => $"limit:{(int)limit.Kind}",
            ChannelSettlementRequirement settlement =>
                $"settlement:{(int)settlement.Operation}:{(int)settlement.Coupling}",
            ChannelAtomicityRequirement atomicity => $"atomicity:{atomicity.AtomicScope.Value}",
            _ => requirement.WireName
        };
        return string.Concat(
            requirement.Scope.Kind.ToString(),
            ":",
            requirement.Scope.Direction?.Value ?? "*",
            ":",
            discriminator);
    }

    static bool IsolationSatisfies(ChannelRoutingIsolationKind required, ChannelRoutingIsolationKind available) =>
        required == ChannelRoutingIsolationKind.None || required == available;

    static bool DeliveryGuaranteeSatisfies(
        ChannelDeliveryGuaranteeKind required,
        ChannelDeliveryGuaranteeKind available) =>
        required == available
        || available == ChannelDeliveryGuaranteeKind.ProtocolExactlyOnce
            && required is ChannelDeliveryGuaranteeKind.AtMostOnce or ChannelDeliveryGuaranteeKind.AtLeastOnce;

    static bool BoundarySatisfies(
        ChannelBoundarySemantics required,
        string? requiredCodec,
        ChannelBoundarySemantics available,
        string? availableCodec) =>
        required == ChannelBoundarySemantics.Unpreserved
        || required == available
        && (required != ChannelBoundarySemantics.CodecReconstructed
            || string.Equals(requiredCodec, availableCodec, StringComparison.Ordinal));

    static bool ReliabilitySatisfies(
        ChannelReliabilityRequirement required,
        ChannelReliabilityRequirement available)
    {
        if (required.Reliability != available.Reliability)
            return false;
        if (required.Reliability != ChannelReliabilityKind.PartiallyReliable)
            return true;

        return MaximumSatisfies(required.MaximumLifetime, available.MaximumLifetime)
            && MaximumSatisfies(required.MaximumRetransmissions, available.MaximumRetransmissions);
    }

    static bool FlowSatisfies(ChannelFlowRequirement required, ChannelFlowRequirement available)
    {
        var continuitySatisfied = required.Continuity switch
        {
            ChannelSessionContinuityKind.None => true,
            ChannelSessionContinuityKind.Reconnect => available.Continuity is
                ChannelSessionContinuityKind.Reconnect or ChannelSessionContinuityKind.BoundedResume,
            ChannelSessionContinuityKind.BoundedResume =>
                available.Continuity == ChannelSessionContinuityKind.BoundedResume,
            _ => false
        };
        return (required.Control == ChannelFlowControlKind.None || required.Control == available.Control)
            && required.Completion == available.Completion
            && continuitySatisfied
            && MaximumSatisfies(required.MaximumInFlight, available.MaximumInFlight)
            && MinimumSatisfies(required.ResumeWindow, available.ResumeWindow)
            && (required.Cancellation == ChannelCancellationKind.None
                || required.Cancellation == available.Cancellation)
            && InitiationLeaseSatisfies(required.InitiationLease, available.InitiationLease);
    }

    static bool InitiationLeaseSatisfies(
        ChannelInitiationLease? required,
        ChannelInitiationLease? available) =>
        required is null
        || available is not null
            && available.MinimumInitiations >= required.MinimumInitiations
            && available.MinimumValidity >= required.MinimumValidity;

    static bool IsSuperset<TEnum>(ImmutableArray<TEnum> available, ImmutableArray<TEnum> required)
        where TEnum : struct, Enum => required.All(available.Contains);

    static bool MinimumSatisfies(TimeSpan? required, TimeSpan? available) =>
        required is null || available is { } supplied && supplied >= required.Value;

    static bool MaximumSatisfies(TimeSpan? required, TimeSpan? available) =>
        required is null || available is { } supplied && supplied <= required.Value;

    static bool MaximumSatisfies(int? required, int? available) =>
        required is null || available is { } supplied && supplied <= required.Value;
}

static class ChannelCapabilityNormalization
{
    public static ImmutableArray<ChannelCapabilityEvidenceId> IdentitySet(
        ImmutableArray<ChannelCapabilityEvidenceId> values,
        string parameterName)
    {
        var normalized = values.IsDefault ? [] : values;
        if (normalized.IsDefaultOrEmpty)
            return [];
        HashSet<ChannelCapabilityEvidenceId> observed = [];
        foreach (var value in normalized)
        {
            if (string.IsNullOrWhiteSpace(value.Value) || !observed.Add(value))
                throw new ArgumentException("Capability evidence references must be non-default and distinct.", parameterName);
        }
        return CanonicalDocumentCollections.SortIfNeeded(
            normalized,
            static (left, right) => StringComparer.Ordinal.Compare(left.Value, right.Value));
    }

    public static ImmutableArray<ChannelLimitRequirement> Boundaries(
        ImmutableArray<ChannelLimitRequirement> values,
        string parameterName)
    {
        var normalized = values.IsDefault ? [] : values;
        if (normalized.IsDefaultOrEmpty)
            return [];
        HashSet<string> slots = new(StringComparer.Ordinal);
        foreach (var value in normalized)
        {
            if (value is null)
                throw new ArgumentException("Operating boundaries cannot contain null entries.", parameterName);
            if (!slots.Add(ChannelRequirementCompatibility.Slot(value)))
                throw new ArgumentException("Operating boundaries cannot repeat a scope and limit dimension.", parameterName);
        }
        return CanonicalDocumentCollections.SortIfNeeded(
            normalized,
            static (left, right) => StringComparer.Ordinal.Compare(
                ChannelRequirementCompatibility.Slot(left),
                ChannelRequirementCompatibility.Slot(right)));
    }

    public static ImmutableArray<EffectiveConfigurationDecision> Configuration(
        ImmutableArray<EffectiveConfigurationDecision> values,
        string parameterName)
    {
        var normalized = values.IsDefault ? [] : values;
        if (normalized.IsDefaultOrEmpty)
            return [];
        if (normalized.Any(static item => item is null))
            throw new ArgumentException("Configuration decisions cannot contain null entries.", parameterName);
        if (normalized.GroupBy(static item => item.Setting, StringComparer.Ordinal).Any(static group => group.Count() > 1))
            throw new ArgumentException("Configuration decisions cannot repeat a setting.", parameterName);
        return CanonicalDocumentCollections.SortIfNeeded(
            normalized,
            static (left, right) => StringComparer.Ordinal.Compare(left.Setting, right.Setting));
    }

    public static ImmutableArray<string> StringSet(
        ImmutableArray<string> values,
        string parameterName,
        bool requireNonEmpty)
    {
        var normalized = values.IsDefault ? [] : values;
        if (requireNonEmpty && normalized.IsDefaultOrEmpty)
            throw new ArgumentException("At least one source reference is required.", parameterName);
        HashSet<string> observed = new(StringComparer.Ordinal);
        foreach (var value in normalized)
        {
            if (string.IsNullOrWhiteSpace(value) || !observed.Add(value))
                throw new ArgumentException("Source references must be non-empty and distinct.", parameterName);
        }
        return CanonicalDocumentCollections.SortIfNeeded(
            normalized,
            static (left, right) => StringComparer.Ordinal.Compare(left, right));
    }
}
