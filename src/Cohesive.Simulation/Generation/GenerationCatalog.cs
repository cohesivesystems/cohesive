using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Simulation.Generation;

/// <summary>Portable facility asserted by a generation-catalog producer profile.</summary>
[JsonConverter(typeof(StrictStringEnumJsonConverterFactory))]
public enum GenerationCatalogProducerCapability
{
    /// <summary>Materializes a finite set of exact values as retained catalog entries.</summary>
    FiniteSnapshot = 0,

    /// <summary>Materializes structured values rather than only independent scalar leaves.</summary>
    StructuredValues = 1,

    /// <summary>Selects one explicit provider locale for a complete import.</summary>
    LocaleSelection = 2,

    /// <summary>Uses an import-local deterministic random seed rather than ambient random state.</summary>
    LocalSeed = 3,

    /// <summary>Uses one explicit fixed UTC reference for provider operations whose results depend on time.</summary>
    FixedUtcDateTimeReference = 4
}

/// <summary>Versioned, attributable capability evidence for one generation-catalog producer.</summary>
/// <remarks>
/// The profile describes how a producer can create a retained finite catalog. It is evidence about production of the
/// snapshot, not an executable dependency of catalog interpretation. Profile identity, capability assertions, and
/// sources are retained in the catalog fingerprint.
/// </remarks>
public sealed record GenerationCatalogCapabilityProfile
{
    /// <summary>Creates a producer capability profile.</summary>
    /// <param name="id">Stable versioned profile identity.</param>
    /// <param name="capabilities">Non-empty set of asserted producer facilities.</param>
    /// <param name="sourceReferences">Exact package, documentation, or conformance evidence for the assertions.</param>
    /// <exception cref="ArgumentNullException">A required value or collection entry is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// The identity is empty; capabilities are empty, duplicated, unknown, or omit
    /// <see cref="GenerationCatalogProducerCapability.FiniteSnapshot"/>; or source references are empty, invalid, or
    /// duplicated.
    /// </exception>
    [JsonConstructor]
    public GenerationCatalogCapabilityProfile(
        string id,
        ImmutableArray<GenerationCatalogProducerCapability> capabilities,
        ImmutableArray<SourceReference> sourceReferences)
    {
        Id = Guard.RequireNotNullOrWhiteSpace(id);
        if (capabilities.IsDefaultOrEmpty)
            throw new ArgumentException("A generation-catalog capability profile requires capabilities.", nameof(capabilities));

        var normalized = ImmutableArray.CreateBuilder<GenerationCatalogProducerCapability>(capabilities.Length);
        foreach (var capability in capabilities)
        {
            if (!Enum.IsDefined(capability))
            {
                throw new ArgumentException(
                    $"Generation-catalog producer capability '{capability}' is unsupported.",
                    nameof(capabilities));
            }

            normalized.Add(capability);
        }

        normalized.Sort();
        for (var index = 1; index < normalized.Count; index++)
        {
            if (normalized[index - 1] == normalized[index])
            {
                throw new ArgumentException(
                    $"Generation-catalog producer capability '{normalized[index]}' is duplicated.",
                    nameof(capabilities));
            }
        }

        if (!normalized.Contains(GenerationCatalogProducerCapability.FiniteSnapshot))
        {
            throw new ArgumentException(
                "A generation-catalog capability profile must assert finite snapshot materialization.",
                nameof(capabilities));
        }

        Capabilities = normalized.MoveToImmutable();
        SourceReferences = SourceReference.NormalizeSet(sourceReferences, requireNonEmpty: true);
    }

    /// <summary>Gets the stable versioned producer-profile identity.</summary>
    public string Id { get; }

    /// <summary>Gets asserted producer facilities in canonical enum order.</summary>
    public ImmutableArray<GenerationCatalogProducerCapability> Capabilities { get; }

    /// <summary>Gets exact package, documentation, or conformance evidence in ordinal order.</summary>
    public ImmutableArray<SourceReference> SourceReferences { get; }

