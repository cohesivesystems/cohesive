using System.Globalization;
using System.Text;
using System.Text.Json;
using Cohesive.Relations.Model;
using Cohesive.Relations.Serialization;
using Cohesive.Simulation.Artifacts;
using Cohesive.Simulation.Cli;
using Cohesive.Simulation.Provisioning;
using Cohesive.Simulation.Relations;
using Cohesive.Simulation.Worlds;

namespace Cohesive.Simulation.Tests;

public sealed class SimulationCliTests
{
    [Fact]
    public async Task StandardStreams_RetainManifestBeforeProvisioningDeterministicJsonLines()
    {
        var worldJson = WorldDefinitionJsonSerializer.Serialize(DemoWorld());
        var manifestOutput = await RunManifestWithStandardStreams(worldJson, long.MinValue);

        Assert.Equal(0, manifestOutput.ExitCode);
        Assert.Empty(manifestOutput.Error);
        var retainedManifest = WorldArtifactManifestJsonSerializer.Deserialize(manifestOutput.Output);
        var first = await RunProvisionWithStandardStreams(manifestOutput.Output);
        var second = await RunProvisionWithStandardStreams(manifestOutput.Output);

        Assert.Equal(0, first.ExitCode);
        Assert.Empty(first.Error);
        Assert.Equal(first.Output, second.Output);
        await using MemoryStream verifiedInput = new(Encoding.UTF8.GetBytes(first.Output));
        var verification = await WorldJsonLinesVerifier.VerifyAsync(retainedManifest, verifiedInput);
        Assert.Equal(retainedManifest.ArtifactId, verification.ArtifactId);
        Assert.Equal("playwright/global-setup", verification.TargetId);
        Assert.Equal(1, verification.BatchSize);
        Assert.Equal(2, verification.ItemCount);

        var lines = first.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < lines.Length; index++)
        {
            using var document = JsonDocument.Parse(lines[index]);
            var root = document.RootElement;
            Assert.Equal(retainedManifest.ArtifactId.Value, root.GetProperty("artifactId").GetString());
            Assert.Equal(retainedManifest.Fingerprint.Value, root.GetProperty("artifactManifestFingerprint").GetString());
            Assert.Equal("-9223372036854775808", root.GetProperty("rootSeed").GetString());
            Assert.Equal(index, root.GetProperty("sequenceIndex").GetInt64());
            Assert.Equal(
                index == 1 ? ["customer-for-ui"] : [],
                root.GetProperty("exemplars")
                    .EnumerateArray()
                    .Select(static item => item.GetString()));
        }
    }

    [Fact]
    public async Task StandardStreams_RetainAndProvisionRelationshipWorldAuthority()
    {
        var relationshipWorld = RelationshipWorldDefinitionDocument.FromDefinition(new(
            DemoWorld(),
            RelationshipCatalogDocument.FromCatalog(RelationshipCatalog.Empty),
            []));
        var worldJson = RelationshipWorldDefinitionJsonSerializer.Serialize(relationshipWorld);

        var manifestOutput = await Run(
            ["manifest", "--relationship-world", "-", "--seed", "42"],
            worldJson);
        var retainedManifest = WorldArtifactManifestJsonSerializer.Deserialize(manifestOutput.Output);
        var provisioned = await RunProvisionWithStandardStreams(manifestOutput.Output);
        await using MemoryStream verificationInput = new(Encoding.UTF8.GetBytes(provisioned.Output));
        var verification = await RelationshipWorldJsonLinesVerifier.VerifyAsync(
            retainedManifest,
            verificationInput);

        Assert.Equal(0, manifestOutput.ExitCode);
        Assert.Empty(manifestOutput.Error);
        Assert.Equal(RelationshipWorldInterpreter.Identity, retainedManifest.Interpreter);
        Assert.Equal(RelationshipWorldDefinitionDocument.CurrentSchemaVersion, retainedManifest.World.SchemaVersion);
        Assert.Equal(0, provisioned.ExitCode);
        Assert.Empty(provisioned.Error);
        Assert.Equal(2, verification.ItemCount);
    }

    [Fact]
    public async Task ManifestFileOutput_ReplacesOnlyAfterSuccessfulValidation()
    {
        var temporaryDirectory = CreateTemporaryDirectory();
        try
        {
            var worldPath = Path.Combine(temporaryDirectory, "demo.world.json");
            var manifestPath = Path.Combine(temporaryDirectory, "nested", "demo.manifest.json");
            await File.WriteAllTextAsync(worldPath, WorldDefinitionJsonSerializer.Serialize(DemoWorld()));

            var firstExitCode = await RunManifestWithFiles(worldPath, manifestPath);
            var firstOutput = await File.ReadAllTextAsync(manifestPath);
            await File.WriteAllTextAsync(worldPath, "{\"invalid\":true}");
            var failedExitCode = await RunManifestWithFiles(worldPath, manifestPath);

            Assert.Equal(0, firstExitCode);
            Assert.Equal(1, failedExitCode);
            Assert.Equal(firstOutput, await File.ReadAllTextAsync(manifestPath));
            _ = WorldArtifactManifestJsonSerializer.Deserialize(firstOutput);
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(manifestPath)!, "*.tmp"));
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ProvisionFileOutput_RequiresStrictManifestAndPreservesLastCompleteArtifact()
    {
        var temporaryDirectory = CreateTemporaryDirectory();
        try
        {
            var manifestPath = Path.Combine(temporaryDirectory, "demo.manifest.json");
            var outputPath = Path.Combine(temporaryDirectory, "nested", "demo.world.jsonl");
            await File.WriteAllTextAsync(manifestPath, CreateManifestJson(rootSeed: 42));

            var firstExitCode = await RunProvisionWithFiles(manifestPath, outputPath);
            var firstOutput = await File.ReadAllTextAsync(outputPath);
            await File.WriteAllTextAsync(manifestPath, "{\"invalid\":true}");
            var failedExitCode = await RunProvisionWithFiles(manifestPath, outputPath);

            Assert.Equal(0, firstExitCode);
            Assert.Equal(1, failedExitCode);
            Assert.Equal(firstOutput, await File.ReadAllTextAsync(outputPath));
            Assert.Equal(2, firstOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(outputPath)!, "*.tmp"));
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task InvalidPortableWorld_ReportsStructuredDiagnosticWithoutManifest()
    {
        var result = await RunManifestWithStandardStreams("{\"invalid\":true}", rootSeed: 42);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Output);
        Assert.Contains("simulation.world.document.contentInvalid", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidManifest_ReportsStructuredDiagnosticWithoutProvisioningOutput()
    {
        var result = await Run(
            ["provision", "--manifest", "-", "--target", "scripts/demo"],
            "{\"invalid\":true}");

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Output);
        Assert.Contains("simulation.worldArtifact.manifest.contentInvalid", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancellation_PreservesExistingFileAndReturnsConventionalExitCode()
    {
        var temporaryDirectory = CreateTemporaryDirectory();
        try
        {
            var manifestPath = Path.Combine(temporaryDirectory, "demo.manifest.json");
            var outputPath = Path.Combine(temporaryDirectory, "demo.world.jsonl");
            await File.WriteAllTextAsync(manifestPath, CreateManifestJson(rootSeed: 42));
            await File.WriteAllTextAsync(outputPath, "previous-fixture");
            using CancellationTokenSource cancellation = new();
            cancellation.Cancel();
            using StringWriter error = new();

            var exitCode = await SimulationCliApplication.RunAsync(
                [
                    "provision",
                    "--manifest", manifestPath,
                    "--target", "scripts/demo",
                    "--out", outputPath
                ],
                Stream.Null,
                Stream.Null,
                error,
                cancellation.Token);

            Assert.Equal(130, exitCode);
            Assert.Equal("previous-fixture", await File.ReadAllTextAsync(outputPath));
            Assert.Contains("cancelled", error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Theory]
    [InlineData("--seed", "not-a-number", "RootSeed")]
    [InlineData("--batch-size", "0", "not a positive 32-bit integer")]
    public async Task SharedCliBinding_RejectsInvalidNumericPolicy(string option, string value, string expectedError)
    {
        string[] arguments = option switch
        {
            "--seed" => ["manifest", "--world", "world.json", option, value],
            _ => ["provision", "--manifest", "manifest.json", "--target", "scripts/demo", option, value]
        };
        var result = await Run(arguments);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Output);
        Assert.Contains(expectedError, result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SharedCliBinding_RejectsDuplicateAndUnknownOptions()
    {
        var duplicate = await Run(
            [
                "provision",
                "--manifest", "manifest.json",
                "--manifest", "another.json",
                "--target", "scripts/demo"
            ]);
        var unknown = await Run(
            [
                "provision",
                "--manifest", "manifest.json",
                "--target", "scripts/demo",
                "--mystery", "value"
            ]);

        Assert.NotEqual(0, duplicate.ExitCode);
        Assert.Contains("--manifest", duplicate.Error, StringComparison.Ordinal);
        Assert.NotEqual(0, unknown.ExitCode);
        Assert.Contains("--mystery", unknown.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ManifestCommand_RequiresExactlyOneWorldDocumentKind()
    {
        var missing = await Run(["manifest", "--seed", "42"]);
        var conflicting = await Run(
            ["manifest", "--world", "world.json", "--relationship-world", "relations.json", "--seed", "42"]);

        Assert.Equal(1, missing.ExitCode);
        Assert.Contains("exactly one", missing.Error, StringComparison.Ordinal);
        Assert.Equal(1, conflicting.ExitCode);
        Assert.Contains("exactly one", conflicting.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CommandHelp_SeparatesManifestCreationFromProvisioning()
    {
        var manifest = await Run(["manifest", "--help"]);
        var provision = await Run(["provision", "--help"]);

        Assert.Equal(0, manifest.ExitCode);
        Assert.Contains("Create and retain", manifest.Output, StringComparison.Ordinal);
        Assert.Contains("--world", manifest.Output, StringComparison.Ordinal);
        Assert.Contains("--relationship-world", manifest.Output, StringComparison.Ordinal);
        Assert.Contains("--seed", manifest.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("--manifest", manifest.Output, StringComparison.Ordinal);
        Assert.Empty(manifest.Error);

        Assert.Equal(0, provision.ExitCode);
        Assert.Contains("Provision a retained", provision.Output, StringComparison.Ordinal);
        Assert.Contains("--manifest", provision.Output, StringComparison.Ordinal);
        Assert.Contains("--batch-size", provision.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("--world", provision.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("--seed", provision.Output, StringComparison.Ordinal);
        Assert.Empty(provision.Error);
    }

    [Fact]
    public async Task RootInvocation_WritesGeneratedHelpWithoutDiagnostics()
    {
        var result = await Run([]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Create and provision deterministic", result.Output, StringComparison.Ordinal);
        Assert.Contains("manifest", result.Output, StringComparison.Ordinal);
        Assert.Contains("provision", result.Output, StringComparison.Ordinal);
        Assert.Empty(result.Error);
    }

    static async Task<(int ExitCode, string Output, string Error)> Run(
        IReadOnlyList<string> arguments,
        string? standardInput = null)
    {
        await using MemoryStream input = standardInput is null
            ? new()
            : new(Encoding.UTF8.GetBytes(standardInput));
        await using MemoryStream output = new();
        using StringWriter error = new();
        var exitCode = await SimulationCliApplication.RunAsync(
            [.. arguments],
            input,
            output,
            error);
        return (exitCode, Encoding.UTF8.GetString(output.ToArray()), error.ToString());
    }

    static Task<(int ExitCode, string Output, string Error)> RunManifestWithStandardStreams(
        string worldJson,
        long rootSeed) =>
        Run(
            [
                "manifest",
                "--world", "-",
                "--seed", rootSeed.ToString(CultureInfo.InvariantCulture)
            ],
            worldJson);

    static Task<(int ExitCode, string Output, string Error)> RunProvisionWithStandardStreams(
        string manifestJson) =>
        Run(
            [
                "provision",
                "--manifest", "-",
                "--target", "playwright/global-setup",
                "--batch-size", "1"
            ],
            manifestJson);

    static async Task<int> RunManifestWithFiles(string worldPath, string manifestPath)
    {
        using StringWriter error = new();
        return await SimulationCliApplication.RunAsync(
            ["manifest", "--world", worldPath, "--seed", "42", "--out", manifestPath],
            Stream.Null,
            Stream.Null,
            error);
    }

    static async Task<int> RunProvisionWithFiles(string manifestPath, string outputPath)
    {
        using StringWriter error = new();
        return await SimulationCliApplication.RunAsync(
            [
                "provision",
                "--manifest", manifestPath,
                "--target", "scripts/demo",
                "--out", outputPath,
                "--batch-size", "1"
            ],
            Stream.Null,
            Stream.Null,
            error);
    }

    static string CreateManifestJson(long rootSeed) =>
        WorldArtifactManifestJsonSerializer.Serialize(
            WorldArtifactManifest.FromWorld(DemoWorld().Compile(), rootSeed));

    static WorldDefinition DemoWorld()
    {
        var customers = Simulation.Define<CliCustomer>(customer => customer
            .Member(value => value.Name, Gen.Categorical(
                Gen.Weighted("Ada", weight: 1d),
                Gen.Weighted("Grace", weight: 1d)))
            .Member(value => value.Age, Gen.Int32(minimum: 18, maximum: 90)));
        return Simulation.DefineWorld("world/cli", "r1", world => world
            .Population("customers", count: 2, customers)
            .Exemplar("customer-for-ui", "customers", sequenceIndex: 1));
    }

    static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cohesive-simulation-cli-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    public sealed record CliCustomer(string Name, int Age);
}
