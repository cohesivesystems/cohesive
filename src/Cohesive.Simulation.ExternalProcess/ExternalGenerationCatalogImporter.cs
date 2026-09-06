using System.Buffers;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Cohesive.Model;
using Cohesive.Model.Authoring;
using Cohesive.Simulation.Generation;

namespace Cohesive.Simulation.ExternalProcess;

/// <summary>Configured executable and asserted semantics for one external catalog provider.</summary>
public sealed class ExternalGenerationCatalogProvider
{
    /// <summary>Default wall-clock limit for one external provider invocation.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Default maximum bytes accepted for a request or response protocol message.</summary>
    public const int DefaultMaximumMessageBytes = 16 * 1024 * 1024;

    /// <summary>Default maximum standard-error bytes retained for diagnostics.</summary>
    public const int DefaultMaximumStandardErrorBytes = 64 * 1024;

    /// <summary>Creates an external catalog provider definition.</summary>
    /// <param name="executable">Executable file name or path launched directly without a command shell.</param>
    /// <param name="arguments">Ordered arguments passed exactly through the platform process API.</param>
    /// <param name="provider">Stable external provider identity expected in every response.</param>
    /// <param name="providerVersion">Exact external provider version expected in every response.</param>
    /// <param name="randomAlgorithm">Exact provider random-algorithm or deterministic-seeding profile.</param>
    /// <param name="capabilityProfile">Versioned capability assertions and evidence for the producer.</param>
    /// <param name="workingDirectory">Optional process working directory.</param>
    /// <param name="timeout">Optional positive wall-clock invocation limit; defaults to 30 seconds.</param>
    /// <param name="maximumMessageBytes">Positive maximum bytes accepted for a request or response message.</param>
    /// <param name="maximumStandardErrorBytes">Positive maximum standard-error bytes retained for diagnostics.</param>
    /// <exception cref="ArgumentNullException">A required reference value or argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">A required identity or optional working directory is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The timeout or a byte limit is not positive, or the timeout exceeds one day.
    /// </exception>
    public ExternalGenerationCatalogProvider(
        string executable,
        ImmutableArray<string> arguments,
        string provider,
        string providerVersion,
        string randomAlgorithm,
        GenerationCatalogCapabilityProfile capabilityProfile,
        string? workingDirectory = null,
        TimeSpan? timeout = null,
        int maximumMessageBytes = DefaultMaximumMessageBytes,
        int maximumStandardErrorBytes = DefaultMaximumStandardErrorBytes)
    {
        Executable = Guard.RequireNotNullOrWhiteSpace(executable);
        if (!arguments.IsDefaultOrEmpty)
        {
            foreach (var argument in arguments)
                ArgumentNullException.ThrowIfNull(argument, nameof(arguments));
        }

        Arguments = arguments.IsDefault ? [] : arguments;
        Provider = Guard.RequireNotNullOrWhiteSpace(provider);
        ProviderVersion = Guard.RequireNotNullOrWhiteSpace(providerVersion);
        RandomAlgorithm = Guard.RequireNotNullOrWhiteSpace(randomAlgorithm);
        CapabilityProfile = Guard.RequireNotNull(capabilityProfile);
        WorkingDirectory = workingDirectory switch
        {
            null => null,
            { Length: > 0 } when !string.IsNullOrWhiteSpace(workingDirectory) => workingDirectory,
            _ => throw new ArgumentException("An external provider working directory cannot be empty.", nameof(workingDirectory))
        };

        Timeout = timeout ?? DefaultTimeout;
        if (Timeout <= TimeSpan.Zero || Timeout > TimeSpan.FromDays(1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                Timeout,
                "An external provider timeout must be positive and no greater than one day.");
        }

        MaximumMessageBytes = RequirePositive(
            maximumMessageBytes,
            nameof(maximumMessageBytes),
            "An external provider message limit must be positive.");
        MaximumStandardErrorBytes = RequirePositive(
            maximumStandardErrorBytes,
            nameof(maximumStandardErrorBytes),
            "An external provider standard-error limit must be positive.");
    }

    /// <summary>Gets the executable launched directly without a command shell.</summary>
    public string Executable { get; }

    /// <summary>Gets the ordered arguments passed exactly to the executable.</summary>
    public ImmutableArray<string> Arguments { get; }

