namespace Cohesive.AI.Training;

/// <summary>
/// Identifies training code before it is resolved into an immutable revision.
/// </summary>
/// <param name="Repository">Provider-specific repository identifier (for example, <c>owner/repo</c> or a logical local repository name).</param>
/// <param name="Revision">Logical revision to resolve, such as a branch, tag, or alias.</param>
/// <param name="SubPath">Optional relative path to package from within the repository.</param>
/// <param name="FileSystem">Optional file-system specific settings used by local repositories.</param>
public readonly record struct CodeReference(
    string Repository,
    string Revision,
    string? SubPath = null,
    FileSystemCodeReferenceOptions? FileSystem = null
    );