    /// <summary>Compares two profiles by exact normalized semantic evidence.</summary>
    /// <param name="other">Other profile.</param>
    /// <returns><see langword="true"/> when every semantic field is equal; otherwise <see langword="false"/>.</returns>
    public bool Equals(GenerationCatalogCapabilityProfile? other) =>
        ReferenceEquals(this, other)
        || other is not null
        && string.Equals(Id, other.Id, StringComparison.Ordinal)
        && Capabilities.SequenceEqual(other.Capabilities)
        && SourceReferences.SequenceEqual(other.SourceReferences);

    /// <summary>Returns a structural hash code for this profile.</summary>
    /// <returns>A hash code derived from every semantic field.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Id, StringComparer.Ordinal);
        foreach (var capability in Capabilities)
            hash.Add(capability);
        foreach (var sourceReference in SourceReferences)
            hash.Add(sourceReference);
        return hash.ToHashCode();
    }
}

/// <summary>Attributable producer and provider evidence for one retained generation catalog.</summary>
/// <remarks>
/// A catalog snapshot is independently executable after it is retained. These coordinates explain how the snapshot
/// was produced; they do not require the producing adapter or provider library to remain installed at interpretation
/// time.
/// </remarks>
public sealed record GenerationCatalogProvenance
{
    /// <summary>Creates catalog provenance.</summary>
    /// <param name="adapter">Stable adapter or importer identity.</param>
    /// <param name="adapterVersion">Exact adapter or importer version.</param>
    /// <param name="provider">Stable external provider or source-library identity.</param>
    /// <param name="providerVersion">Exact external provider or source-library version.</param>
    /// <param name="capabilityProfile">Versioned producer capability assertions and their evidence.</param>
    /// <param name="locale">Optional exact locale or regional catalog identity.</param>
    /// <param name="randomAlgorithm">Optional random-algorithm identity used while producing the snapshot.</param>
    /// <param name="seed">Optional seed representation used while producing the snapshot.</param>
    /// <param name="dateTimeReferenceUtc">Optional fixed UTC provider reference time used during production.</param>
    /// <param name="sourceReferences">Exact catalog-specific application, callback, dataset, or import references.</param>
    /// <param name="knownDeviations">Known semantic deviations in ordinal identity order.</param>
    /// <exception cref="ArgumentNullException">A required string or collection element is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// A required string is empty; the source-reference set is empty; a source reference is invalid or duplicated;
    /// a deviation is empty or duplicated; a date-time reference is not UTC; or locale, seed, and reference-time
    /// coordinates do not agree with the asserted producer capabilities.
    /// </exception>
    [JsonConstructor]
    public GenerationCatalogProvenance(
        string adapter,
        string adapterVersion,
        string provider,
        string providerVersion,
        GenerationCatalogCapabilityProfile capabilityProfile,
        string? locale = null,
        string? randomAlgorithm = null,
        string? seed = null,
        DateTimeOffset? dateTimeReferenceUtc = null,
        ImmutableArray<SourceReference> sourceReferences = default,
        ImmutableArray<string> knownDeviations = default)
    {
        Adapter = Guard.RequireNotNullOrWhiteSpace(adapter);
        AdapterVersion = Guard.RequireNotNullOrWhiteSpace(adapterVersion);
        Provider = Guard.RequireNotNullOrWhiteSpace(provider);
        ProviderVersion = Guard.RequireNotNullOrWhiteSpace(providerVersion);
        CapabilityProfile = Guard.RequireNotNull(capabilityProfile);
        Locale = NormalizeOptional(locale, nameof(locale));
        RandomAlgorithm = NormalizeOptional(randomAlgorithm, nameof(randomAlgorithm));
        Seed = NormalizeOptional(seed, nameof(seed));
        DateTimeReferenceUtc = dateTimeReferenceUtc;
        if ((RandomAlgorithm is null) != (Seed is null))
        {
            throw new ArgumentException(
                "Catalog production random-algorithm and seed evidence must either both be present or both be absent.",
                randomAlgorithm is null ? nameof(randomAlgorithm) : nameof(seed));
        }

        if (DateTimeReferenceUtc is { } referenceTime && referenceTime.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "A catalog production date-time reference must use the UTC offset.",
                nameof(dateTimeReferenceUtc));
        }

