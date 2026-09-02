using System.Text;
using Cohesive.Cli;
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
        var app = new CliApplication("Provision deterministic data from a portable Cohesive simulation world.");
        app.Command<SimulationCliOptions>("provision", "Compile and provision a portable world as JSON Lines.")
            .Validate((SimulationCliOptions options) =>
                options.BatchSize > 0
                    ? []
                    : new[] { $"Batch size '{options.BatchSize}' is not a positive 32-bit integer." })
            .OnExecute((CliCommandContext<SimulationCliOptions> context) =>
                ProvisionAsync(context, standardInput, standardOutput));
        return app;
    }

    static async Task<int> ProvisionAsync(
        CliCommandContext<SimulationCliOptions> context,
        Stream standardInput,
        Stream standardOutput)
    {
        try
        {
            var options = NormalizePaths(context.Configuration);
            var worldJson = await ReadWorldJsonAsync(options, standardInput, context.CancellationToken)
                .ConfigureAwait(false);
            var document = WorldDefinitionJsonSerializer.Deserialize(worldJson);
            if (options.WritesStandardOutput)
            {
                await ProvisionAsync(document, options, standardOutput, context.CancellationToken).ConfigureAwait(false);
            }
            else
            {
                await ProvisionFileAsync(document, options, context.CancellationToken).ConfigureAwait(false);
            }

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

    static SimulationCliOptions NormalizePaths(SimulationCliOptions options) =>
        options with
        {
            WorldPath = NormalizePath(options.WorldPath, "--world"),
            OutputPath = NormalizePath(options.OutputPath, "--out")
        };

    static string NormalizePath(string value, string option)
    {
        if (string.Equals(value, SimulationCliOptions.StandardStreamPath, StringComparison.Ordinal))
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

    static async Task<string> ReadWorldJsonAsync(
        SimulationCliOptions options,
        Stream standardInput,
        CancellationToken cancellationToken)
    {
        if (!options.ReadsStandardInput)
            return await File.ReadAllTextAsync(options.WorldPath, Encoding.UTF8, cancellationToken).ConfigureAwait(false);

        using var reader = CliStandardStreams.OpenUtf8Reader(standardInput);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    static async Task ProvisionFileAsync(
        WorldDefinitionDocument document,
        SimulationCliOptions options,
        CancellationToken cancellationToken)
    {
        var outputDirectory = Path.GetDirectoryName(options.OutputPath)
            ?? throw new InvalidOperationException($"Output path '{options.OutputPath}' has no parent directory.");
        Directory.CreateDirectory(outputDirectory);
        var temporaryPath = Path.Combine(
            outputDirectory,
            $".{Path.GetFileName(options.OutputPath)}.{Guid.NewGuid():N}.tmp");
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
                await ProvisionAsync(document, options, temporaryOutput, cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, options.OutputPath, overwrite: true);
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
            // Preserve the provisioning failure. The uniquely named temporary artifact remains diagnosable.
        }
    }

    static async Task ProvisionAsync(
        WorldDefinitionDocument document,
        SimulationCliOptions options,
        Stream output,
        CancellationToken cancellationToken)
    {
        WorldJsonLinesSink sink = new(options.TargetId, output);
        await WorldProvisioner.ProvisionAsync(
                document.Compile(),
                options.RootSeed,
                sink,
                new(options.BatchSize),
                cancellationToken)
            .ConfigureAwait(false);
    }
}
