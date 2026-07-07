using System.Text.Json.Serialization;

namespace Cohesive.Model;

/// <summary>
/// Semantic role of a field.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FieldRole
{
    Data = 0,
    Identity = 1,
    Reference = 2,
    Computed = 3,
    Metadata = 4
}