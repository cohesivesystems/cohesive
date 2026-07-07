namespace Cohesive.Processes.Runtime;

/// <summary>
/// Durable wait instruction emitted by resumable process execution.
/// </summary>
public sealed record ProcessWaitRequest(
    ProcessWaitType WaitType,
    string NodeName,
    string Key,
    TimeSpan? Timeout,
    string? CaptureVar,
    string? NextNode
);

/// <summary>
/// Partial or completed execution result for a resumable process slice.
/// </summary>
public sealed record ProcessExecutionSliceResult(
    string ProcessId,
    string ProcessName,
    ProcessExecutionStatus Status,
    string CurrentPlace,
    IReadOnlyDictionary<string, object?> Variables,
    IReadOnlyList<TransitionResult> Transitions,
    IReadOnlyList<EffectExecution> ExecutedEffects,
    IReadOnlyList<ProcessPendingEffect> PendingEffects,
    IReadOnlyList<ProcessDeadLetter> DeadLetters,
    object? Result = null,
    ProcessWaitRequest? Wait = null
);
