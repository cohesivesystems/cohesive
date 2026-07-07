namespace Cohesive.AI.Inference;

/// <summary>
/// Describes a batch request for pairwise scoring.
/// </summary>
/// <param name="Pairs">Ordered input pairs to score.</param>
/// <param name="BatchSize">Optional override for provider batch size.</param>
public readonly record struct PairScoreBatchRequest(
    IReadOnlyList<(ReadOnlyMemory<byte> A, ReadOnlyMemory<byte> B)> Pairs,
    int? BatchSize = null
    );