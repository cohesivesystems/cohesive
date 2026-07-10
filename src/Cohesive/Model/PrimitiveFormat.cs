using System.Text.Json.Serialization;

namespace Cohesive.Model;

/// <summary>
/// Optional semantic/transport format metadata for primitive values.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PrimitiveFormat
{
    /// <summary>Represents the absence of a selected option.</summary>
    None = 0,
    /// <summary>Represents the uuid option.</summary>
    Uuid = 1,
    /// <summary>Represents the iso date option.</summary>
    IsoDate = 2,
    /// <summary>Represents the iso date time option.</summary>
    IsoDateTime = 3,
    /// <summary>Represents the iso instant option.</summary>
    IsoInstant = 4,
    /// <summary>Represents the currency code option.</summary>
    CurrencyCode = 5,
    /// <summary>Represents the country code option.</summary>
    CountryCode = 6,
    /// <summary>Represents the email option.</summary>
    Email = 7,
    /// <summary>Represents the uri option.</summary>
    Uri = 8,
    /// <summary>Represents the base64 option.</summary>
    Base64 = 9
}
