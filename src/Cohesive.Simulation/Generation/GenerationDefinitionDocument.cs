using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Simulation.Generation;

/// <summary>Versioned deterministic identity of canonical generation-definition content.</summary>
/// <remarks>
/// The fingerprint covers the exact governing shape graph, output shape, stable member identities, generator nodes,
/// and generator parameters. Logical definition identity and revision remain separate replay coordinates. Computing a
/// fingerprint is a definition-compilation and document-integrity operation: it materializes canonical shape-graph
/// bytes once per compilation and is not part of per-sample generation.
/// </remarks>
public sealed record GenerationDefinitionFingerprint
{
    /// <summary>Cryptographic hash algorithm used by the current generation-definition profile.</summary>
    public const string CurrentAlgorithm = "sha256";

    /// <summary>Canonicalization profile used by the current generation-definition fingerprint.</summary>
    public const string CurrentCanonicalization = "cohesive-generation/v1-c14n/v2";

    /// <summary>Creates generation-definition fingerprint metadata.</summary>
    /// <param name="algorithm">Hash-algorithm identity.</param>
    /// <param name="canonicalization">Canonical generation-definition profile identity.</param>
    /// <param name="value">Lowercase hexadecimal fingerprint value.</param>
    /// <exception cref="ArgumentNullException">A parameter is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A parameter is empty or white space.</exception>
    [JsonConstructor]
    public GenerationDefinitionFingerprint(string algorithm, string canonicalization, string value)
    {
        Algorithm = Guard.RequireNotNullOrWhiteSpace(algorithm);
        Canonicalization = Guard.RequireNotNullOrWhiteSpace(canonicalization);
        Value = Guard.RequireNotNullOrWhiteSpace(value);
    }

    /// <summary>Gets the hash-algorithm identity.</summary>
    public string Algorithm { get; }

    /// <summary>Gets the canonical generation-definition profile identity.</summary>
    public string Canonicalization { get; }

    /// <summary>Gets the lowercase hexadecimal fingerprint value.</summary>
    public string Value { get; }
}

/// <summary>Portable, self-validating envelope for one canonical generation definition.</summary>
/// <remarks>
/// Member declarations are normalized by stable semantic identity. Deserialization therefore rejects documents whose
/// wire order would preserve a second, non-semantic declaration order.
/// </remarks>
public sealed record GenerationDefinitionDocument
{
    /// <summary>Current portable generation-definition document schema.</summary>
    public const string CurrentSchemaVersion = "cohesive-simulation-generation/v1";

    /// <summary>Creates or restores one portable generation-definition document.</summary>
    /// <param name="schemaVersion">Exact portable generation-definition schema.</param>
    /// <param name="definition">Canonical provider-neutral generation definition.</param>
    /// <param name="fingerprint">Persisted fingerprint of the exact semantic content.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="definition"/> or <paramref name="fingerprint"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The schema is empty or unsupported, the definition does not compile, or the fingerprint is incompatible with
    /// the current algorithm, canonicalization profile, or semantic content.
    /// </exception>
    [JsonConstructor]
    public GenerationDefinitionDocument(
        string schemaVersion,
        GenerationDefinition definition,
        GenerationDefinitionFingerprint fingerprint)
        : this(ValidateAndNormalize(schemaVersion, definition, fingerprint))
    {
    }

    GenerationDefinitionDocument(
        (string SchemaVersion, GenerationDefinition Definition, GenerationDefinitionFingerprint Fingerprint) state)
    {
        SchemaVersion = state.SchemaVersion;
        Definition = state.Definition;
        Fingerprint = state.Fingerprint;
    }

    /// <summary>Gets the exact portable generation-definition schema.</summary>
    public string SchemaVersion { get; }

    /// <summary>Gets the normalized provider-neutral generation definition.</summary>
    public GenerationDefinition Definition { get; }

    /// <summary>Gets the fingerprint of the exact semantic content.</summary>
    public GenerationDefinitionFingerprint Fingerprint { get; }

    /// <summary>Creates a current-version portable document from one valid generation definition.</summary>
    /// <param name="definition">Canonical generation definition to validate, normalize, and persist.</param>
    /// <returns>A current-version document with a computed semantic fingerprint.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="definition"/> does not compile successfully.</exception>
    public static GenerationDefinitionDocument FromDefinition(GenerationDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return new(CreateState(definition));
    }

