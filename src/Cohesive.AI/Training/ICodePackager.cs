namespace Cohesive.AI.Training;

/// <summary>
/// Packages repository contents into an addressable code artifact.
/// </summary>
public interface ICodePackager
{
    /// <summary>
    /// Packages the specified code archive into a reusable artifact.
    /// </summary>
    ValueTask<TrainingCodeArtifact> PackageAsync(CodeRevision revision, CodeArchive archive, CancellationToken ct = default);
}
