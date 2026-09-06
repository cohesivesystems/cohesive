using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cohesive.Model.Serialization;

namespace Cohesive.Execution;

/// <summary>
/// Produces normalized semantic bytes and deterministic fingerprints for execution definitions.
/// </summary>
/// <remarks>
/// The v1 profile writes compact UTF-8 JSON with ordinal object-key ordering and exact decimal-rational
/// JSON-number spelling. Every array is order-bearing at this shared envelope layer; producers must
/// normalize kind-specific set-like collections before materializing canonical IR. The fingerprint input
/// contains the execution IR schema version, definition kind, canonical block definition, and exact-versioned
/// extensions. Lifecycle identity, semantic revision identity, fingerprint metadata, display metadata,
/// provenance, source maps, and diagnostics are deliberately excluded. A failed extension retains its value
/// contract, failed state, and stable diagnostic code in semantic content while diagnostic prose and locations
/// remain attributable document observations outside semantic identity.
/// </remarks>
public static class ExecutionDefinitionFingerprinter
{
    /// <summary>Fingerprint algorithm identifier.</summary>
    public const string Algorithm = "sha256";

    /// <summary>Canonical execution-definition semantic-content profile identifier.</summary>
    public const string Canonicalization = "cohesive-execution-definition/v1-c14n/v1";

    /// <summary>Computes the semantic content fingerprint declared by a document.</summary>
    /// <param name="document">Portable execution-definition document to fingerprint.</param>
    /// <returns>The versioned SHA-256 fingerprint of normalized semantic content.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">Semantic content has no canonical JSON encoding.</exception>
    /// <exception cref="JsonException">Semantic content cannot be encoded using the strict JSON contract.</exception>
    /// <exception cref="NotSupportedException">Semantic content contains an unsupported runtime type.</exception>
    public static ExecutionDefinitionFingerprint Compute(ExecutionDefinitionDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return ComputeNormalized(
            document.Metadata.SchemaVersion,
            document.Kind,
            document.Definition,
            document.Extensions);
    }

    /// <summary>Computes a semantic content fingerprint from normalized definition components.</summary>
    /// <param name="schemaVersion">Exact shared execution IR schema version.</param>
    /// <param name="kind">Stable semantic definition family.</param>
    /// <param name="definition">Canonical block-specific definition JSON object.</param>
    /// <param name="extensions">Exact-versioned semantic extensions.</param>
    /// <returns>The versioned SHA-256 fingerprint of normalized semantic content.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="schemaVersion"/> or <paramref name="kind"/> is default,
    /// <paramref name="definition"/> is not a JSON object or contains duplicate properties, or
    /// <paramref name="extensions"/> is malformed.
    /// </exception>
    /// <exception cref="InvalidOperationException">Semantic content has no canonical JSON encoding.</exception>
    /// <exception cref="JsonException">Semantic content cannot be encoded using the strict JSON contract.</exception>
    /// <exception cref="NotSupportedException">Semantic content contains an unsupported runtime type.</exception>
    public static ExecutionDefinitionFingerprint Compute(
        ExecutionIrSchemaVersion schemaVersion,
        ExecutionDefinitionKind kind,
        JsonElement definition,
        ImmutableArray<ExecutionDefinitionExtension> extensions = default)
    {
        ValidateDefinitionComponents(schemaVersion, kind, definition);
        ValidateDefinitionProperties(definition);
        var normalizedExtensions = ExecutionDefinitionDocument.NormalizeExtensions(extensions);
        return ComputeNormalized(schemaVersion, kind, definition, normalizedExtensions);
    }

    internal static ExecutionDefinitionFingerprint ComputeNormalized(
        ExecutionIrSchemaVersion schemaVersion,
        ExecutionDefinitionKind kind,
        JsonElement canonicalDefinition,
        ImmutableArray<ExecutionDefinitionExtension> normalizedExtensions)
    {
        var normalized = GetNormalizedSemanticBytesCore(
            schemaVersion,
            kind,
            canonicalDefinition,
            normalizedExtensions);
        var digest = SHA256.HashData(normalized);
        return new(
            algorithm: Algorithm,
            canonicalization: Canonicalization,
            value: Convert.ToHexStringLower(digest));
    }

    /// <summary>Gets the exact normalized semantic bytes hashed for a document.</summary>
    /// <param name="document">Portable execution-definition document to normalize.</param>
    /// <returns>Canonical UTF-8 JSON containing only semantic fingerprint inputs.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">Semantic content has no canonical JSON encoding.</exception>
    /// <exception cref="JsonException">Semantic content cannot be encoded using the strict JSON contract.</exception>
    /// <exception cref="NotSupportedException">Semantic content contains an unsupported runtime type.</exception>
    public static byte[] GetNormalizedSemanticBytes(ExecutionDefinitionDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return GetNormalizedSemanticBytesCore(
            document.Metadata.SchemaVersion,
            document.Kind,
            document.Definition,
            document.Extensions);
    }

