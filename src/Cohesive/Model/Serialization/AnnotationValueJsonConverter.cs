using System.Text.Json.Serialization;
using System.Text.Json;

namespace Cohesive.Model.Serialization;

/// <summary>
/// Serializes <see cref="AnnotationValue"/> as its raw JSON payload rather than an object envelope.
/// </summary>
public sealed class AnnotationValueJsonConverter : JsonConverter<AnnotationValue>
{
    /// <inheritdoc />
    public override AnnotationValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var node = JsonSerializer.Deserialize<JsonElement>(ref reader, options);
        return new AnnotationValue(node);
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, AnnotationValue value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        value.Value.WriteTo(writer);
    }
}
