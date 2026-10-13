using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cohesive.Model.Serialization;

/// <summary>Reads immutable annotation maps using bounded shared, owned JSON backing.</summary>
/// <remarks>
/// Maps are parsed as complete values. Prefer this converter for already-buffered messages;
/// streaming callers must budget for a complete map. Maps up to 64 KiB share backing across
/// annotations; retaining one annotation can retain that map plus its parser metadata. Larger maps
/// retain independent value snapshots. Custom key, value and JSON element/document converters retain their ordinary behavior.
/// </remarks>
public sealed class AnnotationMapJsonConverter : JsonConverter<ImmutableDictionary<AnnotationKey, AnnotationValue>>
{
    const int MaxSharedBytes = 64 * 1024;
    static readonly Type DefaultElementConverter = JsonSerializerOptions.Default.GetConverter(typeof(JsonElement)).GetType();
    static readonly Type DefaultDocumentConverter = JsonSerializerOptions.Default.GetConverter(typeof(JsonDocument)).GetType();

    /// <inheritdoc />
    public override ImmutableDictionary<AnnotationKey, AnnotationValue> Read(ref Utf8JsonReader reader,
        Type typeToConvert, JsonSerializerOptions options)
    {
        if (options.ReferenceHandler is not null)
            return JsonSerializer.Deserialize<ImmutableDictionary<AnnotationKey, AnnotationValue>>(ref reader, OrdinaryOptions(options))!;
        if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("An annotation map must be a JSON object.");
        var next = reader;
        if (next.Read() && next.TokenType == JsonTokenType.EndObject)
        {
            reader = next;
            return ImmutableDictionary<AnnotationKey, AnnotationValue>.Empty;
        }
        var keys = (JsonConverter<AnnotationKey>)options.GetConverter(typeof(AnnotationKey));
        if (keys is AnnotationKeyJsonConverter
            && options.GetConverter(typeof(AnnotationValue)) is AnnotationValueJsonConverter
            && options.GetConverter(typeof(JsonElement)).GetType() == DefaultElementConverter
            && options.GetConverter(typeof(JsonDocument)).GetType() == DefaultDocumentConverter)
        {
            using var document = JsonSerializer.Deserialize<JsonDocument>(ref reader, options)!;
            var root = document.RootElement;
            if (System.Runtime.InteropServices.JsonMarshal.GetRawUtf8Value(root).Length <= MaxSharedBytes)
                root = root.Clone(); // Owned map backing: child Clone() can now retain it.
            var values = ImmutableDictionary.CreateBuilder<AnnotationKey, AnnotationValue>();
            foreach (var property in root.EnumerateObject())
            {
                var name = property.Name;
                if (string.IsNullOrWhiteSpace(name)) throw new JsonException("Annotation key property names must be non-empty.");
                var key = new AnnotationKey(name);
                values[key] = property.Value.ValueKind == JsonValueKind.Null ? null! : new AnnotationValue(property.Value);
            }
            return values.ToImmutable();
        }

        // Customized converters retain the ordinary per-value contract.
        var fallback = ImmutableDictionary.CreateBuilder<AnnotationKey, AnnotationValue>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject) return fallback.ToImmutable();
            if (reader.TokenType != JsonTokenType.PropertyName) throw new JsonException("Expected an annotation key.");
            var key = keys.ReadAsPropertyName(ref reader, typeof(AnnotationKey), options);
            if (!reader.Read()) throw new JsonException("The annotation map ended before its value.");
            var value = JsonSerializer.Deserialize<AnnotationValue>(ref reader, options);
            if (!options.AllowDuplicateProperties && fallback.ContainsKey(key)) throw new JsonException("Duplicate annotation key.");
            fallback[key] = value!;
        }
        throw new JsonException("The annotation map is incomplete.");
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, ImmutableDictionary<AnnotationKey, AnnotationValue> value,
        JsonSerializerOptions options)
    {
        if (options.ReferenceHandler is not null)
        {
            JsonSerializer.Serialize(writer, value, OrdinaryOptions(options));
            return;
        }
        var keys = (JsonConverter<AnnotationKey>)options.GetConverter(typeof(AnnotationKey));
        var direct = options.GetConverter(typeof(AnnotationValue)) as AnnotationValueJsonConverter;
        writer.WriteStartObject();
        foreach (var entry in value)
        {
            keys.WriteAsPropertyName(writer, entry.Key, options);
            if (direct is null) JsonSerializer.Serialize(writer, entry.Value, options);
            else if (entry.Value is null) writer.WriteNullValue();
            else direct.Write(writer, entry.Value, options);
        }
        writer.WriteEndObject();
    }

    // Reference metadata belongs to the serializer's dictionary converter. Keep its semantics
    // without adding a cache for this uncommon, explicitly configured policy.
    static JsonSerializerOptions OrdinaryOptions(JsonSerializerOptions options)
    {
        var ordinary = new JsonSerializerOptions(options);
        for (var i = ordinary.Converters.Count - 1; i >= 0; i--)
            if (ordinary.Converters[i] is AnnotationMapJsonConverter) ordinary.Converters.RemoveAt(i);
        return ordinary;
    }
}
