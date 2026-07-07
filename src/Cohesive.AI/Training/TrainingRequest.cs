namespace Cohesive.AI.Training;

/// <summary>
/// Describes a model training request.
/// </summary>
/// <param name="ModelName">Logical model name to train.</param>
/// <param name="BaseVersion">Optional base model version used as the starting point.</param>
/// <param name="Datasets">Dataset artifacts made available to the training runtime.</param>
/// <param name="Code">Optional packaged training code artifact made available to the training runtime.</param>
/// <param name="OutputModelName">Logical name for the produced model artifact.</param>
/// <param name="ExperimentName">Optional experiment grouping name.</param>
/// <param name="ComputeTarget">Optional logical or provider-specific compute target.</param>
/// <param name="ConfigJson">Provider-specific training configuration JSON payload.</param>
public sealed record TrainingRequest(
    string ModelName,
    string? BaseVersion,
    IReadOnlyList<TrainingDatasetArtifact> Datasets,
    TrainingCodeArtifact? Code,
    string OutputModelName,
    string? ExperimentName,
    string? ComputeTarget,
    string ConfigJson
    );
