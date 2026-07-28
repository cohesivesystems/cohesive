using System.Text;
using System.Text.Json;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Execution;

/// <summary>
/// Strict JSON serialization, schema gating, and pre-activation validation for execution definitions.
/// </summary>
public static class ExecutionDefinitionJsonSerializer
{
    static readonly ExecutionIrSchemaCompatibilityDeclaration SupportedSchemaVersions =
        new([ExecutionDefinitionDocument.CurrentSchemaVersion]);

    /// <summary>Creates serializer options for the closed execution-definition wire contract.</summary>
    /// <param name="formatting">Desired output formatting.</param>
    /// <returns>Strict, case-sensitive serializer options for execution-definition documents.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="formatting"/> is not recognized.</exception>
    public static JsonSerializerOptions CreateOptions(
        PortableDocumentJsonFormatting formatting = PortableDocumentJsonFormatting.Compact)
    {
        return StrictDocumentJson.CreateOptions(formatting);
    }

    /// <summary>Serializes an execution-definition document using the requested strict wire format.</summary>
    /// <param name="document">Portable execution-definition document to serialize.</param>
    /// <param name="formatting">Canonical compact or human-readable indented output.</param>
    /// <returns>Persisted execution-definition JSON.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="formatting"/> is not recognized.</exception>
    /// <exception cref="InvalidOperationException">The document has no canonical JSON encoding.</exception>
    /// <exception cref="JsonException">The document violates the strict JSON wire contract.</exception>
    /// <exception cref="NotSupportedException">The document contains an unsupported runtime type.</exception>
    public static string Serialize(
        ExecutionDefinitionDocument document,
        PortableDocumentJsonFormatting formatting = PortableDocumentJsonFormatting.Compact)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (formatting == PortableDocumentJsonFormatting.Compact)
            return Encoding.UTF8.GetString(GetCanonicalBytes(document));

