namespace Cohesive.Processes.Runtime;

/// <summary>
/// High-level lifecycle states for durable process execution.
/// </summary>
public enum ProcessExecutionStatus
{
    /// <summary>
    /// Process has been accepted but has not started running.
    /// </summary>
    Pending,

    /// <summary>
    /// Process is actively running.
    /// </summary>
    Running,

    /// <summary>
    /// Process is waiting on an external signal or timer.
    /// </summary>
    Waiting,

    /// <summary>
    /// Process completed successfully.
    /// </summary>
    Completed,

    /// <summary>
    /// Process completed with failure.
    /// </summary>
    Failed,

    /// <summary>
    /// Process was canceled.
    /// </summary>
    Cancelled,

    /// <summary>
    /// Process was explicitly terminated.
    /// </summary>
    Terminated,

    /// <summary>
    /// Process is suspended and not making progress.
    /// </summary>
    Suspended
}

/// <summary>
/// Accepted process start information.
/// </summary>
public sealed record ProcessStartResult(
    string ProcessId,
    string ProcessName,
    DateTimeOffset StartedAtUtc
);

/// <summary>
/// Current durable process execution state.
/// </summary>
public sealed record ProcessExecutionState(
    string ProcessId,
    string? ProcessName,
    ProcessExecutionStatus Status,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? UpdatedAtUtc,
    DateTimeOffset? CompletedAtUtc
    )
{
    /// <summary>
    /// Process input parameters retained by the execution backend, when available.
    /// </summary>
    public IReadOnlyDictionary<string, object?>? Parameters { get; init; }

    /// <summary>
    /// Process output retained by the execution backend, when available.
    /// </summary>
    public object? Output { get; init; }

    /// <summary>
    /// Structured execution error retained by the execution backend, when available.
    /// </summary>
    public ProcessExecutionError? Error { get; init; }

    /// <summary>
    /// Human-readable execution failure message retained by the execution backend, when available.
    /// </summary>
    public string? FailureMessage { get; init; }

    /// <summary>
    /// Returns true when the process is in a terminal state.
    /// </summary>
    public bool IsTerminal =>
        Status is ProcessExecutionStatus.Completed
        or ProcessExecutionStatus.Failed
        or ProcessExecutionStatus.Cancelled
        or ProcessExecutionStatus.Terminated;
}

/// <summary>
/// Structured process execution error data retained by a process-engine execution backend.
/// </summary>
public sealed record ProcessExecutionError(
    string? ErrorType,
    string? ErrorMessage,
    string? StackTrace,
    bool IsNonRetriable = false,
    IReadOnlyDictionary<string, object?>? Properties = null,
    ProcessExecutionError? InnerError = null
);
