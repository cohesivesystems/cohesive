namespace Cohesive.Processes.Runtime;

/// <summary>
/// Result produced by a completed process execution.
/// </summary>
/// <param name="ProcessId">Identifier of the completed process execution.</param>
/// <param name="ProcessName">Name of the executed process definition.</param>
/// <param name="Result">Final process result value, if one was produced.</param>
/// <param name="FinalPlace">Execution place in which the process completed.</param>
/// <param name="Variables">Final process variable bindings captured at completion.</param>
/// <param name="Transitions">Entity transitions committed during the execution.</param>
/// <param name="ExecutedEffects">Effect requests that were executed successfully.</param>
/// <param name="PendingEffects">Effects that remain pending when the run result was produced.</param>
/// <param name="DeadLetters">Effects that were dead-lettered during execution.</param>
public sealed record ProcessRunResult(
    string ProcessId,
    string ProcessName,
    object? Result,
    string FinalPlace,
    IReadOnlyDictionary<string, object?> Variables,
    IReadOnlyList<TransitionResult> Transitions,
    IReadOnlyList<EffectExecution> ExecutedEffects,
    IReadOnlyList<ProcessPendingEffect> PendingEffects,
    IReadOnlyList<ProcessDeadLetter> DeadLetters
);
