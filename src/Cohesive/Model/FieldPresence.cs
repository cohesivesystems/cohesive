using System.Text.Json.Serialization;

namespace Cohesive.Model;

/// <summary>
/// Field presence requirement (optional or required).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FieldPresence
{
    /// <summary>Indicates that the field is required.</summary>
    Required = 0,
    
    /// <summary>Indicates that the field is optional.</summary>
    Optional = 1
}
