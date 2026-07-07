using System.Text.Json;

namespace Cohesive.Host.Cli;

/// <summary>
/// Invocation-specific options used when executing a CLI application.
/// </summary>
public sealed class CliInvocationOptions
{
    public TextWriter? StandardOutput { get; init; }

    public TextWriter? ErrorOutput { get; init; }
    
    public JsonSerializerOptions? JsonSerializerOptions { get; init; }
}