    /// <summary>Gets the stable external provider identity expected in every response.</summary>
    public string Provider { get; }

    /// <summary>Gets the exact external provider version expected in every response.</summary>
    public string ProviderVersion { get; }

    /// <summary>Gets the exact provider random-algorithm or deterministic-seeding profile.</summary>
    public string RandomAlgorithm { get; }

    /// <summary>Gets the versioned producer capabilities and their evidence.</summary>
    public GenerationCatalogCapabilityProfile CapabilityProfile { get; }

    /// <summary>Gets the optional process working directory.</summary>
    public string? WorkingDirectory { get; }

    /// <summary>Gets the wall-clock invocation limit.</summary>
    public TimeSpan Timeout { get; }

    /// <summary>Gets the maximum bytes accepted for either protocol message.</summary>
    public int MaximumMessageBytes { get; }

    /// <summary>Gets the maximum standard-error bytes retained for diagnostics.</summary>
    public int MaximumStandardErrorBytes { get; }

    static int RequirePositive(int value, string parameterName, string message) =>
        value > 0 ? value : throw new ArgumentOutOfRangeException(parameterName, value, message);
}

/// <summary>Application-owned inputs for one finite external generation-catalog import.</summary>
public sealed class ExternalGenerationCatalogImportOptions
{
    /// <summary>Creates exact inputs for one external catalog import.</summary>
    /// <param name="id">Stable logical catalog identity.</param>
    /// <param name="revision">Exact application-owned catalog revision.</param>
    /// <param name="count">Positive number of provider values to retain.</param>
    /// <param name="seed">Signed 64-bit producer-local seed.</param>
    /// <param name="configuration">Provider-owned declarative configuration object.</param>
    /// <param name="sourceReferences">Exact application, script, or specification sources defining this import.</param>
    /// <param name="locale">Optional exact provider locale.</param>
    /// <param name="dateTimeReferenceUtc">Optional fixed UTC provider reference time.</param>
    /// <exception cref="ArgumentNullException">A required reference value is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// An identity or locale is empty, configuration is not an object, the source set is empty or invalid, or the
    /// reference time is not UTC.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is not positive.</exception>
    public ExternalGenerationCatalogImportOptions(
        string id,
        string revision,
        int count,
        long seed,
        JsonElement configuration,
        ImmutableArray<SourceReference> sourceReferences,
        string? locale = null,
        DateTimeOffset? dateTimeReferenceUtc = null)
    {
        Id = Guard.RequireNotNullOrWhiteSpace(id);
        Revision = Guard.RequireNotNullOrWhiteSpace(revision);
        Count = count > 0
            ? count
            : throw new ArgumentOutOfRangeException(nameof(count), count, "An external catalog import requires at least one value.");
        Seed = seed;
        Configuration = ExternalGenerationCatalogProtocol.NormalizeConfiguration(configuration, nameof(configuration));
        SourceReferences = SourceReference.NormalizeSet(sourceReferences, requireNonEmpty: true);
        Locale = ExternalGenerationCatalogProtocol.NormalizeOptionalCoordinate(locale, nameof(locale));
        DateTimeReferenceUtc = ExternalGenerationCatalogProtocol.RequireUtcDateTimeReference(
            dateTimeReferenceUtc,
            nameof(dateTimeReferenceUtc));
    }

    /// <summary>Gets the stable logical catalog identity.</summary>
    public string Id { get; }

    /// <summary>Gets the exact application-owned catalog revision.</summary>
    public string Revision { get; }

    /// <summary>Gets the positive number of provider values retained.</summary>
    public int Count { get; }

    /// <summary>Gets the signed 64-bit producer-local seed.</summary>
    public long Seed { get; }

    /// <summary>Gets a detached provider-owned declarative configuration object.</summary>
    public JsonElement Configuration { get; }

    /// <summary>Gets normalized application, script, or specification sources defining this import.</summary>
    public ImmutableArray<SourceReference> SourceReferences { get; }

    /// <summary>Gets the exact provider locale when locale selection is requested.</summary>
    public string? Locale { get; }

    /// <summary>Gets the fixed UTC provider reference time when one is requested.</summary>
    public DateTimeOffset? DateTimeReferenceUtc { get; }
}

