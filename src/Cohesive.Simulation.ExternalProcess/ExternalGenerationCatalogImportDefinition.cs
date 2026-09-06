using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Simulation.Generation;

namespace Cohesive.Simulation.ExternalProcess;

/// <summary>
/// Portable declarative inputs for one bounded external generation-catalog import.
/// </summary>
/// <remarks>
/// This definition owns reproducible catalog, provider, value-contract, and provenance inputs. Executable paths,
/// arguments, working directories, timeouts, and process byte limits are deliberately supplied separately at the
/// execution site. The successfully imported <see cref="GenerationCatalogDocument"/> becomes the retained
/// generation authority.
/// </remarks>
public sealed record ExternalGenerationCatalogImportDefinition
{
    /// <summary>Current portable external catalog-import definition schema.</summary>
    public const string CurrentSchemaVersion = "cohesive-simulation-external-generation-catalog-import/v1";

    /// <summary>Creates or restores one external catalog-import definition.</summary>
    /// <param name="schemaVersion">Exact portable definition schema.</param>
    /// <param name="catalogId">Stable logical catalog identity.</param>
    /// <param name="catalogRevision">Exact application-owned catalog revision.</param>
    /// <param name="count">Positive number of provider values to retain.</param>
    /// <param name="seed">Signed 64-bit producer-local seed.</param>
    /// <param name="valueType">Portable contract every provider response value must satisfy.</param>
    /// <param name="configuration">Provider-owned declarative configuration object.</param>
    /// <param name="provider">Stable external provider identity expected in the response.</param>
    /// <param name="providerVersion">Exact external provider version expected in the response.</param>
    /// <param name="randomAlgorithm">Exact provider random-algorithm or deterministic-seeding profile.</param>
    /// <param name="capabilityProfile">Versioned producer capability assertions and evidence.</param>
    /// <param name="sourceReferences">Exact application, script, or specification sources defining this import.</param>
    /// <param name="locale">Optional exact provider locale.</param>
    /// <param name="dateTimeReferenceUtc">Optional fixed UTC provider reference time.</param>
    /// <exception cref="ArgumentNullException">A required reference value is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// The schema is unsupported; an identity is empty; the count, value type, configuration, sources, or UTC
    /// reference is invalid; or capability assertions do not match the supplied production coordinates.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is not positive.</exception>
    [JsonConstructor]
    public ExternalGenerationCatalogImportDefinition(
        string schemaVersion,
        string catalogId,
        string catalogRevision,
        int count,
        long seed,
        TypeRef valueType,
        JsonElement configuration,
        string provider,
        string providerVersion,
        string randomAlgorithm,
        GenerationCatalogCapabilityProfile capabilityProfile,
        ImmutableArray<SourceReference> sourceReferences,
        string? locale = null,
        DateTimeOffset? dateTimeReferenceUtc = null)
    {
        schemaVersion = Guard.RequireNotNullOrWhiteSpace(schemaVersion);
        SchemaVersion = string.Equals(schemaVersion, CurrentSchemaVersion, StringComparison.Ordinal)
            ? schemaVersion
            : throw new ArgumentException(
                $"External generation-catalog import schema '{schemaVersion}' is unsupported; expected "
                + $"'{CurrentSchemaVersion}'.",
                nameof(schemaVersion));
        CatalogId = Guard.RequireNotNullOrWhiteSpace(catalogId);
        CatalogRevision = Guard.RequireNotNullOrWhiteSpace(catalogRevision);
        Count = count > 0
            ? count
            : throw new ArgumentOutOfRangeException(
                nameof(count),
                count,
                "An external catalog import requires at least one value.");
        Seed = seed;
        ValueType = RequirePortable(valueType);
        Configuration = ExternalGenerationCatalogProtocol.NormalizeConfiguration(
            configuration,
            nameof(configuration));
        Provider = Guard.RequireNotNullOrWhiteSpace(provider);
        ProviderVersion = Guard.RequireNotNullOrWhiteSpace(providerVersion);
        RandomAlgorithm = Guard.RequireNotNullOrWhiteSpace(randomAlgorithm);
        CapabilityProfile = Guard.RequireNotNull(capabilityProfile);
        SourceReferences = SourceReference.NormalizeSet(sourceReferences, requireNonEmpty: true);
        Locale = ExternalGenerationCatalogProtocol.NormalizeOptionalCoordinate(locale, nameof(locale));
        DateTimeReferenceUtc = ExternalGenerationCatalogProtocol.RequireUtcDateTimeReference(
            dateTimeReferenceUtc,
            nameof(dateTimeReferenceUtc));

        _ = CreateProvenance();
    }

    /// <summary>Gets the exact portable definition schema.</summary>
    public string SchemaVersion { get; }

    /// <summary>Gets the stable logical catalog identity.</summary>
    public string CatalogId { get; }

    /// <summary>Gets the exact application-owned catalog revision.</summary>
    public string CatalogRevision { get; }

    /// <summary>Gets the positive number of provider values retained.</summary>
    public int Count { get; }

