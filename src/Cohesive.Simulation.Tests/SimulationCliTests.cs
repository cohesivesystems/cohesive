using System.Text;
using System.Text.Json;
using Cohesive.Simulation.Cli;
using Cohesive.Simulation.Provisioning;
using Cohesive.Simulation.Worlds;

namespace Cohesive.Simulation.Tests;

public sealed class SimulationCliTests
{
    [Fact]
    public async Task StandardStreams_ProvisionPortableWorldAsDeterministicJsonLines()
    {
        var worldJson = WorldDefinitionJsonSerializer.Serialize(DemoWorld());

        var first = await RunWithStandardStreams(worldJson);
        var second = await RunWithStandardStreams(worldJson);

        Assert.Equal(0, first.ExitCode);
        Assert.Empty(first.Error);
        Assert.Equal(first.Output, second.Output);
        var lines = first.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        for (var index = 0; index < lines.Length; index++)
        {
            using var document = JsonDocument.Parse(lines[index]);
            var root = document.RootElement;
            Assert.Equal(WorldJsonLinesSink.Format, root.GetProperty("format").GetString());
            Assert.Equal("playwright/global-setup", root.GetProperty("targetId").GetString());
            Assert.Equal("-9223372036854775808", root.GetProperty("rootSeed").GetString());
            Assert.Equal(index, root.GetProperty("sequenceIndex").GetInt64());
        }
    }

    [Fact]
    public async Task FileOutput_ReplacesOnlyAfterSuccessfulProvisioning()
    {
        var temporaryDirectory = CreateTemporaryDirectory();
        try
        {
            var worldPath = Path.Combine(temporaryDirectory, "demo.world.json");
            var outputPath = Path.Combine(temporaryDirectory, "nested", "demo.world.jsonl");
            await File.WriteAllTextAsync(worldPath, WorldDefinitionJsonSerializer.Serialize(DemoWorld()));

            var firstExitCode = await RunWithFiles(worldPath, outputPath);
            var firstOutput = await File.ReadAllTextAsync(outputPath);
            await File.WriteAllTextAsync(worldPath, "{\"invalid\":true}");
            var failedExitCode = await RunWithFiles(worldPath, outputPath);

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
    public async Task InvalidPortableWorld_ReportsStructuredDiagnosticWithoutOutput()
    {
        var result = await RunWithStandardStreams("{\"invalid\":true}");

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.Output);
        Assert.Contains("simulation.world.document.contentInvalid", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancellation_PreservesExistingFileAndReturnsConventionalExitCode()
    {
        var temporaryDirectory = CreateTemporaryDirectory();
        try
        {
            var worldPath = Path.Combine(temporaryDirectory, "demo.world.json");
            var outputPath = Path.Combine(temporaryDirectory, "demo.world.jsonl");
            await File.WriteAllTextAsync(worldPath, WorldDefinitionJsonSerializer.Serialize(DemoWorld()));
            await File.WriteAllTextAsync(outputPath, "previous-fixture");
            using CancellationTokenSource cancellation = new();
            cancellation.Cancel();
            using StringWriter error = new();

            var exitCode = await SimulationCliApplication.RunAsync(
                [
                    "provision",
                    "--world", worldPath,
                    "--seed", "42",
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
            "--seed" =>
            [
                "provision",
                "--world", "world.json",
                "--target", "scripts/demo",
                option, value
            ],
            _ =>
            [
                "provision",
                "--world", "world.json",
                "--seed", "42",
                "--target", "scripts/demo",
                option, value
            ]
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
                "--world", "world.json",
                "--world", "another.json",
                "--seed", "42",
                "--target", "scripts/demo"
            ]);
        var unknown = await Run(
            [
                "provision",
                "--world", "world.json",
                "--seed", "42",
                "--target", "scripts/demo",
                "--mystery", "value"
            ]);

        Assert.NotEqual(0, duplicate.ExitCode);
        Assert.Contains("--world", duplicate.Error, StringComparison.Ordinal);
        Assert.NotEqual(0, unknown.ExitCode);
        Assert.Contains("--mystery", unknown.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Help_WritesUsageWithoutDiagnostics()
    {
        await using MemoryStream output = new();
        using StringWriter error = new();

        var exitCode = await SimulationCliApplication.RunAsync(
            ["provision", "--help"],
            Stream.Null,
            output,
            error);

        Assert.Equal(0, exitCode);
        var help = Encoding.UTF8.GetString(output.ToArray());
        Assert.Contains("Compile and provision", help, StringComparison.Ordinal);
        Assert.Contains("--world", help, StringComparison.Ordinal);
        Assert.Contains("--seed", help, StringComparison.Ordinal);
        Assert.Empty(error.ToString());
    }

    [Fact]
    public async Task RootInvocation_WritesGeneratedHelpWithoutDiagnostics()
    {
        var result = await Run([]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Provision deterministic data", result.Output, StringComparison.Ordinal);
        Assert.Contains("provision", result.Output, StringComparison.Ordinal);
        Assert.Empty(result.Error);
    }

    static async Task<(int ExitCode, string Output, string Error)> Run(IReadOnlyList<string> arguments)
    {
        await using MemoryStream output = new();
        using StringWriter error = new();
        var exitCode = await SimulationCliApplication.RunAsync(
            [.. arguments],
            Stream.Null,
            output,
            error);
        return (exitCode, Encoding.UTF8.GetString(output.ToArray()), error.ToString());
    }

    static async Task<(int ExitCode, string Output, string Error)> RunWithStandardStreams(string worldJson)
    {
        await using MemoryStream input = new(Encoding.UTF8.GetBytes(worldJson));
        await using MemoryStream output = new();
        using StringWriter error = new();
        var exitCode = await SimulationCliApplication.RunAsync(
            [
                "provision",
                "--world", "-",
                "--seed", long.MinValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--target", "playwright/global-setup",
                "--batch-size", "1"
            ],
            input,
            output,
            error);
        return (exitCode, Encoding.UTF8.GetString(output.ToArray()), error.ToString());
    }

    static async Task<int> RunWithFiles(string worldPath, string outputPath)
    {
        using StringWriter error = new();
        return await SimulationCliApplication.RunAsync(
            [
                "provision",
                "--world", worldPath,
                "--seed", "42",
                "--target", "scripts/demo",
                "--out", outputPath,
                "--batch-size", "1"
            ],
            Stream.Null,
            Stream.Null,
            error);
    }

    static WorldDefinition DemoWorld()
    {
        var customers = Simulation.Define<CliCustomer>(customer => customer
            .Member(value => value.Name, Gen.Categorical(
                Gen.Weighted("Ada", weight: 1d),
                Gen.Weighted("Grace", weight: 1d)))
            .Member(value => value.Age, Gen.Int32(minimum: 18, maximum: 90)));
        return Simulation.DefineWorld("world/cli", "r1", world => world
            .Population("customers", count: 2, customers));
    }

    static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cohesive-simulation-cli-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    public sealed record CliCustomer(string Name, int Age);
}
