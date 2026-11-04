using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cohesive.Execution;
using Cohesive.Model.Serialization;

namespace Cohesive.Semantics;

/// <summary>Exports derived execution documents with a reproducible, explicitly declared input boundary.</summary>
/// <remarks>Run inside the application's build/export process. No application code is executed by inspection.</remarks>
public static class SemanticWorkspaceExport
{
    internal const string Schema = "cohesive-workspace/v1";
    internal const int MaximumFileBytes = 16 * 1024 * 1024;
    internal const int MaximumDefinitions = 256;
    internal const int MaximumInputs = 4096;
    internal static readonly JsonSerializerOptions JsonOptions = StrictDocumentJson.CreateOptions();

    /// <summary>Discovers C# build inputs under explicitly selected directories using deterministic conventions.</summary>
    /// <param name="root">Repository root.</param>
    /// <param name="sourceDirectories">Repository-relative directories to traverse without following symbolic links.</param>
    /// <param name="additionalFiles">Explicit repository-relative inputs, such as root props and dependency locks.</param>
    /// <returns>Ordinally sorted, unique paths; includes C#, project, props, targets and JSON files, excluding bin, obj and .git directories.</returns>
    /// <exception cref="ArgumentException">A selected path escapes the root, traverses a symbolic link, or exceeds the input count limit.</exception>
    /// <exception cref="ArgumentNullException">The source directory collection is null.</exception>
    /// <exception cref="IOException">A selected directory or file cannot be read.</exception>
    /// <exception cref="UnauthorizedAccessException">Access to a selected input is denied.</exception>
    /// <remarks>This convention does not prove dependency completeness; callers must include external contracts and effective configuration.</remarks>
    public static ImmutableArray<string> DiscoverInputs(string root, IEnumerable<string> sourceDirectories, IEnumerable<string>? additionalFiles = null)
    {
        ArgumentNullException.ThrowIfNull(sourceDirectories);
        var paths = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var directory in sourceDirectories)
            Visit(ResolvePath(root, directory));
        foreach (var file in additionalFiles ?? [])
            Add(ResolvePath(root, file));
        return [.. paths];

        void Add(string path)
        {
            paths.Add(Path.GetRelativePath(Path.GetFullPath(root), path).Replace('\\', '/'));
            if (paths.Count > MaximumInputs)
                throw new ArgumentException($"At most {MaximumInputs} declared inputs are supported.", nameof(sourceDirectories));
        }

