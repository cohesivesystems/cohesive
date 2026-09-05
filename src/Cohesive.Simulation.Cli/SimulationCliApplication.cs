using System.Text;
using Cohesive.Cli;
using Cohesive.Simulation.Relations;
using Cohesive.Simulation.Artifacts;
using Cohesive.Simulation.Provisioning;
using Cohesive.Simulation.Worlds;

namespace Cohesive.Simulation.Cli;

static class SimulationCliApplication
{
    const int SuccessExitCode = 0;
    const int FailureExitCode = 1;
    const int CancelledExitCode = 130;

    public static async Task<int> RunAsync(
        string[] args,
        Stream standardInput,
        Stream standardOutput,
        TextWriter standardError,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(standardInput);
        ArgumentNullException.ThrowIfNull(standardOutput);
        ArgumentNullException.ThrowIfNull(standardError);

        using var standardOutputWriter = CliStandardStreams.OpenUtf8Writer(standardOutput);
        var app = Create(standardInput, standardOutput);
        IReadOnlyList<string> invocationArgs = args.Length == 0 ? ["--help"] : args;
        return await app.InvokeAsync(
                invocationArgs,
                new()
                {
                    StandardOutput = standardOutputWriter,
                    ErrorOutput = standardError
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    static CliApplication Create(Stream standardInput, Stream standardOutput)
    {
        var app = new CliApplication("Create and provision deterministic Cohesive world artifacts.");
        app.Command<WorldManifestCliOptions>(
                "manifest",
                "Create and retain a verified world-artifact manifest.")
            .Validate((Func<WorldManifestCliOptions, IReadOnlyList<string>>)ValidateManifestOptions)
            .OnExecute((CliCommandContext<WorldManifestCliOptions> context) =>
                CreateManifestAsync(context, standardInput, standardOutput));
        app.Command<WorldProvisionCliOptions>(
                "provision",
                "Provision a retained world-artifact manifest as JSON Lines.")
            .Validate((WorldProvisionCliOptions options) =>
                options.BatchSize > 0
                    ? []
                    : new[] { $"Batch size '{options.BatchSize}' is not a positive 32-bit integer." })
            .OnExecute((CliCommandContext<WorldProvisionCliOptions> context) =>
                ProvisionAsync(context, standardInput, standardOutput));
        return app;
    }

    static IReadOnlyList<string> ValidateManifestOptions(WorldManifestCliOptions options)
    {
        var coreWorld = !string.IsNullOrWhiteSpace(options.WorldPath);
        var relationshipWorld = !string.IsNullOrWhiteSpace(options.RelationshipWorldPath);
        return coreWorld != relationshipWorld
            ? []
            : ["Specify exactly one of '--world' or '--relationship-world'."];
    }

    static async Task<int> CreateManifestAsync(
        CliCommandContext<WorldManifestCliOptions> context,
        Stream standardInput,
        Stream standardOutput)
    {
        try
        {
            var options = NormalizePaths(context.Configuration);
            var worldJson = await ReadJsonAsync(
                    options.InputPath,
                    options.ReadsStandardInput,
                    standardInput,
                    context.CancellationToken)
                .ConfigureAwait(false);
            var manifest = options.IsRelationshipWorld
                ? RelationshipWorldArtifact.FromWorld(
                    RelationshipWorldDefinitionJsonSerializer.Deserialize(worldJson),
                    options.RootSeed)
                : WorldArtifactManifest.FromWorld(
                    WorldDefinitionJsonSerializer.Deserialize(worldJson),
                    options.RootSeed);
            var canonicalManifest = WorldArtifactManifestJsonSerializer.GetCanonicalBytes(manifest);
            await WriteOutputAsync(
                    options.OutputPath,
                    options.WritesStandardOutput,
                    standardOutput,
                    async (output, cancellationToken) =>
                    {
                        await output.WriteAsync(canonicalManifest, cancellationToken).ConfigureAwait(false);
                        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                    },
                    context.CancellationToken)
                .ConfigureAwait(false);
            return SuccessExitCode;
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            context.Output.WriteErrorLine("Simulation manifest creation was cancelled.");
            return CancelledExitCode;
        }
        catch (Exception exception)
        {
            context.Output.WriteErrorLine(exception.Message);
            return FailureExitCode;
        }
    }

    static async Task<int> ProvisionAsync(
        CliCommandContext<WorldProvisionCliOptions> context,
        Stream standardInput,
        Stream standardOutput)
    {
        try
        {
            var options = NormalizePaths(context.Configuration);
            var manifestJson = await ReadJsonAsync(
                    options.ManifestPath,
                    options.ReadsStandardInput,
                    standardInput,
                    context.CancellationToken)
                .ConfigureAwait(false);
            var manifest = WorldArtifactManifestJsonSerializer.Deserialize(manifestJson);
            await WriteOutputAsync(
                    options.OutputPath,
                    options.WritesStandardOutput,
                    standardOutput,
                    (output, cancellationToken) => ProvisionAsync(manifest, options, output, cancellationToken),
                    context.CancellationToken)
                .ConfigureAwait(false);
            return SuccessExitCode;
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            context.Output.WriteErrorLine("Simulation provisioning was cancelled.");
            return CancelledExitCode;
        }
        catch (Exception exception)
        {
            context.Output.WriteErrorLine(exception.Message);
            return FailureExitCode;
        }
    }

    static WorldManifestCliOptions NormalizePaths(WorldManifestCliOptions options) =>
        options with
        {
            WorldPath = options.IsRelationshipWorld
                ? string.Empty
                : NormalizePath(options.WorldPath, "--world"),
            RelationshipWorldPath = options.IsRelationshipWorld
                ? NormalizePath(options.RelationshipWorldPath, "--relationship-world")
                : string.Empty,
            OutputPath = NormalizePath(options.OutputPath, "--out")
        };

    static WorldProvisionCliOptions NormalizePaths(WorldProvisionCliOptions options) =>
        options with
        {
            ManifestPath = NormalizePath(options.ManifestPath, "--manifest"),
            OutputPath = NormalizePath(options.OutputPath, "--out")
        };

    static string NormalizePath(string value, string option)
    {
        if (string.Equals(value, SimulationCliPaths.StandardStream, StringComparison.Ordinal))
            return value;

        try
        {
            return Path.GetFullPath(value);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException($"Option '{option}' has invalid path '{value}': {exception.Message}", option, exception);
        }
    }

    static async Task<string> ReadJsonAsync(
        string inputPath,
        bool readsStandardInput,
        Stream standardInput,
        CancellationToken cancellationToken)
    {
        if (!readsStandardInput)
            return await File.ReadAllTextAsync(inputPath, Encoding.UTF8, cancellationToken).ConfigureAwait(false);

        using var reader = CliStandardStreams.OpenUtf8Reader(standardInput);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    static async Task WriteOutputAsync(
        string outputPath,
        bool writesStandardOutput,
        Stream standardOutput,
        Func<Stream, CancellationToken, Task> write,
        CancellationToken cancellationToken)
    {
        if (writesStandardOutput)
        {
            await write(standardOutput, cancellationToken).ConfigureAwait(false);
            return;
        }

        var outputDirectory = Path.GetDirectoryName(outputPath)
            ?? throw new InvalidOperationException($"Output path '{outputPath}' has no parent directory.");
        Directory.CreateDirectory(outputDirectory);
        var temporaryPath = Path.Combine(
            outputDirectory,
            $".{Path.GetFileName(outputPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (FileStream temporaryOutput = new(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 4096,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await write(temporaryOutput, cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, outputPath, overwrite: true);
        }
        catch
        {
            TryDeleteTemporaryFile(temporaryPath);
            throw;
        }
    }

    static void TryDeleteTemporaryFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Preserve the write failure. The uniquely named temporary artifact remains diagnosable.
        }
    }

    static Task ProvisionAsync(
        WorldArtifactManifest manifest,
        WorldProvisionCliOptions options,
        Stream output,
        CancellationToken cancellationToken)
    {
        WorldJsonLinesSink sink = new(options.TargetId, output);
        var optionsValue = new WorldProvisioningOptions(options.BatchSize);
        return string.Equals(
                manifest.Interpreter,
                RelationshipWorldInterpreter.Identity,
                StringComparison.Ordinal)
            ? RelationshipWorldProvisioner.ProvisionAsync(
                manifest,
                sink,
                optionsValue,
                cancellationToken)
            : WorldProvisioner.ProvisionAsync(
                manifest,
                sink,
                optionsValue,
                cancellationToken);
    }
}
