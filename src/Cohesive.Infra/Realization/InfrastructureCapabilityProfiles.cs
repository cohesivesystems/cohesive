using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Cohesive.Infra.Configuration;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Infra.Realization;

/// <summary>Deterministic fingerprint of one complete infrastructure capability profile.</summary>
public sealed record InfrastructureCapabilityProfileFingerprint
{
    /// <summary>Digest algorithm used by the current capability-profile fingerprint.</summary>
    public const string CurrentAlgorithm = "sha256";

    /// <summary>Canonicalization profile used by the current capability-profile fingerprint.</summary>
    public const string CurrentCanonicalization = "cohesive-infra-capability-profile/v2-c14n/v1";

    /// <summary>Creates capability-profile fingerprint metadata.</summary>
    /// <param name="algorithm">Stable digest algorithm identity.</param>
    /// <param name="canonicalization">Stable canonicalization-profile identity.</param>
    /// <param name="value">Lowercase hexadecimal digest.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Any argument is empty or white-space.</exception>
    [JsonConstructor]
    public InfrastructureCapabilityProfileFingerprint(string algorithm, string canonicalization, string value)
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

/// <summary>Exact versioned reference to one infrastructure capability profile.</summary>
public sealed record InfrastructureCapabilityProfileReference
{
    /// <summary>Creates an exact capability-profile reference.</summary>
    /// <param name="schemaVersion">Exact persisted profile schema version.</param>
    /// <param name="id">Stable versioned profile identity.</param>
    /// <param name="fingerprint">Exact canonical profile fingerprint.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="schemaVersion"/> or <paramref name="fingerprint"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="schemaVersion"/> or <paramref name="id"/> is default.</exception>
    [JsonConstructor]
    public InfrastructureCapabilityProfileReference(
        string schemaVersion,
        InfrastructureCapabilityProfileId id,
        InfrastructureCapabilityProfileFingerprint fingerprint)
    {
        SchemaVersion = Guard.RequireNotNullOrWhiteSpace(schemaVersion);
        if (string.IsNullOrWhiteSpace(id.Value))
            throw new ArgumentException("An infrastructure capability-profile reference requires an identity.", nameof(id));

        Id = id;
        Fingerprint = Guard.RequireNotNull(fingerprint);
    }

    /// <summary>Exact persisted profile schema version.</summary>
    public string SchemaVersion { get; }

    /// <summary>Stable versioned profile identity.</summary>
    public InfrastructureCapabilityProfileId Id { get; }

    /// <summary>Exact canonical profile fingerprint.</summary>
    public InfrastructureCapabilityProfileFingerprint Fingerprint { get; }
}

/// <summary>One explicit target operating boundary under which infrastructure capability evidence holds.</summary>
public sealed record InfrastructureOperatingBoundary
{
    /// <summary>Creates an operating boundary.</summary>
    /// <param name="id">Stable boundary identity.</param>
    /// <param name="assertion">Canonical human- and machine-addressable boundary assertion.</param>
    /// <param name="sourceReferences">Adapter, provider, test, benchmark, or policy evidence references.</param>
    /// <exception cref="ArgumentNullException"><paramref name="assertion"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An identity, assertion, or source reference is invalid or duplicated.</exception>
    [JsonConstructor]
    public InfrastructureOperatingBoundary(
        InfrastructureOperatingBoundaryId id,
        string assertion,
        ImmutableArray<SourceReference> sourceReferences = default)
    {
        if (string.IsNullOrWhiteSpace(id.Value))
        {
            throw new ArgumentException("An infrastructure operating boundary requires a stable identity.", nameof(id));
        }

        Id = id;
        Assertion = Guard.RequireNotNullOrWhiteSpace(assertion);
        SourceReferences = SourceReference.NormalizeSet(
            sourceReferences,
            requireNonEmpty: false);
    }

    /// <summary>Stable boundary identity.</summary>
    public InfrastructureOperatingBoundaryId Id { get; }

    /// <summary>Canonical boundary assertion.</summary>
    public string Assertion { get; }

