using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Simulation.Worlds;

/// <summary>Versioned deterministic identity of canonical world-definition content.</summary>
/// <remarks>
/// The fingerprint covers population identities, counts, exact nested generation coordinates and fingerprints,
/// exemplar identities and coordinates, and the population-scope convention. Logical world identity and revision
/// remain separate coordinates.
/// </remarks>
public sealed record WorldDefinitionFingerprint
{
    /// <summary>Cryptographic hash algorithm used by the current world-definition profile.</summary>
    public const string CurrentAlgorithm = "sha256";

    /// <summary>Canonicalization profile used by the current world-definition fingerprint.</summary>
    public const string CurrentCanonicalization = "cohesive-simulation-world/v3-c14n/v1";

    /// <summary>Creates world-definition fingerprint metadata.</summary>
    /// <param name="algorithm">Hash-algorithm identity.</param>
    /// <param name="canonicalization">Canonical world-definition profile identity.</param>
    /// <param name="value">Lowercase hexadecimal fingerprint value.</param>
    /// <exception cref="ArgumentNullException">A parameter is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A parameter is empty or white-space.</exception>
    [JsonConstructor]
    public WorldDefinitionFingerprint(string algorithm, string canonicalization, string value)
    {
        Algorithm = Guard.RequireNotNullOrWhiteSpace(algorithm);
        Canonicalization = Guard.RequireNotNullOrWhiteSpace(canonicalization);
        Value = Guard.RequireNotNullOrWhiteSpace(value);
    }

    /// <summary>Gets the hash-algorithm identity.</summary>
    public string Algorithm { get; }

    /// <summary>Gets the canonical world-definition profile identity.</summary>
    public string Canonicalization { get; }

    /// <summary>Gets the lowercase hexadecimal fingerprint value.</summary>
    public string Value { get; }
}

/// <summary>Portable self-validating envelope for one canonical world definition.</summary>
/// <remarks>
/// Population, exemplar, and nested generation binding/member declarations are normalized by stable identity.
/// Deserialization rejects wire order that would preserve a second non-semantic declaration order.
/// </remarks>
public sealed record WorldDefinitionDocument
{
    /// <summary>Current portable world-definition document schema.</summary>
    public const string CurrentSchemaVersion = "cohesive-simulation-world/v3";

    /// <summary>Creates or restores one portable world-definition document.</summary>
    /// <param name="schemaVersion">Exact portable world-definition schema.</param>
    /// <param name="definition">Canonical provider-neutral world definition.</param>
    /// <param name="fingerprint">Persisted fingerprint of the exact semantic content.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="definition"/> or <paramref name="fingerprint"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The schema is unsupported, the definition does not compile, or the fingerprint does not match current
    /// canonical semantic content.
    /// </exception>
    [JsonConstructor]
    public WorldDefinitionDocument(
        string schemaVersion,
        WorldDefinition definition,
        WorldDefinitionFingerprint fingerprint)
        : this(ValidateAndNormalize(schemaVersion, definition, fingerprint))
    {
    }

    WorldDefinitionDocument(
        (string SchemaVersion, WorldDefinition Definition, WorldDefinitionFingerprint Fingerprint) state)
    {
        SchemaVersion = state.SchemaVersion;
        Definition = state.Definition;
        Fingerprint = state.Fingerprint;
    }

    /// <summary>Gets the exact portable world-definition schema.</summary>
    public string SchemaVersion { get; }

    /// <summary>Gets the normalized provider-neutral world definition.</summary>
    public WorldDefinition Definition { get; }

    /// <summary>Gets the fingerprint of exact world semantic content.</summary>
    public WorldDefinitionFingerprint Fingerprint { get; }

    /// <summary>Creates a current-version portable document from one valid world definition.</summary>
    /// <param name="definition">World definition to validate, normalize, and persist.</param>
    /// <returns>A current-version document with a computed semantic fingerprint.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="definition"/> does not compile successfully.</exception>
    public static WorldDefinitionDocument FromDefinition(WorldDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return new(CreateState(definition));
    }

