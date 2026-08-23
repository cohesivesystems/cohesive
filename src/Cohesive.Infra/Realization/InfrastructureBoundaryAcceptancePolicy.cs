using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Cohesive.Model.Serialization;

namespace Cohesive.Infra.Realization;

/// <summary>Deterministic fingerprint of one complete infrastructure boundary-acceptance policy.</summary>
public sealed record InfrastructureBoundaryAcceptancePolicyFingerprint
{
    /// <summary>Digest algorithm used by the current policy fingerprint.</summary>
    public const string CurrentAlgorithm = "sha256";

    /// <summary>Canonicalization profile used by the current policy fingerprint.</summary>
    public const string CurrentCanonicalization = "cohesive-infra-boundary-acceptance-policy/v1-c14n/v1";

    /// <summary>Creates boundary-acceptance policy fingerprint metadata.</summary>
    /// <param name="algorithm">Stable digest algorithm identity.</param>
    /// <param name="canonicalization">Stable canonicalization-profile identity.</param>
    /// <param name="value">Lowercase hexadecimal digest.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Any argument is empty or white-space.</exception>
    [JsonConstructor]
    public InfrastructureBoundaryAcceptancePolicyFingerprint(
        string algorithm,
        string canonicalization,
        string value)
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

/// <summary>Exact reference to one governed boundary-acceptance policy.</summary>
public sealed record InfrastructureBoundaryAcceptancePolicyReference
{
    /// <summary>Creates an exact boundary-acceptance policy reference.</summary>
    /// <param name="schemaVersion">Exact persisted policy schema.</param>
    /// <param name="id">Stable versioned policy identity.</param>
    /// <param name="definition">Exact governed infrastructure definition.</param>
    /// <param name="profile">Exact governed capability profile.</param>
    /// <param name="bindingProfile">Exact binding-elaboration profile that owns derived requirements.</param>
    /// <param name="target">Exact interpretation target.</param>
    /// <param name="variant">Exact coherent target variant.</param>
    /// <param name="fingerprint">Exact canonical policy fingerprint.</param>
    /// <exception cref="ArgumentNullException">
    /// A required reference, schema, or fingerprint is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">An identity or schema is empty or default.</exception>
    [JsonConstructor]
    public InfrastructureBoundaryAcceptancePolicyReference(
        string schemaVersion,
        InfrastructureBoundaryAcceptancePolicyId id,
        InfrastructureDefinitionReference definition,
        InfrastructureCapabilityProfileReference profile,
        InfrastructureBindingElaborationProfileReference bindingProfile,
        InfrastructureTargetId target,
        InfrastructureCapabilityVariantId variant,
        InfrastructureBoundaryAcceptancePolicyFingerprint fingerprint)
    {
        SchemaVersion = Guard.RequireNotNullOrWhiteSpace(schemaVersion);
        if (string.IsNullOrWhiteSpace(id.Value))
            throw new ArgumentException("A boundary-acceptance policy reference requires an identity.", nameof(id));
        if (string.IsNullOrWhiteSpace(target.Value))
            throw new ArgumentException("A boundary-acceptance policy reference requires a target.", nameof(target));
        if (string.IsNullOrWhiteSpace(variant.Value))
            throw new ArgumentException("A boundary-acceptance policy reference requires a variant.", nameof(variant));

        Id = id;
        Definition = Guard.RequireNotNull(definition);
        Profile = Guard.RequireNotNull(profile);
        BindingProfile = Guard.RequireNotNull(bindingProfile);
        Target = target;
        Variant = variant;
        Fingerprint = Guard.RequireNotNull(fingerprint);
    }

    /// <summary>Exact persisted policy schema.</summary>
    public string SchemaVersion { get; }

    /// <summary>Stable versioned policy identity.</summary>
    public InfrastructureBoundaryAcceptancePolicyId Id { get; }

    /// <summary>Exact governed infrastructure definition.</summary>
    public InfrastructureDefinitionReference Definition { get; }

