using Cohesive.Execution;
using Cohesive.Processes.Distribution;
using Cohesive.Relations.Observability;

namespace OpenTelemetry.Metrics;

/// <summary>Registers Cohesive meters with the native OpenTelemetry metric pipeline.</summary>
public static class CohesiveMeterProviderBuilderExtensions
{
    /// <summary>Registers all core Cohesive meters.</summary>
    /// <param name="builder">The native OpenTelemetry meter provider builder.</param>
    /// <returns><paramref name="builder"/> for fluent composition.</returns>
    /// <remarks>
    /// This method registers only core scopes. Adapter-owned and provider-owned meters remain explicit opt-ins in
    /// the host so selecting core instrumentation never introduces an adapter dependency.
    /// </remarks>
    public static MeterProviderBuilder AddCohesiveInstrumentation(this MeterProviderBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder
            .AddCohesiveExecutionInstrumentation()
            .AddCohesiveRelationsInstrumentation()
            .AddCohesiveProcessDistributionInstrumentation();
    }

    /// <summary>Registers the core execution meter.</summary>
    /// <param name="builder">The native OpenTelemetry meter provider builder.</param>
    /// <returns><paramref name="builder"/> for fluent composition.</returns>
    public static MeterProviderBuilder AddCohesiveExecutionInstrumentation(this MeterProviderBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddMeter(ExecutionTelemetry.MeterName);
    }

    /// <summary>Registers the canonical Relations meter.</summary>
    /// <param name="builder">The native OpenTelemetry meter provider builder.</param>
    /// <returns><paramref name="builder"/> for fluent composition.</returns>
    public static MeterProviderBuilder AddCohesiveRelationsInstrumentation(this MeterProviderBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddMeter(RelationQueryTelemetry.MeterName);
    }

    /// <summary>Registers the portable Process distribution meter.</summary>
    /// <param name="builder">The native OpenTelemetry meter provider builder.</param>
    /// <returns><paramref name="builder"/> for fluent composition.</returns>
    public static MeterProviderBuilder AddCohesiveProcessDistributionInstrumentation(
        this MeterProviderBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddMeter(ProcessDistributionTelemetry.MeterName);
    }
}
