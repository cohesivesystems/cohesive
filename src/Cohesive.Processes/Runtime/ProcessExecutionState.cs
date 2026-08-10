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
/// <param name="ProcessId">Stable identifier assigned to the process instance.</param>
/// <param name="ProcessName">Name of the process definition used to start the instance.</param>
/// <param name="StartedAtUtc">UTC time at which the start was accepted.</param>
public sealed record ProcessStartResult(
    string ProcessId,
    string ProcessName,
    DateTimeOffset StartedAtUtc
);

/// <summary>
/// Current durable process execution state.
/// </summary>
/// <param name="ProcessId">Stable physical repository key assigned by the backing execution engine.</param>
/// <param name="ProcessName">Process definition name or stable definition identity when retained by the execution engine.</param>
/// <param name="Status">Current high-level lifecycle status.</param>
/// <param name="StartedAtUtc">UTC creation or start time when retained by the execution engine.</param>
/// <param name="UpdatedAtUtc">UTC time of the latest retained execution update.</param>
/// <param name="CompletedAtUtc">UTC terminal completion time, or <see langword="null"/> for nonterminal or unknown executions.</param>
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
/// <param name="ErrorType">Provider or application error type when retained.</param>
/// <param name="ErrorMessage">Human-readable error message when retained.</param>
/// <param name="StackTrace">Diagnostic stack trace when retained; callers must not depend on its format.</param>
/// <param name="IsNonRetriable">Whether the backing engine classified the failure as terminal without retry.</param>
/// <param name="Properties">Read-only provider-specific diagnostic properties when retained.</param>
/// <param name="InnerError">Nested causal error evidence when retained.</param>
public sealed record ProcessExecutionError(
    string? ErrorType,
    string? ErrorMessage,
    string? StackTrace,
    bool IsNonRetriable = false,
    IReadOnlyDictionary<string, object?>? Properties = null,
    ProcessExecutionError? InnerError = null
);
