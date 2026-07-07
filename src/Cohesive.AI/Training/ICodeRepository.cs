namespace Cohesive.AI.Training;

/// <summary>
/// Resolves and opens code from an external repository.
/// </summary>
public interface ICodeRepository
{
    /// <summary>
    /// Resolves a logical revision (branch/tag/alias) into a concrete immutable revision.
    /// </summary>
    ValueTask<CodeRevision> ResolveRevisionAsync(CodeReference reference, CancellationToken ct = default);

    /// <summary>
    /// Opens a stream of an archive representing the repository at a specific revision.
    /// The stream is owned by the caller and must be disposed.
    /// </summary>
    ValueTask<CodeArchive> OpenArchiveAsync(CodeRevision revision, CancellationToken ct = default);
}
