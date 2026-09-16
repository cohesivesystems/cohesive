using Cohesive.Execution;
using Cohesive.Processes.Distribution;
using Cohesive.Relations.Observability;

namespace Cohesive.Adapters.OpenTelemetry;

internal readonly record struct CohesiveInstrumentationScope(
    string ActivitySourceName,
    string MeterName);

internal static class CohesiveInstrumentationScopes
{
    public static CohesiveInstrumentationScope Execution { get; } = new(
        ExecutionTelemetry.ActivitySourceName,
        ExecutionTelemetry.MeterName);

    public static CohesiveInstrumentationScope Relations { get; } = new(
        RelationQueryTelemetry.ActivitySourceName,
        RelationQueryTelemetry.MeterName);

    public static CohesiveInstrumentationScope ProcessDistribution { get; } = new(
        ProcessDistributionTelemetry.ActivitySourceName,
        ProcessDistributionTelemetry.MeterName);

    public static IReadOnlyList<CohesiveInstrumentationScope> Core { get; } = Array.AsReadOnly(
        new[]
        {
            Execution,
            Relations,
            ProcessDistribution
        });
}