/// <summary>Stable failure classification for an external catalog-provider invocation.</summary>
public enum ExternalGenerationCatalogFailure
{
    /// <summary>The request exceeded the configured message bound before process launch.</summary>
    RequestTooLarge = 0,

    /// <summary>The operating system could not start the configured executable.</summary>
    StartFailed = 1,

    /// <summary>The provider exceeded its configured wall-clock limit.</summary>
    TimedOut = 2,

    /// <summary>The provider exited with a nonzero code or failed while exchanging standard streams.</summary>
    ProcessFailed = 3,

    /// <summary>The provider response exceeded the configured message bound.</summary>
    ResponseTooLarge = 4,

    /// <summary>The provider response was not strict canonical protocol JSON.</summary>
    ResponseInvalid = 5,

    /// <summary>The response did not match the request, provider identity, pinned version, or requested count.</summary>
    ResponseMismatch = 6
}

/// <summary>Bounded diagnostic failure from an external generation-catalog provider.</summary>
public sealed class ExternalGenerationCatalogException : Exception
{
    internal ExternalGenerationCatalogException(
        ExternalGenerationCatalogFailure failure,
        string message,
        int? exitCode = null,
        string standardError = "",
        bool standardErrorTruncated = false,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Failure = failure;
        ExitCode = exitCode;
        StandardError = standardError;
        StandardErrorTruncated = standardErrorTruncated;
    }

    /// <summary>Gets the stable failure classification.</summary>
    public ExternalGenerationCatalogFailure Failure { get; }

    /// <summary>Gets the provider exit code when the process reached an exited state.</summary>
    public int? ExitCode { get; }

    /// <summary>Gets bounded provider standard error retained for diagnostics.</summary>
    public string StandardError { get; }

    /// <summary>Gets whether additional provider standard-error bytes were discarded.</summary>
    public bool StandardErrorTruncated { get; }
}

/// <summary>Imports exact values from one bounded external process into a portable generation catalog.</summary>
public static class ExternalGenerationCatalogImporter
{
    const string AdapterPackageVersionMetadata = "CohesiveAdapterPackageVersion";
    static readonly byte[] NewLine = "\n"u8.ToArray();
    static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    static readonly DefaultClrTypeRefMapper TypeMapper = new();

    /// <summary>Stable adapter identity retained separately from producer and provider identities.</summary>
    public const string AdapterIdentity = "Cohesive.Simulation.ExternalProcess";

    /// <summary>Gets the exact adapter package version retained in imported catalog provenance.</summary>
    public static string AdapterVersion { get; } = RequirePackageVersion();

    /// <summary>Invokes a provider process once and retains its complete response as a portable catalog.</summary>
    /// <typeparam name="TValue">CLR value type every provider response value must satisfy.</typeparam>
    /// <param name="provider">Executable, operational bounds, and asserted provider semantics.</param>
    /// <param name="options">Application-owned catalog identity, deterministic inputs, configuration, and sources.</param>
    /// <param name="cancellationToken">Token that cancels invocation and terminates the complete provider process tree.</param>
    /// <returns>A fingerprinted finite catalog that no longer depends on the provider process.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="provider"/> or <paramref name="options"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// <typeparamref name="TValue"/> is not portable, or capability assertions do not match import coordinates.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is canceled.</exception>
    /// <exception cref="ExternalGenerationCatalogException">
    /// The process cannot start, times out, fails, exceeds a bound, emits invalid protocol JSON, or emits a response
    /// that does not correlate with the request.
    /// </exception>
    public static async Task<GenerationCatalogDocument> ImportAsync<TValue>(
        ExternalGenerationCatalogProvider provider,
        ExternalGenerationCatalogImportOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();

        var valueType = TypeMapper.Map(typeof(TValue), nullability: null);
        _ = CreateProvenance(provider, options);
        var request = ExternalGenerationCatalogProtocol.CreateRequest(
            options.Id,
            options.Revision,
            options.Count,
            options.Seed,
            valueType,
            options.Configuration,
            options.Locale,
            options.DateTimeReferenceUtc);
        var requestBytes = Encoding.UTF8.GetBytes(ExternalGenerationCatalogProtocol.SerializeRequest(request));
        if (requestBytes.Length > provider.MaximumMessageBytes)
        {
            throw new ExternalGenerationCatalogException(
                ExternalGenerationCatalogFailure.RequestTooLarge,
                $"External catalog request contains '{requestBytes.Length}' bytes; the configured limit is "
                + $"'{provider.MaximumMessageBytes}'.");
        }

        using Process process = new() { StartInfo = CreateStartInfo(provider) };
        try
        {
            if (!process.Start())
            {
                throw new ExternalGenerationCatalogException(
                    ExternalGenerationCatalogFailure.StartFailed,
                    $"External catalog provider executable '{provider.Executable}' did not start.");
            }
        }
        catch (ExternalGenerationCatalogException)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            throw new ExternalGenerationCatalogException(
                ExternalGenerationCatalogFailure.StartFailed,
                $"External catalog provider executable '{provider.Executable}' could not be started.",
                innerException: exception);
        }