        RequireCoordinate(
            capabilityProfile,
            GenerationCatalogProducerCapability.LocaleSelection,
            Locale is not null,
            nameof(locale));
        RequireCoordinate(
            capabilityProfile,
            GenerationCatalogProducerCapability.LocalSeed,
            RandomAlgorithm is not null,
            nameof(randomAlgorithm));
        RequireCoordinate(
            capabilityProfile,
            GenerationCatalogProducerCapability.FixedUtcDateTimeReference,
            DateTimeReferenceUtc is not null,
            nameof(dateTimeReferenceUtc));

        SourceReferences = SourceReference.NormalizeSet(sourceReferences, requireNonEmpty: true);
        KnownDeviations = NormalizeDeviations(knownDeviations);
    }

    /// <summary>Gets the stable adapter or importer identity.</summary>
    public string Adapter { get; }

    /// <summary>Gets the exact adapter or importer version.</summary>
    public string AdapterVersion { get; }

    /// <summary>Gets the stable external provider or source-library identity.</summary>
    public string Provider { get; }

    /// <summary>Gets the exact external provider or source-library version.</summary>
    public string ProviderVersion { get; }

    /// <summary>Gets the versioned producer capability assertions and their evidence.</summary>
    public GenerationCatalogCapabilityProfile CapabilityProfile { get; }

    /// <summary>Gets the exact locale or regional catalog identity when one governed production.</summary>
    public string? Locale { get; }

    /// <summary>Gets the random-algorithm identity used to produce the snapshot when applicable.</summary>
    public string? RandomAlgorithm { get; }

    /// <summary>Gets the seed representation used to produce the snapshot when applicable.</summary>
    public string? Seed { get; }

    /// <summary>Gets the fixed UTC provider reference time used during production when applicable.</summary>
    public DateTimeOffset? DateTimeReferenceUtc { get; }

    /// <summary>Gets exact catalog-specific application, callback, dataset, or import references in ordinal order.</summary>
    public ImmutableArray<SourceReference> SourceReferences { get; }

    /// <summary>Gets known semantic deviations in ordinal order.</summary>
    public ImmutableArray<string> KnownDeviations { get; }

    static string? NormalizeOptional(string? value, string parameterName)
    {
        if (value is null)
            return null;

        value = value.Trim();
        return value.Length > 0
            ? value
            : throw new ArgumentException("An optional catalog-provenance coordinate cannot be empty.", parameterName);
    }

    static void RequireCoordinate(
        GenerationCatalogCapabilityProfile profile,
        GenerationCatalogProducerCapability capability,
        bool isPresent,
        string parameterName)
    {
        var isClaimed = profile.Capabilities.Contains(capability);
        if (isClaimed == isPresent)
            return;

        throw new ArgumentException(
            isClaimed
                ? $"Generation-catalog capability '{capability}' requires its production coordinate."
                : $"A production coordinate for '{capability}' requires that capability in the producer profile.",
            parameterName);
    }

    static ImmutableArray<string> NormalizeDeviations(ImmutableArray<string> deviations)
    {
        if (deviations.IsDefaultOrEmpty)
            return [];

        var normalized = ImmutableArray.CreateBuilder<string>(deviations.Length);
        foreach (var deviation in deviations)
            normalized.Add(Guard.RequireNotNullOrWhiteSpace(deviation));

        normalized.Sort(StringComparer.Ordinal);
        for (var index = 1; index < normalized.Count; index++)
        {
            if (string.Equals(normalized[index - 1], normalized[index], StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"Known catalog deviation '{normalized[index]}' is duplicated.",
                    nameof(deviations));
            }
        }

        return normalized.MoveToImmutable();
    }
}

