using System.Text.Json.Serialization;

namespace Cohesive.Presentation;

/// <summary>
/// Indicates where a state or condition is held or evaluated.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ResidencyHint
{
    /// <summary>Represents the server option.</summary>
    Server = 0,
    /// <summary>Represents the client option.</summary>
    Client = 1,
    /// <summary>Represents the hybrid option.</summary>
    Hybrid = 2,
    /// <summary>Represents the replicated option.</summary>
    Replicated = 3
}