    /// <summary>Exact governed capability profile.</summary>
    public InfrastructureCapabilityProfileReference Profile { get; }

    /// <summary>Exact binding-elaboration profile that owns derived requirements.</summary>
    public InfrastructureBindingElaborationProfileReference BindingProfile { get; }

    /// <summary>Exact interpretation target.</summary>
    public InfrastructureTargetId Target { get; }

    /// <summary>Exact coherent target variant.</summary>
    public InfrastructureCapabilityVariantId Variant { get; }

    /// <summary>Exact canonical policy fingerprint.</summary>
    public InfrastructureBoundaryAcceptancePolicyFingerprint Fingerprint { get; }
}

/// <summary>Attributable acceptance of one operating boundary for one exact capability demand.</summary>
public sealed record InfrastructureBoundaryAcceptance
{
    /// <summary>Creates one demand-scoped operating-boundary acceptance.</summary>
    /// <param name="requirement">Exact declared or binding-derived requirement.</param>
    /// <param name="boundary">Exact operating boundary accepted for the requirement.</param>
    /// <param name="rationale">Human-reviewable governance rationale.</param>
    /// <param name="sourceReferences">Non-empty policy, approval, or specification references.</param>
    /// <exception cref="ArgumentNullException"><paramref name="rationale"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An identity, rationale, or source reference is invalid or missing.</exception>
    [JsonConstructor]
    public InfrastructureBoundaryAcceptance(
        InfrastructureRequirementId requirement,
        InfrastructureOperatingBoundaryId boundary,
        string rationale,
        ImmutableArray<string> sourceReferences)
    {
        if (string.IsNullOrWhiteSpace(requirement.Value))
            throw new ArgumentException("A boundary acceptance requires an exact requirement identity.", nameof(requirement));
        if (string.IsNullOrWhiteSpace(boundary.Value))
            throw new ArgumentException("A boundary acceptance requires an operating-boundary identity.", nameof(boundary));

        Requirement = requirement;
        Boundary = boundary;
        Rationale = Guard.RequireNotNullOrWhiteSpace(rationale);
        SourceReferences = InfrastructureCapabilityCollections.StringSet(
            sourceReferences,
            nameof(sourceReferences),
            requireNonEmpty: true);
    }

    /// <summary>Exact declared or binding-derived requirement.</summary>
    public InfrastructureRequirementId Requirement { get; }

    /// <summary>Exact operating boundary accepted for the requirement.</summary>
    public InfrastructureOperatingBoundaryId Boundary { get; }

    /// <summary>Human-reviewable governance rationale.</summary>
    public string Rationale { get; }

    /// <summary>Attributable policy, approval, or specification references in ordinal order.</summary>
    public ImmutableArray<string> SourceReferences { get; }

    /// <summary>Compares boundary acceptances structurally.</summary>
    /// <param name="other">Other acceptance.</param>
    /// <returns><see langword="true"/> when every field is equal.</returns>
    public bool Equals(InfrastructureBoundaryAcceptance? other) =>
        ReferenceEquals(this, other)
        || other is not null
        && Requirement == other.Requirement
        && Boundary == other.Boundary
        && string.Equals(Rationale, other.Rationale, StringComparison.Ordinal)
        && SourceReferences.SequenceEqual(other.SourceReferences, StringComparer.Ordinal);

    /// <summary>Returns a structural hash code for this acceptance.</summary>
    /// <returns>A hash code derived from every field.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Requirement);
        hash.Add(Boundary);
        hash.Add(Rationale, StringComparer.Ordinal);
        foreach (var source in SourceReferences)
            hash.Add(source, StringComparer.Ordinal);
        return hash.ToHashCode();
    }
}

/// <summary>Portable, exactly fenced policy accepting constrained capability boundaries.</summary>
public sealed record InfrastructureBoundaryAcceptancePolicy
{
    /// <summary>Current persisted boundary-acceptance policy schema.</summary>
    public const string CurrentSchemaVersion = "cohesive.infra.boundary-acceptance/1";