/// <summary>One stable weighted value in a generation catalog.</summary>
public sealed record GenerationCatalogEntry
{
    /// <summary>Creates one catalog entry.</summary>
    /// <param name="id">Stable identity used for normalization and semantic shrinking.</param>
    /// <param name="value">Exact portable catalog value.</param>
    /// <param name="weight">Finite positive relative selection weight.</param>
    /// <exception cref="ArgumentNullException"><paramref name="id"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="id"/> is empty or white space, or <paramref name="weight"/> is not finite and positive.
    /// </exception>
    [JsonConstructor]
    public GenerationCatalogEntry(string id, ObservationValue value, double weight = 1d)
    {
        Id = Guard.RequireNotNullOrWhiteSpace(id);
        Value = value;
        Weight = double.IsFinite(weight) && weight > 0d
            ? weight
            : throw new ArgumentException("A generation-catalog entry requires a finite positive weight.", nameof(weight));
    }

    /// <summary>Gets the stable entry identity.</summary>
    public string Id { get; }

    /// <summary>Gets the exact portable catalog value.</summary>
    public ObservationValue Value { get; }

    /// <summary>Gets the relative selection weight.</summary>
    public double Weight { get; }
}

/// <summary>Canonical semantic content of one finite, versioned generation catalog.</summary>
public sealed record GenerationCatalogDefinition
{
    /// <summary>Creates a generation catalog definition.</summary>
    /// <param name="id">Stable logical catalog identity.</param>
    /// <param name="revision">Exact authored or imported catalog revision.</param>
    /// <param name="valueType">Portable type shared by every catalog entry.</param>
    /// <param name="entries">Finite weighted entries; declaration order is non-semantic.</param>
    /// <param name="provenance">Exact evidence describing the catalog producer and source provider.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="valueType"/> or <paramref name="provenance"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="id"/> or <paramref name="revision"/> is empty.</exception>
    [JsonConstructor]
    public GenerationCatalogDefinition(
        string id,
        string revision,
        TypeRef valueType,
        ImmutableArray<GenerationCatalogEntry> entries,
        GenerationCatalogProvenance provenance)
    {
        Id = Guard.RequireNotNullOrWhiteSpace(id);
        Revision = Guard.RequireNotNullOrWhiteSpace(revision);
        ValueType = Guard.RequireNotNull(valueType);
        Entries = entries.IsDefault ? [] : entries;
        Provenance = Guard.RequireNotNull(provenance);
    }

    /// <summary>Gets the stable logical catalog identity.</summary>
    public string Id { get; }

    /// <summary>Gets the exact authored or imported catalog revision.</summary>
    public string Revision { get; }

    /// <summary>Gets the portable type shared by every catalog entry.</summary>
    public TypeRef ValueType { get; }

    /// <summary>Gets finite weighted entries.</summary>
    public ImmutableArray<GenerationCatalogEntry> Entries { get; }

    /// <summary>Gets exact producer and source-provider evidence.</summary>
    public GenerationCatalogProvenance Provenance { get; }
}

/// <summary>Versioned deterministic identity of canonical generation-catalog content.</summary>
public sealed record GenerationCatalogFingerprint
{
    /// <summary>Cryptographic hash algorithm used by the current catalog profile.</summary>
    public const string CurrentAlgorithm = "sha256";

    /// <summary>Canonicalization profile used by the current catalog fingerprint.</summary>
    public const string CurrentCanonicalization = "cohesive-simulation-generation-catalog/v2-c14n/v1";

    /// <summary>Creates generation-catalog fingerprint metadata.</summary>
    /// <param name="algorithm">Hash-algorithm identity.</param>
    /// <param name="canonicalization">Canonical generation-catalog profile identity.</param>
    /// <param name="value">Lowercase hexadecimal fingerprint value.</param>
    /// <exception cref="ArgumentNullException">A parameter is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A parameter is empty or white space.</exception>
    [JsonConstructor]
    public GenerationCatalogFingerprint(string algorithm, string canonicalization, string value)
    {
        Algorithm = Guard.RequireNotNullOrWhiteSpace(algorithm);
        Canonicalization = Guard.RequireNotNullOrWhiteSpace(canonicalization);
        Value = Guard.RequireNotNullOrWhiteSpace(value);
    }

    /// <summary>Gets the hash-algorithm identity.</summary>
    public string Algorithm { get; }

    /// <summary>Gets the canonical generation-catalog profile identity.</summary>
    public string Canonicalization { get; }