        return JsonSerializer.Serialize(document, CreateOptions(formatting));
    }

    /// <summary>Gets canonical UTF-8 JSON for the complete persisted document.</summary>
    /// <param name="document">Portable execution-definition document to serialize.</param>
    /// <returns>Canonical UTF-8 JSON including semantic content and retained attribution metadata.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The document has no canonical JSON encoding.</exception>
    /// <exception cref="JsonException">The document violates the strict JSON wire contract.</exception>
    /// <exception cref="NotSupportedException">The document contains an unsupported runtime type.</exception>
    public static byte[] GetCanonicalBytes(ExecutionDefinitionDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var options = CreateOptions();
        var node = JsonSerializer.SerializeToNode(document, options)
            ?? throw new InvalidOperationException("Failed to materialize execution-definition document JSON.");
        return CanonicalJsonWriter.GetCanonicalBytes(
            node,
            options,
            static _ => CanonicalJsonArrayOrdering.Sequence,
            numberSemantics: CanonicalJsonNumberSemantics.ExactDecimalRational);
    }

    /// <summary>Deserializes and validates a current-version execution-definition document.</summary>
    /// <param name="json">Persisted execution-definition JSON.</param>
    /// <returns>A structurally valid, self-consistent current-version document.</returns>
    /// <exception cref="JsonException">The JSON cannot be read or the document fails integrity validation.</exception>
    public static ExecutionDefinitionDocument Deserialize(string json)
        => DeserializeCore(json, compatibility: null, graph: null);

    /// <summary>
    /// Deserializes and validates a current-version document using a graph that resolves portable value contracts.
    /// </summary>
    /// <param name="json">Persisted execution-definition JSON.</param>
    /// <param name="graph">Shared shape graph used to resolve named types and qualified shapes.</param>
    /// <returns>A structurally valid, self-consistent current-version document.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="graph"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">The JSON cannot be read or the document fails integrity validation.</exception>
    public static ExecutionDefinitionDocument Deserialize(string json, ShapeGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        return DeserializeCore(json, compatibility: null, graph);
    }

    /// <summary>
    /// Deserializes and validates a document against an interpreter's exact activation compatibility declaration.
    /// </summary>
    /// <param name="json">Persisted execution-definition JSON.</param>
    /// <param name="compatibility">Exact schema, kind, revision, fingerprint, and extension support.</param>
    /// <returns>A self-consistent document admitted for activation by <paramref name="compatibility"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="compatibility"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">
    /// The JSON cannot be read, fails integrity validation, or is incompatible with the interpreter.
    /// </exception>
    public static ExecutionDefinitionDocument Deserialize(
        string json,
        ExecutionDefinitionCompatibilityDeclaration compatibility)
    {
        ArgumentNullException.ThrowIfNull(compatibility);
        return DeserializeCore(json, compatibility, graph: null);
    }

    /// <summary>
    /// Deserializes and validates a document using contextual types and exact activation compatibility.
    /// </summary>
    /// <param name="json">Persisted execution-definition JSON.</param>
    /// <param name="compatibility">Exact schema, kind, revision, fingerprint, and extension support.</param>
    /// <param name="graph">Shared shape graph used to resolve named types and qualified shapes.</param>
    /// <returns>A self-consistent document admitted for activation by <paramref name="compatibility"/>.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="compatibility"/> or <paramref name="graph"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="JsonException">
    /// The JSON cannot be read, fails integrity validation, or is incompatible with the interpreter.
    /// </exception>
    public static ExecutionDefinitionDocument Deserialize(
        string json,
        ExecutionDefinitionCompatibilityDeclaration compatibility,
        ShapeGraph graph)
    {
        ArgumentNullException.ThrowIfNull(compatibility);
        ArgumentNullException.ThrowIfNull(graph);
        return DeserializeCore(json, compatibility, graph);
    }

    static ExecutionDefinitionDocument DeserializeCore(
        string json,
        ExecutionDefinitionCompatibilityDeclaration? compatibility,
        ShapeGraph? graph)
    {
        var validation = TryDeserializeCore(json, compatibility, graph, out var document);
        if (validation.IsValid && document is not null)
            return document;

        throw new JsonException(BuildFailureMessage(validation));
    }

    /// <summary>Attempts to read and self-validate a current-version execution-definition document.</summary>
    /// <param name="json">Persisted execution-definition JSON.</param>
    /// <param name="document">
    /// Receives the parsed document when structural deserialization succeeds, including when integrity
    /// validation subsequently fails; otherwise <see langword="null"/>.
    /// </param>
    /// <returns>Structured read and document-integrity diagnostics.</returns>
    public static DocumentValidationResult TryDeserialize(
        string json,
        out ExecutionDefinitionDocument? document) =>
        TryDeserializeCore(
            json,
            compatibility: null,
            graph: null,
            out document);

    /// <summary>
    /// Attempts to read and self-validate a document using a graph that resolves portable value contracts.
    /// </summary>
    /// <param name="json">Persisted execution-definition JSON.</param>
    /// <param name="graph">Shared shape graph used to resolve named types and qualified shapes.</param>
    /// <param name="document">
    /// Receives the parsed document when structural deserialization succeeds, including when integrity
    /// validation subsequently fails; otherwise <see langword="null"/>.
    /// </param>
    /// <returns>Structured read and document-integrity diagnostics.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="graph"/> is <see langword="null"/>.</exception>
    public static DocumentValidationResult TryDeserialize(
        string json,
        ShapeGraph graph,
        out ExecutionDefinitionDocument? document)
    {
        ArgumentNullException.ThrowIfNull(graph);
        return TryDeserializeCore(json, compatibility: null, graph, out document);
    }

    /// <summary>Attempts to read, self-validate, and admit an execution-definition document.</summary>
    /// <param name="json">Persisted execution-definition JSON.</param>
    /// <param name="compatibility">Exact compatibility declaration of the admitting interpreter.</param>
    /// <param name="document">
    /// Receives the parsed document when structural deserialization succeeds, including when integrity or
    /// compatibility validation subsequently fails; otherwise <see langword="null"/>.
    /// </param>
    /// <returns>Structured read, integrity, and activation-compatibility diagnostics.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="compatibility"/> is <see langword="null"/>.</exception>
    public static DocumentValidationResult TryDeserialize(
        string json,
        ExecutionDefinitionCompatibilityDeclaration compatibility,
        out ExecutionDefinitionDocument? document)
    {
        ArgumentNullException.ThrowIfNull(compatibility);
        return TryDeserializeCore(
            json,
            compatibility,
            graph: null,
            out document);
    }

    /// <summary>
    /// Attempts to read, contextually validate, and admit an execution-definition document.
    /// </summary>
    /// <param name="json">Persisted execution-definition JSON.</param>
    /// <param name="compatibility">Exact compatibility declaration of the admitting interpreter.</param>
    /// <param name="graph">Shared shape graph used to resolve named types and qualified shapes.</param>
    /// <param name="document">
    /// Receives the parsed document when structural deserialization succeeds, including when integrity or
    /// compatibility validation subsequently fails; otherwise <see langword="null"/>.
    /// </param>
    /// <returns>Structured read, integrity, and activation-compatibility diagnostics.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="compatibility"/> or <paramref name="graph"/> is <see langword="null"/>.
    /// </exception>
    public static DocumentValidationResult TryDeserialize(
        string json,
        ExecutionDefinitionCompatibilityDeclaration compatibility,
        ShapeGraph graph,
        out ExecutionDefinitionDocument? document)
    {
        ArgumentNullException.ThrowIfNull(compatibility);
        ArgumentNullException.ThrowIfNull(graph);
        return TryDeserializeCore(json, compatibility, graph, out document);
    }

    /// <summary>Deserializes a document's canonical payload as a block-specific definition type.</summary>
    /// <typeparam name="TDefinition">Portable block-specific definition type.</typeparam>
    /// <param name="document">Document whose canonical definition payload is projected.</param>
    /// <returns>The typed definition represented by the document payload.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">
    /// The payload cannot be decoded as <typeparamref name="TDefinition"/> or produces a null value.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// <typeparamref name="TDefinition"/> is not supported by the strict execution JSON contract.
    /// </exception>
    public static TDefinition DeserializeDefinition<TDefinition>(ExecutionDefinitionDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document.Definition.Deserialize<TDefinition>(CreateOptions())
            ?? throw new JsonException(
                $"Execution definition payload deserialized to null for '{typeof(TDefinition).FullName}'.");
    }

    static DocumentValidationResult TryDeserializeCore(
        string json,
        ExecutionDefinitionCompatibilityDeclaration? compatibility,
        ShapeGraph? graph,
        out ExecutionDefinitionDocument? document)
    {
        document = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            return Error(
                ExecutionDefinitionDiagnosticCodes.JsonEmpty,
                "Execution-definition document JSON cannot be empty.",
                "$");
        }

        JsonDocument parsed;
        try
        {
            parsed = JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            return Error(
                ExecutionDefinitionDiagnosticCodes.JsonInvalid,
                exception.Message,
                exception.Path ?? "$");
        }

        using (parsed)
        {
            var root = parsed.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Error(
                    ExecutionDefinitionDiagnosticCodes.DocumentRootInvalid,
                    "An execution-definition document must be a JSON object.",
                    "$");
            }
            if (StrictDocumentJson.TryFindDuplicateProperty(root, string.Empty, out var duplicateLocation))
            {
                return Error(
                    ExecutionDefinitionDiagnosticCodes.JsonDuplicateProperty,
                    "Canonical execution-definition JSON cannot contain duplicate object property names.",
                    duplicateLocation);
            }

            var envelopeValidation = ValidateEnvelope(root);
            if (!envelopeValidation.IsValid)
                return envelopeValidation;
        }

        try
        {
            document = JsonSerializer.Deserialize<ExecutionDefinitionDocument>(json, CreateOptions());
            if (document is null)
            {
                return Error(
                    ExecutionDefinitionDiagnosticCodes.DeserializationNull,
                    "JSON deserialized to a null execution-definition document.",
                    "$");
            }
        }
        catch (Exception exception) when (exception is JsonException
                                          or ArgumentException
                                          or InvalidOperationException
                                          or NotSupportedException)
        {
            return Error(
                ExecutionDefinitionDiagnosticCodes.DeserializationInvalid,
                exception.Message,
                exception is JsonException jsonException ? jsonException.Path ?? "$" : "$");
        }

        DocumentValidationResult integrity;
        try
        {
            integrity = ExecutionDefinitionDocumentValidator.Validate(document, graph);
        }
        catch (Exception exception) when (exception is JsonException
                                          or ArgumentException
                                          or InvalidOperationException
                                          or NotSupportedException
                                          or FormatException
                                          or OverflowException)
        {
            return Error(
                ExecutionDefinitionDiagnosticCodes.ContentInvalid,
                exception.Message,
                "/definition");
        }
        if (compatibility is null)
            return integrity;

        return DocumentValidationResult.Combine(
            integrity,
            ExecutionDefinitionCompatibilityValidator.Validate(document, compatibility));
    }

    static DocumentValidationResult ValidateEnvelope(JsonElement root)
    {
        if (!root.TryGetProperty("kind", out var kind))
            return Error(ExecutionDefinitionDiagnosticCodes.KindMissing, "An execution definition must declare kind.", "/kind");
        if (kind.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(kind.GetString()))
            return Error(ExecutionDefinitionDiagnosticCodes.KindInvalid, "Execution-definition kind must be a string.", "/kind");
        if (!root.TryGetProperty("metadata", out var metadata))
        {
            return Error(
                ExecutionDefinitionDiagnosticCodes.MetadataMissing,
                "An execution definition must contain metadata.",
                "/metadata");
        }
        if (metadata.ValueKind != JsonValueKind.Object)
        {
            return Error(
                ExecutionDefinitionDiagnosticCodes.MetadataInvalid,
                "Execution-definition metadata must be a JSON object.",
                "/metadata");
        }
        if (!metadata.TryGetProperty("schemaVersion", out var schemaVersion))
        {
            return Error(
                ExecutionDefinitionDiagnosticCodes.SchemaVersionMissing,
                "Execution-definition metadata must declare schemaVersion.",
                "/metadata/schemaVersion");
        }
        if (schemaVersion.ValueKind != JsonValueKind.String
            || schemaVersion.GetString() is not { } version
            || string.IsNullOrWhiteSpace(version))
        {
            return Error(
                ExecutionDefinitionDiagnosticCodes.SchemaVersionInvalid,
                "Execution-definition schemaVersion must be a string.",
                "/metadata/schemaVersion");
        }
        if (!SupportedSchemaVersions.Supports(new ExecutionIrSchemaVersion(version)))
        {
            return Error(
                ExecutionDefinitionDiagnosticCodes.SchemaVersionUnsupported,
                $"Execution IR schema version '{version}' is not supported by this serializer.",
                "/metadata/schemaVersion");
        }
        if (!metadata.TryGetProperty("fingerprint", out _))
        {
            return Error(
                ExecutionDefinitionDiagnosticCodes.FingerprintMissing,
                "Execution-definition metadata must contain a fingerprint.",
                "/metadata/fingerprint");
        }
        if (!metadata.TryGetProperty("provenance", out _))
        {
            return Error(
                ExecutionDefinitionDiagnosticCodes.ProvenanceMissing,
                "Execution-definition metadata must contain provenance.",
                "/metadata/provenance");
        }
        if (!root.TryGetProperty("definition", out var definition))
        {
            return Error(
                ExecutionDefinitionDiagnosticCodes.ContentMissing,
                "An execution-definition document must contain definition content.",
                "/definition");
        }
        if (definition.ValueKind != JsonValueKind.Object)
        {
            return Error(
                ExecutionDefinitionDiagnosticCodes.ContentInvalid,
                "Canonical execution-definition content must be a JSON object.",
                "/definition");
        }
        if (!root.TryGetProperty("extensions", out var extensions))
        {
            return Error(
                ExecutionDefinitionDiagnosticCodes.ExtensionsMissing,
                "An execution-definition document must contain its extensions collection.",
                "/extensions");
        }
        if (extensions.ValueKind != JsonValueKind.Array)
        {
            return Error(
                ExecutionDefinitionDiagnosticCodes.ExtensionsInvalid,
                "Execution-definition extensions must be a JSON array.",
                "/extensions");
        }

        return DocumentValidationResult.Valid;
    }

    static DocumentValidationResult Error(string code, string message, string location) =>
        StrictDocumentJson.Error(code, message, location);

    static string BuildFailureMessage(DocumentValidationResult validation)
    {
        var message = string.Join(
            Environment.NewLine,
            validation.Diagnostics.Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}"));
        return message.Length == 0
            ? "Failed to deserialize execution-definition document."
            : message;
    }
}
