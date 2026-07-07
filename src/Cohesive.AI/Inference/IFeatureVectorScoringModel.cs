namespace Cohesive.AI.Inference;

/// <summary>
/// Represents a model that scores numeric feature vectors.
/// </summary>
public interface IFeatureVectorScoringModel : IModel
{
    /// <summary>
    /// Computes scores for a batch of feature vectors.
    /// </summary>
    /// <param name="request">Feature-vector scoring request.</param>
    /// <param name="ct">Cancellation token for cooperative cancellation.</param>
    /// <returns>Scores aligned to the input feature-vector order.</returns>
    ValueTask<FeatureVectorBatchResult> ScoreAsync(FeatureVectorBatchRequest request, CancellationToken ct = default);
}
