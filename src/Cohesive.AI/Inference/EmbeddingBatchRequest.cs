namespace Cohesive.AI.Inference;

/// <summary>
/// Describes a batch embedding request.
/// </summary>
/// <param name="Inputs">UTF-8 encoded payloads to embed.</param>
/// <param name="BatchSize">Optional override for provider batch size.</param>
public readonly record struct EmbeddingBatchRequest(
    IReadOnlyList<ReadOnlyMemory<byte>> Inputs,
    int? BatchSize = null
    );