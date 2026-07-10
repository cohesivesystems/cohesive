using System.Text.Json.Serialization;

namespace Cohesive.Model;

/// <summary>
/// Semantic role of a field.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FieldRole
{
    /// <summary>Represents the data option.</summary>
    Data = 0,
    /// <summary>Represents the identity option.</summary>
    Identity = 1,
    /// <summary>Represents the reference option.</summary>
    Reference = 2,
    /// <summary>Represents the computed option.</summary>
    Computed = 3,
    /// <summary>Represents the metadata option.</summary>
    Metadata = 4
}
