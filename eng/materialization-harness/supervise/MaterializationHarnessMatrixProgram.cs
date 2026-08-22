using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using Cohesive.MaterializationHarness.Control;

namespace Cohesive.MaterializationHarness.Supervise;

static class MaterializationHarnessMatrixProgram
{
    internal static int PrintCatalog(string[] args)
    {
        if (args is not ["catalog", _])
            throw new ArgumentException("Expected: catalog <source-providers|elastic-failures|aggregate-cell-ids>.", nameof(args));
        IEnumerable<string> values = args[1] switch
        {
            "source-providers" => MaterializationHarnessMatrixCatalog.SourceProviders,
            "elastic-failures" => MaterializationHarnessMatrixCatalog.ElasticFaults.Select(
                MaterializationHarnessMatrixCatalog.ElasticWireName),
            "aggregate-cell-ids" => MaterializationHarnessMatrixCatalog.ExpectedAggregateCellIds,
            _ => throw new ArgumentException("Unsupported materialization matrix catalog.", nameof(args))
        };
        foreach (var value in values)
            Console.WriteLine(value);
        return 0;
    }

    internal static async Task<int> WriteAggregateManifestAsync(string[] args)
    {
        if (args is not ["aggregate-manifest", _])
            throw new ArgumentException("Expected: aggregate-manifest <absolute-artifact-root>.", nameof(args));
        if (!Path.IsPathFullyQualified(args[1]))
            throw new ArgumentException("The aggregate artifact root must be absolute.", nameof(args));
        var root = Path.GetFullPath(args[1]);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Aggregate artifact root '{root}' does not exist.");

        var cells = ImmutableArray.CreateBuilder<AggregateManifestEntry>();
        var support = ImmutableArray.CreateBuilder<AggregateManifestEntry>();
        foreach (var manifestPath in Directory.EnumerateFiles(root, "manifest.json", SearchOption.AllDirectories)
                     .Where(path => !string.Equals(path, Path.Combine(root, "manifest.json"), StringComparison.Ordinal))
                     .OrderBy(path => Relative(root, path), StringComparer.Ordinal))
        {
            var info = new FileInfo(manifestPath);
            if (info.Length > SupervisorOptions.MaximumArtifactBytes)
            {
                throw new InvalidDataException(
                    $"Child manifest '{Relative(root, manifestPath)}' exceeds the bounded manifest limit.");
            }
            var bytes = await File.ReadAllBytesAsync(manifestPath).ConfigureAwait(false);
            using var document = JsonDocument.Parse(bytes);
            var rootElement = document.RootElement;
            var summary = rootElement.GetProperty("summary");
            if (!summary.GetProperty("completed").GetBoolean())
                throw new InvalidDataException($"Child manifest '{Relative(root, manifestPath)}' is incomplete.");
            var manifestKind = summary.GetProperty("manifestKind").GetString()
                ?? throw new InvalidDataException("A child manifest omitted its kind.");
            var identity = manifestKind switch
            {
                "cell" => summary.GetProperty("cellId").GetString(),
                "support" => summary.GetProperty("supportId").GetString(),
                _ => throw new InvalidDataException($"Unsupported child manifest kind '{manifestKind}'.")
            } ?? throw new InvalidDataException("A child manifest omitted its identity.");
            var expectedOutcome = manifestKind == "cell"
                ? summary.GetProperty("expectedOutcome").GetString()
                : null;
            var requiredControlAction = OptionalString(summary, "requiredControlAction");
            if (manifestKind == "cell")
            {
                var expectation = MaterializationHarnessMatrixCatalog.ExpectedAggregateCells.SingleOrDefault(
                    cell => string.Equals(cell.CellId, identity, StringComparison.Ordinal))
                    ?? throw new InvalidDataException($"Unexpected matrix cell '{identity}'.");
                if (!string.Equals(
                        expectedOutcome,
                        expectation.ExpectedOutcome.ToString(),
                        StringComparison.Ordinal)
                    || !string.Equals(
                        requiredControlAction,
                        expectation.RequiredControlAction?.ToString(),
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"Matrix cell '{identity}' does not match its expected outcome or recovery requirement.");
                }
            }
            var artifacts = ValidateArtifacts(
                aggregateRoot: root,
                manifestPath: manifestPath,
                artifacts: rootElement.GetProperty("artifacts"));
            var entry = new AggregateManifestEntry(
                Identity: identity,
                Manifest: Relative(root, manifestPath),
                ManifestBytes: bytes.LongLength,
                ManifestSha256: Sha256(bytes),
                ExpectedOutcome: expectedOutcome,
                ExpectedDisposition: OptionalString(summary, "expectedDisposition"),
                ActualDisposition: OptionalString(summary, "actualDisposition"),
                RequiredControlAction: requiredControlAction,
                Artifacts: artifacts);
            if (manifestKind == "cell")
                cells.Add(entry);
            else
                support.Add(entry);
        }

        var orderedCells = cells.OrderBy(static cell => cell.Identity, StringComparer.Ordinal).ToImmutableArray();
        var actualIds = orderedCells.Select(static cell => cell.Identity).ToImmutableArray();
        var expectedIds = MaterializationHarnessMatrixCatalog.ExpectedAggregateCellIds;
        if (!actualIds.SequenceEqual(expectedIds, StringComparer.Ordinal))
        {
            var missing = expectedIds.Except(actualIds, StringComparer.Ordinal);
            var unexpected = actualIds.Except(expectedIds, StringComparer.Ordinal);
            throw new InvalidDataException(
                $"Aggregate cell catalog mismatch. Missing: [{string.Join(", ", missing)}]. "
                + $"Unexpected: [{string.Join(", ", unexpected)}].");
        }

        var aggregate = new
        {
            schemaVersion = 1,
            summary = new
            {
                manifestKind = "aggregate",
                expectedCellCount = expectedIds.Length,
                completedCellCount = orderedCells.Length,
                supportManifestCount = support.Count,
                completed = true
            },
            cells = orderedCells,
            support = support.OrderBy(static entry => entry.Identity, StringComparer.Ordinal)
        };
        var output = JsonSerializer.SerializeToUtf8Bytes(
            aggregate,
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
        if (output.Length > SupervisorOptions.MaximumArtifactBytes)
            throw new InvalidDataException("The aggregate manifest exceeds its bounded artifact limit.");
        var outputPath = Path.Combine(root, "manifest.json");
        await File.WriteAllBytesAsync(outputPath, output).ConfigureAwait(false);
        Console.WriteLine(outputPath);
        return 0;
    }

    static ImmutableArray<AggregateArtifactEntry> ValidateArtifacts(
        string aggregateRoot,
        string manifestPath,
        JsonElement artifacts)
    {
        var manifestDirectory = Path.GetDirectoryName(manifestPath)
            ?? throw new InvalidDataException("A child manifest has no parent directory.");
        var builder = ImmutableArray.CreateBuilder<AggregateArtifactEntry>();
        foreach (var artifact in artifacts.EnumerateArray().OrderBy(
                     static artifact => artifact.GetProperty("name").GetString(),
                     StringComparer.Ordinal))
        {
            var name = artifact.GetProperty("name").GetString()
                ?? throw new InvalidDataException("A child artifact omitted its name.");
            var path = Path.GetFullPath(Path.Combine(manifestDirectory, name));
            var directoryPrefix = manifestDirectory.EndsWith(Path.DirectorySeparatorChar)
                ? manifestDirectory
                : manifestDirectory + Path.DirectorySeparatorChar;
            if (!path.StartsWith(directoryPrefix, StringComparison.Ordinal) || !File.Exists(path))
                throw new InvalidDataException($"Child artifact '{name}' escapes or is absent from its manifest directory.");
            var bytes = File.ReadAllBytes(path);
            var retainedBytes = artifact.GetProperty("retainedBytes").GetInt32();
            var retainedSha256 = artifact.GetProperty("retainedSha256").GetString()
                ?? throw new InvalidDataException("A child artifact omitted its retained fingerprint.");
            if (bytes.Length != retainedBytes
                || !string.Equals(Sha256(bytes), retainedSha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Child artifact '{Relative(aggregateRoot, path)}' failed hash validation.");
            }
            builder.Add(new(
                Path: Relative(aggregateRoot, path),
                ObservedBytes: artifact.GetProperty("observedBytes").GetInt64(),
                RetainedBytes: retainedBytes,
                Truncated: artifact.GetProperty("truncated").GetBoolean(),
                RetainedSha256: retainedSha256));
        }
        return builder.ToImmutable();
    }

    static string? OptionalString(JsonElement value, string property) =>
        value.TryGetProperty(property, out var candidate) && candidate.ValueKind == JsonValueKind.String
            ? candidate.GetString()
            : null;

    static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');

    static string Sha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    sealed record AggregateManifestEntry(
        string Identity,
        string Manifest,
        long ManifestBytes,
        string ManifestSha256,
        string? ExpectedOutcome,
        string? ExpectedDisposition,
        string? ActualDisposition,
        string? RequiredControlAction,
        ImmutableArray<AggregateArtifactEntry> Artifacts);

    sealed record AggregateArtifactEntry(
        string Path,
        long ObservedBytes,
        int RetainedBytes,
        bool Truncated,
        string RetainedSha256);
}
