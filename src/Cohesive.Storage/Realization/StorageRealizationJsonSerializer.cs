using System.Text;
using System.Text.Json;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Serialization;

namespace Cohesive.Storage.Realization;

/// <summary>Strict canonical JSON serialization for portable Storage Realization documents.</summary>
public static class StorageRealizationJsonSerializer
{
    /// <summary>Creates strict closed-contract JSON options for Storage Realization IR.</summary>
    /// <param name="formatting">Compact or human-readable output formatting.</param>
    /// <returns>Strict case-sensitive serializer options including canonical model converters.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="formatting"/> is unsupported.</exception>
    public static JsonSerializerOptions CreateOptions(
        PortableDocumentJsonFormatting formatting = PortableDocumentJsonFormatting.Compact)
    {
        if (!Enum.IsDefined(formatting))
            throw new ArgumentOutOfRangeException(nameof(formatting), formatting, "Unsupported JSON formatting mode.");

        return RelationQueryJsonSerializer.CreateOptions(
            indented: formatting == PortableDocumentJsonFormatting.Indented);
    }

    /// <summary>Serializes one validated current-version Storage Realization document.</summary>
    /// <param name="document">Document to serialize.</param>
    /// <param name="formatting">Compact canonical or human-readable indented formatting.</param>
    /// <returns>Deterministic Storage Realization JSON.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The document fails semantic, linkage, schema, or fingerprint validation.</exception>
    /// <exception cref="JsonException">Content cannot be serialized.</exception>
    /// <exception cref="NotSupportedException">Content contains an unsupported runtime type.</exception>
    /// <exception cref="InvalidOperationException">Content has no canonical JSON representation.</exception>
    public static string Serialize(
        StorageRealizationDocument document,
        PortableDocumentJsonFormatting formatting = PortableDocumentJsonFormatting.Indented)
    {
        ArgumentNullException.ThrowIfNull(document);
        RequireValid(document, nameof(document));
        return formatting == PortableDocumentJsonFormatting.Compact
            ? Encoding.UTF8.GetString(StrictDocumentJson.GetCanonicalBytes(document, CreateOptions()))
            : JsonSerializer.Serialize(document, CreateOptions(formatting));
    }

    /// <summary>Deserializes and validates one current-version Storage Realization document.</summary>
    /// <param name="json">Persisted document JSON.</param>
    /// <returns>The exact validated document.</returns>
    /// <exception cref="JsonException">The wire contract, semantics, linkage, schema, or fingerprint is invalid.</exception>
    public static StorageRealizationDocument Deserialize(string json)
    {
        var validation = TryDeserialize(json, out var document);
        if (validation.IsValid && document is not null)
            return document;

        throw new JsonException(
            validation.Diagnostics.IsDefaultOrEmpty
                ? "Failed to deserialize a Storage Realization document."
                : string.Join(Environment.NewLine, validation.Diagnostics.Select(static diagnostic =>
                    $"{diagnostic.Code}: {diagnostic.Message}")));
    }

    /// <summary>Strictly reads and validates one portable Storage Realization document.</summary>
    /// <param name="json">Persisted document JSON.</param>
    /// <param name="document">Typed document when wire projection succeeds, even if semantic validation fails.</param>
    /// <returns>Structured deterministic wire, semantic, linkage, schema, and fingerprint diagnostics.</returns>
    public static DocumentValidationResult TryDeserialize(
        string json,
        out StorageRealizationDocument? document)
    {
        document = null;
        if (!StrictDocumentJson.TryReadCanonicalObject(
                json,
                CreateOptions(),
                "Storage Realization document",
                out StorageRealizationDocument? parsed,
                out var wireError))
        {
            return DocumentValidationResult.FromDiagnostics(
            [
                new(
                    $"storage.realization.json.{wireError.Failure}",
                    DiagnosticSeverity.Error,
                    wireError.Message,
                    wireError.Location)
            ]);
        }

        document = parsed;
        return StorageRealizationValidator.Validate(parsed!);
    }

    static void RequireValid(StorageRealizationDocument document, string parameterName)
    {
        var validation = StorageRealizationValidator.Validate(document);
        if (!validation.IsValid)
        {
            throw new ArgumentException(
                string.Join(" ", validation.Diagnostics.Select(static diagnostic =>
                    $"{diagnostic.Code}: {diagnostic.Message}")),
                parameterName);
        }
    }
}
