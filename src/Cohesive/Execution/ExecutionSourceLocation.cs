using System.Text.Json.Serialization;

namespace Cohesive.Execution;

/// <summary>A producer-supplied repository-relative line span; attribution, not semantic identity or access authority.</summary>
public sealed record ExecutionSourceLocation
{
    /// <summary>Creates a normalized portable source span.</summary>
    /// <param name="path">Slash-separated repository-relative file path without empty, dot or parent segments.</param>
    /// <param name="startLine">One-based first line.</param>
    /// <param name="endLine">One-based inclusive last line. A single line need not cover a complete expression.</param>
    /// <exception cref="ArgumentNullException">The path is null.</exception>
    /// <exception cref="ArgumentException">The path is empty, absolute, or contains noncanonical segments.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The line range is invalid.</exception>
    [JsonConstructor]
    public ExecutionSourceLocation(string path, int startLine, int endLine)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var segmentStart = 0;
        for (var index = 0; index <= path.Length; index++)
        {
            if (index < path.Length && path[index] != '/')
            {
                if (path[index] is '\\' or ':' || char.IsControl(path[index]))
                    throw new ArgumentException("Use a normalized repository-relative source path.", nameof(path));
                continue;
            }
            var segment = path.AsSpan(segmentStart, index - segmentStart);
            if (segment.IsEmpty || segment.SequenceEqual(".".AsSpan()) || segment.SequenceEqual("..".AsSpan()))
                throw new ArgumentException("Use a normalized repository-relative source path.", nameof(path));
            segmentStart = index + 1;
        }
        ArgumentOutOfRangeException.ThrowIfLessThan(startLine, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(endLine, startLine);
        Path = path;
        StartLine = startLine;
        EndLine = endLine;
    }

    /// <summary>Repository-relative path supplied by the producer.</summary>
    public string Path { get; }
    /// <summary>One-based first line.</summary>
    public int StartLine { get; }
    /// <summary>One-based inclusive last line.</summary>
    public int EndLine { get; }

    /// <summary>Captures a declaration line from an explicitly path-mapped compiler location.</summary>
    /// <param name="file">Compiler file path mapped under the portable virtual root /_/.</param>
    /// <param name="line">Compiler-supplied one-based declaration line.</param>
    /// <returns>A single-line location, or null when no portable mapping or line was provided.</returns>
    /// <exception cref="ArgumentNullException">The compiler file is null.</exception>
    /// <exception cref="ArgumentException">A mapped path contains invalid relative segments.</exception>
    public static ExecutionSourceLocation? FromCompiler(string file, int line)
    {
        ArgumentNullException.ThrowIfNull(file);
        var normalized = file.Replace('\\', '/');
        return line > 0 && normalized.StartsWith("/_/", StringComparison.Ordinal)
            ? new(normalized[3..], line, line) : null;
    }
}
