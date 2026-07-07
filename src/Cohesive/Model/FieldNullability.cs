using System.Text.Json.Serialization;

namespace Cohesive.Model;

/// <summary>
/// Field nullability semantics.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FieldNullability
{
    NonNullable = 0,
    Nullable = 1
}