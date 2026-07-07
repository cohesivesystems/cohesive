namespace Cohesive.AI.Inference;

/// <summary>
/// Represents a model that scores graph inputs.
/// </summary>
public interface IGraphScoringModel : IModel
{
    /// <summary>
    /// Computes scores for a batch of graphs.
    /// </summary>
    /// <param name="request">Graph-scoring request.</param>
    /// <param name="ct">Cancellation token for cooperative cancellation.</param>
    /// <returns>Scores for the requested graphs.</returns>
    ValueTask<GraphBatchResult> ScoreAsync(GraphBatchRequest request, CancellationToken ct = default);
}
