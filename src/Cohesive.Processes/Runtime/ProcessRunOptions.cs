namespace Cohesive.Processes.Runtime;

/// <summary>
/// Per-run execution options.
/// </summary>
public sealed record ProcessRunOptions
{
    /// <summary>
    /// Optional explicit process id.
    /// </summary>
    public string? ProcessId { get; init; }

    /// <summary>
    /// Optional initial place override.
    /// </summary>
    public string? InitialPlace { get; init; }
}