    /// <summary>Gets the lowercase hexadecimal fingerprint value.</summary>
    public string Value { get; }
}

/// <summary>Portable self-validating envelope for one exact finite generation catalog.</summary>
/// <remarks>
/// Entries are normalized by stable identity. The exact retained values are the executable authority; producer and
/// provider coordinates remain attributable semantic evidence and do not require an external library at replay time.
/// </remarks>
public sealed record GenerationCatalogDocument
{
    /// <summary>Current portable generation-catalog document schema.</summary>
    public const string CurrentSchemaVersion = "cohesive-simulation-generation-catalog/v2";

    /// <summary>Creates or restores one portable generation-catalog document.</summary>
    /// <param name="schemaVersion">Exact portable catalog schema.</param>
    /// <param name="definition">Canonical finite catalog definition.</param>
    /// <param name="fingerprint">Persisted fingerprint of exact catalog content and provenance.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="definition"/> or <paramref name="fingerprint"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The schema is unsupported, the catalog is invalid, or the fingerprint does not match canonical content.
    /// </exception>
    [JsonConstructor]
    public GenerationCatalogDocument(
        string schemaVersion,
        GenerationCatalogDefinition definition,
        GenerationCatalogFingerprint fingerprint)
        : this(ValidateAndNormalize(schemaVersion, definition, fingerprint))
    {
    }

    GenerationCatalogDocument(CatalogState state)
    {
        SchemaVersion = state.SchemaVersion;
        Definition = state.Definition;
        Fingerprint = state.Fingerprint;
    }

    /// <summary>Gets the exact portable generation-catalog schema.</summary>
    public string SchemaVersion { get; }

    /// <summary>Gets the normalized finite catalog definition.</summary>
    public GenerationCatalogDefinition Definition { get; }

    /// <summary>Gets the fingerprint of exact catalog content and provenance.</summary>
    public GenerationCatalogFingerprint Fingerprint { get; }

    /// <summary>Creates a current-version portable document from one valid catalog definition.</summary>
    /// <param name="definition">Catalog definition to validate, normalize, and retain.</param>
    /// <returns>A current-version fingerprinted generation-catalog document.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="definition"/> is invalid.</exception>
    public static GenerationCatalogDocument FromDefinition(GenerationCatalogDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return new(CreateState(definition));
    }

    static CatalogState ValidateAndNormalize(
        string schemaVersion,
        GenerationCatalogDefinition definition,
        GenerationCatalogFingerprint fingerprint)
    {
        schemaVersion = Guard.RequireNotNullOrWhiteSpace(schemaVersion);
        if (!string.Equals(schemaVersion, CurrentSchemaVersion, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Generation-catalog schema '{schemaVersion}' is unsupported; expected '{CurrentSchemaVersion}'.",
                nameof(schemaVersion));
        }

        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(fingerprint);
        var state = CreateState(definition);
        if (fingerprint != state.Fingerprint)
        {
            throw new ArgumentException(
                "The supplied generation-catalog fingerprint does not match canonical content and provenance.",
                nameof(fingerprint));
        }

        return state;
    }

    static CatalogState CreateState(GenerationCatalogDefinition definition)
    {
        var normalized = NormalizeAndValidate(definition);
        return new(
            CurrentSchemaVersion,
            normalized,
            GenerationCatalogFingerprinter.ComputeNormalized(normalized));
    }

