namespace Cohesive.MaterializationHarness.Supervise;

sealed record ControlEquivalenceOptions(
    string Provider,
    string RepositoryRoot,
    string ArtifactDirectory,
    string RunIdentity,
    string SdkProcessInstancePrefix,
    string HttpProcessInstancePrefix,
    Uri HostUrl,
    TimeSpan Timeout)
{
    internal string HostAssemblyPath => Path.Combine(
        RepositoryRoot,
        "eng",
        "materialization-harness",
        "host",
        "bin",
        "Release",
        "net10.0",
        "Cohesive.MaterializationHarness.Host.dll");

    internal string HttpProcessInstanceId => $"{HttpProcessInstancePrefix}/{Provider}";

    internal static ControlEquivalenceOptions Parse(string[] args)
    {
        if (args is not ["control-equivalence", var provider, var artifactDirectory])
        {
            throw new ArgumentException(
                "Expected: control-equivalence <provider> <absolute-artifact-directory>.",
                nameof(args));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        if (!Path.IsPathFullyQualified(artifactDirectory))
            throw new ArgumentException("The artifact directory must be absolute.", nameof(args));
        var repositoryRoot = Path.GetFullPath(RequiredEnvironment("COHESIVE_MATERIALIZATION_REPOSITORY_ROOT"));
        var basePrefix = Environment.GetEnvironmentVariable("COHESIVE_MATERIALIZATION_PROCESS_INSTANCE_ID")
            ?? "process/materialization-harness/freight-rebuild";
        var runIdentity = $"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfffffff}-{Environment.ProcessId}";
        return new(
            Provider: provider,
            RepositoryRoot: repositoryRoot,
            ArtifactDirectory: Path.GetFullPath(artifactDirectory),
            RunIdentity: runIdentity,
            SdkProcessInstancePrefix: $"{basePrefix}/control-equivalence/{runIdentity}/sdk",
            HttpProcessInstancePrefix: $"{basePrefix}/control-equivalence/{runIdentity}/http",
            HostUrl: new(RequiredEnvironment("COHESIVE_MATERIALIZATION_HOST_URL"), UriKind.Absolute),
            Timeout: TimeSpan.FromMinutes(20));
    }

    static string RequiredEnvironment(string name) =>
        Environment.GetEnvironmentVariable(name) is { } value && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException($"Set {name} before running control equivalence.");
}