    internal static WorldDefinitionDocument FromCompiledPlan(CompiledWorldPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return new(CreateState(plan));
    }

    /// <summary>Compiles the validated document into isolated deterministic population streams.</summary>
    /// <returns>An immutable plan for the exact persisted world definition.</returns>
    /// <exception cref="InvalidOperationException">
    /// The retained definition no longer satisfies the document construction invariant.
    /// </exception>
    public CompiledWorldPlan Compile()
    {
        var result = WorldCompiler.Compile(Definition);
        return result.Plan ?? throw new InvalidOperationException(
            $"Validated world-definition document '{Definition.Id}' could not be recompiled.");
    }

    static (
        string SchemaVersion,
        WorldDefinition Definition,
        WorldDefinitionFingerprint Fingerprint) ValidateAndNormalize(
            string schemaVersion,
            WorldDefinition definition,
            WorldDefinitionFingerprint fingerprint)
    {
        schemaVersion = Guard.RequireNotNullOrWhiteSpace(schemaVersion);
        if (!string.Equals(schemaVersion, CurrentSchemaVersion, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"World-definition schema '{schemaVersion}' is unsupported; expected '{CurrentSchemaVersion}'.",
                nameof(schemaVersion));
        }

        ArgumentNullException.ThrowIfNull(definition);
        fingerprint = Guard.RequireNotNull(fingerprint);
        var state = CreateState(definition);
        if (fingerprint != state.Fingerprint)
        {
            throw new ArgumentException(
                "The supplied world-definition fingerprint does not match canonical semantic content.",
                nameof(fingerprint));
        }

        return state;
    }

    static (
        string SchemaVersion,
        WorldDefinition Definition,
        WorldDefinitionFingerprint Fingerprint) CreateState(WorldDefinition definition)
    {
        return CreateState(RequirePlan(definition));
    }

    static (
        string SchemaVersion,
        WorldDefinition Definition,
        WorldDefinitionFingerprint Fingerprint) CreateState(CompiledWorldPlan plan)
    {
        return (
            CurrentSchemaVersion,
            plan.Definition,
            new(
                plan.FingerprintAlgorithm,
                plan.FingerprintCanonicalization,
                plan.Fingerprint));
    }

    static CompiledWorldPlan RequirePlan(WorldDefinition definition)
    {
        var result = WorldCompiler.Compile(definition);
        if (result.Plan is not null)
            return result.Plan;

        var errors = result.Validation.Diagnostics
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}");
        throw new ArgumentException(
            $"World definition does not compile: {string.Join(" | ", errors)}",
            nameof(definition));
    }
}

/// <summary>Strict deterministic JSON boundary for portable world-definition documents.</summary>
public static class WorldDefinitionJsonSerializer
{
    const string ContractName = "world-definition document";

    /// <summary>Creates strict serializer options for the closed world-definition wire contract.</summary>
    /// <param name="formatting">Desired output formatting.</param>
    /// <returns>Strict case-sensitive portable-document options.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="formatting"/> is unsupported.</exception>
    public static JsonSerializerOptions CreateOptions(
        PortableDocumentJsonFormatting formatting = PortableDocumentJsonFormatting.Compact) =>
        StrictDocumentJson.CreateOptions(formatting);

    /// <summary>Serializes one verified portable world-definition document.</summary>
    /// <param name="document">Document to serialize.</param>
    /// <param name="formatting">Canonical compact or human-readable indented output.</param>
    /// <returns>Portable world-definition JSON.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="formatting"/> is unsupported.</exception>
    /// <exception cref="InvalidOperationException">Document content has no canonical JSON representation.</exception>
    /// <exception cref="JsonException">Document content violates the strict JSON contract.</exception>
    /// <exception cref="NotSupportedException">Document content contains an unsupported runtime type.</exception>
    public static string Serialize(
        WorldDefinitionDocument document,
        PortableDocumentJsonFormatting formatting = PortableDocumentJsonFormatting.Compact)
    {
        ArgumentNullException.ThrowIfNull(document);
        return formatting == PortableDocumentJsonFormatting.Compact
            ? Encoding.UTF8.GetString(GetCanonicalBytes(document))
            : JsonSerializer.Serialize(document, CreateOptions(formatting));
    }