    static GenerationCatalogDefinition NormalizeAndValidate(GenerationCatalogDefinition definition)
    {
        if (definition.Entries.IsDefaultOrEmpty)
            throw new ArgumentException("A generation catalog requires at least one entry.", nameof(definition));

        var entries = ImmutableArray.CreateBuilder<GenerationCatalogEntry>(definition.Entries.Length);
        foreach (var entry in definition.Entries)
        {
            if (entry is null)
                throw new ArgumentException("A generation catalog cannot contain a null entry.", nameof(definition));
            entries.Add(entry);
        }

        entries.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.Id, right.Id));
        var contract = new ValueContract(definition.ValueType);
        var totalWeight = 0d;
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            if (index > 0 && string.Equals(entries[index - 1].Id, entry.Id, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"Generation catalog entry identity '{entry.Id}' is duplicated.",
                    nameof(definition));
            }
            if (!double.IsFinite(entry.Weight) || entry.Weight <= 0d)
            {
                throw new ArgumentException(
                    $"Generation catalog entry '{entry.Id}' requires a finite positive weight.",
                    nameof(definition));
            }
            if (!contract.IsSatisfiedByConstant(entry.Value))
            {
                throw new ArgumentException(
                    $"Generation catalog entry '{entry.Id}' does not satisfy the catalog value type.",
                    nameof(definition));
            }

            totalWeight += entry.Weight;
        }

        if (!double.IsFinite(totalWeight))
            throw new ArgumentException("Generation catalog entry weights require a finite total.", nameof(definition));

        var normalizedEntries = entries.MoveToImmutable();
        return definition.Entries.SequenceEqual(normalizedEntries)
            ? definition
            : new(
                definition.Id,
                definition.Revision,
                definition.ValueType,
                normalizedEntries,
                definition.Provenance);
    }

    readonly record struct CatalogState(
        string SchemaVersion,
        GenerationCatalogDefinition Definition,
        GenerationCatalogFingerprint Fingerprint);
}

/// <summary>Computes fingerprints for canonical finite generation catalogs.</summary>
public static class GenerationCatalogFingerprinter
{
    /// <summary>Computes a semantic fingerprint for one normalized catalog definition.</summary>
    /// <param name="definition">Catalog definition to validate, normalize, and fingerprint.</param>
    /// <returns>Fingerprint covering exact entries, types, and producer provenance.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="definition"/> is invalid.</exception>
    /// <exception cref="InvalidOperationException">Catalog content has no canonical JSON representation.</exception>
    /// <exception cref="JsonException">Catalog content violates the strict JSON contract.</exception>
    /// <exception cref="NotSupportedException">Catalog content contains an unsupported runtime type.</exception>
    public static GenerationCatalogFingerprint Compute(GenerationCatalogDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return GenerationCatalogDocument.FromDefinition(definition).Fingerprint;
    }

    internal static GenerationCatalogFingerprint ComputeNormalized(GenerationCatalogDefinition definition)
    {
        using SimulationFingerprintWriter writer = new();
        writer.Append(GenerationCatalogFingerprint.CurrentCanonicalization);
        writer.Append(StrictDocumentJson.GetCanonicalBytes(
            definition,
            GenerationCatalogJsonSerializer.CreateOptions()));
        return new(
            GenerationCatalogFingerprint.CurrentAlgorithm,
            GenerationCatalogFingerprint.CurrentCanonicalization,
            writer.Complete());
    }
}

/// <summary>Strict deterministic JSON boundary for portable generation-catalog documents.</summary>
public static class GenerationCatalogJsonSerializer
{
    const string ContractName = "generation-catalog document";

    /// <summary>Creates strict serializer options for the closed generation-catalog wire contract.</summary>
    /// <param name="formatting">Desired output formatting.</param>
    /// <returns>Strict case-sensitive portable-document options.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="formatting"/> is unsupported.</exception>
    public static JsonSerializerOptions CreateOptions(
        PortableDocumentJsonFormatting formatting = PortableDocumentJsonFormatting.Compact) =>
        StrictDocumentJson.CreateOptions(formatting);

    /// <summary>Serializes one verified portable generation-catalog document.</summary>
    /// <param name="document">Document to serialize.</param>
    /// <param name="formatting">Canonical compact or human-readable indented output.</param>
    /// <returns>Portable generation-catalog JSON.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="formatting"/> is unsupported.</exception>
    /// <exception cref="InvalidOperationException">Document content has no canonical JSON representation.</exception>
    /// <exception cref="JsonException">Document content violates the strict JSON contract.</exception>
    /// <exception cref="NotSupportedException">Document content contains an unsupported runtime type.</exception>
    public static string Serialize(
        GenerationCatalogDocument document,
        PortableDocumentJsonFormatting formatting = PortableDocumentJsonFormatting.Compact)
    {
        ArgumentNullException.ThrowIfNull(document);
        return formatting == PortableDocumentJsonFormatting.Compact
            ? Encoding.UTF8.GetString(GetCanonicalBytes(document))
            : JsonSerializer.Serialize(document, CreateOptions(formatting));
    }

