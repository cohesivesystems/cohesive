using Cohesive.MaterializationHarness.Control;
using Cohesive.MaterializationHarness.Model;
using Cohesive.Storage.Materialization;

namespace Cohesive.MaterializationHarness.Supervise;

enum RecoveryMode
{
    Resume = 0,
    RestartAttempt = 1
}

sealed record SupervisorOptions(
    RecoveryMode Mode,
    string Provider,
    MaterializationExecutionBoundaryPoint Boundary,
    string RepositoryRoot,
    string ArtifactDirectory,
    string RunIdentity,
    string ProcessInstancePrefix,
    Uri HostUrl,
    Uri ElasticUrl,
    long ExpectedVisibleItemCount,
    TimeSpan Timeout)
{
    internal const int MaximumArtifactBytes = 256 * 1024;

    internal string HostAssemblyPath => Path.Combine(
        RepositoryRoot,
        "eng",
        "materialization-harness",
        "host",
        "bin",
        "Release",
        "net10.0",
        "Cohesive.MaterializationHarness.Host.dll");

    internal string SeedAssemblyPath => Path.Combine(
        RepositoryRoot,
        "eng",
        "materialization-harness",
        "seed",
        "bin",
        "Release",
        "net10.0",
        "Cohesive.MaterializationHarness.Seed.dll");

    internal string ProcessInstanceId => $"{ProcessInstancePrefix}/{Provider}";

    internal string MarkerPath => Path.Combine(ArtifactDirectory, "reached-boundary.json");

    internal string ElasticFaultMarkerPath => Path.Combine(ArtifactDirectory, "elastic-fault.json");

    internal static async Task<SupervisorOptions> ParseAsync(
        string[] args,
        CancellationToken cancellationToken = default)
    {
        if (args.Length != 4)
        {
            throw new ArgumentException(
                "Expected: <resume|restart-attempt> <provider> <boundary> <absolute-artifact-directory>.",
                nameof(args));
        }
        var mode = args[0] switch
        {
            "resume" => RecoveryMode.Resume,
            "restart-attempt" => RecoveryMode.RestartAttempt,
            _ => throw new ArgumentException("Recovery mode must be resume or restart-attempt.", nameof(args))
        };
        var provider = RequireValue(args[1], "provider");
        if (!Enum.TryParse<MaterializationExecutionBoundaryPoint>(args[2], ignoreCase: true, out var boundary)
            || !Enum.IsDefined(boundary))
        {
            throw new ArgumentException("Boundary must be a supported materialization execution point.", nameof(args));
        }
        return await CreateAsync(
            mode: mode,
            provider: provider,
            boundary: boundary,
            artifactDirectory: args[3],
            runPurpose: mode.ToString().ToLowerInvariant(),
            processPurpose: "supervised",
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<SupervisorOptions> ParseSourceMatrixAsync(
        string[] args,
        CancellationToken cancellationToken = default)
    {
        if (args is not ["source-matrix", _, _])
        {
            throw new ArgumentException(
                "Expected: source-matrix <provider> <absolute-artifact-directory>.",
                nameof(args));
        }
        var provider = RequireValue(args[1], "provider");
        return await CreateAsync(
            mode: RecoveryMode.Resume,
            provider: provider,
            boundary: MaterializationExecutionBoundaryPoint.AfterTargetBatch,
            artifactDirectory: args[2],
            runPurpose: "source-matrix",
            processPurpose: "source-matrix",
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<(SupervisorOptions Options, MaterializationHarnessElasticFaultKind Fault)>
        ParseElasticFailureAsync(
            string[] args,
            CancellationToken cancellationToken = default)
    {
        if (args is not ["elastic-failure", _, _, _])
        {
            throw new ArgumentException(
                "Expected: elastic-failure <provider> <retryable-bulk-rejection|permanent-bulk-item-failure|applied-promotion-response-loss> <absolute-artifact-directory>.",
                nameof(args));
        }
        var provider = RequireValue(args[1], "provider");
        var fault = args[2] switch
        {
            "retryable-bulk-rejection" => MaterializationHarnessElasticFaultKind.RetryableBulkRejection,
            "permanent-bulk-item-failure" => MaterializationHarnessElasticFaultKind.PermanentBulkItemFailure,
            "applied-promotion-response-loss" => MaterializationHarnessElasticFaultKind.AppliedPromotionResponseLoss,
            _ => throw new ArgumentException("Unsupported Elastic failure scenario.", nameof(args))
        };
        var options = await CreateAsync(
            mode: RecoveryMode.Resume,
            provider: provider,
            boundary: MaterializationExecutionBoundaryPoint.AfterTargetBatch,
            artifactDirectory: args[3],
            runPurpose: $"elastic-{args[2]}",
            processPurpose: "elastic-failure",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return (options, fault);
    }

    static async Task<SupervisorOptions> CreateAsync(
        RecoveryMode mode,
        string provider,
        MaterializationExecutionBoundaryPoint boundary,
        string artifactDirectory,
        string runPurpose,
        string processPurpose,
        CancellationToken cancellationToken)
    {
        if (!Path.IsPathFullyQualified(artifactDirectory))
            throw new ArgumentException("The artifact directory must be absolute.", nameof(artifactDirectory));
        var absoluteArtifactDirectory = Path.GetFullPath(artifactDirectory);
        var repositoryRoot = Path.GetFullPath(RequiredEnvironment("COHESIVE_MATERIALIZATION_REPOSITORY_ROOT"));
        var basePrefix = Environment.GetEnvironmentVariable("COHESIVE_MATERIALIZATION_PROCESS_INSTANCE_ID")
            ?? "process/materialization-harness/freight-rebuild";
        var runIdentity = $"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfffffff}-{Environment.ProcessId}-{runPurpose}";
        var timeoutSeconds = OptionalPositiveInt(
            name: "COHESIVE_MATERIALIZATION_SUPERVISOR_TIMEOUT_SECONDS",
            defaultValue: 900,
            maximumValue: 1_800);
        var journal = await FreightScenarioJournal.LoadAsync(
            path: RequiredEnvironment("COHESIVE_MATERIALIZATION_SCENARIO_PATH"),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return new(
            Mode: mode,
            Provider: provider,
            Boundary: boundary,
            RepositoryRoot: repositoryRoot,
            ArtifactDirectory: absoluteArtifactDirectory,
            RunIdentity: runIdentity,
            ProcessInstancePrefix: $"{basePrefix}/{processPurpose}/{runIdentity}",
            HostUrl: new Uri(RequiredEnvironment("COHESIVE_MATERIALIZATION_HOST_URL"), UriKind.Absolute),
            ElasticUrl: new Uri(RequiredEnvironment("COHESIVE_MATERIALIZATION_ELASTIC_ENDPOINT"), UriKind.Absolute),
            ExpectedVisibleItemCount: journal.Baseline.Orders.Length,
            Timeout: TimeSpan.FromSeconds(timeoutSeconds));
    }

    static string RequiredEnvironment(string name) =>
        Environment.GetEnvironmentVariable(name) is { } value && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException($"Set {name} before running the materialization supervisor.");

    static string RequireValue(string value, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        return value;
    }

    static int OptionalPositiveInt(string name, int defaultValue, int maximumValue)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
            return defaultValue;
        if (!int.TryParse(value, out var parsed) || parsed <= 0 || parsed > maximumValue)
            throw new InvalidOperationException($"Set {name} to an integer from one through {maximumValue}.");
        return parsed;
    }
}
