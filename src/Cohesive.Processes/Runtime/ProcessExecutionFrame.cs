namespace Cohesive.Processes.Runtime;

/// <summary>
/// Resumable execution frame persisted for nested process control flow.
/// </summary>
public sealed record ProcessExecutionFrame(
    string? NextNode,
    string ReturnPlace
);
