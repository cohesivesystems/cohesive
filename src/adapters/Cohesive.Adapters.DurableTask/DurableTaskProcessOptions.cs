namespace Cohesive.Adapters.DurableTask;

/// <summary>
/// Durable Task adapter options for process orchestration.
/// </summary>
public sealed record DurableTaskProcessOptions
{
    /// <summary>
    /// Registered orchestration name used for process executions.
    /// </summary>
    public string OrchestrationName { get; init; } = "Cohesive.Process";

    /// <summary>
    /// Registered orchestration version.
    /// </summary>
    public string OrchestrationVersion { get; init; } = string.Empty;

    /// <summary>
    /// Registered activity name used for activity-backed process node execution.
    /// </summary>
    public string ActivityName { get; init; } = "Cohesive.Process.Node";

    /// <summary>
    /// Registered activity version.
    /// </summary>
    public string ActivityVersion { get; init; } = string.Empty;

    /// <summary>
    /// Timeout used by durable completion waits.
    /// </summary>
    public TimeSpan CompletionTimeout { get; init; } = Timeout.InfiniteTimeSpan;
}