    /// <summary>Attributable evidence references in ordinal order.</summary>
    public ImmutableArray<SourceReference> SourceReferences { get; }

    /// <summary>Compares operating boundaries structurally.</summary>
    /// <param name="other">Other boundary.</param>
    /// <returns><see langword="true"/> when every field is equal.</returns>
    public bool Equals(InfrastructureOperatingBoundary? other) =>
        ReferenceEquals(this, other)
        || other is not null
        && Id == other.Id
        && string.Equals(Assertion, other.Assertion, StringComparison.Ordinal)
        && SourceReferences.SequenceEqual(other.SourceReferences);

    /// <summary>Returns a structural hash code for this boundary.</summary>
    /// <returns>A hash code derived from every field.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Id);
        hash.Add(Assertion, StringComparer.Ordinal);
        foreach (var sourceReference in SourceReferences)
        {
            hash.Add(sourceReference);
        }

        return hash.ToHashCode();
    }
}

/// <summary>One attributable reusable target strategy that can supply an infrastructure capability.</summary>
/// <remarks>
/// This is planning evidence, not proof about a selected physical instance. A physical interpreter must later bind
/// the strategy and its auxiliaries to exact resources and lifecycle authority before deployment.
/// </remarks>
public sealed record InfrastructureCapabilityEvidence
{
    /// <summary>Creates capability evidence.</summary>
    /// <param name="id">Stable evidence identity.</param>
    /// <param name="capability">Canonical requirement-shaped capability supplied by the target.</param>
    /// <param name="realization">Native, composed, or constrained target-strategy classification.</param>
    /// <param name="auxiliaries">Evidence identities composed by this assertion.</param>
    /// <param name="operatingBoundaries">Boundaries under which the assertion holds.</param>
    /// <param name="configuration">Effective configuration attribution used by the assertion.</param>
    /// <param name="sourceReferences">Adapter, provider, conformance, deployment, or override evidence references.</param>
    /// <exception cref="ArgumentException">An identity, collection, realization-specific field, or source reference is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="realization"/> is unavailable, unknown, override, or unsupported. Overrides are exact
    /// demand-scoped compiler policy and cannot be embedded in reusable target profiles.
    /// </exception>
    [JsonConstructor]
    public InfrastructureCapabilityEvidence(
        InfrastructureCapabilityEvidenceId id,
        InfrastructureCapabilityId capability,
        CapabilityRealizationKind realization,
        ImmutableArray<InfrastructureCapabilityEvidenceId> auxiliaries = default,
        ImmutableArray<InfrastructureOperatingBoundaryId> operatingBoundaries = default,
        ImmutableArray<EffectiveConfigurationDecision> configuration = default,
        ImmutableArray<SourceReference> sourceReferences = default)
    {
        if (string.IsNullOrWhiteSpace(id.Value))
        {
            throw new ArgumentException("Infrastructure capability evidence requires a stable identity.", nameof(id));
        }

        if (string.IsNullOrWhiteSpace(capability.Value))
        {
            throw new ArgumentException("Infrastructure capability evidence requires a capability.", nameof(capability));
        }

        if (!Enum.IsDefined(realization)
            || realization is CapabilityRealizationKind.Unavailable
                or CapabilityRealizationKind.Unknown
                or CapabilityRealizationKind.Override)
        {
            throw new ArgumentOutOfRangeException(
                nameof(realization),
                realization,
                "Target capability evidence must describe a native, composed, or constrained strategy; overrides are demand-scoped compiler policy.");
        }

        Id = id;
        Capability = capability;
        Realization = realization;
        Auxiliaries = InfrastructureCapabilityCollections.IdentitySet(
            auxiliaries,
            static identity => identity.Value,
            nameof(auxiliaries));
        OperatingBoundaries = InfrastructureCapabilityCollections.IdentitySet(
            operatingBoundaries,
            static identity => identity.Value,
            nameof(operatingBoundaries));
        Configuration = InfrastructureCapabilityCollections.Configuration(configuration, nameof(configuration));
        SourceReferences = SourceReference.NormalizeSet(
            sourceReferences,
            requireNonEmpty: true);

        if (Auxiliaries.Contains(id))
            throw new ArgumentException("Infrastructure capability evidence cannot compose itself.", nameof(auxiliaries));

        switch (realization)
        {
            case CapabilityRealizationKind.Native
                when !Auxiliaries.IsDefaultOrEmpty || !OperatingBoundaries.IsDefaultOrEmpty:
                throw new ArgumentException(
                    "Native capability evidence cannot claim auxiliary evidence or operating boundaries.",
                    nameof(realization));
            case CapabilityRealizationKind.Composed
                when Auxiliaries.IsDefaultOrEmpty || !OperatingBoundaries.IsDefaultOrEmpty:
                throw new ArgumentException(
                    "Composed capability evidence requires auxiliaries and cannot claim constrained boundaries.",
                    nameof(realization));
            case CapabilityRealizationKind.Constrained when OperatingBoundaries.IsDefaultOrEmpty:
                throw new ArgumentException(
                    "Constrained capability evidence requires at least one operating boundary.",
                    nameof(realization));
        }
    }

