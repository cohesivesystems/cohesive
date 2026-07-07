using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cohesive.Relations.Serialization;

/// <summary>
/// Serializer helpers for relation JSON payloads.
/// </summary>
public static class RelationJsonSerializer
{
    /// <summary>
    /// Creates serializer options for relation model + JSON contracts.
    /// </summary>
    public static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
