using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cohesive.Storage.Materialization;

namespace Cohesive.MaterializationHarness.Host;

sealed record MaterializationHarnessBoundaryFaultPlan
{
    internal const string BoundaryEnvironmentVariable = "COHESIVE_MATERIALIZATION_FAULT_BOUNDARY";
    internal const string MarkerPathEnvironmentVariable = "COHESIVE_MATERIALIZATION_FAULT_MARKER_PATH";
    internal const string OccurrenceEnvironmentVariable = "COHESIVE_MATERIALIZATION_FAULT_OCCURRENCE";
    internal const string OperationEnvironmentVariable = "COHESIVE_MATERIALIZATION_FAULT_OPERATION";
    internal const string ProviderEnvironmentVariable = "COHESIVE_MATERIALIZATION_FAULT_PROVIDER";
    internal const string RunEnvironmentVariable = "COHESIVE_MATERIALIZATION_FAULT_RUN_ID";
    internal const string ScopeEnvironmentVariable = "COHESIVE_MATERIALIZATION_FAULT_SCOPE";

    internal MaterializationHarnessBoundaryFaultPlan(
        string runIdentity,
        string provider,
        MaterializationExecutionBoundaryPoint point,
        int occurrence,
        string markerPath,
        string? scopeIdentity = null,
        string? operationIdentity = null)
    {
        RunIdentity = RequireValue(runIdentity, nameof(runIdentity));
        Provider = RequireValue(provider, nameof(provider));
        if (!Enum.IsDefined(point))
            throw new ArgumentOutOfRangeException(nameof(point), point, "The boundary point must be supported.");
        if (occurrence < 0)
            throw new ArgumentOutOfRangeException(nameof(occurrence), occurrence, "Occurrence cannot be negative.");
        ArgumentException.ThrowIfNullOrWhiteSpace(markerPath);
        if (!Path.IsPathFullyQualified(markerPath))
            throw new ArgumentException("The boundary marker path must be absolute.", nameof(markerPath));

        Point = point;
        Occurrence = occurrence;
        MarkerPath = Path.GetFullPath(markerPath);
        ScopeIdentity = NormalizeOptional(scopeIdentity);
        OperationIdentity = NormalizeOptional(operationIdentity);
    }

    internal string RunIdentity { get; }

    internal string Provider { get; }

    internal MaterializationExecutionBoundaryPoint Point { get; }

    internal int Occurrence { get; }

    internal string MarkerPath { get; }

    internal string? ScopeIdentity { get; }

    internal string? OperationIdentity { get; }

    internal bool Matches(
        string provider,
        MaterializationExecutionBoundaryObservation observation) =>
        string.Equals(Provider, provider, StringComparison.Ordinal)
        && Point == observation.Point
        && Occurrence == observation.Occurrence
        && (ScopeIdentity is null
            || string.Equals(ScopeIdentity, observation.ScopeIdentity, StringComparison.Ordinal))
        && (OperationIdentity is null
            || string.Equals(OperationIdentity, observation.OperationIdentity, StringComparison.Ordinal));

    internal static MaterializationHarnessBoundaryFaultPlan? FromEnvironment()
    {
        var boundary = Environment.GetEnvironmentVariable(BoundaryEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(boundary))
        {
            var configuredWithoutBoundary = new[]
            {
                ProviderEnvironmentVariable,
                ScopeEnvironmentVariable,
                OperationEnvironmentVariable,
                OccurrenceEnvironmentVariable,
                MarkerPathEnvironmentVariable,
                RunEnvironmentVariable
            }.FirstOrDefault(name => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name)));
            if (configuredWithoutBoundary is not null)
            {
                throw new InvalidOperationException(
                    $"Set {BoundaryEnvironmentVariable} when {configuredWithoutBoundary} is configured.");
            }
            return null;
        }

        if (!Enum.TryParse<MaterializationExecutionBoundaryPoint>(boundary, ignoreCase: true, out var point)
            || !Enum.IsDefined(point))
        {
            throw new InvalidOperationException(
                $"Set {BoundaryEnvironmentVariable} to a supported materialization execution boundary point.");
        }
        var occurrenceText = Environment.GetEnvironmentVariable(OccurrenceEnvironmentVariable);
        var occurrence = 0;
        if (!string.IsNullOrWhiteSpace(occurrenceText)
            && (!int.TryParse(occurrenceText, NumberStyles.None, CultureInfo.InvariantCulture, out occurrence)
                || occurrence < 0))
        {
            throw new InvalidOperationException($"Set {OccurrenceEnvironmentVariable} to a non-negative integer.");
        }

        return new(
            runIdentity: RequiredEnvironmentValue(RunEnvironmentVariable),
            provider: RequiredEnvironmentValue(ProviderEnvironmentVariable),
            point: point,
            occurrence: occurrence,
            markerPath: RequiredEnvironmentValue(MarkerPathEnvironmentVariable),
            scopeIdentity: Environment.GetEnvironmentVariable(ScopeEnvironmentVariable),
            operationIdentity: Environment.GetEnvironmentVariable(OperationEnvironmentVariable));
    }

    static string RequiredEnvironmentValue(string name) =>
        Environment.GetEnvironmentVariable(name) is { } value && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException($"Set {name} when a materialization boundary fault is configured.");

    static string RequireValue(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value;
    }

    static string? NormalizeOptional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}

