using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cohesive.Model.Serialization;

/// <summary>
/// Serializes <see cref="ObservationValue"/> as plain JSON values rather than an object envelope.
/// </summary>
public sealed class ObservationValueJsonConverter(ObservationBytesJsonEncoding bytesEncoding) : JsonConverter<ObservationValue>
{
    /// <summary>Initializes a new instance of the observation value json converter type.</summary>
    public ObservationValueJsonConverter()
        : this(ObservationBytesJsonEncoding.Throw)
    {
    }

    /// <summary>Gets the bytes encoding.</summary>
    public ObservationBytesJsonEncoding BytesEncoding { get; } = bytesEncoding;

    /// <inheritdoc />
    public override ObservationValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => ObservationJsonReader.ReadValue(ref reader);

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, ObservationValue value, JsonSerializerOptions options)
    {
        value.WriteTo(writer, BytesEncoding);
    }
}
