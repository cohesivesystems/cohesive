using System.Text.Json;
using System.Text.Json.Serialization;
using Cohesive.Model.Serialization;

namespace Cohesive.Relations.Serialization;

/// <summary>
/// Strict JSON serialization for portable relation/query IR documents.
/// </summary>
public static class RelationQueryJsonSerializer
{
    /// <summary>Creates strict serializer options for canonical relation/query IR.</summary>
    /// <param name="indented">Whether serialized JSON should be indented.</param>
    /// <returns>Serializer options configured for the canonical wire contract.</returns>
    public static JsonSerializerOptions CreateOptions(bool indented = false)
    {
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web)
        {
            AllowOutOfOrderMetadataProperties = true,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            WriteIndented = indented
        };
        options.Converters.Add(new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false));
        return options;
    }

    /// <summary>Serializes a portable relation/query document.</summary>
    /// <param name="document">Document to serialize.</param>
    /// <param name="indented">Whether serialized JSON should be indented.</param>
    /// <returns>Persisted relation/query document JSON.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    /// <exception cref="NotSupportedException">
    /// The document contains a runtime type that the canonical JSON serializer does not support.
    /// </exception>
    public static string Serialize(RelationQueryDocument document, bool indented = true)
    {
        ArgumentNullException.ThrowIfNull(document);
        return JsonSerializer.Serialize(document, CreateOptions(indented));
    }

    /// <summary>Deserializes a portable relation/query document.</summary>
    /// <param name="json">Persisted relation/query document JSON.</param>
    /// <returns>A structurally and semantically valid current-version document.</returns>
    /// <exception cref="JsonException">The document cannot be read or fails validation.</exception>
    public static RelationQueryDocument Deserialize(string json)
    {
        var validation = TryDeserialize(json, out var document);
        if (validation.IsValid && document is not null)
            return document;

        var message = string.Join(
            Environment.NewLine,
            validation.Diagnostics.Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}"));
        throw new JsonException(message.Length == 0
            ? "Failed to deserialize relation/query document."
            : message);
    }

    /// <summary>
    /// Reads a relation/query document with schema-version dispatch, strict JSON handling,
    /// semantic validation, and fingerprint verification.
    /// </summary>
    /// <param name="json">Persisted relation/query document JSON.</param>
    /// <param name="document">Parsed document when deserialization succeeds, even if semantic validation fails.</param>
    /// <returns>Structured read and validation diagnostics.</returns>
    public static DocumentValidationResult TryDeserialize(
        string json,
        out RelationQueryDocument? document)
    {
        document = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            return Error(
                code: "relationQuery.json.empty",
                message: "Relation/query document JSON cannot be empty.",
                location: "$");
        }

        JsonDocument parsedJson;
        try
        {
            parsedJson = JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            return Error(
                code: "relationQuery.json.invalid",
                message: exception.Message,
                location: "$");
        }

        using (parsedJson)
        {
            if (parsedJson.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Error(
                    code: "relationQuery.document.rootInvalid",
                    message: "A relation/query document must be a JSON object.",
                    location: "$");
            }

            if (TryFindDuplicateProperty(parsedJson.RootElement, path: string.Empty, out var duplicateLocation))
            {
                return Error(
                    code: "relationQuery.json.duplicateProperty",
                    message: "Canonical relation/query JSON cannot contain duplicate object property names.",
                    location: duplicateLocation);
            }

            if (!parsedJson.RootElement.TryGetProperty("schemaVersion", out var versionElement))
            {
                return Error(
                    code: "relationQuery.schemaVersion.missing",
                    message: "A relation/query document must declare schemaVersion.",
                    location: "/schemaVersion");
            }

            if (versionElement.ValueKind != JsonValueKind.String)
            {
                return Error(
                    code: "relationQuery.schemaVersion.invalid",
                    message: "Relation/query document schemaVersion must be a string.",
                    location: "/schemaVersion");
            }

            var version = versionElement.GetString();
            if (!string.Equals(version, RelationQueryDocument.CurrentSchemaVersion, StringComparison.Ordinal))
            {
                return Error(
                    code: "relationQuery.schemaVersion.unsupported",
                    message: $"Unsupported relation/query document schema version '{version}'.",
                    location: "/schemaVersion");
            }

            if (!parsedJson.RootElement.TryGetProperty("definition", out _))
            {
                return Error(
                    code: "relationQuery.definition.missing",
                    message: "A relation/query document must contain a definition.",
                    location: "/definition");
            }

            if (!parsedJson.RootElement.TryGetProperty("definitionFingerprint", out _))
            {
                return Error(
                    code: "relationQuery.fingerprint.missing",
                    message: "A relation/query document must contain a definition fingerprint.",
                    location: "/definitionFingerprint");
            }
        }

        try
        {
            document = JsonSerializer.Deserialize<RelationQueryDocument>(json, CreateOptions());
            if (document is null)
            {
                return Error(
                    code: "relationQuery.deserialize.null",
                    message: "JSON deserialized to a null relation/query document.",
                    location: "$");
            }
        }
        catch (Exception exception) when (exception is JsonException
                                          or ArgumentException
                                          or InvalidOperationException
                                          or NotSupportedException)
        {
            return Error(
                code: "relationQuery.deserialize.invalid",
                message: exception.Message,
                location: "$");
        }

        try
        {
            return RelationQueryDocumentSemanticValidator.Validate(document);
        }
        catch (Exception exception) when (exception is ArgumentException
                                          or InvalidOperationException
                                          or NullReferenceException)
        {
            return Error(
                code: "relationQuery.semantic.invalidObject",
                message: exception.Message,
                location: "/definition");
        }
    }

    static bool TryFindDuplicateProperty(JsonElement element, string path, out string duplicateLocation)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                HashSet<string> names = new(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    var propertyPath = $"{path}/{EscapeJsonPointerSegment(property.Name)}";
                    if (!names.Add(property.Name))
                    {
                        duplicateLocation = propertyPath;
                        return true;
                    }

                    if (TryFindDuplicateProperty(property.Value, propertyPath, out duplicateLocation))
                        return true;
                }
                break;
            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    if (TryFindDuplicateProperty(item, $"{path}/{index}", out duplicateLocation))
                        return true;
                    index++;
                }
                break;
        }

        duplicateLocation = string.Empty;
        return false;
    }

    static string EscapeJsonPointerSegment(string value) =>
        value.Replace("~", "~0", StringComparison.Ordinal)
            .Replace("/", "~1", StringComparison.Ordinal);

    static DocumentValidationResult Error(string code, string message, string location)
    {
        return DocumentValidationResult.FromDiagnostics([
            new(
                Code: code,
                Severity: DiagnosticSeverity.Error,
                Message: message,
                Location: location)
        ]);
    }
}
