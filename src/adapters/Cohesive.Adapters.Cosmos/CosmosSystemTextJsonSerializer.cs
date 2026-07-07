using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Azure.Cosmos;

namespace Cohesive.Adapters.Cosmos;

/// <summary>
/// Cosmos SDK serializer backed by <see cref="System.Text.Json"/>.
/// </summary>
public sealed class CosmosSystemTextJsonSerializer : CosmosSerializer
{
    readonly JsonSerializerOptions options;

    /// <summary>
    /// Creates a Cosmos serializer backed by <see cref="System.Text.Json"/>.
    /// </summary>
    /// <param name="options">
    /// Optional serializer options. When omitted, the serializer uses web defaults and omits null properties.
    /// </param>
    public CosmosSystemTextJsonSerializer(JsonSerializerOptions? options = null)
    {
        this.options = options is null ? CreateDefaultOptions() : new JsonSerializerOptions(options);
    }

    /// <summary>
    /// Creates default options for Cosmos adapter JSON serialization.
    /// </summary>
    public static JsonSerializerOptions CreateDefaultOptions()
    {
        return new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
    }

    /// <inheritdoc />
    public override T FromStream<T>(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (typeof(Stream).IsAssignableFrom(typeof(T)))
            return (T)(object)stream;

        using (stream)
        {
            if (stream.CanSeek && stream.Length == 0)
                return default!;

            return JsonSerializer.Deserialize<T>(stream, options)!;
        }
    }

    /// <inheritdoc />
    public override Stream ToStream<T>(T input)
    {
        var stream = new MemoryStream();

        using (var writer = new Utf8JsonWriter(stream))
            JsonSerializer.Serialize(writer, input, options);

        stream.Position = 0;
        return stream;
    }
}