    /// <summary>Compiles the validated document for provider-neutral reference interpretation.</summary>
    /// <returns>An immutable plan for the exact persisted generation definition.</returns>
    /// <exception cref="InvalidOperationException">
    /// The retained definition no longer satisfies the document's construction invariant.
    /// </exception>
    public CompiledGenerationPlan Compile()
    {
        var result = GenerationCompiler.Compile(Definition);
        return result.Plan ?? throw new InvalidOperationException(
            $"Validated generation-definition document '{Definition.Id}' could not be recompiled.");
    }

    static CompiledGenerationPlan RequirePlan(GenerationDefinition definition)
    {
        var result = GenerationCompiler.Compile(definition);
        if (result.Plan is not null)
            return result.Plan;

        var errors = result.Validation.Diagnostics
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}");
        throw new ArgumentException(
            $"Generation definition does not compile: {string.Join(" | ", errors)}",
            nameof(definition));
    }

    static (string SchemaVersion, GenerationDefinition Definition, GenerationDefinitionFingerprint Fingerprint)
        ValidateAndNormalize(
            string schemaVersion,
            GenerationDefinition definition,
            GenerationDefinitionFingerprint fingerprint)
    {
        schemaVersion = Guard.RequireNotNullOrWhiteSpace(schemaVersion);
        if (!string.Equals(schemaVersion, CurrentSchemaVersion, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Generation-definition schema '{schemaVersion}' is unsupported; expected '{CurrentSchemaVersion}'.",
                nameof(schemaVersion));
        }

        ArgumentNullException.ThrowIfNull(definition);
        fingerprint = Guard.RequireNotNull(fingerprint);
        var state = CreateState(definition);
        if (fingerprint != state.Fingerprint)
        {
            throw new ArgumentException(
                "The supplied generation-definition fingerprint does not match canonical semantic content.",
                nameof(fingerprint));
        }

        return state;
    }

    static (string SchemaVersion, GenerationDefinition Definition, GenerationDefinitionFingerprint Fingerprint)
        CreateState(GenerationDefinition definition)
    {
        var plan = RequirePlan(definition);
        return (
            CurrentSchemaVersion,
            Normalize(definition, plan.Members),
            CreateFingerprint(plan));
    }

    static GenerationDefinition Normalize(
        GenerationDefinition definition,
        ImmutableArray<RecordGenerationMember> orderedMembers)
    {
        if (definition.Root.Members.SequenceEqual(orderedMembers))
            return definition;

        return new(
            definition.Id,
            definition.Revision,
            definition.ShapeGraph,
            new(definition.Root.ShapeId, orderedMembers));
    }

    static GenerationDefinitionFingerprint CreateFingerprint(CompiledGenerationPlan plan) => new(
        plan.FingerprintAlgorithm,
        plan.FingerprintCanonicalization,
        plan.Fingerprint);
}

/// <summary>Strict deterministic JSON boundary for portable generation-definition documents.</summary>
public static class GenerationDefinitionJsonSerializer
{
    const string ContractName = "generation-definition document";

    /// <summary>Creates strict serializer options for the closed generation-definition wire contract.</summary>
    /// <param name="formatting">Desired output formatting.</param>
    /// <returns>Strict, case-sensitive portable-document options.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="formatting"/> is unsupported.</exception>
    public static JsonSerializerOptions CreateOptions(
        PortableDocumentJsonFormatting formatting = PortableDocumentJsonFormatting.Compact) =>
        StrictDocumentJson.CreateOptions(formatting);

    /// <summary>Serializes one verified portable generation-definition document.</summary>
    /// <param name="document">Document to serialize.</param>
    /// <param name="formatting">Canonical compact or human-readable indented output.</param>
    /// <returns>Portable generation-definition JSON.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="formatting"/> is unsupported.</exception>
    /// <exception cref="InvalidOperationException">Document content has no canonical JSON representation.</exception>
    /// <exception cref="JsonException">Document content violates the strict JSON contract.</exception>
    /// <exception cref="NotSupportedException">Document content contains an unsupported runtime type.</exception>
    public static string Serialize(
        GenerationDefinitionDocument document,
        PortableDocumentJsonFormatting formatting = PortableDocumentJsonFormatting.Compact)
    {
        ArgumentNullException.ThrowIfNull(document);
        return formatting == PortableDocumentJsonFormatting.Compact
            ? Encoding.UTF8.GetString(GetCanonicalBytes(document))
            : JsonSerializer.Serialize(document, CreateOptions(formatting));
    }

