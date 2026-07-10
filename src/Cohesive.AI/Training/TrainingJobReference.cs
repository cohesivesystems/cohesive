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
    /// <summary>Represents the pending job option.</summary>
    Pending = 0,
    
    /// <summary>Represents the running job option.</summary>
    Running = 1,
    
    /// <summary>Represents the completed job option.</summary>
    Completed = 2,
    
    /// <summary>Represents the failed job option.</summary>
    Failed = 3,
    
    /// <summary>Represents the canceled job option.</summary>
    Cancelled = 4,
    
    /// <summary>Represents an unknown job option.</summary>
    Unknown = 5
}
