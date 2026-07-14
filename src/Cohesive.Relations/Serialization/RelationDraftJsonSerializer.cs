using System.Text.Json;
using Cohesive.Model.Serialization;

namespace Cohesive.Relations.Serialization;

/// <summary>
/// Strict JSON serialization for portable relation draft documents.
/// </summary>
public static class RelationDraftJsonSerializer
{
    /// <summary>Creates strict serializer options for canonical relation draft JSON.</summary>
    /// <param name="indented">Whether serialized JSON should be indented.</param>
    /// <returns>Serializer options configured for the canonical wire contract.</returns>
    public static JsonSerializerOptions CreateOptions(bool indented = false) =>
        StrictDocumentJson.CreateOptions(indented);

    /// <summary>Serializes a portable relation draft document.</summary>
    /// <param name="document">Document to serialize.</param>
    /// <param name="indented">Whether serialized JSON should be indented.</param>
    /// <returns>Persisted relation draft document JSON.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">
    /// The document contains a value that cannot be written using the strict canonical wire contract.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// The document contains a runtime type that the canonical JSON serializer does not support.
    /// </exception>
    public static string Serialize(RelationDraftDocument document, bool indented = true)
    {
        ArgumentNullException.ThrowIfNull(document);
        return JsonSerializer.Serialize(document, CreateOptions(indented));
    }

    /// <summary>Deserializes a portable relation draft document.</summary>
    /// <param name="json">Persisted relation draft document JSON.</param>
    /// <returns>A structurally and semantically valid current-version document.</returns>
    /// <exception cref="JsonException">The document cannot be read or fails validation.</exception>
    public static RelationDraftDocument Deserialize(string json)
    {
        var validation = TryDeserialize(json, out var document);
        if (validation.IsValid && document is not null)
            return document;

        var message = string.Join(
            Environment.NewLine,
            validation.Diagnostics.Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}"));
        throw new JsonException(message.Length == 0
            ? "Failed to deserialize relation draft document."
            : message);
    }

    /// <summary>
    /// Reads a relation draft document with schema-version dispatch, strict JSON handling,
    /// draft-local semantic validation, and fingerprint verification.
    /// </summary>
    /// <param name="json">Persisted relation draft document JSON.</param>
    /// <param name="document">
    /// Parsed document when deserialization succeeds, including when subsequent semantic validation fails.
    /// </param>
    /// <returns>Structured read and validation diagnostics.</returns>
    public static DocumentValidationResult TryDeserialize(
        string json,
        out RelationDraftDocument? document)
    {
        document = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            return StrictDocumentJson.Error(
                code: "relationDraft.json.empty",
                message: "Relation draft document JSON cannot be empty.",
                location: "$");
        }

        JsonDocument parsedJson;
        try
        {
            parsedJson = JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            return StrictDocumentJson.Error(
                code: "relationDraft.json.invalid",
                message: exception.Message,
                location: "$");
        }

        using (parsedJson)
        {
            if (parsedJson.RootElement.ValueKind != JsonValueKind.Object)
            {
                return StrictDocumentJson.Error(
                    code: "relationDraft.document.rootInvalid",
                    message: "A relation draft document must be a JSON object.",
                    location: "$");
            }

            if (StrictDocumentJson.TryFindDuplicateProperty(
                    parsedJson.RootElement,
                    path: string.Empty,
                    out var duplicateLocation))
            {
                return StrictDocumentJson.Error(
                    code: "relationDraft.json.duplicateProperty",
                    message: "Canonical relation draft JSON cannot contain duplicate object property names.",
                    location: duplicateLocation);
            }

            if (!parsedJson.RootElement.TryGetProperty("schemaVersion", out var versionElement))
            {
                return StrictDocumentJson.Error(
                    code: "relationDraft.schemaVersion.missing",
                    message: "A relation draft document must declare schemaVersion.",
                    location: "/schemaVersion");
            }

            if (versionElement.ValueKind != JsonValueKind.String)
            {
                return StrictDocumentJson.Error(
                    code: "relationDraft.schemaVersion.invalid",
                    message: "Relation draft document schemaVersion must be a string.",
                    location: "/schemaVersion");
            }

            var version = versionElement.GetString();
            if (!string.Equals(
                    version,
                    RelationDraftDocument.CurrentSchemaVersion,
                    StringComparison.Ordinal))
            {
                return StrictDocumentJson.Error(
                    code: "relationDraft.schemaVersion.unsupported",
                    message: $"Unsupported relation draft document schema version '{version}'.",
                    location: "/schemaVersion");
            }

            if (!parsedJson.RootElement.TryGetProperty("draft", out _))
            {
                return StrictDocumentJson.Error(
                    code: "relationDraft.draft.missing",
                    message: "A relation draft document must contain a draft.",
                    location: "/draft");
            }

            if (!parsedJson.RootElement.TryGetProperty("draftFingerprint", out _))
            {
                return StrictDocumentJson.Error(
                    code: "relationDraft.fingerprint.missing",
                    message: "A relation draft document must contain a draft fingerprint.",
                    location: "/draftFingerprint");
            }
        }

        try
        {
            document = JsonSerializer.Deserialize<RelationDraftDocument>(json, CreateOptions());
            if (document is null)
            {
                return StrictDocumentJson.Error(
                    code: "relationDraft.deserialize.null",
                    message: "JSON deserialized to a null relation draft document.",
                    location: "$");
            }
        }
        catch (Exception exception) when (exception is JsonException
                                          or ArgumentException
                                          or InvalidOperationException
                                          or NotSupportedException)
        {
            return StrictDocumentJson.Error(
                code: "relationDraft.deserialize.invalid",
                message: exception.Message,
                location: "$");
        }

        try
        {
            return RelationDraftDocumentSemanticValidator.Validate(document);
        }
        catch (Exception exception) when (exception is JsonException
                                          or ArgumentException
                                          or InvalidOperationException
                                          or NotSupportedException
                                          or NullReferenceException)
        {
            return StrictDocumentJson.Error(
                code: "relationDraft.semantic.invalidObject",
                message: exception.Message,
                location: "/draft");
        }
    }
}