    /// <summary>Validates, normalizes, and serializes one canonical generation definition.</summary>
    /// <param name="definition">Generation definition to persist.</param>
    /// <param name="formatting">Canonical compact or human-readable indented output.</param>
    /// <returns>Portable generation-definition JSON.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="definition"/> does not compile successfully.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="formatting"/> is unsupported.</exception>
    /// <exception cref="InvalidOperationException">Definition content has no canonical JSON representation.</exception>
    /// <exception cref="JsonException">Definition content violates the strict JSON contract.</exception>
    /// <exception cref="NotSupportedException">Definition content contains an unsupported runtime type.</exception>
    public static string Serialize(
        GenerationDefinition definition,
        PortableDocumentJsonFormatting formatting = PortableDocumentJsonFormatting.Compact) =>
        Serialize(GenerationDefinitionDocument.FromDefinition(definition), formatting);

    /// <summary>Gets canonical UTF-8 JSON for one complete generation-definition document.</summary>
    /// <param name="document">Document to serialize.</param>
    /// <returns>Canonical compact UTF-8 JSON.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">Document content has no canonical JSON representation.</exception>
    /// <exception cref="JsonException">Document content violates the strict JSON contract.</exception>
    /// <exception cref="NotSupportedException">Document content contains an unsupported runtime type.</exception>
    public static byte[] GetCanonicalBytes(GenerationDefinitionDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return StrictDocumentJson.GetCanonicalBytes(document, CreateOptions());
    }

    /// <summary>Deserializes and validates one current-version generation-definition document.</summary>
    /// <param name="json">Persisted generation-definition JSON.</param>
    /// <returns>A normalized, fingerprint-verified document ready to compile.</returns>
    /// <exception cref="JsonException">
    /// JSON is empty, malformed, duplicated, noncanonical, unsupported, invalid, or fingerprint-inconsistent.
    /// </exception>
    public static GenerationDefinitionDocument Deserialize(string json)
    {
        var validation = TryDeserialize(json, out var document);
        if (validation.IsValid && document is not null)
            return document;

        throw new JsonException(BuildFailureMessage(validation));
    }

    /// <summary>Attempts to deserialize and validate one generation-definition document.</summary>
    /// <param name="json">Persisted generation-definition JSON.</param>
    /// <param name="document">Receives the validated document when successful; otherwise <see langword="null"/>.</param>
    /// <returns>Structured wire, schema, compilation, and fingerprint diagnostics.</returns>
    public static DocumentValidationResult TryDeserialize(
        string json,
        out GenerationDefinitionDocument? document)
    {
        if (StrictDocumentJson.TryReadCanonicalObject(
                json,
                CreateOptions(),
                ContractName,
                out document,
                out var error))
        {
            return new DocumentValidationResult([]);
        }

        document = null;
        return StrictDocumentJson.Error(
            Code(error.Failure),
            error.Message,
            error.Location);
    }

    static string Code(StrictDocumentJsonReadFailure failure) => failure switch
    {
        StrictDocumentJsonReadFailure.Empty => "simulation.generation.document.jsonEmpty",
        StrictDocumentJsonReadFailure.InvalidJson => "simulation.generation.document.jsonInvalid",
        StrictDocumentJsonReadFailure.RootInvalid => "simulation.generation.document.rootInvalid",
        StrictDocumentJsonReadFailure.DuplicateProperty => "simulation.generation.document.duplicateProperty",
        StrictDocumentJsonReadFailure.DeserializationInvalid => "simulation.generation.document.contentInvalid",
        StrictDocumentJsonReadFailure.DeserializationNull => "simulation.generation.document.contentMissing",
        StrictDocumentJsonReadFailure.WireNonCanonical => "simulation.generation.document.wireNonCanonical",
        _ => "simulation.generation.document.unknown"
    };

    static string BuildFailureMessage(DocumentValidationResult validation) =>
        string.Join(
            " | ",
            validation.Diagnostics.Select(static diagnostic =>
                $"{diagnostic.Code} at {diagnostic.Location}: {diagnostic.Message}"));
}