    /// <summary>Creates or restores an exact boundary-acceptance policy.</summary>
    /// <param name="schemaVersion">Exact persisted policy schema.</param>
    /// <param name="id">Stable versioned policy identity.</param>
    /// <param name="definition">Exact governed infrastructure definition.</param>
    /// <param name="profile">Exact governed capability profile.</param>
    /// <param name="bindingProfile">Exact binding-elaboration profile that owns derived requirements.</param>
    /// <param name="target">Exact interpretation target.</param>
    /// <param name="variant">Exact coherent target variant.</param>
    /// <param name="acceptances">Demand-scoped acceptances in any producer order.</param>
    /// <param name="fingerprint">Persisted exact fingerprint, or <see langword="null"/> to compute it.</param>
    /// <exception cref="ArgumentNullException">A required schema or reference is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// An identity, target, variant, acceptance, or fingerprint is invalid, missing, or duplicated.
    /// </exception>
    [JsonConstructor]
    public InfrastructureBoundaryAcceptancePolicy(
        string schemaVersion,
        InfrastructureBoundaryAcceptancePolicyId id,
        InfrastructureDefinitionReference definition,
        InfrastructureCapabilityProfileReference profile,
        InfrastructureBindingElaborationProfileReference bindingProfile,
        InfrastructureTargetId target,
        InfrastructureCapabilityVariantId variant,
        ImmutableArray<InfrastructureBoundaryAcceptance> acceptances = default,
        InfrastructureBoundaryAcceptancePolicyFingerprint? fingerprint = null)
    {
        SchemaVersion = Guard.RequireNotNullOrWhiteSpace(schemaVersion);
        if (string.IsNullOrWhiteSpace(id.Value))
            throw new ArgumentException("A boundary-acceptance policy requires a stable identity.", nameof(id));
        if (string.IsNullOrWhiteSpace(target.Value))
            throw new ArgumentException("A boundary-acceptance policy requires a target.", nameof(target));
        if (string.IsNullOrWhiteSpace(variant.Value))
            throw new ArgumentException("A boundary-acceptance policy requires a coherent variant.", nameof(variant));

        Id = id;
        Definition = Guard.RequireNotNull(definition);
        Profile = Guard.RequireNotNull(profile);
        BindingProfile = Guard.RequireNotNull(bindingProfile);
        Target = target;
        Variant = variant;
        Acceptances = NormalizeAcceptances(acceptances);
        var computed = InfrastructureBoundaryAcceptancePolicyFingerprinting.Compute(
            SchemaVersion,
            Id,
            Definition,
            Profile,
            BindingProfile,
            Target,
            Variant,
            Acceptances);
        if (fingerprint is not null && fingerprint != computed)
            throw new ArgumentException("The supplied boundary-acceptance policy fingerprint is not canonical.", nameof(fingerprint));
        Fingerprint = computed;
    }

    /// <summary>Exact persisted policy schema.</summary>
    public string SchemaVersion { get; }

    /// <summary>Stable versioned policy identity.</summary>
    public InfrastructureBoundaryAcceptancePolicyId Id { get; }

    /// <summary>Exact governed infrastructure definition.</summary>
    public InfrastructureDefinitionReference Definition { get; }

    /// <summary>Exact governed capability profile.</summary>
    public InfrastructureCapabilityProfileReference Profile { get; }

    /// <summary>Exact binding-elaboration profile that owns derived requirements.</summary>
    public InfrastructureBindingElaborationProfileReference BindingProfile { get; }

    /// <summary>Exact interpretation target.</summary>
    public InfrastructureTargetId Target { get; }

    /// <summary>Exact coherent target variant.</summary>
    public InfrastructureCapabilityVariantId Variant { get; }

    /// <summary>Demand-scoped acceptances in requirement-then-boundary order.</summary>
    public ImmutableArray<InfrastructureBoundaryAcceptance> Acceptances { get; }