        void Visit(string directory)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if (Path.GetFileName(entry) is "bin" or "obj" or ".git") continue;
                var path = ResolvePath(root, Path.GetRelativePath(Path.GetFullPath(root), entry));
                if (Directory.Exists(path)) Visit(path);
                else if (Path.GetExtension(path) is ".cs" or ".csproj" or ".props" or ".targets" or ".json") Add(path);
            }
        }
    }

    /// <summary>Lowers and atomically publishes a derived bundle using a temporary file in the destination directory.</summary>
    /// <param name="root">Repository root.</param>
    /// <param name="outputPath">Repository-relative output file, distinct from every declared input.</param>
    /// <param name="inputPaths">Complete declared authoring and build inputs.</param>
    /// <param name="producerVersion">Exact producer/compiler revision.</param>
    /// <param name="lower">Lowering callback receiving the declared-input revision.</param>
    /// <returns>Absolute published bundle path.</returns>
    /// <exception cref="ArgumentException">The output escapes the root, traverses a link, or names a declared input.</exception>
    /// <exception cref="ArgumentNullException">The input collection or lowering callback is null.</exception>
    /// <exception cref="IOException">Input capture or atomic file replacement fails.</exception>
    /// <exception cref="UnauthorizedAccessException">File access is denied.</exception>
    /// <exception cref="InvalidOperationException">Lowering produces invalid or oversized bundle contents.</exception>
    /// <remarks>Lowering and serialization failures propagate; an existing destination is preserved until replacement succeeds.</remarks>
    public static string Write(string root, string outputPath, IEnumerable<string> inputPaths, string producerVersion,
        Func<string, IReadOnlyList<ExecutionDefinitionDocument>> lower)
    {
        ArgumentNullException.ThrowIfNull(inputPaths);
        var output = ResolvePath(root, outputPath);
        var inputs = inputPaths.ToImmutableArray();
        if (inputs.Any(input => string.Equals(ResolvePath(root, input), output, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("An exported bundle cannot replace an authoring input.", nameof(outputPath));
        var json = Create(root, inputs, producerVersion, lower);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var temporary = output + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, json);
            File.Move(temporary, output, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        return output;
    }

    /// <summary>Captures declared inputs around lowering and returns a derived, portable inspection bundle.</summary>
    /// <param name="root">Repository directory; declared files must remain inside it without symbolic links.</param>
    /// <param name="inputPaths">Repository-relative source, contract, build, convention and configuration files.</param>
    /// <param name="producerVersion">Exact compiler/exporter version or source revision.</param>
    /// <param name="lower">Authoring/lowering operation receiving the declared input revision and returning documents to inspect.</param>
    /// <returns>JSON to write atomically as a build artifact; never an editable semantic authority.</returns>
    /// <exception cref="ArgumentException">Inputs are empty, duplicated, outside the root, or symbolic links.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="lower"/> is null.</exception>
    /// <exception cref="IOException">A file is unreadable, too large, or changes during lowering.</exception>
    /// <exception cref="InvalidOperationException">Documents have duplicate identities or exceed the bundle limit.</exception>
    /// <remarks>Exceptions from the supplied lowering operation propagate unchanged. Input completeness is producer-declared.</remarks>
    public static string Create(
        string root,
        IEnumerable<string> inputPaths,
        string producerVersion,
        Func<string, IReadOnlyList<ExecutionDefinitionDocument>> lower)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(producerVersion);
        ArgumentNullException.ThrowIfNull(lower);
        var before = Capture(root, inputPaths);
        if (before.Count == 0)
            throw new ArgumentException("Declare at least one authoring input.", nameof(inputPaths));
        var lowered = lower(InputRevision(producerVersion, before))
            ?? throw new InvalidOperationException("Lowering must return a document collection.");
        if (lowered.Count > MaximumDefinitions || lowered.Any(d => d is null))
            throw new InvalidOperationException($"A bundle supports at most {MaximumDefinitions} non-null documents.");
        var documents = lowered.OrderBy(d => d.Metadata.DefinitionId.Value, StringComparer.Ordinal).ToArray();
        if (documents.Select(d => d.Metadata.DefinitionId).Distinct().Count() != documents.Length)
            throw new InvalidOperationException("A bundle must contain unique definition identities.");
        var after = Capture(root, before.Keys);
        if (!before.SequenceEqual(after))
            throw new IOException("Declared inputs changed during lowering. Rebuild before exporting.");
        var bundle = new WorkspaceBundle(Schema, producerVersion, before,
            [.. documents.Select(d => JsonSerializer.Deserialize<JsonElement>(ExecutionDefinitionJsonSerializer.Serialize(d)))]);
        var json = JsonSerializer.Serialize(bundle, JsonOptions);
        if (Encoding.UTF8.GetByteCount(json) > MaximumFileBytes)
            throw new InvalidOperationException("Workspace bundle exceeds 16 MiB; split the export scope.");
        return json;
    }

    internal static ImmutableSortedDictionary<string, string> Capture(string root, IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var result = ImmutableSortedDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            var fullPath = ResolvePath(root, path);
            var normalized = Path.GetRelativePath(Path.GetFullPath(root), fullPath).Replace('\\', '/');
            if (!result.TryAdd(normalized, DigestFile(fullPath)))
                throw new ArgumentException($"Duplicate input '{normalized}'.", nameof(paths));
            if (result.Count > MaximumInputs)
                throw new ArgumentException($"At most {MaximumInputs} declared inputs are supported.", nameof(paths));
        }
        return result.ToImmutable();
    }

    internal static string ResolvePath(string root, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fullPath = Path.GetFullPath(Path.Combine(fullRoot, path));
        var relative = Path.GetRelativePath(fullRoot, fullPath);
        if (Path.IsPathRooted(path) || relative == ".." || relative.StartsWith("../", StringComparison.Ordinal)
            || relative.StartsWith("..\\", StringComparison.Ordinal) || Path.IsPathRooted(relative) || relative == ".")
            throw new ArgumentException("Paths must identify files inside the workspace root.", nameof(path));
        var current = fullRoot;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            if ((File.Exists(current) || Directory.Exists(current))
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("Symbolic links are not supported in workspace input paths.", nameof(path));
        }
        return fullPath;
    }

    internal static byte[] ReadBounded(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > MaximumFileBytes)
            throw new IOException("Workspace files must be at most 16 MiB.");
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1)
            throw new IOException("File changed while being read.");
        return bytes;
    }

    internal static string Digest(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    // Hash bounded file contents without allocating a file-sized managed buffer.
    internal static string DigestFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1);
        var remaining = stream.Length;
        if (remaining > MaximumFileBytes)
            throw new IOException("Workspace files must be at most 16 MiB.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> buffer = stackalloc byte[8192];
        while (remaining > 0)
        {
            var count = stream.Read(buffer[..(int)Math.Min(remaining, buffer.Length)]);
            if (count == 0) throw new IOException("File changed while being read.");
            hash.AppendData(buffer[..count]);
            remaining -= count;
        }
        if (stream.ReadByte() != -1) throw new IOException("File changed while being read.");
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
    internal static string InputRevision(string producerVersion, ImmutableSortedDictionary<string, string> inputs)
        => Convert.ToHexStringLower(StrictDocumentJson.ComputeCanonicalSha256(new { Schema, ProducerVersion = producerVersion, Inputs = inputs }, JsonOptions));
}

internal sealed record WorkspaceBundle(
    string SchemaVersion,
    string ProducerVersion,
    ImmutableSortedDictionary<string, string> Inputs,
    ImmutableArray<JsonElement> Documents);
