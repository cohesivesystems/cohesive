namespace Cohesive.AI.Training;

/// <summary>
/// Represents the result of a completed training job.
/// </summary>
/// <param name="ModelName">Logical model name of the trained artifact.</param>
/// <param name="Version">Version assigned to the trained artifact.</param>
/// <param name="ArtifactLocation">URI where model artifacts are stored.</param>
/// <param name="Metrics">Reported training/evaluation metrics.</param>
public sealed record TrainingResult(
    string ModelName,
    string Version,
    Uri ArtifactLocation,
    IReadOnlyDictionary<string, float> Metrics
    );