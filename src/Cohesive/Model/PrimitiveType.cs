using System.Text.Json.Serialization;

namespace Cohesive.Model;

/// <summary>
/// Canonical primitive type kinds.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PrimitiveType
{
    Bool = 0,
    Int32 = 1,
    Int64 = 2,
    Decimal = 3,
    String = 4,
    Guid = 5,
    Date = 6,
    DateTime = 7,
    Instant = 8,
    Bytes = 9
}