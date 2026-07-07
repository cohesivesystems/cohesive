namespace Cohesive.AI.Training;

/// <summary>
/// Addressable packaged training code artifact.
/// </summary>
/// <param name="BlobUri">Location of the packaged code archive.</param>
/// <param name="Version">Stable content hash identifying the artifact contents.</param>
public sealed record TrainingCodeArtifact(
    string BlobUri,
    string Version
    );
