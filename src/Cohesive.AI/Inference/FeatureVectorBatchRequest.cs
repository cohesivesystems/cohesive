namespace Cohesive.AI.Inference;

/// <summary>
/// Describes a batch request for feature-vector scoring.
/// </summary>
/// <param name="FeatureVectors">Ordered feature vectors to score.</param>
/// <param name="BatchSize">Optional override for provider batch size.</param>
public readonly record struct FeatureVectorBatchRequest(
    IReadOnlyList<ReadOnlyMemory<float>> FeatureVectors,
    int? BatchSize = null
    );
