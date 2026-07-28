using System.Text.Json;
using Cohesive.Model.Serialization;

namespace Cohesive.Relations.Serialization;

/// <summary>
/// Strict JSON serialization for standalone portable relationship catalog documents.
/// </summary>
public static class RelationshipCatalogJsonSerializer
{
    /// <summary>Creates strict serializer options for canonical relationship catalog JSON.</summary>
    /// <param name="indented">Whether serialized JSON should be indented.</param>
    /// <returns>Serializer options configured for the canonical wire contract.</returns>
    public static JsonSerializerOptions CreateOptions(bool indented = false) =>
        StrictDocumentJson.CreateOptions(indented
            ? PortableDocumentJsonFormatting.Indented
            : PortableDocumentJsonFormatting.Compact);

    /// <summary>Serializes a portable relationship catalog document.</summary>
    /// <param name="document">Document to serialize.</param>
    /// <param name="indented">Whether serialized JSON should be indented.</param>
    /// <returns>Persisted relationship catalog document JSON.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    /// <exception cref="JsonException">
    /// The document contains a value that cannot be written using the strict canonical wire contract.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// The document contains a runtime type that the canonical JSON serializer does not support.
    /// </exception>
    public static string Serialize(RelationshipCatalogDocument document, bool indented = true)
    {
        ArgumentNullException.ThrowIfNull(document);
        return JsonSerializer.Serialize(document, CreateOptions(indented));
    }

    /// <summary>Deserializes a portable relationship catalog document.</summary>
    /// <param name="json">Persisted relationship catalog document JSON.</param>
    /// <returns>A structurally and semantically valid current-version document.</returns>
    /// <exception cref="JsonException">The document cannot be read or fails validation.</exception>
    public static RelationshipCatalogDocument Deserialize(string json)
    {
        var validation = TryDeserialize(json, out var document);
        if (validation.IsValid && document is not null)
            return document;

        var message = string.Join(
            Environment.NewLine,
            validation.Diagnostics.Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}"));
        throw new JsonException(message.Length == 0
            ? "Failed to deserialize relationship catalog document."
            : message);
    }

    /// <summary>
    /// Reads a relationship catalog document with schema-version dispatch, strict JSON handling,
    /// catalog-local semantic validation, and fingerprint verification.
    /// </summary>
    /// <param name="json">Persisted relationship catalog document JSON.</param>
    /// <param name="document">Parsed document when deserialization succeeds, even if semantic validation fails.</param>
    /// <returns>Structured read and validation diagnostics.</returns>
    public static DocumentValidationResult TryDeserialize(
        string json,
        out RelationshipCatalogDocument? document)
    {
        document = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            return StrictDocumentJson.Error(
                code: "relationshipCatalog.json.empty",
                message: "Relationship catalog document JSON cannot be empty.",
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
                code: "relationshipCatalog.json.invalid",
                message: exception.Message,
                location: "$");
        }

        using (parsedJson)
        {
            if (parsedJson.RootElement.ValueKind != JsonValueKind.Object)
            {
                return StrictDocumentJson.Error(
                    code: "relationshipCatalog.document.rootInvalid",
                    message: "A relationship catalog document must be a JSON object.",
                    location: "$");
            }

            if (StrictDocumentJson.TryFindDuplicateProperty(
                    parsedJson.RootElement,
                    path: string.Empty,
                    out var duplicateLocation))
            {
                return StrictDocumentJson.Error(
                    code: "relationshipCatalog.json.duplicateProperty",
                    message: "Canonical relationship catalog JSON cannot contain duplicate object property names.",
                    location: duplicateLocation);
            }

            if (!parsedJson.RootElement.TryGetProperty("schemaVersion", out var versionElement))
            {
                return StrictDocumentJson.Error(
                    code: "relationshipCatalog.schemaVersion.missing",
                    message: "A relationship catalog document must declare schemaVersion.",
                    location: "/schemaVersion");
            }

            if (versionElement.ValueKind != JsonValueKind.String)
            {
                return StrictDocumentJson.Error(
                    code: "relationshipCatalog.schemaVersion.invalid",
                    message: "Relationship catalog document schemaVersion must be a string.",
                    location: "/schemaVersion");
            }

            var version = versionElement.GetString();
            if (!string.Equals(
                    version,
                    RelationshipCatalogDocument.CurrentSchemaVersion,
                    StringComparison.Ordinal))
            {
                return StrictDocumentJson.Error(
                    code: "relationshipCatalog.schemaVersion.unsupported",
                    message: $"Unsupported relationship catalog document schema version '{version}'.",
                    location: "/schemaVersion");
            }

            if (!parsedJson.RootElement.TryGetProperty("catalog", out _))
            {
                return StrictDocumentJson.Error(
                    code: "relationshipCatalog.catalog.missing",
                    message: "A relationship catalog document must contain a catalog.",
                    location: "/catalog");
            }

            if (!parsedJson.RootElement.TryGetProperty("catalogFingerprint", out _))
            {
                return StrictDocumentJson.Error(
                    code: "relationshipCatalog.fingerprint.missing",
                    message: "A relationship catalog document must contain a catalog fingerprint.",
                    location: "/catalogFingerprint");
            }
        }

        try
        {
            document = JsonSerializer.Deserialize<RelationshipCatalogDocument>(json, CreateOptions());
            if (document is null)
            {
                return StrictDocumentJson.Error(
                    code: "relationshipCatalog.deserialize.null",
                    message: "JSON deserialized to a null relationship catalog document.",
                    location: "$");
            }
        }
        catch (Exception exception) when (exception is JsonException
                                          or ArgumentException
                                          or InvalidOperationException
                                          or NotSupportedException)
        {
            return StrictDocumentJson.Error(
                code: "relationshipCatalog.deserialize.invalid",
                message: exception.Message,
                location: "$");
        }

        try
        {
            return RelationshipCatalogDocumentSemanticValidator.Validate(document);
        }
        catch (Exception exception) when (exception is ArgumentException
                                          or InvalidOperationException
                                          or NullReferenceException)
        {
            return StrictDocumentJson.Error(
                code: "relationshipCatalog.semantic.invalidObject",
                message: exception.Message,
                location: "/catalog");
        }
    }
}