    /// <summary>Validates, normalizes, and serializes one canonical generation catalog.</summary>
    /// <param name="definition">Generation catalog to persist.</param>
    /// <param name="formatting">Canonical compact or human-readable indented output.</param>
    /// <returns>Portable generation-catalog JSON.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="definition"/> is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="formatting"/> is unsupported.</exception>
    /// <exception cref="InvalidOperationException">Definition content has no canonical JSON representation.</exception>
    /// <exception cref="JsonException">Definition content violates the strict JSON contract.</exception>
    /// <exception cref="NotSupportedException">Definition content contains an unsupported runtime type.</exception>
    public static string Serialize(
        GenerationCatalogDefinition definition,
        PortableDocumentJsonFormatting formatting = PortableDocumentJsonFormatting.Compact) =>
        Serialize(GenerationCatalogDocument.FromDefinition(definition), formatting);

    /// <summary>Gets canonical UTF-8 JSON for one complete generation-catalog document.</summary>
    /// <param name="document">Document to serialize.</param>
    /// <returns>Canonical compact UTF-8 JSON.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">Document content has no canonical JSON representation.</exception>
    /// <exception cref="JsonException">Document content violates the strict JSON contract.</exception>
    /// <exception cref="NotSupportedException">Document content contains an unsupported runtime type.</exception>
    public static byte[] GetCanonicalBytes(GenerationCatalogDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return StrictDocumentJson.GetCanonicalBytes(document, CreateOptions());
    }

    /// <summary>Deserializes and validates one current-version generation-catalog document.</summary>
    /// <param name="json">Persisted generation-catalog JSON.</param>
    /// <returns>A normalized fingerprint-verified catalog document.</returns>
    /// <exception cref="JsonException">JSON or its semantic content is invalid or noncanonical.</exception>
    public static GenerationCatalogDocument Deserialize(string json)
    {
        var validation = TryDeserialize(json, out var document);
        if (validation.IsValid && document is not null)
            return document;

        throw new JsonException(string.Join(
            " | ",
            validation.Diagnostics.Select(static diagnostic =>
                $"{diagnostic.Code} at {diagnostic.Location}: {diagnostic.Message}")));
    }

    /// <summary>Attempts strict deserialization with structured diagnostics.</summary>
    /// <param name="json">Persisted generation-catalog JSON.</param>
    /// <param name="document">Validated document when successful; otherwise <see langword="null"/>.</param>
    /// <returns>Structured wire and semantic validation.</returns>
    public static DocumentValidationResult TryDeserialize(
        string json,
        out GenerationCatalogDocument? document)
    {
        if (StrictDocumentJson.TryReadCanonicalObject(
                json,
                CreateOptions(),
                ContractName,
                out document,
                out var error))
        {
            return DocumentValidationResult.Valid;
        }

        document = null;
        return StrictDocumentJson.Error(
            error.Failure switch
            {
                StrictDocumentJsonReadFailure.Empty => "simulation.generation.catalog.document.jsonEmpty",
                StrictDocumentJsonReadFailure.InvalidJson => "simulation.generation.catalog.document.jsonInvalid",
                StrictDocumentJsonReadFailure.RootInvalid => "simulation.generation.catalog.document.rootInvalid",
                StrictDocumentJsonReadFailure.DuplicateProperty => "simulation.generation.catalog.document.duplicateProperty",
                StrictDocumentJsonReadFailure.DeserializationInvalid => "simulation.generation.catalog.document.contentInvalid",
                StrictDocumentJsonReadFailure.DeserializationNull => "simulation.generation.catalog.document.contentMissing",
                StrictDocumentJsonReadFailure.WireNonCanonical => "simulation.generation.catalog.document.wireNonCanonical",
                _ => "simulation.generation.catalog.document.unknown"
            },
            error.Message,
            error.Location);
    }
}
