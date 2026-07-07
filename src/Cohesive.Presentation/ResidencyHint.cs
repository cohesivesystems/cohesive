using System.Text.Json.Serialization;

namespace Cohesive.Presentation;

/// <summary>
/// Indicates where a state or condition is held or evaluated.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ResidencyHint
{
    Server = 0,
    Client = 1,
    Hybrid = 2,
    Replicated = 3
}