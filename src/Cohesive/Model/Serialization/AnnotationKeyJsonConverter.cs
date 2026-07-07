using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cohesive.Model.Serialization;

/// <summary>
/// JSON converter for annotation keys, including dictionary key serialization.
/// </summary>
public sealed class AnnotationKeyJsonConverter : JsonConverter<AnnotationKey>
{
    /// <inheritdoc />
    public override AnnotationKey Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetString();
        if (string.IsNullOrWhiteSpace(value))
            throw new JsonException("Annotation key values must be non-empty.");

        return new AnnotationKey(value);
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, AnnotationKey value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.Value);
    }

    /// <inheritdoc />
    public override AnnotationKey ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetString();
        if (string.IsNullOrWhiteSpace(value))
            throw new JsonException("Annotation key property names must be non-empty.");

        return new AnnotationKey(value);
    }

    /// <inheritdoc />
    public override void WriteAsPropertyName(Utf8JsonWriter writer, AnnotationKey value, JsonSerializerOptions options)
    {
        writer.WritePropertyName(value.Value);
    }
}
