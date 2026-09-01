using System.Text;
using Cohesive.Simulation.Provisioning;
using Cohesive.Simulation.Worlds;

namespace Cohesive.Simulation.Cli;

static class SimulationCliApplication
{
    const int SuccessExitCode = 0;
    const int FailureExitCode = 1;
    const int UsageExitCode = 2;
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
        if (!SimulationCliParser.TryParse(args, out var options, out var error, out var showHelp))
        {
            if (showHelp)
            {
                using StreamWriter helpWriter = new(
                    standardOutput,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    bufferSize: 1024,
                    leaveOpen: true)
                {
                    AutoFlush = true
                };
                SimulationCliUsage.WriteTo(helpWriter);
                return SuccessExitCode;
            }

            await standardError.WriteLineAsync(error).ConfigureAwait(false);
            SimulationCliUsage.WriteTo(standardError);
            return UsageExitCode;
        }

        try
        {
            var parsedOptions = options!;
            var worldJson = await ReadWorldJsonAsync(parsedOptions, standardInput, cancellationToken)
                .ConfigureAwait(false);
            var document = WorldDefinitionJsonSerializer.Deserialize(worldJson);
            if (parsedOptions.WritesStandardOutput)
            {
                await ProvisionAsync(document, parsedOptions, standardOutput, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await ProvisionFileAsync(document, parsedOptions, cancellationToken).ConfigureAwait(false);
            }

            return SuccessExitCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await standardError.WriteLineAsync("Simulation provisioning was cancelled.").ConfigureAwait(false);
            return CancelledExitCode;
        }
        catch (Exception exception)
        {
            await standardError.WriteLineAsync(exception.Message).ConfigureAwait(false);
            return FailureExitCode;
        }
    }

    static async Task<string> ReadWorldJsonAsync(
        SimulationCliOptions options,
        Stream standardInput,
        CancellationToken cancellationToken)
    {
        if (!options.ReadsStandardInput)
            return await File.ReadAllTextAsync(options.WorldPath, Encoding.UTF8, cancellationToken).ConfigureAwait(false);

        using StreamReader reader = new(
            standardInput,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 4096,
            leaveOpen: true);
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
