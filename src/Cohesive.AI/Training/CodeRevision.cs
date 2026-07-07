namespace Cohesive.AI.Training;

/// <summary>
/// Immutable, content-addressable revision of a source repository.
/// </summary>
/// <param name="Repository">Canonical repository identifier.</param>
/// <param name="CommitHash">Canonical commit SHA.</param>
/// <param name="SubPath">Optional relative path packaged from within the repository.</param>
public readonly record struct CodeRevision(
    string Repository,
    string CommitHash,
    string? SubPath = null
    );
