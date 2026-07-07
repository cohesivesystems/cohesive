namespace Cohesive.AI.Training;

/// <summary>
/// Caches packaged training code artifacts by immutable source revision.
/// </summary>
public interface ICodeArtifactCache
{
    /// <summary>
    /// Attempts to locate a cached artifact for the supplied immutable revision.
    /// </summary>
    ValueTask<TrainingCodeArtifact?> GetAsync(CodeRevision revision, CancellationToken ct = default);

    /// <summary>
    /// Stores a packaged artifact for future reuse.
    /// </summary>
    ValueTask SetAsync(CodeRevision revision, TrainingCodeArtifact artifact, CancellationToken ct = default);
}
