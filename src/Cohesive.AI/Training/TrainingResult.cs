namespace Cohesive.AI.Training;

/// <summary>
/// Represents the result of a completed training job.
/// </summary>
/// <param name="ModelName">Logical model name of the trained artifact.</param>
/// <param name="Version">Version assigned to the trained artifact.</param>
/// <param name="ArtifactLocation">Portable absolute URI text identifying where model artifacts are stored.</param>
/// <param name="Metrics">
/// Reported training/evaluation metrics with unique names in stable ordinal name order.
/// </param>
public sealed record TrainingResult(
    string ModelName,
    string Version,
    string ArtifactLocation,
    IReadOnlyList<TrainingMetric> Metrics
    );

/// <summary>One portable named training or evaluation metric.</summary>
/// <param name="Name">Stable metric identity.</param>
/// <param name="Value">Observed metric value.</param>
public sealed record TrainingMetric(string Name, float Value);
