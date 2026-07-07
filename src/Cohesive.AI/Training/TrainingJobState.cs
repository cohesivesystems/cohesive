namespace Cohesive.AI.Training;

/// <summary>
/// Snapshot of the current training-job state.
/// </summary>
/// <param name="JobId">Provider-specific training job identifier.</param>
/// <param name="Status">Current job status.</param>
/// <param name="Result">Training result when the provider can surface one.</param>
/// <param name="Failure">Failure details when the provider reports an unsuccessful terminal state.</param>
public sealed record TrainingJobState(
    string JobId,
    TrainingJobStatus Status,
    TrainingResult? Result,
    TrainingJobFailure? Failure
    );

/// <summary>
/// Failure details reported for a training job.
/// </summary>
/// <param name="ErrorType">Provider-defined error classification.</param>
/// <param name="ErrorMessage">Human-readable failure description.</param>
/// <param name="IsTransient">Whether the failure is expected to be transient.</param>
public sealed record TrainingJobFailure(
    string ErrorType,
    string ErrorMessage,
    bool IsTransient
    );
