using System.Text.Json.Serialization;

namespace Cohesive.Model;

/// <summary>
/// Allowed mutation semantics for a field.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FieldMutability
{
    /// <summary>Represents the mutable option.</summary>
    Mutable = 0,
    
    /// <summary>Represents the write once option.</summary>
    WriteOnce = 1,
    
    /// <summary>Represents the computed option.</summary>
    Computed = 2
}
