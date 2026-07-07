using System.Text.Json.Serialization;

namespace Cohesive.Model;

/// <summary>
/// Field presence requirement (optional or required).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FieldPresence
{
    Required = 0,
    Optional = 1
}