    /// <summary>Gets the signed 64-bit producer-local seed.</summary>
    [JsonConverter(typeof(StringEncodedInt64JsonConverter))]
    public long Seed { get; }

    /// <summary>Gets the portable contract every provider response value must satisfy.</summary>
    public TypeRef ValueType { get; }

    /// <summary>Gets a detached provider-owned declarative configuration object.</summary>
    public JsonElement Configuration { get; }

    /// <summary>Gets the stable external provider identity expected in every response.</summary>
    public string Provider { get; }

    /// <summary>Gets the exact external provider version expected in every response.</summary>
    public string ProviderVersion { get; }

    /// <summary>Gets the exact provider random-algorithm or deterministic-seeding profile.</summary>
    public string RandomAlgorithm { get; }

    /// <summary>Gets versioned producer capability assertions and evidence.</summary>
    public GenerationCatalogCapabilityProfile CapabilityProfile { get; }

    /// <summary>Gets normalized application, script, or specification sources defining this import.</summary>
    public ImmutableArray<SourceReference> SourceReferences { get; }

    /// <summary>Gets the exact provider locale when locale selection is requested.</summary>
    public string? Locale { get; }

    /// <summary>Gets the fixed UTC provider reference time when one is requested.</summary>
    public DateTimeOffset? DateTimeReferenceUtc { get; }

    /// <summary>Creates a current-version definition from explicit semantic import inputs.</summary>
    /// <param name="catalogId">Stable logical catalog identity.</param>
    /// <param name="catalogRevision">Exact application-owned catalog revision.</param>
    /// <param name="count">Positive number of provider values to retain.</param>
    /// <param name="seed">Signed 64-bit producer-local seed.</param>
    /// <param name="valueType">Portable contract every provider response value must satisfy.</param>
    /// <param name="configuration">Provider-owned declarative configuration object.</param>
    /// <param name="provider">Stable external provider identity expected in the response.</param>
    /// <param name="providerVersion">Exact external provider version expected in the response.</param>
    /// <param name="randomAlgorithm">Exact provider random-algorithm or deterministic-seeding profile.</param>
    /// <param name="capabilityProfile">Versioned producer capability assertions and evidence.</param>
    /// <param name="sourceReferences">Exact application, script, or specification sources defining this import.</param>
    /// <param name="locale">Optional exact provider locale.</param>
    /// <param name="dateTimeReferenceUtc">Optional fixed UTC provider reference time.</param>
    /// <returns>A validated current-version portable import definition.</returns>
    /// <exception cref="ArgumentNullException">A required reference value is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An input or capability-coordinate combination is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is not positive.</exception>
    public static ExternalGenerationCatalogImportDefinition Create(
        string catalogId,
        string catalogRevision,
        int count,
        long seed,
        TypeRef valueType,
        JsonElement configuration,
        string provider,
        string providerVersion,
        string randomAlgorithm,
        GenerationCatalogCapabilityProfile capabilityProfile,
        ImmutableArray<SourceReference> sourceReferences,
        string? locale = null,
        DateTimeOffset? dateTimeReferenceUtc = null) =>
        new(
            CurrentSchemaVersion,
            catalogId,
            catalogRevision,
            count,
            seed,
            valueType,
            configuration,
            provider,
            providerVersion,
            randomAlgorithm,
            capabilityProfile,
            sourceReferences,
            locale,
            dateTimeReferenceUtc);

    internal ExternalGenerationCatalogRequest CreateRequest() =>
        ExternalGenerationCatalogProtocol.CreateRequest(
            CatalogId,
            CatalogRevision,
            Count,
            Seed,
            ValueType,
            Configuration,
            Locale,
            DateTimeReferenceUtc);

    internal GenerationCatalogProvenance CreateProvenance(string? requestId = null)
    {
        var sources = requestId is null
            ? SourceReferences
            : SourceReferences.Add(SourceReference.Create(
                ExternalGenerationCatalogProtocol.RequestReferenceScheme,
                requestId));
        return new(
            adapter: ExternalGenerationCatalogImporter.AdapterIdentity,
            adapterVersion: ExternalGenerationCatalogImporter.AdapterVersion,
            provider: Provider,
            providerVersion: ProviderVersion,
            capabilityProfile: CapabilityProfile,
            locale: Locale,
            randomAlgorithm: RandomAlgorithm,
            seed: Seed.ToString(CultureInfo.InvariantCulture),
            dateTimeReferenceUtc: DateTimeReferenceUtc,
            sourceReferences: sources);
    }

    static TypeRef RequirePortable(TypeRef valueType)
    {
        ArgumentNullException.ThrowIfNull(valueType);
        return IsPortable(valueType)
            ? valueType
            : throw new ArgumentException(
                "An external catalog import requires a portable value type without opaque runtime references.",
                nameof(valueType));
    }

