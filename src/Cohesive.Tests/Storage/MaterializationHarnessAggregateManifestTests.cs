extern alias supervise;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cohesive.MaterializationHarness.Control;
using MaterializationHarnessMatrixProgram =
    supervise::Cohesive.MaterializationHarness.Supervise.MaterializationHarnessMatrixProgram;

namespace Cohesive.Tests.Storage;

public sealed class MaterializationHarnessAggregateManifestTests
{
    [Fact]
    public async Task CompleteCatalogWritesOneSortedHashValidatedManifest()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            foreach (var cell in MaterializationHarnessMatrixCatalog.ExpectedAggregateCellIds)
                await WriteCellAsync(directory, cell);

            var exitCode = await MaterializationHarnessMatrixProgram.WriteAggregateManifestAsync(
                ["aggregate-manifest", directory]);

            Assert.Equal(0, exitCode);
            using var manifest = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(directory, "manifest.json")));
            var root = manifest.RootElement;
            Assert.True(root.GetProperty("summary").GetProperty("completed").GetBoolean());
            Assert.Equal(15, root.GetProperty("summary").GetProperty("completedCellCount").GetInt32());
            Assert.Equal(
                MaterializationHarnessMatrixCatalog.ExpectedAggregateCellIds,
                root.GetProperty("cells").EnumerateArray()
                    .Select(static cell => cell.GetProperty("identity").GetString())
                    .ToArray());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ChangedChildArtifactPreventsAggregatePublication()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            foreach (var cell in MaterializationHarnessMatrixCatalog.ExpectedAggregateCellIds)
                await WriteCellAsync(directory, cell);
            var changed = Path.Combine(directory, "cells", "source", "postgres", "evidence.json");
            await File.WriteAllTextAsync(changed, "changed-after-manifest");

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                MaterializationHarnessMatrixProgram.WriteAggregateManifestAsync(
                    ["aggregate-manifest", directory]));
            Assert.False(File.Exists(Path.Combine(directory, "manifest.json")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task MislabeledExpectedOutcomePreventsAggregatePublication()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            foreach (var cell in MaterializationHarnessMatrixCatalog.ExpectedAggregateCellIds)
                await WriteCellAsync(directory, cell);
            var manifestPath = Path.Combine(
                directory,
                "cells",
                "drift",
                "postgres",
                "cursor",
                "manifest.json");
            var manifest = await File.ReadAllTextAsync(manifestPath);
            await File.WriteAllTextAsync(
                manifestPath,
                manifest.Replace("ExpectedFailure", "Success", StringComparison.Ordinal));

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                MaterializationHarnessMatrixProgram.WriteAggregateManifestAsync(
                    ["aggregate-manifest", directory]));
            Assert.False(File.Exists(Path.Combine(directory, "manifest.json")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    static async Task WriteCellAsync(string root, string cellId)
    {
        var expectation = MaterializationHarnessMatrixCatalog.GetAggregateCell(cellId);
        var directory = Path.Combine(root, "cells", cellId.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(directory);
        var artifact = Encoding.UTF8.GetBytes($"evidence/{cellId}");
        await File.WriteAllBytesAsync(Path.Combine(directory, "evidence.json"), artifact);
        var manifest = new
        {
            schemaVersion = 1,
            summary = new
            {
                schemaVersion = 1,
                manifestKind = "cell",
                cellId,
                expectedOutcome = expectation.ExpectedOutcome.ToString(),
                requiredControlAction = expectation.RequiredControlAction?.ToString(),
                completed = true
            },
            artifacts = new[]
            {
                new
                {
                    name = "evidence.json",
                    observedBytes = artifact.LongLength,
                    retainedBytes = artifact.Length,
                    truncated = false,
                    retainedSha256 = Convert.ToHexStringLower(SHA256.HashData(artifact))
                }
            }
        };
        await File.WriteAllTextAsync(
            Path.Combine(directory, "manifest.json"),
            JsonSerializer.Serialize(manifest));
    }

    static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "cohesive-matrix-manifest-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