    /// <summary>Gets the exact normalized semantic bytes hashed by the v1 profile.</summary>
    /// <param name="schemaVersion">Exact shared execution IR schema version.</param>
    /// <param name="kind">Stable semantic definition family.</param>
    /// <param name="definition">Canonical block-specific definition JSON object.</param>
    /// <param name="extensions">Exact-versioned semantic extensions.</param>
    /// <returns>Canonical UTF-8 JSON containing only semantic fingerprint inputs.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="schemaVersion"/> or <paramref name="kind"/> is default,
    /// <paramref name="definition"/> is not a JSON object or contains duplicate properties, or
    /// <paramref name="extensions"/> is malformed.
    /// </exception>
    /// <exception cref="InvalidOperationException">Semantic content has no canonical JSON encoding.</exception>
    /// <exception cref="JsonException">Semantic content cannot be encoded using the strict JSON contract.</exception>
    /// <exception cref="NotSupportedException">Semantic content contains an unsupported runtime type.</exception>
    public static byte[] GetNormalizedSemanticBytes(
        ExecutionIrSchemaVersion schemaVersion,
        ExecutionDefinitionKind kind,
        JsonElement definition,
        ImmutableArray<ExecutionDefinitionExtension> extensions = default)
    {
        ValidateDefinitionComponents(schemaVersion, kind, definition);
        ValidateDefinitionProperties(definition);
        var normalizedExtensions = ExecutionDefinitionDocument.NormalizeExtensions(extensions);
        return GetNormalizedSemanticBytesCore(
            schemaVersion,
            kind,
            definition,
            normalizedExtensions);
    }

    internal static JsonElement NormalizeDefinition(JsonElement definition)
    {
        if (definition.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException(
                "Canonical execution-definition content must be a JSON object.",
                nameof(definition));
        }
        ValidateDefinitionProperties(definition);

        var options = ExecutionDefinitionJsonSerializer.CreateOptions();
        var definitionNode = JsonNode.Parse(definition.GetRawText())
            ?? throw new InvalidOperationException("Failed to materialize canonical execution-definition JSON.");
        var canonical = CanonicalJsonWriter.GetCanonicalSequenceBytes(
            definitionNode,
            options,
            numberSemantics: CanonicalJsonNumberSemantics.ExactDecimalRational);
        using var document = JsonDocument.Parse(canonical);
        return document.RootElement.Clone();
    }

    static byte[] GetNormalizedSemanticBytesCore(
        ExecutionIrSchemaVersion schemaVersion,
        ExecutionDefinitionKind kind,
        JsonElement canonicalDefinition,
        ImmutableArray<ExecutionDefinitionExtension> normalizedExtensions)
    {
        ValidateDefinitionComponents(schemaVersion, kind, canonicalDefinition);
        if (normalizedExtensions.IsDefault)
            throw new ArgumentException("Normalized execution extensions must be initialized.", nameof(normalizedExtensions));

        var options = ExecutionDefinitionJsonSerializer.CreateOptions();
        var definitionNode = JsonNode.Parse(canonicalDefinition.GetRawText())
            ?? throw new InvalidOperationException("Failed to materialize canonical execution-definition JSON.");
        var extensionNode = CreateSemanticExtensionsNode(normalizedExtensions, options);
        JsonObject semanticContent = new()
        {
            ["schemaVersion"] = schemaVersion.Value,
            ["kind"] = kind.Value,
            ["definition"] = definitionNode,
            ["extensions"] = extensionNode
        };

        return CanonicalJsonWriter.GetCanonicalSequenceBytes(
            semanticContent,
            options,
            numberSemantics: CanonicalJsonNumberSemantics.ExactDecimalRational);
    }

    static JsonNode CreateSemanticExtensionsNode(
        ImmutableArray<ExecutionDefinitionExtension> extensions,
        JsonSerializerOptions options)
    {
        var node = JsonSerializer.SerializeToNode(extensions, options)
            ?? throw new InvalidOperationException("Failed to materialize canonical execution-extension JSON.");
        var array = node.AsArray();
        var valuePropertyName = options.PropertyNamingPolicy?.ConvertName(
            nameof(ExecutionDefinitionExtension.Value))
            ?? nameof(ExecutionDefinitionExtension.Value);
        for (var index = 0; index < extensions.Length; index++)
        {
            var value = extensions[index].Value;
            var valueNode = array[index]?[valuePropertyName]?.AsObject()
                ?? throw new InvalidOperationException("Failed to materialize a portable extension value.");
            PortableValueJsonConverter.ProjectSemanticFingerprint(valueNode, value);
        }

        return node;
    }

    static void ValidateDefinitionComponents(
        ExecutionIrSchemaVersion schemaVersion,
        ExecutionDefinitionKind kind,
        JsonElement definition)
    {
        if (string.IsNullOrWhiteSpace(schemaVersion.Value))
            throw new ArgumentException("Semantic content requires a non-default IR schema version.", nameof(schemaVersion));
        if (string.IsNullOrWhiteSpace(kind.Value))
            throw new ArgumentException("Semantic content requires a non-default definition kind.", nameof(kind));
        if (definition.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException(
                "Canonical execution-definition content must be a JSON object.",
                nameof(definition));
        }
    }

    static void ValidateDefinitionProperties(JsonElement definition)
    {
        if (StrictDocumentJson.TryFindDuplicateProperty(definition, string.Empty, out var duplicateLocation))
        {
            throw new ArgumentException(
                $"Canonical execution-definition content contains duplicate property '{duplicateLocation}'.",
                nameof(definition));
        }
    }
}
