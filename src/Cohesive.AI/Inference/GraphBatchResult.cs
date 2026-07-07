namespace Cohesive.AI.Inference;

/// <summary>
/// Represents scores for a graph batch request.
/// </summary>
/// <param name="Scores">Score vector returned by the model.</param>
public readonly record struct GraphBatchResult(
    ReadOnlyMemory<float> Scores
    );