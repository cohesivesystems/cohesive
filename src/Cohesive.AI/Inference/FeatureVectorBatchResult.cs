namespace Cohesive.AI.Inference;

/// <summary>
/// Represents scores for a feature-vector batch request.
/// </summary>
/// <param name="Scores">Scores aligned to the request feature-vector order.</param>
public readonly record struct FeatureVectorBatchResult(
    IReadOnlyList<float> Scores
    );
