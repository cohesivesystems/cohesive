using System.Text.Json.Serialization;

namespace Cohesive.Model;

/// <summary>
/// Severity level for shape graph diagnostics.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DiagnosticSeverity
{
    Info = 0,
    Warning = 1,
    Error = 2
}