    /// <summary>Deterministic fingerprint of every policy fence and acceptance.</summary>
    public InfrastructureBoundaryAcceptancePolicyFingerprint Fingerprint { get; }

    /// <summary>Finds an acceptance for one exact requirement and boundary pair.</summary>
    /// <param name="requirement">Exact declared or binding-derived requirement.</param>
    /// <param name="boundary">Exact selected operating boundary.</param>
    /// <returns>The matching acceptance, or <see langword="null"/> when absent.</returns>
    /// <exception cref="ArgumentException">An identity is default.</exception>
    public InfrastructureBoundaryAcceptance? FindAcceptance(
        InfrastructureRequirementId requirement,
        InfrastructureOperatingBoundaryId boundary)
    {
        if (string.IsNullOrWhiteSpace(requirement.Value))
            throw new ArgumentException("A default requirement identity cannot be searched.", nameof(requirement));
        if (string.IsNullOrWhiteSpace(boundary.Value))
            throw new ArgumentException("A default boundary identity cannot be searched.", nameof(boundary));

        var key = (Requirement: requirement.Value, Boundary: boundary.Value);
        var index = CanonicalDocumentCollections.BinarySearchIndex(
            Acceptances,
            key,
            static (acceptance, sought) =>
            {
                var comparison = StringComparer.Ordinal.Compare(acceptance.Requirement.Value, sought.Requirement);
                return comparison != 0
                    ? comparison
                    : StringComparer.Ordinal.Compare(acceptance.Boundary.Value, sought.Boundary);
            });
        return index < 0 ? null : Acceptances[index];
    }

    /// <summary>Creates an exact policy from its owning portable artifacts.</summary>
    /// <param name="id">Stable versioned policy identity.</param>
    /// <param name="definition">Exact governed infrastructure definition.</param>
    /// <param name="profile">Exact governed capability profile.</param>
    /// <param name="bindingProfile">Exact binding-elaboration profile that owns derived requirements.</param>
    /// <param name="variant">Exact coherent target variant.</param>
    /// <param name="acceptances">Demand-scoped acceptances in any producer order.</param>
    /// <returns>An exactly fenced current-schema policy with a computed fingerprint.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="definition"/>, <paramref name="profile"/>, or <paramref name="bindingProfile"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">An identity, variant, or acceptance is invalid or duplicated.</exception>
    public static InfrastructureBoundaryAcceptancePolicy Create(
        InfrastructureBoundaryAcceptancePolicyId id,
        InfrastructureDefinitionDocument definition,
        InfrastructureCapabilityProfile profile,
        InfrastructureBindingElaborationProfile bindingProfile,
        InfrastructureCapabilityVariantId variant,
        ImmutableArray<InfrastructureBoundaryAcceptance> acceptances = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(bindingProfile);
        if (profile.FindVariant(variant) is null)
            throw new ArgumentException($"Capability profile '{profile.Id.Value}' does not declare variant '{variant.Value}'.", nameof(variant));
        return new(
            schemaVersion: CurrentSchemaVersion,
            id: id,
            definition: definition.ToReference(),
            profile: profile.ToReference(),
            bindingProfile: bindingProfile.ToReference(),
            target: profile.Target,
            variant: variant,
            acceptances: acceptances);
    }

    /// <summary>Creates an exact reference to this policy.</summary>
    /// <returns>Every policy fence and its canonical fingerprint.</returns>
    public InfrastructureBoundaryAcceptancePolicyReference ToReference() =>
        new(
            schemaVersion: SchemaVersion,
            id: Id,
            definition: Definition,
            profile: Profile,
            bindingProfile: BindingProfile,
            target: Target,
            variant: Variant,
            fingerprint: Fingerprint);

