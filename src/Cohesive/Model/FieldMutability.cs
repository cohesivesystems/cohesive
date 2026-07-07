using System.Text.Json.Serialization;

namespace Cohesive.Model;

/// <summary>
/// Allowed mutation semantics for a field.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FieldMutability
{
    Mutable = 0,
    WriteOnce = 1,
    Computed = 2
}
