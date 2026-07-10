using System.Text.Json;

namespace Cohesive.Host.Cli;

/// <summary>
/// Invocation-specific options used when executing a CLI application.
/// </summary>
public sealed class CliInvocationOptions
{
    /// <summary>Gets the standard output.</summary>
    public TextWriter? StandardOutput { get; init; }

    /// <summary>Gets the error output.</summary>
    public TextWriter? ErrorOutput { get; init; }
    
    /// <summary>Gets the json serializer options.</summary>
    public JsonSerializerOptions? JsonSerializerOptions { get; init; }
}
