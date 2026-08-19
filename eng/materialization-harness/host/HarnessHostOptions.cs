using System.Globalization;
using Cohesive.Execution;

namespace Cohesive.MaterializationHarness.Host;

sealed record HarnessHostOptions(
    string PostgresConnectionString,
    string Url,
    ProcessInstanceId ProcessInstanceId,
    InteractionAuthorityScope AuthorityScope,
    TimeSpan PageDelay)
{
    internal static HarnessHostOptions FromEnvironment() => new(
        Required("COHESIVE_MATERIALIZATION_POSTGRES_CONNECTION_STRING"),
        Environment.GetEnvironmentVariable("COHESIVE_MATERIALIZATION_HOST_URL")
            ?? "http://localhost:59399",
        new(
            Environment.GetEnvironmentVariable("COHESIVE_MATERIALIZATION_PROCESS_INSTANCE_ID")
                ?? "process/materialization-harness/freight-rebuild"),
        new(
            authority: "authority/materialization-harness",
            tenant: "tenant/materialization-harness"),
        TimeSpan.FromMilliseconds(OptionalNonNegativeInt(
            "COHESIVE_MATERIALIZATION_PAGE_DELAY_MS",
            defaultValue: 0,
            maximumValue: 60_000)));

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
