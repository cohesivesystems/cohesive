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
            if (!result.TryAdd(normalized, Digest(ReadBounded(fullPath))))
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
    internal static string Digest(string value) => Digest(Encoding.UTF8.GetBytes(value));
    internal static string InputRevision(string producerVersion, ImmutableSortedDictionary<string, string> inputs)
        => Digest(StrictDocumentJson.GetCanonicalBytes(new { Schema, ProducerVersion = producerVersion, Inputs = inputs }, JsonOptions));
}

internal sealed record WorkspaceBundle(
    string SchemaVersion,
    string ProducerVersion,
    ImmutableSortedDictionary<string, string> Inputs,
    ImmutableArray<JsonElement> Documents);
