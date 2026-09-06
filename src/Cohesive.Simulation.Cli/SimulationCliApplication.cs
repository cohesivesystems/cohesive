using Cohesive.Cli;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Simulation.Artifacts;
using Cohesive.Simulation.ExternalProcess;
using Cohesive.Simulation.Generation;
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
        var catalog = app.Command<CatalogCliOptions>(
            "catalog",
            "Import, inspect, and validate retained generation catalogs.");
        catalog.SubCommand<CatalogVerifyCliOptions>(
                "verify",
                "Verify a retained generation-catalog document.")
            .OnExecute(VerifyCatalogAsync);
        catalog.SubCommand<ExternalCatalogImportCliOptions>(
                "import-external",
                "Import and retain a catalog from a bounded external provider process.")
            .OnExecute(ImportExternalCatalogAsync);
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
            context.Io.WriteJson(new CliVerificationReport<WorldVerifyCliEvidence>(
                CliVerificationReportSchemas.WorldArtifact,
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

    static async Task<int> VerifyCatalogAsync(CliCommandContext<CatalogVerifyCliOptions> context)
    {
        try
        {
            var options = NormalizePaths(context.Configuration);
            var catalogJson = await context.Io.ReadUtf8TextAsync(
                    options.CatalogPath,
                    context.CancellationToken)
                .ConfigureAwait(false);
            var validation = GenerationCatalogJsonSerializer.TryDeserialize(
                catalogJson,
                out var catalog);
            if (!validation.IsValid || catalog is null)
            {
                WriteCatalogVerificationFailure(context, validation.Diagnostics);
                return FailureExitCode;
            }

            var definition = catalog.Definition;
            context.Io.WriteJson(new CliVerificationReport<CatalogVerifyCliEvidence>(
                CliVerificationReportSchemas.GenerationCatalog,
                IsValid: true,
                new(
                    catalog.SchemaVersion,
                    definition.Id,
                    definition.Revision,
                    catalog.Fingerprint.Value,
                    definition.ValueType,
                    definition.Entries.Length,
                    definition.Provenance),
                Diagnostics: []));
            return SuccessExitCode;
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            context.Io.WriteErrorLine("Generation-catalog verification was cancelled.");
            return CancelledExitCode;
        }
        catch (Exception exception)
        {
            WriteCatalogVerificationFailure(
                context,
                [new(
                    CatalogVerificationFailureCode(exception),
                    DiagnosticSeverity.Error,
                    exception.Message)]);
            return FailureExitCode;
        }
    }

    static async Task<int> ImportExternalCatalogAsync(
        CliCommandContext<ExternalCatalogImportCliOptions> context)
    {
        try
        {
            var options = NormalizePaths(context.Configuration);
            var definitionJson = await context.Io.ReadUtf8TextAsync(
                    options.DefinitionPath,
                    context.CancellationToken)
                .ConfigureAwait(false);
            var validation = ExternalGenerationCatalogImportJsonSerializer.TryDeserialize(
                definitionJson,
                out var definition);
            if (!validation.IsValid || definition is null)
            {
                WriteExternalCatalogImportFailure(
                    context,
                    code: "simulation.cli.catalog.import.external.definitionInvalid",
                    message: "The external generation-catalog import definition is invalid.",
                    diagnostics: validation.Diagnostics);
                return FailureExitCode;
            }

            var provider = new ExternalGenerationCatalogProvider(
                executable: options.Executable,
                arguments: [.. options.Arguments],
                provider: definition.Provider,
                providerVersion: definition.ProviderVersion,
                randomAlgorithm: definition.RandomAlgorithm,
                capabilityProfile: definition.CapabilityProfile,
                workingDirectory: options.WorkingDirectory,
                timeout: TimeSpan.FromSeconds(options.TimeoutSeconds),
                maximumMessageBytes: options.MaximumMessageBytes,
                maximumStandardErrorBytes: options.MaximumStandardErrorBytes);
            var imported = await ExternalGenerationCatalogImporter.ImportAsync(
                    provider,
                    definition,
                    context.CancellationToken)
                .ConfigureAwait(false);
            var canonicalCatalog = GenerationCatalogJsonSerializer.GetCanonicalBytes(imported);
            await context.Io.WriteOutputAsync(
                    options.OutputPath,
                    async (output, cancellationToken) =>
                    {
                        await output.WriteAsync(canonicalCatalog, cancellationToken).ConfigureAwait(false);
                        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                    },
                    context.CancellationToken)
                .ConfigureAwait(false);
            return SuccessExitCode;
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            WriteExternalCatalogImportFailure(
                context,
                code: "simulation.cli.catalog.import.external.cancelled",
                message: "External generation-catalog import was cancelled.");
            return CancelledExitCode;
        }
        catch (ExternalGenerationCatalogException exception)
        {
            WriteExternalCatalogImportFailure(
                context,
                ExternalCatalogImportFailureCode(exception.Failure),
                exception.Message,
                exception);
            return FailureExitCode;
        }
        catch (Exception exception)
        {
            WriteExternalCatalogImportFailure(
                context,
                ExternalCatalogImportFailureCode(exception),
                exception.Message);
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

    static CatalogVerifyCliOptions NormalizePaths(CatalogVerifyCliOptions options) =>
        options with
        {
            CatalogPath = NormalizePath(options.CatalogPath, "--catalog")
        };

    static ExternalCatalogImportCliOptions NormalizePaths(ExternalCatalogImportCliOptions options) =>
        options with
        {
            DefinitionPath = NormalizePath(options.DefinitionPath, "--definition"),
            WorkingDirectory = options.WorkingDirectory is null
                ? null
                : NormalizePath(options.WorkingDirectory, "--working-directory"),
            OutputPath = NormalizePath(options.OutputPath, "--out")
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
        context.Io.WriteJsonError(new CliVerificationReport<WorldVerifyCliEvidence>(
            CliVerificationReportSchemas.WorldArtifact,
            IsValid: false,
            Verification: null,
            diagnostics));

    static void WriteCatalogVerificationFailure(
        CliCommandContext<CatalogVerifyCliOptions> context,
        IReadOnlyList<DocumentValidationDiagnostic> diagnostics) =>
        context.Io.WriteJsonError(new CliVerificationReport<CatalogVerifyCliEvidence>(
            CliVerificationReportSchemas.GenerationCatalog,
            IsValid: false,
            Verification: null,
            diagnostics));

    static void WriteExternalCatalogImportFailure(
        CliCommandContext<ExternalCatalogImportCliOptions> context,
        string code,
        string message,
        ExternalGenerationCatalogException? providerFailure = null,
        IReadOnlyList<DocumentValidationDiagnostic>? diagnostics = null) =>
        context.Io.WriteJsonError(new ExternalCatalogImportCliFailureReport(
            ExternalCatalogImportCliFailureReport.CurrentSchemaVersion,
            IsSuccessful: false,
            code,
            message,
            providerFailure?.Failure,
            providerFailure?.ExitCode,
            string.IsNullOrEmpty(providerFailure?.StandardError)
                ? null
                : providerFailure.StandardError,
            providerFailure?.StandardErrorTruncated ?? false,
            diagnostics ?? []));

    static string VerificationFailureCode(Exception exception) => exception switch
    {
        NotSupportedException => "simulation.cli.verify.artifactUnsupported",
        System.Text.Json.JsonException or ArgumentException => "simulation.cli.verify.artifactInvalid",
        IOException or UnauthorizedAccessException => "simulation.cli.verify.inputUnavailable",
        _ => "simulation.cli.verify.failed"
    };

    static string CatalogVerificationFailureCode(Exception exception) => exception switch
    {
        System.Text.Json.JsonException or ArgumentException => "simulation.cli.catalog.verify.catalogInvalid",
        IOException or UnauthorizedAccessException => "simulation.cli.catalog.verify.inputUnavailable",
        _ => "simulation.cli.catalog.verify.failed"
    };

    static string ExternalCatalogImportFailureCode(ExternalGenerationCatalogFailure failure) => failure switch
    {
        ExternalGenerationCatalogFailure.RequestTooLarge =>
            "simulation.cli.catalog.import.external.requestTooLarge",
        ExternalGenerationCatalogFailure.StartFailed =>
            "simulation.cli.catalog.import.external.startFailed",
        ExternalGenerationCatalogFailure.TimedOut =>
            "simulation.cli.catalog.import.external.timedOut",
        ExternalGenerationCatalogFailure.ProcessFailed =>
            "simulation.cli.catalog.import.external.processFailed",
        ExternalGenerationCatalogFailure.ResponseTooLarge =>
            "simulation.cli.catalog.import.external.responseTooLarge",
        ExternalGenerationCatalogFailure.ResponseInvalid =>
            "simulation.cli.catalog.import.external.responseInvalid",
        ExternalGenerationCatalogFailure.ResponseMismatch =>
            "simulation.cli.catalog.import.external.responseMismatch",
        _ => "simulation.cli.catalog.import.external.failed"
    };

    static string ExternalCatalogImportFailureCode(Exception exception) => exception switch
    {
        System.Text.Json.JsonException or ArgumentException =>
            "simulation.cli.catalog.import.external.invalid",
        IOException or UnauthorizedAccessException =>
            "simulation.cli.catalog.import.external.ioUnavailable",
        _ => "simulation.cli.catalog.import.external.failed"
    };
}
