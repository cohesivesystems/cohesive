using System.Text;
using Cohesive.Cli;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Simulation.Artifacts;
using Cohesive.Simulation.Provisioning;
using Cohesive.Simulation.Relations;
using Cohesive.Simulation.Worlds;

namespace Cohesive.Simulation.Cli;

static class SimulationCliApplication
{
    const int SuccessExitCode = 0;
    const int FailureExitCode = 1;
    const int CancelledExitCode = 130;

    public static Task<int> RunAsync(
        string[] args,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        return InvokeAsync(Create().UseConsoleCancellation(), args, cancellationToken);
    }

    public static Task<int> RunAsync(
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

        return InvokeAsync(
            Create(standardInput, standardOutput, standardError),
            args,
            cancellationToken);
    }

    static Task<int> InvokeAsync(
        CliApplication app,
        string[] args,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string> invocationArgs = args.Length == 0 ? ["--help"] : args;
        return app.InvokeAsync(invocationArgs, cancellationToken);
    }

    static CliApplication Create(
        Stream? standardInput = null,
        Stream? standardOutput = null,
        TextWriter? standardError = null)
    {
        var app = new CliApplication(
            "Create and provision deterministic Cohesive world artifacts.",
            standardInput,
            standardOutput,
            standardError);
        app.Command<WorldManifestCliOptions>(
                "manifest",
                "Create and retain a verified world-artifact manifest.")
            .RequireExactlyOne(
                options => options.WorldPath,
                options => options.RelationshipWorldPath)
            .OnExecute(CreateManifestAsync);
        app.Command<WorldProvisionCliOptions>(
                "provision",
                "Provision a retained world-artifact manifest as JSON Lines."
                )
            .Validate((WorldProvisionCliOptions options) =>
                options.BatchSize > 0
                    ? []
                    : new[] { $"Batch size '{options.BatchSize}' is not a positive 32-bit integer." })
            .OnExecute(ProvisionAsync);
        app.Command<WorldVerifyCliOptions>(
                "verify",
                "Verify world JSON Lines against an independently retained manifest.")
            .AllowStandardInputForAtMostOne(
                options => options.ManifestPath,
                options => options.JsonLinesPath)
            .OnExecute(VerifyAsync);
        return app;
    }

    static async Task<int> CreateManifestAsync(
        CliCommandContext<WorldManifestCliOptions> context)
    {
        try
        {
            var options = NormalizePaths(context.Configuration);
            var worldJson = await ReadJsonAsync(
                    options.InputPath,
                    options.ReadsStandardInput,
                    context.StandardInput,
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
                    context.StandardOutput,
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
        CliCommandContext<WorldProvisionCliOptions> context)
    {
        try
        {
            var options = NormalizePaths(context.Configuration);
            var manifestJson = await ReadJsonAsync(
                    options.ManifestPath,
                    options.ReadsStandardInput,
                    context.StandardInput,
                    context.CancellationToken)
                .ConfigureAwait(false);
            var manifest = WorldArtifactManifestJsonSerializer.Deserialize(manifestJson);
            await WriteOutputAsync(
                    options.OutputPath,
                    options.WritesStandardOutput,
                    context.StandardOutput,
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

    static async Task<int> VerifyAsync(
        CliCommandContext<WorldVerifyCliOptions> context)
    {
        try
        {
            var options = NormalizePaths(context.Configuration);
            var manifestJson = await ReadJsonAsync(
                    options.ManifestPath,
                    options.ReadsManifestStandardInput,
                    context.StandardInput,
                    context.CancellationToken)
                .ConfigureAwait(false);
            var manifestValidation = WorldArtifactManifestJsonSerializer.TryDeserialize(
                manifestJson,
                out var manifest);
            if (!manifestValidation.IsValid || manifest is null)
            {
                WriteVerificationFailure(context, manifestValidation.Diagnostics);
                return FailureExitCode;
            }

            WorldJsonLinesValidationResult validation;
            if (options.ReadsJsonLinesStandardInput)
            {
                validation = await ValidateJsonLinesAsync(
                        manifest,
                        context.StandardInput,
                        context.CancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await using FileStream input = new(
                    options.JsonLinesPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 4096,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                validation = await ValidateJsonLinesAsync(
                        manifest,
                        input,
                        context.CancellationToken)
                    .ConfigureAwait(false);
            }

            if (!validation.IsSuccessful || validation.Verification is null)
            {
                WriteVerificationFailure(context, validation.Validation.Diagnostics);
                return FailureExitCode;
            }

            var verification = validation.Verification;
            context.Output.WriteJson(new WorldVerifyCliReport(
                WorldVerifyCliReport.CurrentSchemaVersion,
                IsValid: true,
                new(
                    manifest.ArtifactId.Value,
                    manifest.Fingerprint.Value,
                    manifest.World.Id,
                    manifest.World.Revision,
                    manifest.World.Fingerprint.Value,
                    manifest.Interpreter,
                    manifest.EntropyAlgorithm,
                    verification.TargetId,
                    verification.RunId?.Value,
                    verification.BatchSize,
                    verification.ItemCount),
                Diagnostics: []));
            return SuccessExitCode;
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            context.Output.WriteErrorLine("Simulation verification was cancelled.");
            return CancelledExitCode;
        }
        catch (Exception exception)
        {
            WriteVerificationFailure(
                context,
                [new(
                    VerificationFailureCode(exception),
                    DiagnosticSeverity.Error,
                    exception.Message)]);
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

    static WorldVerifyCliOptions NormalizePaths(WorldVerifyCliOptions options) =>
        options with
        {
            ManifestPath = NormalizePath(options.ManifestPath, "--manifest"),
            JsonLinesPath = NormalizePath(options.JsonLinesPath, "--jsonl")
        };

    static string NormalizePath(string value, string option)
    {
        if (CliStandardStreams.IsStandardStreamPath(value))
        {
            return value;
        }

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
        {
            return await File.ReadAllTextAsync(inputPath, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        }

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

    static Task<WorldJsonLinesValidationResult> ValidateJsonLinesAsync(
        WorldArtifactManifest manifest,
        Stream input,
        CancellationToken cancellationToken) =>
        string.Equals(
            manifest.Interpreter,
            RelationshipWorldInterpreter.Identity,
            StringComparison.Ordinal)
            ? RelationshipWorldJsonLinesVerifier.ValidateAsync(manifest, input, cancellationToken)
            : WorldJsonLinesVerifier.ValidateAsync(manifest, input, cancellationToken);

    static void WriteVerificationFailure(
        CliCommandContext<WorldVerifyCliOptions> context,
        IReadOnlyList<DocumentValidationDiagnostic> diagnostics) =>
        context.Output.WriteJsonError(new WorldVerifyCliReport(
            WorldVerifyCliReport.CurrentSchemaVersion,
            IsValid: false,
            Verification: null,
            diagnostics));

    static string VerificationFailureCode(Exception exception) => exception switch
    {
        NotSupportedException => "simulation.cli.verify.artifactUnsupported",
        System.Text.Json.JsonException or ArgumentException => "simulation.cli.verify.artifactInvalid",
        IOException or UnauthorizedAccessException => "simulation.cli.verify.inputUnavailable",
        _ => "simulation.cli.verify.failed"
    };
}