        var standardOutputTask = ReadBoundedAsync(process.StandardOutput.BaseStream, provider.MaximumMessageBytes);
        var standardErrorTask = ReadBoundedAsync(process.StandardError.BaseStream, provider.MaximumStandardErrorBytes);
        using CancellationTokenSource invocation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        invocation.CancelAfter(provider.Timeout);

        try
        {
            await process.StandardInput.BaseStream.WriteAsync(requestBytes, invocation.Token).ConfigureAwait(false);
            await process.StandardInput.BaseStream.WriteAsync(NewLine, invocation.Token).ConfigureAwait(false);
            await process.StandardInput.BaseStream.FlushAsync(invocation.Token).ConfigureAwait(false);
            process.StandardInput.Close();
            await process.WaitForExitAsync(invocation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (invocation.IsCancellationRequested)
        {
            TryTerminate(process);
            await AwaitTerminationAsync(process).ConfigureAwait(false);
            _ = await standardOutputTask.ConfigureAwait(false);
            var error = await standardErrorTask.ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
                cancellationToken.ThrowIfCancellationRequested();

            throw new ExternalGenerationCatalogException(
                ExternalGenerationCatalogFailure.TimedOut,
                $"External catalog provider exceeded its '{provider.Timeout}' invocation limit.",
                process.HasExited ? process.ExitCode : null,
                DecodeDiagnostic(error.Bytes),
                error.Truncated);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            TryTerminate(process);
            await AwaitTerminationAsync(process).ConfigureAwait(false);
            _ = await standardOutputTask.ConfigureAwait(false);
            var error = await standardErrorTask.ConfigureAwait(false);
            throw new ExternalGenerationCatalogException(
                ExternalGenerationCatalogFailure.ProcessFailed,
                "External catalog provider failed while exchanging standard streams.",
                process.HasExited ? process.ExitCode : null,
                DecodeDiagnostic(error.Bytes),
                error.Truncated,
                exception);
        }

        var standardOutput = await standardOutputTask.ConfigureAwait(false);
        var standardError = await standardErrorTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new ExternalGenerationCatalogException(
                ExternalGenerationCatalogFailure.ProcessFailed,
                $"External catalog provider exited with code '{process.ExitCode}'.",
                process.ExitCode,
                DecodeDiagnostic(standardError.Bytes),
                standardError.Truncated);
        }
        if (standardOutput.Truncated)
        {
            throw new ExternalGenerationCatalogException(
                ExternalGenerationCatalogFailure.ResponseTooLarge,
                $"External catalog response exceeded the configured '{provider.MaximumMessageBytes}'-byte limit.",
                process.ExitCode,
                DecodeDiagnostic(standardError.Bytes),
                standardError.Truncated);
        }

        ExternalGenerationCatalogResponse response;
        try
        {
            response = ExternalGenerationCatalogProtocol.DeserializeResponse(StrictUtf8.GetString(standardOutput.Bytes));
        }
        catch (Exception exception) when (exception is JsonException or DecoderFallbackException or ArgumentException)
        {
            throw new ExternalGenerationCatalogException(
                ExternalGenerationCatalogFailure.ResponseInvalid,
                "External catalog provider emitted invalid protocol JSON.",
                process.ExitCode,
                DecodeDiagnostic(standardError.Bytes),
                standardError.Truncated,
                exception);
        }

        if (!string.Equals(response.RequestId, request.RequestId, StringComparison.Ordinal)
            || !string.Equals(response.Provider, provider.Provider, StringComparison.Ordinal)
            || !string.Equals(response.ProviderVersion, provider.ProviderVersion, StringComparison.Ordinal)
            || response.Values.Length != request.Count)
        {
            throw new ExternalGenerationCatalogException(
                ExternalGenerationCatalogFailure.ResponseMismatch,
                "External catalog provider response does not match the request identity, provider identity, pinned "
                + "provider version, or requested count.",
                process.ExitCode,
                DecodeDiagnostic(standardError.Bytes),
                standardError.Truncated);
        }

        var entries = ImmutableArray.CreateBuilder<GenerationCatalogEntry>(response.Values.Length);
        for (var index = 0; index < response.Values.Length; index++)
        {
            entries.Add(new(
                $"sample/{index.ToString("D8", CultureInfo.InvariantCulture)}",
                response.Values[index]));
        }

        try
        {
            return GenerationCatalogDocument.FromDefinition(new(
                options.Id,
                options.Revision,
                valueType,
                entries.MoveToImmutable(),
                CreateProvenance(provider, options, request.RequestId)));
        }
        catch (ArgumentException exception)
        {
            throw new ExternalGenerationCatalogException(
                ExternalGenerationCatalogFailure.ResponseInvalid,
                "External catalog provider values do not satisfy the requested portable value contract.",
                process.ExitCode,
                DecodeDiagnostic(standardError.Bytes),
                standardError.Truncated,
                exception);
        }
    }

