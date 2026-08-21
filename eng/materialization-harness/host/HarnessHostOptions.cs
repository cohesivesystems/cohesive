using System.Globalization;
using Cohesive.Execution;

namespace Cohesive.MaterializationHarness.Host;

sealed record HarnessHostOptions(
    string PostgresConnectionString,
    string Url,
    string ProcessInstancePrefix,
    InteractionAuthorityScope AuthorityScope,
    TimeSpan OperationBoundaryDelay,
    MaterializationHarnessBoundaryFaultPlan? BoundaryFaultPlan)
{
    internal static HarnessHostOptions FromEnvironment() => new(
        PostgresConnectionString: Required("COHESIVE_MATERIALIZATION_POSTGRES_CONNECTION_STRING"),
        Url: Environment.GetEnvironmentVariable("COHESIVE_MATERIALIZATION_HOST_URL")
            ?? "http://localhost:59399",
        ProcessInstancePrefix: Environment.GetEnvironmentVariable("COHESIVE_MATERIALIZATION_PROCESS_INSTANCE_ID")
            ?? "process/materialization-harness/freight-rebuild",
        AuthorityScope: new(
            authority: "authority/materialization-harness",
            tenant: "tenant/materialization-harness"),
        OperationBoundaryDelay: TimeSpan.FromMilliseconds(OptionalNonNegativeInt(
            name: "COHESIVE_MATERIALIZATION_PAGE_DELAY_MS",
            defaultValue: 0,
            maximumValue: 60_000)),
        BoundaryFaultPlan: MaterializationHarnessBoundaryFaultPlan.FromEnvironment());

    internal ProcessInstanceId ProcessInstance(string provider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        return new($"{ProcessInstancePrefix}/{provider}");
    }

    static string Required(string name) =>
        Environment.GetEnvironmentVariable(name) is { } value && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException($"Set {name} before starting the materialization host.");

    static int OptionalNonNegativeInt(string name, int defaultValue, int maximumValue)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
            return defaultValue;
        if (!int.TryParse(
                value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var parsed)
            || parsed < 0
            || parsed > maximumValue)
        {
            throw new InvalidOperationException(
                $"Set {name} to an integer from zero through {maximumValue}.");
        }
        return parsed;
    }
}
