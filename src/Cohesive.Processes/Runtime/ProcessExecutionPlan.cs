namespace Cohesive.Processes.Runtime;

/// <summary>
/// Durable execution directive for the next process step.
/// </summary>
public enum ProcessExecutionPlanKind
{
    Advance = 0,
    ExecuteNode = 1,
    Wait = 2,
    Complete = 3
}

/// <summary>
/// Planned next step for a process execution snapshot.
/// </summary>
/// <param name="Kind">Execution directive describing how the caller should advance the process.</param>
/// <param name="Checkpoint">Checkpoint snapshot produced after planning the next step.</param>
/// <param name="NodeName">Node name to execute when <see cref="Kind"/> is <see cref="ProcessExecutionPlanKind.ExecuteNode"/>.</param>
/// <param name="Wait">Wait metadata when <see cref="Kind"/> is <see cref="ProcessExecutionPlanKind.Wait"/>.</param>
public sealed record ProcessExecutionPlan(
    ProcessExecutionPlanKind Kind,
    ProcessCheckpoint Checkpoint,
    string? NodeName = null,
    ProcessWaitRequest? Wait = null
);