    /// <summary>Stable evidence identity.</summary>
    public InfrastructureCapabilityEvidenceId Id { get; }

    /// <summary>Canonical requirement-shaped capability supplied by the target.</summary>
    public InfrastructureCapabilityId Capability { get; }

    /// <summary>How this evidence realizes the capability.</summary>
    public CapabilityRealizationKind Realization { get; }

    /// <summary>Evidence identities composed by this assertion in ordinal order.</summary>
    public ImmutableArray<InfrastructureCapabilityEvidenceId> Auxiliaries { get; }

    /// <summary>Operating-boundary identities in ordinal order.</summary>
    public ImmutableArray<InfrastructureOperatingBoundaryId> OperatingBoundaries { get; }

    /// <summary>Effective configuration attribution in stable setting order.</summary>
    public ImmutableArray<EffectiveConfigurationDecision> Configuration { get; }

    /// <summary>Attributable evidence references in ordinal order.</summary>
    public ImmutableArray<SourceReference> SourceReferences { get; }

    /// <summary>Compares capability evidence structurally.</summary>
    /// <param name="other">Other evidence.</param>
    /// <returns><see langword="true"/> when every field is equal.</returns>
    public bool Equals(InfrastructureCapabilityEvidence? other) =>
        ReferenceEquals(this, other)
        || other is not null
        && Id == other.Id
        && Capability == other.Capability
        && Realization == other.Realization
        && Auxiliaries.SequenceEqual(other.Auxiliaries)
        && OperatingBoundaries.SequenceEqual(other.OperatingBoundaries)
        && Configuration.SequenceEqual(other.Configuration)
        && SourceReferences.SequenceEqual(other.SourceReferences);

    /// <summary>Returns a structural hash code for this evidence.</summary>
    /// <returns>A hash code derived from every field.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Id);
        hash.Add(Capability);
        hash.Add(Realization);
        foreach (var auxiliary in Auxiliaries)
        {
            hash.Add(auxiliary);
        }

        foreach (var boundary in OperatingBoundaries)
        {
            hash.Add(boundary);
        }

        foreach (var decision in Configuration)
        {
            hash.Add(decision);
        }

        foreach (var sourceReference in SourceReferences)
        {
            hash.Add(sourceReference);
        }

        return hash.ToHashCode();
    }
}

