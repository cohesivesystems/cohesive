using System.Text.Json.Serialization;

namespace Cohesive.Model;

/// <summary>
/// Canonical primitive type kinds.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PrimitiveType
{
    /// <summary>Represents the bool option.</summary>
    Bool = 0,
    /// <summary>Represents the int32 option.</summary>
    Int32 = 1,
    /// <summary>Represents the int64 option.</summary>
    Int64 = 2,
    /// <summary>Represents the decimal option.</summary>
    Decimal = 3,
    /// <summary>Represents the string option.</summary>
    String = 4,
    /// <summary>Represents the guid option.</summary>
    Guid = 5,
    /// <summary>Represents the date option.</summary>
    Date = 6,
    /// <summary>Represents the date time option.</summary>
    DateTime = 7,
    /// <summary>Represents the instant option.</summary>
    Instant = 8,
    /// <summary>Represents the bytes option.</summary>
    Bytes = 9
}
