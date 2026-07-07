namespace Cohesive.AI.Inference;

/// <summary>
/// Represents a model that generates embeddings from batches of inputs.
/// </summary>
public interface IEmbeddingModel : IModel
{
    /// <summary>
    /// Computes embeddings for a batch of input payloads.
    /// </summary>
    /// <param name="request">Batch embedding request.</param>
    /// <param name="ct">Cancellation token for cooperative cancellation.</param>
    /// <returns>The produced embedding vectors.</returns>
    ValueTask<EmbeddingBatchResult> EmbedAsync(EmbeddingBatchRequest request, CancellationToken ct = default);
}