/// <summary>A versioned Horn-style rule that composes prerequisite capabilities into one supplied capability.</summary>
public sealed record InfrastructureCapabilityRule
{
    /// <summary>Creates a capability-composition rule.</summary>
    /// <param name="id">Stable versioned rule identity.</param>
    /// <param name="providedCapability">Capability proved when every prerequisite is discharged.</param>
    /// <param name="requiredCapabilities">Capabilities that must all be discharged.</param>
    /// <param name="preservedGuarantees">Guarantee capabilities explicitly preserved by the composition.</param>
    /// <param name="operatingBoundaries">Boundaries required by the complete composition.</param>
    /// <param name="sourceReferences">Compiler-rule, protocol, provider, or conformance evidence references.</param>
    /// <exception cref="ArgumentException">An identity, capability, collection, or source reference is invalid.</exception>
    [JsonConstructor]
    public InfrastructureCapabilityRule(
        InfrastructureCapabilityRuleId id,
        InfrastructureCapabilityId providedCapability,
        ImmutableArray<InfrastructureCapabilityId> requiredCapabilities,
        ImmutableArray<InfrastructureCapabilityId> preservedGuarantees = default,
        ImmutableArray<InfrastructureOperatingBoundaryId> operatingBoundaries = default,
        ImmutableArray<SourceReference> sourceReferences = default)
    {
        if (string.IsNullOrWhiteSpace(id.Value))
        {
            throw new ArgumentException("An infrastructure capability rule requires a stable identity.", nameof(id));
        }

        if (string.IsNullOrWhiteSpace(providedCapability.Value))
        {
            throw new ArgumentException("An infrastructure capability rule requires a provided capability.", nameof(providedCapability));
        }

        Id = id;
        ProvidedCapability = providedCapability;
        RequiredCapabilities = InfrastructureCapabilityCollections.IdentitySet(
            requiredCapabilities,
            static identity => identity.Value,
            nameof(requiredCapabilities),
            requireNonEmpty: true);
        PreservedGuarantees = InfrastructureCapabilityCollections.IdentitySet(
            preservedGuarantees,
            static identity => identity.Value,
            nameof(preservedGuarantees));
        OperatingBoundaries = InfrastructureCapabilityCollections.IdentitySet(
            operatingBoundaries,
            static identity => identity.Value,
            nameof(operatingBoundaries));
        SourceReferences = SourceReference.NormalizeSet(sourceReferences);

        if (RequiredCapabilities.Contains(providedCapability))
        {
            throw new ArgumentException("A capability rule cannot directly require the capability it provides.", nameof(requiredCapabilities));
        }
    }

    /// <summary>Stable versioned rule identity.</summary>
    public InfrastructureCapabilityRuleId Id { get; }

    /// <summary>Capability proved when every prerequisite is discharged.</summary>
    public InfrastructureCapabilityId ProvidedCapability { get; }

    /// <summary>Capabilities that must all be discharged, in ordinal order.</summary>
    public ImmutableArray<InfrastructureCapabilityId> RequiredCapabilities { get; }

    /// <summary>Guarantee capabilities explicitly preserved by the rule, in ordinal order.</summary>
    public ImmutableArray<InfrastructureCapabilityId> PreservedGuarantees { get; }

    /// <summary>Operating boundaries required by the composition, in ordinal order.</summary>
    public ImmutableArray<InfrastructureOperatingBoundaryId> OperatingBoundaries { get; }

    /// <summary>Attributable source references in ordinal order.</summary>
    public ImmutableArray<SourceReference> SourceReferences { get; }

    /// <summary>Compares capability rules structurally.</summary>
    /// <param name="other">Other rule.</param>
    /// <returns><see langword="true"/> when every field is equal.</returns>
    public bool Equals(InfrastructureCapabilityRule? other) =>
        ReferenceEquals(this, other)
        || other is not null
        && Id == other.Id
        && ProvidedCapability == other.ProvidedCapability
        && RequiredCapabilities.SequenceEqual(other.RequiredCapabilities)
        && PreservedGuarantees.SequenceEqual(other.PreservedGuarantees)
        && OperatingBoundaries.SequenceEqual(other.OperatingBoundaries)
        && SourceReferences.SequenceEqual(other.SourceReferences);

    /// <summary>Returns a structural hash code for this rule.</summary>
    /// <returns>A hash code derived from every field.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Id);
        hash.Add(ProvidedCapability);
        foreach (var capability in RequiredCapabilities)
        {
            hash.Add(capability);
        }

        foreach (var guarantee in PreservedGuarantees)
        {
            hash.Add(guarantee);
        }

