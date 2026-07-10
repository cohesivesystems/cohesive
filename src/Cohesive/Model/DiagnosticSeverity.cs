using System.Text.Json.Serialization;

namespace Cohesive.Model;

/// <summary>
/// Severity level for shape graph diagnostics.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DiagnosticSeverity
{
    /// <summary>Represents the info option.</summary>
    Info = 0,
    /// <summary>Represents the warning option.</summary>
    Warning = 1,
    /// <summary>Represents the error option.</summary>
    Error = 2
}
