namespace Cohesive.AI.Training;

/// <summary>
/// Describes a dataset artifact passed to a model-training runtime.
/// </summary>
/// <param name="Name">Stable dataset binding name exposed to the training runtime.</param>
/// <param name="Location">Location of the artifact.</param>
/// <param name="Kind">Whether the artifact should be treated as a file or folder input.</param>
/// <param name="Format">Optional dataset serialization format.</param>
/// <param name="SchemaHash">Optional schema/version fingerprint.</param>
/// <param name="RowCount">Optional row-count hint.</param>
public sealed record TrainingDatasetArtifact(
    string Name,
    Uri Location,
    TrainingDatasetArtifactKind Kind,
    string? Format,
    string? SchemaHash,
    int? RowCount
    );

/// <summary>
/// Declares how a dataset artifact should be mounted or downloaded by a training runtime.
/// </summary>
public enum TrainingDatasetArtifactKind : byte
{
    /// <summary>Represents the file option.</summary>
    File = 0,
    /// <summary>Represents the folder option.</summary>
    Folder = 1
}
