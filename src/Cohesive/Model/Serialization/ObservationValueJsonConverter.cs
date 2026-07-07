using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cohesive.Model.Serialization;

/// <summary>
/// Serializes <see cref="ObservationValue"/> as plain JSON values rather than an object envelope.
/// </summary>
public sealed class ObservationValueJsonConverter(ObservationBytesJsonEncoding bytesEncoding) : JsonConverter<ObservationValue>
{
    public ObservationValueJsonConverter()
        : this(ObservationBytesJsonEncoding.Throw)
    {
    }

    public ObservationBytesJsonEncoding BytesEncoding { get; } = bytesEncoding;

    public override ObservationValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        return ObservationValue.FromJsonElement(document.RootElement);
    }

    public override void Write(Utf8JsonWriter writer, ObservationValue value, JsonSerializerOptions options)
    {
        value.WriteTo(writer, BytesEncoding);
    }
}
