using System.Text.Json.Serialization;

namespace Cohesive.Model;

/// <summary>
/// Field nullability semantics.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FieldNullability
{
    /// <summary>Represents the 'non nullable' option.</summary>
    NonNullable = 0,
    
    /// <summary>Represents the nullable option.</summary>
    Nullable = 1
}
