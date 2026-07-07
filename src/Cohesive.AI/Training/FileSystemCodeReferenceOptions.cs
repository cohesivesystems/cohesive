namespace Cohesive.AI.Training;

/// <summary>
/// Optional file-system specific settings used when resolving local training code.
/// </summary>
public sealed record FileSystemCodeReferenceOptions
{
    /// <summary>
    /// Absolute or relative repository path to package from.
    /// </summary>
    public string? RepositoryPath { get; init; }

    /// <summary>
    /// Whether the default generated-file exclusions should be applied.
    /// </summary>
    public bool? IncludeDefaultExclusions { get; init; }

    /// <summary>
    /// Additional directory names excluded at any depth.
    /// </summary>
    public string[]? ExcludedDirectoryNames { get; init; }

    /// <summary>
    /// Additional directory names excluded only when they appear at the repository root.
    /// </summary>
    public string[]? ExcludedRootDirectoryNames { get; init; }

    /// <summary>
    /// Additional file names excluded regardless of path.
    /// </summary>
    public string[]? ExcludedFileNames { get; init; }

    /// <summary>
    /// Additional file extensions excluded regardless of path.
    /// </summary>
    public string[]? ExcludedFileExtensions { get; init; }

    /// <summary>
    /// Creates a merged view where <paramref name="overrides"/> replaces scalar values and unions collection values.
    /// </summary>
    public FileSystemCodeReferenceOptions Merge(FileSystemCodeReferenceOptions? overrides)
    {
        if (overrides is null)
            return this;

        return new()
        {
            RepositoryPath = string.IsNullOrWhiteSpace(overrides.RepositoryPath) ? RepositoryPath : overrides.RepositoryPath,
            IncludeDefaultExclusions = overrides.IncludeDefaultExclusions ?? IncludeDefaultExclusions,
            ExcludedDirectoryNames = Union(ExcludedDirectoryNames, overrides.ExcludedDirectoryNames),
            ExcludedRootDirectoryNames = Union(ExcludedRootDirectoryNames, overrides.ExcludedRootDirectoryNames),
            ExcludedFileNames = Union(ExcludedFileNames, overrides.ExcludedFileNames),
            ExcludedFileExtensions = Union(ExcludedFileExtensions, overrides.ExcludedFileExtensions)
        };
    }

    static string[]? Union(string[]? left, string[]? right)
    {
        if ((left is null || left.Length == 0) && (right is null || right.Length == 0))
            return null;

        return [.. (left ?? [])
            .Concat(right ?? [])
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }
}
