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

    public static CliApplication Create(CommandIo? io = null)
    {
        var app = new CliApplication(
            "Create and provision deterministic Cohesive world artifacts.",
            io);
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

    static async Task<int> CreateManifestAsync(CliCommandContext<WorldManifestCliOptions> context)
    {
        try
        {
            var options = NormalizePaths(context.Configuration);
            var worldJson = await context.Io.ReadUtf8TextAsync(
                    options.InputPath,
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
            await context.Io.WriteOutputAsync(
                    options.OutputPath,
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
            context.Io.WriteErrorLine("Simulation manifest creation was cancelled.");
            return CancelledExitCode;
        }
        catch (Exception exception)
        {
            context.Io.WriteErrorLine(exception.Message);
            return FailureExitCode;
        }
    }

    static async Task<int> ProvisionAsync(CliCommandContext<WorldProvisionCliOptions> context)
    {
        try
        {
            var options = NormalizePaths(context.Configuration);
            var manifestJson = await context.Io.ReadUtf8TextAsync(
                    options.ManifestPath,
                    context.CancellationToken)
                .ConfigureAwait(false);
            var manifest = WorldArtifactManifestJsonSerializer.Deserialize(manifestJson);
            await context.Io.WriteOutputAsync(
                    options.OutputPath,
                    (output, cancellationToken) => ProvisionAsync(manifest, options, output, cancellationToken),
                    context.CancellationToken)
                .ConfigureAwait(false);
            return SuccessExitCode;
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            context.Io.WriteErrorLine("Simulation provisioning was cancelled.");
            return CancelledExitCode;
        }
        catch (Exception exception)
        {
            context.Io.WriteErrorLine(exception.Message);
            return FailureExitCode;
        }
    }

    static async Task<int> VerifyAsync(CliCommandContext<WorldVerifyCliOptions> context)
    {
        try
        {
            var options = NormalizePaths(context.Configuration);
            var manifestJson = await context.Io.ReadUtf8TextAsync(
                    options.ManifestPath,
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

            var validation = await context.Io.ReadInputAsync(
                    options.JsonLinesPath,
                    (input, cancellationToken) => ValidateJsonLinesAsync(
                        manifest,
                        input,
                        cancellationToken),
                    context.CancellationToken)
                .ConfigureAwait(false);

            if (!validation.IsSuccessful || validation.Verification is null)
            {
                WriteVerificationFailure(context, validation.Validation.Diagnostics);
                return FailureExitCode;
            }

            var verification = validation.Verification;
            context.Io.WriteJson(new WorldVerifyCliReport(
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
            context.Io.WriteErrorLine("Simulation verification was cancelled.");
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
        if (CommandIo.IsStandardStreamPath(value))
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
        context.Io.WriteJsonError(new WorldVerifyCliReport(
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
