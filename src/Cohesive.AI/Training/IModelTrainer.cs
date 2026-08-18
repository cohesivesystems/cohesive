namespace Cohesive.AI.Training;

/// <summary>
/// Submits, reconciles, and observes model training jobs.
/// </summary>
public interface IModelTrainer
{
    /// <summary>
    /// Submits an exact training request under a stable logical identity.
    /// </summary>
    /// <param name="submission">Stable logical submission identity and exact request content.</param>
    /// <param name="ct">Cancellation token for cooperative cancellation.</param>
    /// <returns>The provider job accepted for the logical submission.</returns>
    /// <exception cref="TrainingJobSubmissionConflictException">
    /// Thrown when the submission identity is already bound to a different request fingerprint.
    /// </exception>
    ValueTask<TrainingJobReference> SubmitAsync(
        TrainingJobSubmission submission,
        CancellationToken ct = default);

    /// <summary>
    /// Reconciles a possibly ambiguous submission attempt without creating a second logical training job.
    /// </summary>
    /// <param name="submission">Stable logical submission identity and exact request content to reconcile.</param>
    /// <param name="ct">Cancellation token for cooperative cancellation.</param>
    /// <returns>
    /// Whether the exact submission was accepted, is authoritatively absent, or could not be resolved.
    /// </returns>
    /// <exception cref="TrainingJobSubmissionConflictException">
    /// Thrown when the submission identity is already bound to a different request fingerprint.
    /// </exception>
    ValueTask<TrainingJobSubmissionResolution> ReconcileSubmissionAsync(
        TrainingJobSubmission submission,
        CancellationToken ct = default);

    /// <summary>
    /// Resolves the latest known status for a training job by identifier.
    /// </summary>
    /// <param name="jobId">Stable training job identifier.</param>
    /// <param name="ct">Cancellation token for cooperative cancellation.</param>
    /// <returns>The current job state snapshot.</returns>
    ValueTask<TrainingJobState> GetStatusAsync(string jobId, CancellationToken ct = default);
}
