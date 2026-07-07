namespace Cohesive.Processes.Runtime;

/// <summary>
/// Mutable in-memory runtime state accumulated while a process instance executes.
/// </summary>
/// <param name="context">Execution context backing the current runtime state.</param>
sealed class ProcessRuntimeState(ProcessExecutionContext context)
{
    /// <summary>
    /// Current execution context, including parameters, variables, and place.
    /// </summary>
    public ProcessExecutionContext Context { get; } = context;

    /// <summary>
    /// UTC timestamp when the current process execution started.
    /// </summary>
    public required DateTimeOffset StartedAtUtc { get; init; }

    /// <summary>
    /// UTC timestamp when the current process execution completed, if it has completed.
    /// </summary>
    public DateTimeOffset? CompletedAtUtc { get; set; }

    /// <summary>
    /// Continuation frames representing nested control-flow scopes that must be resumed later.
    /// </summary>
    public List<ProcessExecutionFrame> ContinuationFrames { get; } = [];

    /// <summary>
    /// Transition results applied during the current execution.
    /// </summary>
    public List<TransitionResult> Transitions { get; } = [];

    /// <summary>
    /// Effect executions completed during the current execution.
    /// </summary>
    public List<EffectExecution> ExecutedEffects { get; } = [];

    /// <summary>
    /// Effects deferred for later dispatch.
    /// </summary>
    public List<ProcessPendingEffect> DeferredEffects { get; } = [];

    /// <summary>
    /// Automatically scheduled effects waiting to be drained.
    /// </summary>
    public List<ProcessPendingEffect> AutoEffects { get; } = [];

    /// <summary>
    /// Dead-letter records produced during execution.
    /// </summary>
    public List<ProcessDeadLetter> DeadLetters { get; } = [];

    /// <summary>
    /// Indicates whether automatic effects are currently being drained.
    /// </summary>
    public bool IsDrainingAutoEffects { get; set; }

    /// <summary>
    /// Nesting depth of the active transaction scope.
    /// </summary>
    public int TransactionDepth { get; set; }

    /// <summary>
    /// Rehydrates runtime state from a durable checkpoint.
    /// </summary>
    /// <param name="checkpoint">Checkpoint snapshot to restore.</param>
    /// <returns>Restored runtime state.</returns>
    public static ProcessRuntimeState Restore(ProcessCheckpoint checkpoint)
    {
        var restoredContext = new ProcessExecutionContext(
            processId: checkpoint.ProcessId,
            processName: checkpoint.ProcessName,
            parameters: checkpoint.Parameters,
            currentPlace: checkpoint.CurrentPlace
            );
        restoredContext.RestoreVariables(new(checkpoint.Variables, StringComparer.Ordinal));

        var restoredState = new ProcessRuntimeState(restoredContext)
        {
            StartedAtUtc = checkpoint.StartedAtUtc,
            CompletedAtUtc = checkpoint.CompletedAtUtc
        };
        restoredState.ContinuationFrames.AddRange(checkpoint.ContinuationFrames);
        restoredState.Transitions.AddRange(checkpoint.Transitions);
        restoredState.ExecutedEffects.AddRange(checkpoint.ExecutedEffects);
        restoredState.DeferredEffects.AddRange(checkpoint.PendingEffects);
        restoredState.DeadLetters.AddRange(checkpoint.DeadLetters);
        return restoredState;
    }
}

sealed record ProcessRuntimeSnapshot(
    string CurrentPlace,
    Dictionary<string, object?> Variables,
    IReadOnlyList<ProcessExecutionFrame> ContinuationFrames,
    int TransitionCount,
    int ExecutedEffectCount,
    int DeferredEffectCount,
    int AutoEffectCount,
    int DeadLetterCount,
    int TransactionDepth
    );

enum ProcessWaitHandlingMode
{
    Block = 0,
    Yield = 1
}

readonly record struct ProcessNodeExecutionOutcome(
    string? NextNode,
    bool IsEnded,
    object? Result,
    ProcessWaitRequest? Wait)
{
    public bool IsWaiting => Wait is not null;
}

readonly record struct LoadedProcessExecutionState(ProcessRuntimeState State, string? CurrentNode);
