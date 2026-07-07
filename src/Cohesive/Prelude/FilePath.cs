using System.Collections.Immutable;

namespace Cohesive.Prelude;

/// <summary>
/// Identifies whether a <see cref="FilePath"/> represents a file or a directory.
/// </summary>
public enum FilePathKind
{
    /// <summary>
    /// The path represents a file.
    /// </summary>
    File,

    /// <summary>
    /// The path represents a directory.
    /// </summary>
    Directory
}

/// <summary>
/// A normalized file-system path that preserves whether it represents a file or directory,
/// and whether it is relative or absolute.
/// </summary>
public readonly record struct FilePath : IEquatable<FilePath>
{
    static readonly ImmutableArray<string> EmptySegments = [];
    readonly record struct ParsedRoot(string? Root, int BodyStart, int BodyLength, bool HadTrailingSeparator);

    /// <summary>
    /// Initializes a normalized path from an explicit root, segment list, and path kind.
    /// </summary>
    /// <param name="root">
    /// The absolute root, such as <c>/</c>, <c>C:</c>, or a UNC share root, or <see langword="null"/>
    /// for relative paths.
    /// </param>
    /// <param name="segments">The path segments beneath <paramref name="root"/>.</param>
    /// <param name="kind">Whether the path represents a file or a directory.</param>
    public FilePath(string? root, ImmutableArray<string> segments, FilePathKind kind)
    {
        Root = NormalizeRoot(root);
        Segments = NormalizeSegments(segments);
        Kind = kind;
        if (Kind == FilePathKind.File && Segments.IsDefaultOrEmpty)
            throw new ArgumentException("A file path must contain at least one segment.", nameof(segments));
    }

    /// <summary>
    /// Gets the absolute root for the path, or <see langword="null"/> when the path is relative.
    /// </summary>
    public string? Root { get; }

    /// <summary>
    /// Gets the normalized path segments beneath <see cref="Root"/>.
    /// </summary>
    public ImmutableArray<string> Segments { get; }

    /// <summary>
    /// Gets whether the path represents a file or a directory.
    /// </summary>
    public FilePathKind Kind { get; }

    /// <summary>
    /// Gets a value indicating whether the path is absolute.
    /// </summary>
    public bool IsAbsolute => Root is not null;

    /// <summary>
    /// Gets a value indicating whether the path is relative.
    /// </summary>
    public bool IsRelative => !IsAbsolute;

    /// <summary>
    /// Gets a value indicating whether the path represents a file.
    /// </summary>
    public bool IsFile => Kind == FilePathKind.File;

    /// <summary>
    /// Gets a value indicating whether the path represents a directory.
    /// </summary>
    public bool IsDirectory => Kind == FilePathKind.Directory;

    /// <summary>
    /// Gets a value indicating whether the path is an absolute root directory with no child segments.
    /// </summary>
    public bool IsRoot => IsAbsolute && Segments.IsDefaultOrEmpty && IsDirectory;

    /// <summary>
    /// Gets the terminal segment name or an empty string when the path has no segments.
    /// </summary>
    public string Name => Segments.IsDefaultOrEmpty ? string.Empty : Segments[^1];

    /// <summary>
    /// Gets the file name when the path represents a file; otherwise <see langword="null"/>.
    /// </summary>
    public string? FileName => IsFile ? Name : null;

    /// <summary>
    /// Gets the file name without its extension when the path represents a file; otherwise <see langword="null"/>.
    /// </summary>
    public string? FileNameWithoutExtension => FileName is null ? null : Path.GetFileNameWithoutExtension(FileName);

    /// <summary>
    /// Gets the extension, including the leading period, when the path represents a file.
    /// </summary>
    public string? Extension => FileName is null ? null : Path.GetExtension(FileName);

    /// <summary>
    /// Gets the parent directory of the current path.
    /// </summary>
    public FilePath Parent => Segments.IsDefaultOrEmpty
        ? AsDirectory()
        : new(root: Root, Segments.RemoveAt(Segments.Length - 1), FilePathKind.Directory);

    /// <summary>
    /// Determines whether the current path has the same root, segments, and kind as another path.
    /// </summary>
    /// <param name="other">The other path to compare.</param>
    /// <returns><see langword="true"/> when the paths are equal by value; otherwise <see langword="false"/>.</returns>
    public bool Equals(FilePath other) =>
        Kind == other.Kind
        && string.Equals(Root, other.Root, StringComparison.Ordinal)
        && Segments.SequenceEqual(other.Segments);

    /// <summary>
    /// Returns a hash code based on the root, path segments, and path kind.
    /// </summary>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Root, StringComparer.Ordinal);
        hash.Add(Kind);
        foreach (var segment in Segments)
            hash.Add(segment, StringComparer.Ordinal);
        return hash.ToHashCode();
    }

    /// <summary>
    /// Parses a textual path and infers whether it represents a file or a directory.
    /// </summary>
    /// <param name="path">The textual path to parse.</param>
    /// <param name="separator">The separator used by <paramref name="path"/> when it is not already normalized.</param>
    /// <returns>A normalized <see cref="FilePath"/> instance.</returns>
    /// <remarks>
    /// Extension-bearing terminal segments default to files. Extensionless paths default to directories
    /// unless they are constructed explicitly via <see cref="File(string, char)"/>.
    /// </remarks>
    public static FilePath FromPath(string path, char separator = '/') =>
        Parse(path, kind: null, separator);

    /// <summary>
    /// Parses a textual path using an explicit file or directory kind.
    /// </summary>
    /// <param name="path">The textual path to parse.</param>
    /// <param name="kind">The expected path kind.</param>
    /// <param name="separator">The separator used by <paramref name="path"/> when it is not already normalized.</param>
    /// <returns>A normalized <see cref="FilePath"/> instance.</returns>
    public static FilePath FromPath(string path, FilePathKind kind, char separator = '/') =>
        Parse(path, kind: kind, separator);

    /// <summary>
    /// Parses a textual path as a file path, including extensionless file names.
    /// </summary>
    public static FilePath File(string path, char separator = '/') =>
        FromPath(path, FilePathKind.File, separator);

    /// <summary>
    /// Parses a textual path as a directory path.
    /// </summary>
    public static FilePath Directory(string path, char separator = '/') =>
        FromPath(path, FilePathKind.Directory, separator);
    
    /// <summary>
    /// Parses a textual path as a directory path using the native directory separator.
    /// </summary>
    /// <param name="path"></param>
    /// <returns></returns>
    public static FilePath NativeDirectory(string path) =>
        Directory(path, Path.DirectorySeparatorChar);

    /// <summary>
    /// Formats the path using the specified separator.
    /// </summary>
    /// <param name="separator">The separator used to join the normalized segments.</param>
    /// <returns>The formatted path string.</returns>
    public string ToPath(char separator = '/')
    {
        var estimatedLength = GetFormattedPathLength();
        Span<char> initialBuffer = stackalloc char[Math.Min(estimatedLength, 256)];
        var builder = new ValueStringBuilder(initialBuffer);

        if (Root is not null)
        {
            if (Root == "/")
            {
                builder.Append(separator);
            }
            else
            {
                AppendWithSeparator(ref builder, Root.AsSpan(), separator);
                builder.Append(separator);
            }
        }

        for (var i = 0; i < Segments.Length; i++)
        {
            if (i > 0)
                builder.Append(separator);

            builder.Append(Segments[i]);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Formats the path using <see cref="Path.DirectorySeparatorChar"/>.
    /// </summary>
    public string ToNativePath() => ToPath(Path.DirectorySeparatorChar);

    /// <summary>
    /// Returns the normalized path formatted with forward slashes.
    /// </summary>
    public override string ToString() => ToPath();

    /// <summary>
    /// Returns a copy of the current path marked as a directory.
    /// </summary>
    public FilePath AsDirectory() =>
        IsDirectory ? this : new(Root, Segments, FilePathKind.Directory);

    /// <summary>
    /// Returns a copy of the current path marked as a file.
    /// </summary>
    public FilePath AsFile() =>
        IsFile ? this : new(Root, Segments, FilePathKind.File);

    /// <summary>
    /// Determines whether the current path begins with the specified prefix path.
    /// </summary>
    /// <param name="prefix">The candidate prefix to match.</param>
    /// <param name="comparison">The comparison used for roots and segments.</param>
    /// <returns>
    /// <see langword="true"/> when the roots match and the prefix segments match the leading portion
    /// of the current path. File prefixes must match the full path exactly.
    /// </returns>
    public bool StartsWith(FilePath prefix, StringComparison comparison = StringComparison.Ordinal)
    {
        if (IsAbsolute != prefix.IsAbsolute)
            return false;

        if (!string.Equals(Root, prefix.Root, comparison))
            return false;

        if (prefix.Segments.Length > Segments.Length)
            return false;

        if (prefix.IsFile && prefix.Segments.Length != Segments.Length)
            return false;

        for (var i = 0; i < prefix.Segments.Length; i++)
        {
            if (!string.Equals(Segments[i], prefix.Segments[i], comparison))
                return false;
        }

        if (prefix.Segments.Length == Segments.Length)
            return Kind == prefix.Kind;

        return prefix.IsDirectory;
    }

    /// <summary>
    /// Combines the current directory path with a relative segment or subpath.
    /// </summary>
    /// <param name="segment">The relative segment or subpath to append.</param>
    /// <returns>The combined path.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the current path represents a file.</exception>
    public FilePath Combine(string segment) => Combine(ParseRelativeCombinePath(segment));

    /// <summary>
    /// Combines the current directory path with a relative segment.
    /// </summary>
    /// <param name="segment">The relative segment to append.</param>
    /// <returns>The combined path.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the current path represents a file.</exception>
    public FilePath Combine(ReadOnlySpan<char> segment) => Combine(segment.ToString());

    /// <summary>
    /// Combines the current directory path with two relative segments in sequence.
    /// </summary>
    /// <param name="segment1">The first relative segment to append.</param>
    /// <param name="segment2">The second relative segment to append.</param>
    /// <returns>The combined path.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the current path represents a file.</exception>
    public FilePath Combine(string segment1, string segment2) => Combine(segment1).Combine(segment2);

    /// <summary>
    /// Combines the current directory path with a sequence of relative segments.
    /// </summary>
    /// <param name="segments">The relative segments to append in order.</param>
    /// <returns>The combined path.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the current path represents a file.</exception>
    public FilePath Combine(ReadOnlySpan<string> segments)
    {
        var path = this;
        foreach (var segment in segments)
            path = path.Combine(segment);
        return path;
    }

    /// <summary>
    /// Combines the current directory path with a relative <see cref="FilePath"/>.
    /// </summary>
    /// <param name="path2">The relative path to append.</param>
    /// <returns>The combined path.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the current path represents a file.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="path2"/> is absolute or invalid to append.</exception>
    public FilePath Combine(FilePath path2)
    {
        if (IsFile)
            throw new InvalidOperationException("Cannot combine additional path segments onto a file path.");

        if (path2.IsAbsolute)
            throw new ArgumentException("The combined path must be relative.", nameof(path2));

        if (path2.Segments.IsDefaultOrEmpty)
            return path2.IsDirectory ? this : throw new ArgumentException("Cannot combine an empty file path.", nameof(path2));

        return new(Root, Segments.AddRange(path2.Segments), path2.Kind);
    }

    /// <summary>
    /// Combines a directory path with a relative segment or subpath.
    /// </summary>
    /// <param name="path">The directory path receiving the segment.</param>
    /// <param name="segment">The relative segment or subpath to append.</param>
    /// <returns>The combined path.</returns>
    public static FilePath operator /(FilePath path, string segment) => path.Combine(segment);

    /// <summary>
    /// Combines a directory path with a relative <see cref="FilePath"/>.
    /// </summary>
    /// <param name="path1">The directory path receiving the appended path.</param>
    /// <param name="path2">The relative path to append.</param>
    /// <returns>The combined path.</returns>
    public static FilePath operator /(FilePath path1, FilePath path2) => path1.Combine(path2);

    /// <summary>
    /// Implicitly parses a textual path.
    /// </summary>
    /// <param name="path">The textual path to parse.</param>
    /// <returns>A normalized <see cref="FilePath"/>.</returns>
    public static implicit operator FilePath(string path) => FromPath(path);

    /// <summary>
    /// Implicitly formats the path using forward slashes.
    /// </summary>
    /// <param name="path">The path to format.</param>
    /// <returns>The formatted path string.</returns>
    public static implicit operator string(FilePath path) => path.ToPath();

    static FilePath Parse(string path, FilePathKind? kind, char separator)
    {
        ArgumentNullException.ThrowIfNull(path);
        return ParseNormalized(NormalizePath(path, separator), kind);
    }

    static FilePath ParseRelativeCombinePath(string segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        if (string.IsNullOrWhiteSpace(segment))
            return new(null, EmptySegments, FilePathKind.Directory);

        var normalized = NormalizePath(segment, '/');
        var inferredKind = InferKindForCombine(normalized);
        return ParseNormalized(normalized, inferredKind);
    }

    static string NormalizePath(string path, char separator)
    {
        var span = path.AsSpan();
        var trimmed = TrimWhitespace(span);
        if (trimmed.IsEmpty)
            return string.Empty;

        var requiresRewrite = separator != '/' && trimmed.IndexOf(separator) >= 0;
        if (!requiresRewrite && trimmed.IndexOf('\\') < 0)
        {
            if (trimmed.Length == path.Length)
                return path;

            return trimmed.ToString();
        }

        Span<char> initialBuffer = stackalloc char[Math.Min(trimmed.Length, 256)];
        var builder = new ValueStringBuilder(initialBuffer);
        AppendNormalizedPath(ref builder, trimmed, originalSeparator: separator);
        return builder.ToString();
    }

    static string? NormalizeRoot(string? root)
    {
        if (string.IsNullOrWhiteSpace(root))
            return null;

        var span = root.AsSpan();
        var end = span.Length;
        while (end > 0 && IsPathSeparator(span[end - 1]))
            end--;

        if (end == 0)
            return "/";

        var candidate = span[..end];
        if (candidate.IndexOf('\\') < 0 && end == root.Length)
            return root;

        Span<char> initialBuffer = stackalloc char[Math.Min(candidate.Length, 256)];
        var builder = new ValueStringBuilder(initialBuffer);
        AppendNormalizedPath(ref builder, candidate, originalSeparator: '/');
        return builder.ToString();
    }

    static ImmutableArray<string> NormalizeSegments(ImmutableArray<string> segments)
    {
        if (segments.IsDefaultOrEmpty)
            return EmptySegments;

        var allSimple = true;
        foreach (var segment in segments)
        {
            if (!IsSimpleNormalizedSegment(segment))
            {
                allSimple = false;
                break;
            }
        }

        if (allSimple)
            return segments;

        var builder = ImmutableArray.CreateBuilder<string>(segments.Length);
        foreach (var segment in segments)
        {
            if (IsSimpleNormalizedSegment(segment))
            {
                builder.Add(segment);
                continue;
            }

            AppendSegments(builder, segment.AsSpan());
        }

        if (builder.Count == 0)
            return EmptySegments;

        return builder.ToImmutable();
    }

    static ParsedRoot ParseRoot(string path)
    {
        if (string.IsNullOrEmpty(path))
            return new(null, 0, 0, false);

        var hadTrailingSeparator = path.Length > 1 && path[^1] == '/';
        var span = path.AsSpan();

        if (span.StartsWith("//".AsSpan(), StringComparison.Ordinal))
        {
            var remainder = span[2..];
            var serverSeparator = remainder.IndexOf('/');
            if (serverSeparator < 0)
                return new(path, path.Length, 0, true);

            var shareSpan = remainder[(serverSeparator + 1)..];
            var shareSeparator = shareSpan.IndexOf('/');
            if (shareSeparator < 0)
                return new(path, path.Length, 0, true);

            var bodyStart = 2 + serverSeparator + 1 + shareSeparator + 1;
            var root = path[..(bodyStart - 1)];
            return new(root, bodyStart, path.Length - bodyStart, hadTrailingSeparator);
        }

        if (path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':' && (path.Length == 2 || path[2] == '/'))
        {
            Span<char> rootBuffer = stackalloc char[2];
            rootBuffer[0] = char.ToUpperInvariant(path[0]);
            rootBuffer[1] = ':';
            var bodyStart = path.Length > 2 ? 3 : path.Length;
            return new(new string(rootBuffer), bodyStart, path.Length - bodyStart, hadTrailingSeparator || path.Length == 2);
        }

        if (path[0] == '/')
            return new("/", 1, path.Length - 1, hadTrailingSeparator);

        return new(null, 0, path.Length, hadTrailingSeparator);
    }

    static ImmutableArray<string> ParseSegments(ReadOnlySpan<char> body)
    {
        if (body.IsEmpty)
            return EmptySegments;

        var builder = ImmutableArray.CreateBuilder<string>(CountPotentialSegments(body));
        AppendSegments(builder, body);
        return builder.Count == 0 ? EmptySegments : builder.ToImmutable();
    }

    static FilePathKind InferKind(ImmutableArray<string> segments, bool hadTrailingSeparator)
    {
        if (hadTrailingSeparator || segments.IsDefaultOrEmpty)
            return FilePathKind.Directory;

        return string.IsNullOrEmpty(Path.GetExtension(segments[^1]))
            ? FilePathKind.Directory
            : FilePathKind.File;
    }

    static FilePathKind InferKindForCombine(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return FilePathKind.Directory;

        if (path[^1] == '/')
            return FilePathKind.Directory;

        var segments = ParseSegments(path.AsSpan());
        if (segments.IsDefaultOrEmpty)
            return FilePathKind.Directory;

        return string.IsNullOrEmpty(Path.GetExtension(segments[^1]))
            ? FilePathKind.Directory
            : FilePathKind.File;
    }

    static FilePath ParseNormalized(string normalizedPath, FilePathKind? kind)
    {
        var parsedRoot = ParseRoot(normalizedPath);
        var body = normalizedPath.AsSpan(parsedRoot.BodyStart, parsedRoot.BodyLength);
        var segments = ParseSegments(body);
        var resolvedKind = kind ?? InferKind(segments, parsedRoot.HadTrailingSeparator);
        return new(parsedRoot.Root, segments, resolvedKind);
    }

    static bool IsSimpleNormalizedSegment(string? segment)
    {
        if (string.IsNullOrWhiteSpace(segment))
            return false;

        var span = segment.AsSpan();
        var trimmed = TrimWhitespace(span);
        if (trimmed.Length != span.Length)
            return false;

        return trimmed.IndexOfAny('/', '\\') < 0;
    }

    static void AppendSegments(ImmutableArray<string>.Builder builder, ReadOnlySpan<char> value)
    {
        var index = 0;
        while (index < value.Length)
        {
            while (index < value.Length && IsPathSeparator(value[index]))
                index++;

            if (index >= value.Length)
                break;

            var segmentStart = index;
            while (index < value.Length && !IsPathSeparator(value[index]))
                index++;

            var trimmed = TrimWhitespace(value[segmentStart..index]);
            if (!trimmed.IsEmpty)
                builder.Add(trimmed.ToString());
        }
    }

    static int CountPotentialSegments(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty)
            return 0;

        var count = 1;
        foreach (var c in value)
        {
            if (IsPathSeparator(c))
                count++;
        }

        return count;
    }

    static ReadOnlySpan<char> TrimWhitespace(ReadOnlySpan<char> value)
    {
        var start = 0;
        var end = value.Length - 1;

        while (start <= end && char.IsWhiteSpace(value[start]))
            start++;

        while (end >= start && char.IsWhiteSpace(value[end]))
            end--;

        return start > end ? ReadOnlySpan<char>.Empty : value[start..(end + 1)];
    }

    static bool IsPathSeparator(char c) => c is '/' or '\\';

    int GetFormattedPathLength()
    {
        var length = 0;
        if (Root is not null)
            length += Root.Length == 1 && Root[0] == '/' ? 1 : Root.Length + 1;

        if (!Segments.IsDefaultOrEmpty)
        {
            foreach (var segment in Segments)
                length += segment.Length;

            length += Segments.Length - 1;
        }

        return length;
    }

    static void AppendNormalizedPath(ref ValueStringBuilder builder, ReadOnlySpan<char> value, char originalSeparator)
    {
        foreach (var c in value)
            builder.Append(c == '\\' || (originalSeparator != '/' && c == originalSeparator) ? '/' : c);
    }

    static void AppendWithSeparator(ref ValueStringBuilder builder, ReadOnlySpan<char> value, char separator)
    {
        foreach (var c in value)
            builder.Append(c == '/' ? separator : c);
    }
}