        foreach (var boundary in OperatingBoundaries)
        {
            hash.Add(boundary);
        }

        foreach (var sourceReference in SourceReferences)
        {
            hash.Add(sourceReference);
        }

        return hash.ToHashCode();
    }
}

/// <summary>One coherent configured target variant whose evidence may participate in a realization proof.</summary>
public sealed record InfrastructureCapabilityVariant
{
    /// <summary>Creates a coherent target variant.</summary>
    /// <param name="id">Stable profile-local variant identity.</param>
    /// <param name="evidence">Direct capability evidence supplied by the variant.</param>
    /// <param name="rules">Capability-composition rules available to the variant.</param>
    /// <param name="operatingBoundaries">Operating boundaries referenced by evidence or rules.</param>
    /// <exception cref="ArgumentException">An identity, collection, reference, or variant invariant is invalid.</exception>
    [JsonConstructor]
    public InfrastructureCapabilityVariant(
        InfrastructureCapabilityVariantId id,
        ImmutableArray<InfrastructureCapabilityEvidence> evidence = default,
        ImmutableArray<InfrastructureCapabilityRule> rules = default,
        ImmutableArray<InfrastructureOperatingBoundary> operatingBoundaries = default)
    {
        if (string.IsNullOrWhiteSpace(id.Value))
            throw new ArgumentException("An infrastructure capability variant requires a stable identity.", nameof(id));

        Id = id;
        Evidence = NormalizeByIdentity(
            evidence,
            static item => item.Id.Value,
            nameof(evidence));
        Rules = NormalizeByIdentity(rules, static item => item.Id.Value, nameof(rules));
        OperatingBoundaries = NormalizeByIdentity(
            operatingBoundaries,
            static item => item.Id.Value,
            nameof(operatingBoundaries));

        ValidateReferences();
    }

    /// <summary>Stable profile-local variant identity.</summary>
    public InfrastructureCapabilityVariantId Id { get; }

    /// <summary>Direct capability evidence in stable identity order.</summary>
    public ImmutableArray<InfrastructureCapabilityEvidence> Evidence { get; }

    /// <summary>Composition rules in stable identity order.</summary>
    public ImmutableArray<InfrastructureCapabilityRule> Rules { get; }

    /// <summary>Operating boundaries in stable identity order.</summary>
    public ImmutableArray<InfrastructureOperatingBoundary> OperatingBoundaries { get; }

    /// <summary>Compares coherent variants structurally.</summary>
    /// <param name="other">Other variant.</param>
    /// <returns><see langword="true"/> when every field is equal.</returns>
    public bool Equals(InfrastructureCapabilityVariant? other) =>
        ReferenceEquals(this, other)
        || other is not null
        && Id == other.Id
        && Evidence.SequenceEqual(other.Evidence)
        && Rules.SequenceEqual(other.Rules)
        && OperatingBoundaries.SequenceEqual(other.OperatingBoundaries);

