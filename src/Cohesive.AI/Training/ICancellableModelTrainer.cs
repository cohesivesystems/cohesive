namespace Cohesive.AI.Training;

/// <summary>
/// Extends model training with an idempotent, provider-observed job-cancellation capability.
/// </summary>
/// <remarks>
/// Implementations must treat an exact <see cref="TrainingJobCancellation"/> replay as the same logical
/// cancellation operation. Provider acceptance is not terminal job evidence; callers must continue observation
/// through <see cref="IModelTrainer.GetStatusAsync(string, CancellationToken)"/> until the provider reports a
/// terminal state. A cancellation token passed to <see cref="CancelAsync"/> cancels the caller's wait and does not
/// itself assert that the provider job was cancelled.
/// </remarks>
public interface ICancellableModelTrainer : IModelTrainer
{
    /// <summary>
    /// Requests cancellation of one exact provider training job under a stable logical operation identity.
    /// </summary>
    /// <param name="cancellation">Stable cancellation operation identity and provider job identity.</param>
    /// <param name="ct">Token that cancels the caller's wait for an authoritative cancellation result.</param>
    /// <returns>
    /// A closed provider-neutral result distinguishing acceptance, prior terminal state, authoritative absence,
    /// deterministic rejection, and an unresolved or transient attempt.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="cancellation"/> is <see langword="null"/>.</exception>
    /// <exception cref="TrainingJobCancellationConflictException">
    /// The implementation observes <paramref name="cancellation"/>'s identity already bound to another job.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="ct"/> is cancelled before the implementation returns an authoritative result. The provider
    /// cancellation outcome may still be ambiguous and must be reconciled before redispatch.
    /// </exception>
    ValueTask<TrainingJobCancellationResult> CancelAsync(
        TrainingJobCancellation cancellation,
        CancellationToken ct = default);
}
