using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cohesive.Model.Serialization;

/// <summary>
/// Serializes one property value with the stable <see cref="JsonSerializerDefaults.Web"/> profile,
/// independently of the serializer options used by its containing document.
/// </summary>
/// <remarks>
/// Apply this converter to a property whose nested wire contract uses web JSON conventions while
/// its containing document uses a different naming policy. The value is read from or written to the
/// active JSON stream directly; no intermediate <see cref="JsonDocument"/> or
/// <see cref="JsonElement"/> is materialized.
/// </remarks>
/// <typeparam name="T">Nested property value type.</typeparam>
public sealed class WebJsonPropertyConverter<T> : JsonConverter<T>
{
    /// <summary>Creates a web-profile property converter.</summary>
    public WebJsonPropertyConverter()
    {
    }

    /// <inheritdoc />
    public override T? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options) =>
        JsonSerializer.Deserialize<T>(ref reader, WebJsonPropertySerialization.Options);

    /// <inheritdoc />
    public override void Write(
        Utf8JsonWriter writer,
        T value,
        JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value, WebJsonPropertySerialization.Options);
}

static class WebJsonPropertySerialization
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    static JsonSerializerOptions CreateOptions()
    {
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web);
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
