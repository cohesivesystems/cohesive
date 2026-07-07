namespace Cohesive.Processes.Runtime;

/// <summary>
/// Process state checkpoint persisted by storage adapters.
/// </summary>
/// <param name="ProcessId">Stable process execution id.</param>
/// <param name="ProcessName">Process definition name.</param>
/// <param name="CurrentNode">Current node to execute next, or null when the process has completed or is unwinding continuations.</param>
/// <param name="CurrentPlace">Current execution place.</param>
/// <param name="Status">Current process execution status.</param>
/// <param name="Parameters">Immutable process parameters.</param>
/// <param name="Variables">Mutable process variables captured at the checkpoint.</param>
/// <param name="ContinuationFrames">Continuation stack used for nested move/locality flow.</param>
/// <param name="Transitions">Transition results produced so far.</param>
/// <param name="ExecutedEffects">Effect executions completed so far.</param>
/// <param name="PendingEffects">Deferred effect requests still pending dispatch.</param>
/// <param name="DeadLetters">Dead-lettered effects accumulated so far.</param>
/// <param name="Result">Final process result when available.</param>
/// <param name="StartedAtUtc">Process start timestamp.</param>
/// <param name="UpdatedAtUtc">Last checkpoint update timestamp.</param>
/// <param name="CompletedAtUtc">Process completion timestamp when the execution is terminal.</param>
public sealed record ProcessCheckpoint(
    string ProcessId,
    string ProcessName,
    string? CurrentNode,
    string CurrentPlace,
    ProcessExecutionStatus Status,
    IReadOnlyDictionary<string, object?> Parameters,
    IReadOnlyDictionary<string, object?> Variables,
    IReadOnlyList<ProcessExecutionFrame> ContinuationFrames,
    IReadOnlyList<TransitionResult> Transitions,
    IReadOnlyList<EffectExecution> ExecutedEffects,
    IReadOnlyList<ProcessPendingEffect> PendingEffects,
    IReadOnlyList<ProcessDeadLetter> DeadLetters,
    object? Result,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? CompletedAtUtc
);