    /// <summary>Returns a structural hash code for this variant.</summary>
    /// <returns>A hash code derived from every field.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Id);
        foreach (var item in Evidence)
            hash.Add(item);
        foreach (var item in Rules)
            hash.Add(item);
        foreach (var item in OperatingBoundaries)
            hash.Add(item);
        return hash.ToHashCode();
    }

    void ValidateReferences()
    {
        var evidenceIds = Evidence.Select(static item => item.Id).ToHashSet();
        var boundaryIds = OperatingBoundaries.Select(static item => item.Id).ToHashSet();

        foreach (var item in Evidence)
        {
            foreach (var auxiliary in item.Auxiliaries)
            {
                if (!evidenceIds.Contains(auxiliary))
                {
                    throw new ArgumentException(
                        $"Capability evidence '{item.Id.Value}' cites unknown auxiliary evidence '{auxiliary.Value}'.",
                        nameof(Evidence));
                }
            }
            foreach (var boundary in item.OperatingBoundaries)
                EnsureBoundaryExists(boundaryIds, boundary, $"Capability evidence '{item.Id.Value}'");
        }

        foreach (var rule in Rules)
        {
            foreach (var boundary in rule.OperatingBoundaries)
                EnsureBoundaryExists(boundaryIds, boundary, $"Capability rule '{rule.Id.Value}'");
        }
    }

    static void EnsureBoundaryExists(
        HashSet<InfrastructureOperatingBoundaryId> boundaryIds,
        InfrastructureOperatingBoundaryId boundary,
        string subject)
    {
        if (!boundaryIds.Contains(boundary))
        {
            throw new ArgumentException(
                $"{subject} cites unknown operating boundary '{boundary.Value}'.",
                nameof(OperatingBoundaries));
        }
    }

    static ImmutableArray<T> NormalizeByIdentity<T>(
        ImmutableArray<T> items,
        Func<T, string> selectIdentity,
        string paramName)
        where T : class
    {
        if (items.IsDefaultOrEmpty)
            return [];
        if (items.Any(static item => item is null))
            throw new ArgumentException("Infrastructure capability collections cannot contain null.", paramName);

        var ordered = items.Sort(Comparer<T>.Create(
            (left, right) => StringComparer.Ordinal.Compare(selectIdentity(left), selectIdentity(right))));
        for (var index = 1; index < ordered.Length; index++)
        {
            if (string.Equals(selectIdentity(ordered[index - 1]), selectIdentity(ordered[index]), StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"Infrastructure capability identity '{selectIdentity(ordered[index])}' is duplicated.",
                    paramName);
            }
        }
        return ordered;
    }
}

/// <summary>Portable, exactly fingerprinted target-planning profile containing mutually coherent configured variants.</summary>
public sealed record InfrastructureCapabilityProfile
{
    /// <summary>Current persisted capability-profile schema version.</summary>
    public const string CurrentSchemaVersion = "cohesive.infra.capabilities/2";

    /// <summary>Creates an infrastructure capability profile.</summary>
    /// <param name="schemaVersion">Exact persisted capability-profile schema version.</param>
    /// <param name="id">Stable, versioned profile identity.</param>
    /// <param name="target">Stable interpretation-target identity.</param>
    /// <param name="supportedDefinitionSchemaVersions">Infrastructure-definition schema versions understood by this target.</param>
    /// <param name="variants">Mutually coherent configured target variants.</param>
    /// <param name="fingerprint">Persisted exact fingerprint, or <see langword="null"/> to compute it.</param>
    /// <exception cref="ArgumentNullException"><paramref name="schemaVersion"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// An identity, schema version, collection, or variant identity is invalid or duplicated, or
    /// <paramref name="fingerprint"/> does not match canonical profile content.
    /// </exception>
    [JsonConstructor]
    public InfrastructureCapabilityProfile(
        string schemaVersion,
        InfrastructureCapabilityProfileId id,
        InfrastructureTargetId target,
        ImmutableArray<string> supportedDefinitionSchemaVersions,
        ImmutableArray<InfrastructureCapabilityVariant> variants,
        InfrastructureCapabilityProfileFingerprint? fingerprint = null)
    {
        SchemaVersion = Guard.RequireNotNullOrWhiteSpace(schemaVersion);
        if (string.IsNullOrWhiteSpace(id.Value))
            throw new ArgumentException("An infrastructure capability profile requires a stable identity.", nameof(id));
        if (string.IsNullOrWhiteSpace(target.Value))
            throw new ArgumentException("An infrastructure capability profile requires a target identity.", nameof(target));

        Id = id;
        Target = target;
        SupportedDefinitionSchemaVersions = InfrastructureCapabilityCollections.StringSet(
            supportedDefinitionSchemaVersions,
            nameof(supportedDefinitionSchemaVersions),
            requireNonEmpty: true);
        Variants = NormalizeVariants(variants);
        var computed = InfrastructureCapabilityProfileFingerprinting.Compute(
            SchemaVersion,
            Id,
            Target,
            SupportedDefinitionSchemaVersions,
            Variants);
        if (fingerprint is not null && fingerprint != computed)
            throw new ArgumentException("The supplied infrastructure capability-profile fingerprint is not canonical.", nameof(fingerprint));
        Fingerprint = computed;
    }

