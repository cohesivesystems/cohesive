namespace Cohesive.AI.Inference;

/// <summary>
/// Represents a model that scores input pairs.
/// </summary>
public interface IPairScoringModel : IModel
{
    /// <summary>
    /// Computes scores for a batch of input pairs.
    /// </summary>
    /// <param name="request">Pair-scoring request.</param>
    /// <param name="ct">Cancellation token for cooperative cancellation.</param>
    /// <returns>Scores for each requested pair.</returns>
    ValueTask<PairScoreBatchResult> ScoreAsync(PairScoreBatchRequest request, CancellationToken ct = default);
}