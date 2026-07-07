namespace Cohesive.AI.Inference;

/// <summary>
/// Represents a batch embedding result.
/// </summary>
/// <param name="Embeddings">Embedding vectors aligned to the request inputs.</param>
public readonly record struct EmbeddingBatchResult(
    IReadOnlyList<ReadOnlyMemory<float>> Embeddings
    );