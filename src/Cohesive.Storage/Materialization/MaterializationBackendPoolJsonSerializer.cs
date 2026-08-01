using System.Text;
using System.Text.Json;
using Cohesive.Model.Serialization;

namespace Cohesive.Storage.Materialization;

/// <summary>Strict canonical JSON serialization for portable materialization backend-pool documents.</summary>
/// <remarks>
/// The closed wire boundary reprojects deserialized content through
/// <see cref="MaterializationBackendPoolDocument"/>, restoring member normalization, schema compatibility, and
/// the exact canonical definition fingerprint before returning a document.
/// </remarks>
public static class MaterializationBackendPoolJsonSerializer
{
    /// <summary>Creates strict backend-pool JSON options including canonical Relations converters.</summary>
    /// <param name="formatting">Compact or human-readable output formatting.</param>
    /// <returns>Strict case-sensitive closed-contract serializer options.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="formatting"/> is unsupported.</exception>
    public static JsonSerializerOptions CreateOptions(
        PortableDocumentJsonFormatting formatting = PortableDocumentJsonFormatting.Compact) =>
        MaterializationJsonSerializer.CreateOptions(formatting);

    /// <summary>Serializes one current, exactly fingerprinted backend-pool document.</summary>
    /// <param name="document">Backend-pool document to serialize.</param>
    /// <param name="formatting">Compact or human-readable output formatting.</param>
    /// <returns>Deterministic backend-pool document JSON.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// The schema is unsupported or the retained definition fingerprint differs from canonical content.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="formatting"/> is unsupported.</exception>
    /// <exception cref="JsonException">The document cannot be serialized under the strict wire contract.</exception>
    /// <exception cref="NotSupportedException">The document contains an unsupported runtime value.</exception>
    /// <exception cref="InvalidOperationException">The document has no canonical JSON representation.</exception>
    public static string Serialize(
        MaterializationBackendPoolDocument document,
        PortableDocumentJsonFormatting formatting = PortableDocumentJsonFormatting.Indented)
    {
        Validate(document);
        return formatting == PortableDocumentJsonFormatting.Compact
            ? Encoding.UTF8.GetString(GetCanonicalBytes(document))
            : JsonSerializer.Serialize(document, CreateOptions(formatting));
    }

    /// <summary>Gets the unique canonical compact UTF-8 representation of one backend-pool document.</summary>
    /// <param name="document">Backend-pool document to encode.</param>
    /// <returns>Canonical compact UTF-8 JSON bytes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// The schema is unsupported or the retained definition fingerprint differs from canonical content.
    /// </exception>
    /// <exception cref="JsonException">The document cannot be serialized under the strict wire contract.</exception>
    /// <exception cref="NotSupportedException">The document contains an unsupported runtime value.</exception>
    /// <exception cref="InvalidOperationException">The document has no canonical JSON representation.</exception>
    public static byte[] GetCanonicalBytes(MaterializationBackendPoolDocument document)
    {
        Validate(document);
        return StrictDocumentJson.GetCanonicalBytes(document, CreateOptions());
    }

    /// <summary>Deserializes and verifies one current-version materialization backend-pool document.</summary>
    /// <param name="json">Persisted backend-pool document JSON.</param>
    /// <returns>An exactly normalized document whose persisted fingerprint matches all canonical definition content.</returns>
    /// <exception cref="JsonException">
    /// The wire is empty, malformed, open, duplicate, non-canonical, uses an unsupported schema, violates a pool
    /// invariant, or carries a stale or forged fingerprint.
    /// </exception>
    public static MaterializationBackendPoolDocument Deserialize(string json)
    {
        if (!StrictDocumentJson.TryReadCanonicalObject(
                json,
                CreateOptions(),
                "materialization backend-pool document",
                out MaterializationBackendPoolDocument? document,
                out var error)
            || document is null)
        {
            throw new JsonException(error.Message);
        }

        try
        {
            Validate(document);
        }
        catch (Exception exception) when (exception is ArgumentException
                                          or JsonException
                                          or NotSupportedException
                                          or InvalidOperationException)
        {
            throw new JsonException("The materialization backend-pool document is invalid.", exception);
        }

        return document;
    }

    static void Validate(MaterializationBackendPoolDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!string.Equals(
                document.SchemaVersion,
                MaterializationBackendPoolDocument.CurrentSchemaVersion,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Unsupported materialization backend-pool schema version '{document.SchemaVersion}'.",
                nameof(document));
        }

        var expected = MaterializationBackendPoolFingerprinter.Compute(document.Definition);
        if (document.DefinitionFingerprint != expected)
        {
            throw new ArgumentException(
                "The backend-pool definition fingerprint does not match canonical content.",
                nameof(document));
        }
    }
}
