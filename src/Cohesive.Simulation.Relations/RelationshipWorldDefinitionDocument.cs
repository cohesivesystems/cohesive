using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Simulation.Relations;

/// <summary>Versioned fingerprint metadata for one canonical relationship-world definition.</summary>
/// <remarks>
/// The fingerprint covers the canonical world semantic fingerprint, exact relationship-catalog fingerprint, and
/// normalized population bindings. Logical world identity and revision remain separate coordinates.
/// </remarks>
public sealed record RelationshipWorldDefinitionFingerprint
{
    /// <summary>Creates relationship-world fingerprint metadata.</summary>
    /// <param name="algorithm">Hash algorithm identity.</param>
    /// <param name="canonicalization">Canonicalization profile identity.</param>
    /// <param name="value">Lowercase hexadecimal fingerprint value.</param>
    /// <exception cref="ArgumentNullException">A coordinate is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A coordinate is empty or white-space.</exception>
    [JsonConstructor]
    public RelationshipWorldDefinitionFingerprint(string algorithm, string canonicalization, string value)
    {
        Algorithm = Guard.RequireNotNullOrWhiteSpace(algorithm);
        Canonicalization = Guard.RequireNotNullOrWhiteSpace(canonicalization);
        Value = Guard.RequireNotNullOrWhiteSpace(value);
    }

    /// <summary>Gets the hash algorithm identity.</summary>
    public string Algorithm { get; }

    /// <summary>Gets the canonicalization profile identity.</summary>
    public string Canonicalization { get; }

    /// <summary>Gets the lowercase hexadecimal fingerprint value.</summary>
    public string Value { get; }
}

/// <summary>Portable self-validating envelope for one relationship-linked simulation world.</summary>
public sealed record RelationshipWorldDefinitionDocument
{
    /// <summary>Current portable document schema version.</summary>
    public const string CurrentSchemaVersion = "cohesive-simulation-relations-world/v1";

    /// <summary>Creates or restores a portable relationship-world document.</summary>
    /// <param name="schemaVersion">Exact portable document schema.</param>
    /// <param name="definition">Canonical relationship-world definition.</param>
    /// <param name="fingerprint">Persisted fingerprint of exact semantic content.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="definition"/> or <paramref name="fingerprint"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The schema is unsupported, compilation fails, or the fingerprint does not match semantic content.
    /// </exception>
    [JsonConstructor]
    public RelationshipWorldDefinitionDocument(
        string schemaVersion,
        RelationshipWorldDefinition definition,
        RelationshipWorldDefinitionFingerprint fingerprint)
        : this(Validate(schemaVersion, definition, fingerprint))
    {
    }

    RelationshipWorldDefinitionDocument(
        (string Schema, RelationshipWorldDefinition Definition, RelationshipWorldDefinitionFingerprint Fingerprint) state)
    {
        SchemaVersion = state.Schema;
        Definition = state.Definition;
        Fingerprint = state.Fingerprint;
    }

    /// <summary>Gets the exact portable document schema.</summary>
    public string SchemaVersion { get; }

    /// <summary>Gets the normalized canonical definition.</summary>
    public RelationshipWorldDefinition Definition { get; }

    /// <summary>Gets the fingerprint of exact semantic content.</summary>
    public RelationshipWorldDefinitionFingerprint Fingerprint { get; }

    /// <summary>Creates a current-version document from a valid definition.</summary>
    /// <param name="definition">Definition to validate, normalize, and persist.</param>
    /// <returns>A current-version self-validating document.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="definition"/> does not compile.</exception>
    public static RelationshipWorldDefinitionDocument FromDefinition(RelationshipWorldDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return new(CreateState(RequirePlan(definition)));
    }

    /// <summary>Compiles this validated document.</summary>
    /// <returns>A relationship-aware executable plan.</returns>
    /// <exception cref="InvalidOperationException">The retained construction invariant can no longer be reproduced.</exception>
    public CompiledRelationshipWorldPlan Compile()
    {
        var result = RelationshipWorldCompiler.Compile(Definition);
        return result.Plan ?? throw new InvalidOperationException(
            $"Validated relationship-world document '{Definition.World.Id}' could not be recompiled.");
    }

    static (
        string Schema,
        RelationshipWorldDefinition Definition,
        RelationshipWorldDefinitionFingerprint Fingerprint) Validate(
        string schemaVersion,
        RelationshipWorldDefinition definition,
        RelationshipWorldDefinitionFingerprint fingerprint)
    {
        schemaVersion = Guard.RequireNotNullOrWhiteSpace(schemaVersion);
        if (!string.Equals(schemaVersion, CurrentSchemaVersion, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Relationship-world schema '{schemaVersion}' is unsupported; expected '{CurrentSchemaVersion}'.",
                nameof(schemaVersion));
        }

        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(fingerprint);
        var state = CreateState(RequirePlan(definition));
        if (fingerprint != state.Fingerprint)
        {
            throw new ArgumentException(
                "The supplied relationship-world fingerprint does not match canonical semantic content.",
                nameof(fingerprint));
        }
        return state;
    }

