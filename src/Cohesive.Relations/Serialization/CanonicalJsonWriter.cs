using System.Buffers;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cohesive.Relations.Serialization;

/// <summary>
/// Writes canonical UTF-8 JSON shared by Cohesive.Relations semantic fingerprint profiles.
/// </summary>
static class CanonicalJsonWriter
{
    /// <summary>Writes a JSON node using canonical object and configured set-like collection ordering.</summary>
    /// <param name="node">JSON value to canonicalize.</param>
    /// <param name="options">Serializer options used when writing scalar JSON values.</param>
    /// <param name="getArraySortProperty">
    /// Resolves the object property used to order a set-like array property, or <see langword="null"/>
    /// when array order is semantically significant.
    /// </param>
    /// <param name="isStringSetArray">
    /// Determines whether an array property is a set-like collection of JSON strings that should
    /// be ordered using ordinal string comparison.
    /// </param>
    /// <returns>Canonical UTF-8 JSON bytes.</returns>
    public static byte[] GetCanonicalBytes(
        JsonNode node,
        JsonSerializerOptions options,
        Func<string, string?> getArraySortProperty,
        Func<string, bool>? isStringSetArray = null)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(getArraySortProperty);

        ArrayBufferWriter<byte> buffer = new();
        using (Utf8JsonWriter writer = new(buffer, new JsonWriterOptions
               {
                   Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                   Indented = false
               }))
        {
            WriteCanonical(writer, node, options, getArraySortProperty, isStringSetArray);
        }

        return buffer.WrittenSpan.ToArray();
    }

    static void WriteCanonical(
        Utf8JsonWriter writer,
        JsonNode? node,
        JsonSerializerOptions options,
        Func<string, string?> getArraySortProperty,
        Func<string, bool>? isStringSetArray)
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
                    WriteCanonicalPropertyValue(
                        writer,
                        property.Key,
                        property.Value,
                        options,
                        getArraySortProperty,
                        isStringSetArray);
                }
                writer.WriteEndObject();
                return;
            case JsonArray array:
                writer.WriteStartArray();
                foreach (var item in array)
                    WriteCanonical(writer, item, options, getArraySortProperty, isStringSetArray);
                writer.WriteEndArray();
                return;
            case JsonValue value:
                if (value.TryGetValue<ObservationValue>(out var observationValue))
                {
                    WriteCanonicalObservationValue(writer, observationValue);
                }
                else if (value.TryGetValue<JsonElement>(out var element)
                         && element.ValueKind == JsonValueKind.Number)
                {
                    WriteCanonicalObservationValue(writer, ObservationValue.FromJsonElement(element));
                }
                else if (value.TryGetValue<double>(out var number)
                         && BitConverter.DoubleToInt64Bits(number) == long.MinValue)
                {
                    writer.WriteNumberValue(0);
                }
                else
                {
                    value.WriteTo(writer, options);
                }
                return;
            default:
                throw new InvalidOperationException(
                    $"Unsupported JSON node '{node.GetType().Name}' during canonicalization.");
        }
    }

    static void WriteCanonicalPropertyValue(
        Utf8JsonWriter writer,
        string propertyName,
        JsonNode? value,
        JsonSerializerOptions options,
        Func<string, string?> getArraySortProperty,
        Func<string, bool>? isStringSetArray)
    {
        var sortProperty = getArraySortProperty(propertyName);
        if (value is not JsonArray array)
        {
            WriteCanonical(writer, value, options, getArraySortProperty, isStringSetArray);
            return;
        }

        if (sortProperty is null && isStringSetArray?.Invoke(propertyName) != true)
        {
            WriteCanonical(writer, value, options, getArraySortProperty, isStringSetArray);
            return;
        }

        writer.WriteStartArray();
        var ordered = sortProperty is not null
            ? array.OrderBy(
                item => GetCanonicalObjectSortValue(item, sortProperty),
                StringComparer.Ordinal)
            : array.OrderBy(GetCanonicalStringSortValue, StringComparer.Ordinal);
        foreach (var item in ordered)
        {
            WriteCanonical(writer, item, options, getArraySortProperty, isStringSetArray);
        }
        writer.WriteEndArray();
    }

    static string GetCanonicalObjectSortValue(JsonNode? item, string propertyName)
    {
        if (item is not JsonObject obj
            || obj[propertyName] is not JsonValue value
            || !value.TryGetValue<string>(out var text))
        {
            return string.Empty;
        }

        return text;
    }

    static string GetCanonicalStringSortValue(JsonNode? item)
    {
        if (item is JsonValue value && value.TryGetValue<string>(out var text))
            return text;

        throw new InvalidOperationException(
            "A canonical string-set array can contain only JSON string values.");
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
                var normalizedDouble = value.Double == 0d ? 0d : value.Double;
                if (Math.TryGetCanonicalDecimalFromDouble(normalizedDouble, out var exactDecimal))
                {
                    writer.WriteRawValue(
                        exactDecimal.ToString("G29", CultureInfo.InvariantCulture),
                        skipInputValidation: true);
                }
                else
                {
                    writer.WriteNumberValue(normalizedDouble);
                }
                return;
            case ObservationValueKind.Decimal:
                writer.WriteRawValue(
                    value.Decimal.ToString("G29", CultureInfo.InvariantCulture),
                    skipInputValidation: true);
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
                    $"Observation value kind '{value.Kind}' does not have a canonical Cohesive.Relations JSON encoding.");
        }
    }
}
