namespace Cohesive.AI.Inference;

/// <summary>
/// Represents scores for a batch pairwise scoring request.
/// </summary>
/// <param name="Scores">Scores aligned to the request pair order.</param>
public readonly record struct PairScoreBatchResult(
    IReadOnlyList<float> Scores
    );