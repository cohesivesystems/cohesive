namespace Cohesive.AI.Training;

/// <summary>
/// Describes training dataset extraction rules.
/// </summary>
/// <param name="ModelName">Logical model name the dataset is intended for.</param>
/// <param name="Since">Lower bound timestamp for included source data.</param>
/// <param name="ConfigJson">Provider-specific dataset build configuration JSON payload.</param>
public sealed record TrainingDatasetSpec(
    string ModelName,
    DateTime Since,
    string ConfigJson
    );