    /// <summary>Compares boundary-acceptance policies structurally.</summary>
    /// <param name="other">Other policy.</param>
    /// <returns><see langword="true"/> when every field is equal.</returns>
    public bool Equals(InfrastructureBoundaryAcceptancePolicy? other) =>
        ReferenceEquals(this, other)
        || other is not null
        && string.Equals(SchemaVersion, other.SchemaVersion, StringComparison.Ordinal)
        && Id == other.Id
        && Definition == other.Definition
        && Profile == other.Profile
        && BindingProfile == other.BindingProfile
        && Target == other.Target
        && Variant == other.Variant
        && Acceptances.SequenceEqual(other.Acceptances)
        && Fingerprint == other.Fingerprint;

    /// <summary>Returns a structural hash code for this policy.</summary>
    /// <returns>A hash code derived from every field.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(SchemaVersion, StringComparer.Ordinal);
        hash.Add(Id);
        hash.Add(Definition);
        hash.Add(Profile);
        hash.Add(BindingProfile);
        hash.Add(Target);
        hash.Add(Variant);
        foreach (var acceptance in Acceptances)
            hash.Add(acceptance);
        hash.Add(Fingerprint);
        return hash.ToHashCode();
    }

    static ImmutableArray<InfrastructureBoundaryAcceptance> NormalizeAcceptances(
        ImmutableArray<InfrastructureBoundaryAcceptance> acceptances)
    {
        if (acceptances.IsDefaultOrEmpty)
            return [];
        if (acceptances.Any(static acceptance => acceptance is null))
            throw new ArgumentException("Boundary acceptances cannot contain null.", nameof(acceptances));

        var ordered = acceptances.Sort(static (left, right) =>
        {
            var comparison = StringComparer.Ordinal.Compare(left.Requirement.Value, right.Requirement.Value);
            return comparison != 0
                ? comparison
                : StringComparer.Ordinal.Compare(left.Boundary.Value, right.Boundary.Value);
        });
        for (var index = 1; index < ordered.Length; index++)
        {
            if (ordered[index - 1].Requirement == ordered[index].Requirement
                && ordered[index - 1].Boundary == ordered[index].Boundary)
            {
                throw new ArgumentException(
                    $"Boundary acceptance '{ordered[index].Requirement.Value}/{ordered[index].Boundary.Value}' is duplicated.",
                    nameof(acceptances));
            }
        }
        return ordered;
    }
}

static class InfrastructureBoundaryAcceptancePolicyFingerprinting
{
    internal static InfrastructureBoundaryAcceptancePolicyFingerprint Compute(
        string schemaVersion,
        InfrastructureBoundaryAcceptancePolicyId id,
        InfrastructureDefinitionReference definition,
        InfrastructureCapabilityProfileReference profile,
        InfrastructureBindingElaborationProfileReference bindingProfile,
        InfrastructureTargetId target,
        InfrastructureCapabilityVariantId variant,
        ImmutableArray<InfrastructureBoundaryAcceptance> acceptances)
    {
        var canonical = StrictDocumentJson.GetCanonicalBytes(
            new FingerprintInput(
                SchemaVersion: schemaVersion,
                Id: id,
                Definition: definition,
                Profile: profile,
                BindingProfile: bindingProfile,
                Target: target,
                Variant: variant,
                Acceptances: acceptances),
            StrictDocumentJson.CreateOptions());
        return new(
            algorithm: InfrastructureBoundaryAcceptancePolicyFingerprint.CurrentAlgorithm,
            canonicalization: InfrastructureBoundaryAcceptancePolicyFingerprint.CurrentCanonicalization,
            value: Convert.ToHexStringLower(SHA256.HashData(canonical)));
    }

    sealed record FingerprintInput(
        string SchemaVersion,
        InfrastructureBoundaryAcceptancePolicyId Id,
        InfrastructureDefinitionReference Definition,
        InfrastructureCapabilityProfileReference Profile,
        InfrastructureBindingElaborationProfileReference BindingProfile,
        InfrastructureTargetId Target,
        InfrastructureCapabilityVariantId Variant,
        ImmutableArray<InfrastructureBoundaryAcceptance> Acceptances);
}