    /// <summary>Validates, normalizes, and serializes one canonical world definition.</summary>
    /// <param name="definition">World definition to persist.</param>
    /// <param name="formatting">Canonical compact or human-readable indented output.</param>
    /// <returns>Portable world-definition JSON.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="definition"/> does not compile successfully.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="formatting"/> is unsupported.</exception>
    /// <exception cref="InvalidOperationException">Definition content has no canonical JSON representation.</exception>
    /// <exception cref="JsonException">Definition content violates the strict JSON contract.</exception>
    /// <exception cref="NotSupportedException">Definition content contains an unsupported runtime type.</exception>
    public static string Serialize(
        WorldDefinition definition,
        PortableDocumentJsonFormatting formatting = PortableDocumentJsonFormatting.Compact) =>
        Serialize(WorldDefinitionDocument.FromDefinition(definition), formatting);

    /// <summary>Gets canonical UTF-8 JSON for one complete world-definition document.</summary>
    /// <param name="document">Document to serialize.</param>
    /// <returns>Canonical compact UTF-8 JSON.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">Document content has no canonical JSON representation.</exception>
    /// <exception cref="JsonException">Document content violates the strict JSON contract.</exception>
    /// <exception cref="NotSupportedException">Document content contains an unsupported runtime type.</exception>
    public static byte[] GetCanonicalBytes(WorldDefinitionDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return StrictDocumentJson.GetCanonicalBytes(document, CreateOptions());
    }

    /// <summary>Deserializes and validates one current-version world-definition document.</summary>
    /// <param name="json">Persisted world-definition JSON.</param>
    /// <returns>A normalized fingerprint-verified document ready to compile.</returns>
    /// <exception cref="JsonException">
    /// JSON is empty, malformed, duplicated, noncanonical, unsupported, invalid, or fingerprint-inconsistent.
    /// </exception>
    public static WorldDefinitionDocument Deserialize(string json)
    {
        var validation = TryDeserialize(json, out var document);
        if (validation.IsValid && document is not null)
            return document;

        throw new JsonException(BuildFailureMessage(validation));
    }

    /// <summary>Attempts to deserialize and validate one world-definition document.</summary>
    /// <param name="json">Persisted world-definition JSON.</param>
    /// <param name="document">Receives the validated document when successful; otherwise <see langword="null"/>.</param>
    /// <returns>Structured wire, schema, compilation, and fingerprint diagnostics.</returns>
    public static DocumentValidationResult TryDeserialize(
        string json,
        out WorldDefinitionDocument? document)
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
            Code(error.Failure),
            error.Message,
            error.Location);
    }

    static string Code(StrictDocumentJsonReadFailure failure) => failure switch
    {
        StrictDocumentJsonReadFailure.Empty => "simulation.world.document.jsonEmpty",
        StrictDocumentJsonReadFailure.InvalidJson => "simulation.world.document.jsonInvalid",
        StrictDocumentJsonReadFailure.RootInvalid => "simulation.world.document.rootInvalid",
        StrictDocumentJsonReadFailure.DuplicateProperty => "simulation.world.document.duplicateProperty",
        StrictDocumentJsonReadFailure.DeserializationInvalid => "simulation.world.document.contentInvalid",
        StrictDocumentJsonReadFailure.DeserializationNull => "simulation.world.document.contentMissing",
        StrictDocumentJsonReadFailure.WireNonCanonical => "simulation.world.document.wireNonCanonical",
        _ => "simulation.world.document.unknown"
    };

    static string BuildFailureMessage(DocumentValidationResult validation) =>
        string.Join(
            " | ",
            validation.Diagnostics.Select(static diagnostic =>
                $"{diagnostic.Code} at {diagnostic.Location}: {diagnostic.Message}"));
}
