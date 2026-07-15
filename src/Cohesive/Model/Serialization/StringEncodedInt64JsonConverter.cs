using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cohesive.Model.Serialization;

/// <summary>
/// Serializes signed 64-bit integers as canonical invariant decimal JSON strings so every value
/// round-trips exactly through JavaScript and other JSON runtimes without an exact integer type.
/// </summary>
public sealed class StringEncodedInt64JsonConverter : JsonConverterFactory
{
    /// <inheritdoc />
    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert == typeof(long) || typeToConvert == typeof(long?);

    /// <inheritdoc />
    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(typeToConvert);
        ArgumentNullException.ThrowIfNull(options);
        return typeToConvert == typeof(long)
            ? new Int64Converter()
            : typeToConvert == typeof(long?)
                ? new NullableInt64Converter()
                : throw new NotSupportedException(
                    $"Converter '{nameof(StringEncodedInt64JsonConverter)}' cannot convert '{typeToConvert}'.");
    }

    static long ReadCanonicalString(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException("A portable Int64 value must be encoded as a JSON string.");

        var text = reader.GetString();
        if (text is null
            || !long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)
            || !string.Equals(text, value.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
        {
            throw new JsonException("A portable Int64 value must be a canonical signed decimal string.");
        }

        return value;
    }

    sealed class Int64Converter : JsonConverter<long>
    {
        public override long Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            ReadCanonicalString(ref reader);

        public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
    }

    sealed class NullableInt64Converter : JsonConverter<long?>
    {
        public override long? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.Null ? null : ReadCanonicalString(ref reader);

        public override void Write(Utf8JsonWriter writer, long? value, JsonSerializerOptions options)
        {
            if (value is null)
                writer.WriteNullValue();
            else
                writer.WriteStringValue(value.Value.ToString(CultureInfo.InvariantCulture));
        }
    }
}