    static (
        string Schema,
        RelationshipWorldDefinition Definition,
        RelationshipWorldDefinitionFingerprint Fingerprint) CreateState(CompiledRelationshipWorldPlan plan) =>
        (
            CurrentSchemaVersion,
            plan.Definition,
            new(plan.FingerprintAlgorithm, plan.FingerprintCanonicalization, plan.Fingerprint));

    static CompiledRelationshipWorldPlan RequirePlan(RelationshipWorldDefinition definition)
    {
        var result = RelationshipWorldCompiler.Compile(definition);
        if (result.Plan is not null)
            return result.Plan;
        throw new ArgumentException(
            "Relationship-world definition does not compile: " + string.Join(
                " | ",
                result.Validation.Diagnostics
                    .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
                    .Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")),
            nameof(definition));
    }
}

/// <summary>Strict deterministic JSON boundary for portable relationship-world documents.</summary>
public static class RelationshipWorldDefinitionJsonSerializer
{
    const string ContractName = "relationship-world definition document";

    /// <summary>Serializes a verified portable relationship-world document.</summary>
    /// <param name="document">Document to serialize.</param>
    /// <param name="formatting">Compact canonical or indented output.</param>
    /// <returns>Portable relationship-world JSON.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="formatting"/> is unsupported.</exception>
    /// <exception cref="JsonException">The document cannot be serialized under the strict wire contract.</exception>
    public static string Serialize(
        RelationshipWorldDefinitionDocument document,
        PortableDocumentJsonFormatting formatting = PortableDocumentJsonFormatting.Compact)
    {
        ArgumentNullException.ThrowIfNull(document);
        return formatting == PortableDocumentJsonFormatting.Compact
            ? Encoding.UTF8.GetString(GetCanonicalBytes(document))
            : JsonSerializer.Serialize(document, CreateOptions(formatting));
    }

    /// <summary>Validates, normalizes, and serializes a canonical relationship-world definition.</summary>
    /// <param name="definition">Definition to persist.</param>
    /// <param name="formatting">Compact canonical or indented output.</param>
    /// <returns>Portable relationship-world JSON.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="definition"/> does not compile.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="formatting"/> is unsupported.</exception>
    /// <exception cref="JsonException">The document cannot be serialized under the strict wire contract.</exception>
    public static string Serialize(
        RelationshipWorldDefinition definition,
        PortableDocumentJsonFormatting formatting = PortableDocumentJsonFormatting.Compact) =>
        Serialize(RelationshipWorldDefinitionDocument.FromDefinition(definition), formatting);

    /// <summary>Gets canonical compact UTF-8 JSON for a complete document.</summary>
    /// <param name="document">Document to serialize.</param>
    /// <returns>Canonical compact UTF-8 JSON.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">The document cannot be serialized under the strict wire contract.</exception>
    public static byte[] GetCanonicalBytes(RelationshipWorldDefinitionDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return StrictDocumentJson.GetCanonicalBytes(document, CreateOptions());
    }

    /// <summary>Deserializes and verifies one current-version document.</summary>
    /// <param name="json">Persisted relationship-world JSON.</param>
    /// <returns>A normalized fingerprint-verified document.</returns>
    /// <exception cref="JsonException">The JSON or its semantic content is invalid or noncanonical.</exception>
    public static RelationshipWorldDefinitionDocument Deserialize(string json)
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
    /// <param name="json">Persisted relationship-world JSON.</param>
    /// <param name="document">Validated document when successful; otherwise <see langword="null"/>.</param>
    /// <returns>Structured wire and semantic validation.</returns>
    public static DocumentValidationResult TryDeserialize(
        string json,
        out RelationshipWorldDefinitionDocument? document)
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
                StrictDocumentJsonReadFailure.Empty => "simulation.relationshipWorld.document.jsonEmpty",
                StrictDocumentJsonReadFailure.InvalidJson => "simulation.relationshipWorld.document.jsonInvalid",
                StrictDocumentJsonReadFailure.RootInvalid => "simulation.relationshipWorld.document.rootInvalid",
                StrictDocumentJsonReadFailure.DuplicateProperty => "simulation.relationshipWorld.document.duplicateProperty",
                StrictDocumentJsonReadFailure.DeserializationInvalid => "simulation.relationshipWorld.document.contentInvalid",
                StrictDocumentJsonReadFailure.DeserializationNull => "simulation.relationshipWorld.document.contentMissing",
                StrictDocumentJsonReadFailure.WireNonCanonical => "simulation.relationshipWorld.document.wireNonCanonical",
                _ => "simulation.relationshipWorld.document.unknown"
            },
            error.Message,
            error.Location);
    }

    static JsonSerializerOptions CreateOptions(PortableDocumentJsonFormatting formatting = PortableDocumentJsonFormatting.Compact) =>
        StrictDocumentJson.CreateOptions(formatting);
}
