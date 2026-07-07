namespace Cohesive.AI.Registry;

/// <summary>
/// Represents registered model metadata.
/// </summary>
/// <param name="ModelName">Logical model name.</param>
/// <param name="Version">Version identifier.</param>
/// <param name="ArtifactLocation">URI where model artifacts are stored.</param>
/// <param name="Metrics">Associated model metrics.</param>
/// <param name="IsProduction">Indicates whether the version is marked as production.</param>
public sealed record ModelMetadata(
    string ModelName,
    string Version,
    Uri ArtifactLocation,
    IReadOnlyDictionary<string, float> Metrics,
    bool IsProduction
    );