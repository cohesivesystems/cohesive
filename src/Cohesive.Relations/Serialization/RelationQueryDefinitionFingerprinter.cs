using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cohesive.Model;
using Cohesive.Relations.IR;

namespace Cohesive.Relations.Serialization;

/// <summary>
/// Computes stable content fingerprints for canonical relation/query definitions.
/// </summary>
/// <remarks>
/// The v1 canonicalization profile writes UTF-8 JSON with ordinal object-key ordering,
/// stable-id ordering for set-like definition collections, preserved order for semantic
/// sequences, unescaped Unicode scalar text, and shortest round-trip JSON numbers.
/// Numerically equivalent positive and negative zero values are normalized to zero.
/// </remarks>
public static class RelationQueryDefinitionFingerprinter
{
    /// <summary>Fingerprint algorithm identifier.</summary>
    public const string Algorithm = "sha256";

    /// <summary>Canonicalization profile identifier.</summary>
    public const string Canonicalization = "relation-query/v1-c14n/v1";

    /// <summary>Computes a content fingerprint that excludes document metadata and physical plans.</summary>
    /// <param name="definition">Canonical semantic definition to fingerprint.</param>
    /// <returns>Versioned canonicalization profile and SHA-256 digest.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// The definition contains a value that has no canonical relation/query JSON encoding.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// The definition contains a runtime type that the canonical JSON serializer does not support.
    /// </exception>
    public static RelationQueryDefinitionFingerprint Compute(RelationQueryDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var canonicalDefinition = GetCanonicalDefinitionBytes(definition);
        var version = Encoding.UTF8.GetBytes(RelationQueryDocument.CurrentSchemaVersion);
        var content = new byte[version.Length + 1 + canonicalDefinition.Length];
        version.CopyTo(content, 0);
        content[version.Length] = 0;
        canonicalDefinition.CopyTo(content, version.Length + 1);

        var hash = SHA256.HashData(content);
        return new(
            algorithm: Algorithm,
            canonicalization: Canonicalization,
            value: Convert.ToHexString(hash).ToLowerInvariant());
    }

    internal static byte[] GetCanonicalDefinitionBytes(RelationQueryDefinition definition)
    {
        var options = RelationQueryJsonSerializer.CreateOptions();
        var node = JsonSerializer.SerializeToNode(definition, options)
                   ?? throw new InvalidOperationException("Failed to materialize canonical relation/query definition JSON.");

        ArrayBufferWriter<byte> buffer = new();
        using (Utf8JsonWriter writer = new(buffer, new JsonWriterOptions
               {
                   Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                   Indented = false
               }))
        {
            WriteCanonical(writer, node, options);
        }
        return buffer.WrittenSpan.ToArray();
    }

    static void WriteCanonical(Utf8JsonWriter writer, JsonNode? node, JsonSerializerOptions options)
    {
        switch (node)
        {
            case null:
                writer.WriteNullValue();
                return;
            case JsonObject obj:
                writer.WriteStartObject();
                foreach (var property in obj.OrderBy(static property => property.Key, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Key);
                    WriteCanonicalPropertyValue(writer, property.Key, property.Value, options);
                }
                writer.WriteEndObject();
                return;
            case JsonArray array:
                writer.WriteStartArray();
                foreach (var item in array)
                    WriteCanonical(writer, item, options);
                writer.WriteEndArray();
                return;
            case JsonValue value:
                if (value.TryGetValue<ObservationValue>(out var observationValue))
                    WriteCanonicalObservationValue(writer, observationValue);
                else if (value.TryGetValue<JsonElement>(out var element)
                         && element.ValueKind == JsonValueKind.Number
                         && element.GetDouble() == 0d
                         && element.GetRawText()[0] == '-')
                    writer.WriteNumberValue(0);
                else if (value.TryGetValue<double>(out var number)
                         && BitConverter.DoubleToInt64Bits(number) == long.MinValue)
                    writer.WriteNumberValue(0);
                else
                    value.WriteTo(writer, options);
                return;
            default:
                throw new InvalidOperationException($"Unsupported JSON node '{node.GetType().Name}' during canonicalization.");
        }
    }

    static void WriteCanonicalPropertyValue(
        Utf8JsonWriter writer,
        string propertyName,
        JsonNode? value,
        JsonSerializerOptions options)
    {
        var sortKey = propertyName switch
        {
            "nodes" or "parameters" or "results" or "assignments" or "groupings" or "aggregates" => "id",
            "invariants" => "name",
            _ => null
        };
        if (sortKey is null || value is not JsonArray array)
        {
            WriteCanonical(writer, value, options);
            return;
        }

        writer.WriteStartArray();
        foreach (var item in array.OrderBy(
                     item => GetCanonicalSortValue(item, sortKey),
                     StringComparer.Ordinal))
        {
            WriteCanonical(writer, item, options);
        }
        writer.WriteEndArray();
    }

    static string GetCanonicalSortValue(JsonNode? item, string propertyName)
    {
        if (item is not JsonObject obj
            || obj[propertyName] is not JsonValue value
            || !value.TryGetValue<string>(out var text))
        {
            return string.Empty;
        }

        return text;
    }

    static void WriteCanonicalObservationValue(Utf8JsonWriter writer, ObservationValue value)
    {
        switch (value.Kind)
        {
            case ObservationValueKind.Undefined:
            case ObservationValueKind.Null:
                writer.WriteNullValue();
                return;
            case ObservationValueKind.Int64:
                writer.WriteNumberValue(value.Int64);
                return;
            case ObservationValueKind.Double:
                writer.WriteNumberValue(value.Double == 0d ? 0d : value.Double);
                return;
            case ObservationValueKind.Bool:
                writer.WriteBooleanValue(value.Bool);
                return;
            case ObservationValueKind.String:
                writer.WriteStringValue(value.String);
                return;
            case ObservationValueKind.Object:
                writer.WriteStartObject();
                if (value.Fields is not null)
                {
                    foreach (var (property, child) in value.Fields.OrderBy(
                                 static property => property.Key,
                                 StringComparer.Ordinal))
                    {
                        writer.WritePropertyName(property);
                        WriteCanonicalObservationValue(writer, child);
                    }
                }
                writer.WriteEndObject();
                return;
            case ObservationValueKind.Array:
                writer.WriteStartArray();
                if (value.Array is not null)
                {
                    foreach (var item in value.Array)
                        WriteCanonicalObservationValue(writer, item);
                }
                writer.WriteEndArray();
                return;
            default:
                throw new InvalidOperationException(
                    $"Observation value kind '{value.Kind}' does not have a canonical relation/query JSON encoding.");
        }
    }
}
