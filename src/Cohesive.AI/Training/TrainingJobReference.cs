namespace Cohesive.AI.Training;

/// <summary>
/// Stable identifier returned when a training job is accepted by an external runtime.
/// </summary>
/// <param name="JobId">Provider-specific training job identifier.</param>
/// <param name="Status">Initial training job status.</param>
public sealed record TrainingJobReference(
    string JobId,
    TrainingJobStatus Status
    );

/// <summary>
/// Represents high-level training job lifecycle states.
/// </summary>
public enum TrainingJobStatus
{
    Pending = 0,
    Running = 1,
    Completed = 2,
    Failed = 3,
    Cancelled = 4,
    Unknown = 5
}