    static GenerationCatalogProvenance CreateProvenance(
        ExternalGenerationCatalogProvider provider,
        ExternalGenerationCatalogImportOptions options,
        string? requestId = null)
    {
        var sourceReferences = requestId is null
            ? options.SourceReferences
            : options.SourceReferences.Add(SourceReference.Create(
                ExternalGenerationCatalogProtocol.RequestReferenceScheme,
                requestId));
        return new(
            adapter: AdapterIdentity,
            adapterVersion: AdapterVersion,
            provider: provider.Provider,
            providerVersion: provider.ProviderVersion,
            capabilityProfile: provider.CapabilityProfile,
            locale: options.Locale,
            randomAlgorithm: provider.RandomAlgorithm,
            seed: options.Seed.ToString(CultureInfo.InvariantCulture),
            dateTimeReferenceUtc: options.DateTimeReferenceUtc,
            sourceReferences: sourceReferences);
    }

    static ProcessStartInfo CreateStartInfo(ExternalGenerationCatalogProvider provider)
    {
        ProcessStartInfo start = new()
        {
            FileName = provider.Executable,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (provider.WorkingDirectory is not null)
            start.WorkingDirectory = provider.WorkingDirectory;
        foreach (var argument in provider.Arguments)
            start.ArgumentList.Add(argument);
        return start;
    }

    static async Task<BoundedBytes> ReadBoundedAsync(Stream stream, int maximumBytes)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(8192);
        ArrayBufferWriter<byte> retained = new(Math.Min(maximumBytes, buffer.Length));
        var truncated = false;
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer).ConfigureAwait(false);
                if (read == 0)
                    break;

                var available = maximumBytes - retained.WrittenCount;
                var retain = Math.Min(read, Math.Max(available, 0));
                if (retain > 0)
                    retained.Write(buffer.AsSpan(0, retain));
                if (retain != read)
                    truncated = true;
            }

            return new(retained.WrittenSpan.ToArray(), truncated);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    static string DecodeDiagnostic(byte[] bytes) => Encoding.UTF8.GetString(bytes);

    static void TryTerminate(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }

    static async Task AwaitTerminationAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
                await process.WaitForExitAsync().ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
        }
    }

    static string RequirePackageVersion()
    {
        foreach (var metadata in typeof(ExternalGenerationCatalogImporter)
                     .Assembly
                     .GetCustomAttributes<AssemblyMetadataAttribute>())
        {
            if (string.Equals(metadata.Key, AdapterPackageVersionMetadata, StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(metadata.Value))
            {
                return metadata.Value;
            }
        }

        throw new InvalidOperationException($"Adapter assembly metadata '{AdapterPackageVersionMetadata}' is required.");
    }

    readonly record struct BoundedBytes(byte[] Bytes, bool Truncated);
}