sealed record MaterializationHarnessReachedBoundary(
    int SchemaVersion,
    string RunIdentity,
    string Provider,
    string ProcessInstanceId,
    string ProcessAttemptId,
    DateTimeOffset AttemptStartedAtUtc,
    string Generation,
    MaterializationExecutionBoundaryPoint Point,
    string ScopeIdentity,
    string OperationIdentity,
    int Occurrence,
    DateTimeOffset ObservedAtUtc,
    int HostProcessId);

sealed class MaterializationHarnessBoundaryObserver : IMaterializationExecutionBoundaryObserver
{
    static readonly JsonSerializerOptions MarkerJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    readonly string provider;
    readonly TimeSpan delay;
    readonly MaterializationHarnessBoundaryFaultPlan? faultPlan;
    int markerPublished;

    MaterializationHarnessBoundaryObserver(
        string provider,
        TimeSpan delay,
        MaterializationHarnessBoundaryFaultPlan? faultPlan)
    {
        this.provider = provider;
        this.delay = delay;
        this.faultPlan = faultPlan;
    }

    internal static IMaterializationExecutionBoundaryObserver Create(
        string provider,
        TimeSpan delay,
        MaterializationHarnessBoundaryFaultPlan? faultPlan)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        if (delay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(delay), delay, "A boundary delay cannot be negative.");
        return delay == TimeSpan.Zero && faultPlan is null
            ? NoOpMaterializationExecutionBoundaryObserver.Instance
            : new MaterializationHarnessBoundaryObserver(provider, delay, faultPlan);
    }

    public async ValueTask ObserveAsync(
        OperationContext context,
        MaterializationExecutionBoundaryObservation observation)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(observation);
        context.ThrowIfCancellationRequested();
        if (delay > TimeSpan.Zero)
            await Task.Delay(delay, context.CancellationToken).ConfigureAwait(false);
        if (faultPlan is null
            || !faultPlan.Matches(provider, observation)
            || Interlocked.CompareExchange(ref markerPublished, 1, 0) != 0)
        {
            return;
        }

        await PublishMarkerAsync(context, faultPlan, observation).ConfigureAwait(false);
        await Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken).ConfigureAwait(false);
    }

    static async Task PublishMarkerAsync(
        OperationContext context,
        MaterializationHarnessBoundaryFaultPlan faultPlan,
        MaterializationExecutionBoundaryObservation observation)
    {
        var directory = Path.GetDirectoryName(faultPlan.MarkerPath)
            ?? throw new InvalidOperationException("The materialization boundary marker has no parent directory.");
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException($"Create boundary marker directory '{directory}' before starting the host.");
        var temporaryPath = $"{faultPlan.MarkerPath}.{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}.tmp";
        MaterializationHarnessReachedBoundary marker = new(
            SchemaVersion: 1,
            RunIdentity: faultPlan.RunIdentity,
            Provider: faultPlan.Provider,
            ProcessInstanceId: observation.Attempt.Continuation.ProcessInstanceId.Value,
            ProcessAttemptId: observation.Attempt.Continuation.ProcessAttemptId.Value,
            AttemptStartedAtUtc: observation.Attempt.StartedAtUtc,
            Generation: observation.Generation.Value,
            Point: observation.Point,
            ScopeIdentity: observation.ScopeIdentity,
            OperationIdentity: observation.OperationIdentity,
            Occurrence: observation.Occurrence,
            ObservedAtUtc: context.UtcNow,
            HostProcessId: Environment.ProcessId);
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                options: FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                        utf8Json: stream,
                        value: marker,
                        options: MarkerJsonOptions,
                        cancellationToken: context.CancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(context.CancellationToken).ConfigureAwait(false);
            }
            File.Move(temporaryPath, faultPlan.MarkerPath, overwrite: false);
        }
        catch
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
            throw;
        }
    }
}