    static bool IsPortable(TypeRef type) => type switch
    {
        OpaqueRuntimeTypeRef => false,
        ArrayTypeRef array => IsPortable(array.ElementType),
        ObjectTypeRef obj => obj.Fields.All(static field => IsPortable(field.Type)),
        _ => true
    };
}

/// <summary>Strict deterministic JSON boundary for portable external catalog-import definitions.</summary>
public static class ExternalGenerationCatalogImportJsonSerializer
{
    const string ContractName = "external generation-catalog import definition";

    /// <summary>Creates strict serializer options for the closed import-definition wire contract.</summary>
    /// <param name="formatting">Desired output formatting.</param>
    /// <returns>Strict case-sensitive portable-document options.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="formatting"/> is unsupported.</exception>
    public static JsonSerializerOptions CreateOptions(
        PortableDocumentJsonFormatting formatting = PortableDocumentJsonFormatting.Compact) =>
        StrictDocumentJson.CreateOptions(formatting);

    /// <summary>Serializes one validated external catalog-import definition.</summary>
    /// <param name="definition">Definition to serialize.</param>
    /// <param name="formatting">Canonical compact or human-readable indented output.</param>
    /// <returns>Portable external catalog-import definition JSON.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="formatting"/> is unsupported.</exception>
    /// <exception cref="InvalidOperationException">Definition content has no canonical JSON representation.</exception>
    /// <exception cref="JsonException">Definition content violates the strict JSON contract.</exception>
    /// <exception cref="NotSupportedException">Definition content contains an unsupported runtime type.</exception>
    public static string Serialize(
        ExternalGenerationCatalogImportDefinition definition,
        PortableDocumentJsonFormatting formatting = PortableDocumentJsonFormatting.Compact)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return formatting == PortableDocumentJsonFormatting.Compact
            ? Encoding.UTF8.GetString(GetCanonicalBytes(definition))
            : JsonSerializer.Serialize(definition, CreateOptions(formatting));
    }

    /// <summary>Gets canonical UTF-8 JSON for one complete import definition.</summary>
    /// <param name="definition">Definition to serialize.</param>
    /// <returns>Canonical compact UTF-8 JSON.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">Definition content has no canonical JSON representation.</exception>
    /// <exception cref="JsonException">Definition content violates the strict JSON contract.</exception>
    /// <exception cref="NotSupportedException">Definition content contains an unsupported runtime type.</exception>
    public static byte[] GetCanonicalBytes(ExternalGenerationCatalogImportDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return StrictDocumentJson.GetCanonicalBytes(definition, CreateOptions());
    }

    /// <summary>Deserializes and validates one current-version external catalog-import definition.</summary>
    /// <param name="json">Persisted import-definition JSON.</param>
    /// <returns>A validated portable import definition.</returns>
    /// <exception cref="JsonException">JSON or its semantic content is invalid or noncanonical.</exception>
    public static ExternalGenerationCatalogImportDefinition Deserialize(string json)
    {
        var validation = TryDeserialize(json, out var definition);
        if (validation.IsValid && definition is not null)
            return definition;

        throw new JsonException(string.Join(
            " | ",
            validation.Diagnostics.Select(static diagnostic =>
                $"{diagnostic.Code} at {diagnostic.Location}: {diagnostic.Message}")));
    }

    /// <summary>Attempts strict deserialization with structured diagnostics.</summary>
    /// <param name="json">Persisted import-definition JSON.</param>
    /// <param name="definition">Validated definition when successful; otherwise <see langword="null"/>.</param>
    /// <returns>Structured wire and semantic validation.</returns>
    public static DocumentValidationResult TryDeserialize(
        string json,
        out ExternalGenerationCatalogImportDefinition? definition)
    {
        if (StrictDocumentJson.TryReadCanonicalObject(
                json,
                CreateOptions(),
                ContractName,
                out definition,
                out var error))
        {
            return DocumentValidationResult.Valid;
        }

        definition = null;
        return StrictDocumentJson.Error(
            error.Failure switch
            {
                StrictDocumentJsonReadFailure.Empty =>
                    "simulation.generation.catalog.externalImport.document.jsonEmpty",
                StrictDocumentJsonReadFailure.InvalidJson =>
                    "simulation.generation.catalog.externalImport.document.jsonInvalid",
                StrictDocumentJsonReadFailure.RootInvalid =>
                    "simulation.generation.catalog.externalImport.document.rootInvalid",
                StrictDocumentJsonReadFailure.DuplicateProperty =>
                    "simulation.generation.catalog.externalImport.document.duplicateProperty",
                StrictDocumentJsonReadFailure.DeserializationInvalid =>
                    "simulation.generation.catalog.externalImport.document.contentInvalid",
                StrictDocumentJsonReadFailure.DeserializationNull =>
                    "simulation.generation.catalog.externalImport.document.contentMissing",
                StrictDocumentJsonReadFailure.WireNonCanonical =>
                    "simulation.generation.catalog.externalImport.document.wireNonCanonical",
                _ => "simulation.generation.catalog.externalImport.document.unknown"
            },
            error.Message,
            error.Location);
    }
}