    /// <summary>Exact persisted capability-profile schema version.</summary>
    public string SchemaVersion { get; }

    /// <summary>Stable, versioned capability-profile identity.</summary>
    public InfrastructureCapabilityProfileId Id { get; }

    /// <summary>Stable interpretation-target identity.</summary>
    public InfrastructureTargetId Target { get; }

    /// <summary>Supported infrastructure-definition schema versions in ordinal order.</summary>
    public ImmutableArray<string> SupportedDefinitionSchemaVersions { get; }

    /// <summary>Mutually coherent target variants in stable identity order.</summary>
    public ImmutableArray<InfrastructureCapabilityVariant> Variants { get; }

    /// <summary>Deterministic fingerprint of exact profile identity, target, compatibility, evidence, and rules.</summary>
    public InfrastructureCapabilityProfileFingerprint Fingerprint { get; }

    /// <summary>Finds one exact coherent target variant.</summary>
    /// <param name="id">Variant identity to find.</param>
    /// <returns>The matching variant, or <see langword="null"/> when unavailable.</returns>
    public InfrastructureCapabilityVariant? FindVariant(InfrastructureCapabilityVariantId id)
    {
        var index = CanonicalDocumentCollections.BinarySearchIndex(
            Variants,
            id,
            static (variant, sought) => StringComparer.Ordinal.Compare(variant.Id.Value, sought.Value));
        return index < 0 ? null : Variants[index];
    }

    /// <summary>Creates an exact reference to this capability profile.</summary>
    /// <returns>The schema, identity, and canonical fingerprint of this profile.</returns>
    public InfrastructureCapabilityProfileReference ToReference() => new(SchemaVersion, Id, Fingerprint);

    /// <summary>Compares capability profiles structurally.</summary>
    /// <param name="other">Other profile.</param>
    /// <returns><see langword="true"/> when every field is equal.</returns>
    public bool Equals(InfrastructureCapabilityProfile? other) =>
        ReferenceEquals(this, other)
        || other is not null
        && string.Equals(SchemaVersion, other.SchemaVersion, StringComparison.Ordinal)
        && Id == other.Id
        && Target == other.Target
        && SupportedDefinitionSchemaVersions.SequenceEqual(other.SupportedDefinitionSchemaVersions, StringComparer.Ordinal)
        && Variants.SequenceEqual(other.Variants)
        && Fingerprint == other.Fingerprint;

    /// <summary>Returns a structural hash code for this profile.</summary>
    /// <returns>A hash code derived from every field.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(SchemaVersion, StringComparer.Ordinal);
        hash.Add(Id);
        hash.Add(Target);
        foreach (var version in SupportedDefinitionSchemaVersions)
            hash.Add(version, StringComparer.Ordinal);
        foreach (var variant in Variants)
            hash.Add(variant);
        hash.Add(Fingerprint);
        return hash.ToHashCode();
    }

    static ImmutableArray<InfrastructureCapabilityVariant> NormalizeVariants(
        ImmutableArray<InfrastructureCapabilityVariant> variants)
    {
        if (variants.IsDefaultOrEmpty)
            throw new ArgumentException("An infrastructure capability profile requires at least one coherent variant.", nameof(variants));
        if (variants.Any(static variant => variant is null))
            throw new ArgumentException("Infrastructure capability variants cannot contain null.", nameof(variants));

        var ordered = variants.Sort(static (left, right) =>
            StringComparer.Ordinal.Compare(left.Id.Value, right.Id.Value));
        for (var index = 1; index < ordered.Length; index++)
        {
            if (ordered[index - 1].Id == ordered[index].Id)
                throw new ArgumentException($"Capability variant '{ordered[index].Id.Value}' is duplicated.", nameof(variants));
        }
        return ordered;
    }
}

