namespace Cohesive.AI.Training;

/// <summary>
/// Starts and manages model training workflows.
/// </summary>
public interface IModelTrainer
{
    /// <summary>
    /// Starts a training job.
    /// </summary>
    /// <param name="request">Training job request.</param>
    /// <param name="ct">Cancellation token for cooperative cancellation.</param>
    /// <returns>The accepted training job reference.</returns>
    ValueTask<TrainingJobReference> StartAsync(TrainingRequest request, CancellationToken ct = default);

    /// <summary>
    /// Resolves the latest known status for a training job by identifier.
    /// </summary>
    /// <param name="jobId">Stable training job identifier.</param>
    /// <param name="ct">Cancellation token for cooperative cancellation.</param>
    /// <returns>The current job state snapshot.</returns>
    ValueTask<TrainingJobState> GetStatusAsync(string jobId, CancellationToken ct = default);
}
