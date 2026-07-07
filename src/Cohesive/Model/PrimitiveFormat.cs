using System.Text.Json.Serialization;

namespace Cohesive.Model;

/// <summary>
/// Optional semantic/transport format metadata for primitive values.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PrimitiveFormat
{
    None = 0,
    Uuid = 1,
    IsoDate = 2,
    IsoDateTime = 3,
    IsoInstant = 4,
    CurrencyCode = 5,
    CountryCode = 6,
    Email = 7,
    Uri = 8,
    Base64 = 9
}