static class InfrastructureCapabilityProfileFingerprinting
{
    internal static InfrastructureCapabilityProfileFingerprint Compute(
        string schemaVersion,
        InfrastructureCapabilityProfileId id,
        InfrastructureTargetId target,
        ImmutableArray<string> supportedDefinitionSchemaVersions,
        ImmutableArray<InfrastructureCapabilityVariant> variants)
    {
        var canonical = StrictDocumentJson.GetCanonicalBytes(
            new FingerprintInput(
                schemaVersion,
                id,
                target,
                supportedDefinitionSchemaVersions,
                variants),
            StrictDocumentJson.CreateOptions());
        return new(
            InfrastructureCapabilityProfileFingerprint.CurrentAlgorithm,
            InfrastructureCapabilityProfileFingerprint.CurrentCanonicalization,
            Convert.ToHexStringLower(SHA256.HashData(canonical)));
    }

    sealed record FingerprintInput(
        string SchemaVersion,
        InfrastructureCapabilityProfileId Id,
        InfrastructureTargetId Target,
        ImmutableArray<string> SupportedDefinitionSchemaVersions,
        ImmutableArray<InfrastructureCapabilityVariant> Variants);
}

static class InfrastructureCapabilityCollections
{
    internal static ImmutableArray<T> IdentitySet<T>(
        ImmutableArray<T> values,
        Func<T, string> selectValue,
        string paramName,
        bool requireNonEmpty = false)
        where T : struct
    {
        if (values.IsDefaultOrEmpty)
        {
            if (requireNonEmpty)
                throw new ArgumentException("The infrastructure capability collection cannot be empty.", paramName);
            return [];
        }

        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(selectValue(value)))
                throw new ArgumentException("Infrastructure capability identities cannot be default or empty.", paramName);
        }

        var ordered = values.Sort(Comparer<T>.Create(
            (left, right) => StringComparer.Ordinal.Compare(selectValue(left), selectValue(right))));
        for (var index = 1; index < ordered.Length; index++)
        {
            if (string.Equals(selectValue(ordered[index - 1]), selectValue(ordered[index]), StringComparison.Ordinal))
                throw new ArgumentException($"Infrastructure capability identity '{selectValue(ordered[index])}' is duplicated.", paramName);
        }
        return ordered;
    }

    internal static ImmutableArray<string> StringSet(
        ImmutableArray<string> values,
        string paramName,
        bool requireNonEmpty = false)
    {
        if (values.IsDefaultOrEmpty)
        {
            if (requireNonEmpty)
                throw new ArgumentException("The infrastructure string collection cannot be empty.", paramName);
            return [];
        }

        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("Infrastructure string collections cannot contain empty values.", paramName);
        }

        var ordered = values.Sort(StringComparer.Ordinal);
        for (var index = 1; index < ordered.Length; index++)
        {
            if (string.Equals(ordered[index - 1], ordered[index], StringComparison.Ordinal))
            {
                throw new ArgumentException($"Infrastructure value '{ordered[index]}' is duplicated.", paramName);
            }
        }
        return ordered;
    }

    internal static ImmutableArray<EffectiveConfigurationDecision> Configuration(
        ImmutableArray<EffectiveConfigurationDecision> values,
        string paramName)
    {
        if (values.IsDefaultOrEmpty)
            return [];
        if (values.Any(static value => value is null))
            throw new ArgumentException("Effective infrastructure configuration cannot contain null.", paramName);

        var ordered = values.Sort(static (left, right) =>
        {
            var comparison = StringComparer.Ordinal.Compare(left.Setting, right.Setting);
            if (comparison != 0)
                return comparison;
            comparison = left.Origin.CompareTo(right.Origin);
            return comparison != 0
                ? comparison
                : StringComparer.Ordinal.Compare(left.Authority, right.Authority);
        });
        for (var index = 1; index < ordered.Length; index++)
        {
            if (string.Equals(ordered[index - 1].Setting, ordered[index].Setting, StringComparison.Ordinal))
                throw new ArgumentException($"Effective setting '{ordered[index].Setting}' is attributed more than once.", paramName);
        }
        return ordered;
    }
}
