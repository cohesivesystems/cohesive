using Cohesive.Execution;
using Cohesive.Processes.Distribution;
using Cohesive.Relations.Observability;

namespace OpenTelemetry.Trace;

/// <summary>Registers Cohesive activity sources with the native OpenTelemetry trace pipeline.</summary>
public static class CohesiveTracerProviderBuilderExtensions
{
    /// <summary>Registers all core Cohesive activity sources.</summary>
    /// <param name="builder">The native OpenTelemetry trace provider builder.</param>
    /// <returns><paramref name="builder"/> for fluent composition.</returns>
    /// <remarks>
    /// This method registers only core scopes. Adapter-owned and provider-owned sources remain explicit opt-ins in
    /// the host so selecting core instrumentation never introduces an adapter dependency.
    /// </remarks>
    public static TracerProviderBuilder AddCohesiveInstrumentation(this TracerProviderBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder
            .AddCohesiveExecutionInstrumentation()
            .AddCohesiveRelationsInstrumentation()
            .AddCohesiveProcessDistributionInstrumentation();
    }

    /// <summary>Registers the core execution activity source.</summary>
    /// <param name="builder">The native OpenTelemetry trace provider builder.</param>
    /// <returns><paramref name="builder"/> for fluent composition.</returns>
    public static TracerProviderBuilder AddCohesiveExecutionInstrumentation(this TracerProviderBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddSource(ExecutionTelemetry.ActivitySourceName);
    }

    /// <summary>Registers the canonical Relations activity source.</summary>
    /// <param name="builder">The native OpenTelemetry trace provider builder.</param>
    /// <returns><paramref name="builder"/> for fluent composition.</returns>
    public static TracerProviderBuilder AddCohesiveRelationsInstrumentation(this TracerProviderBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddSource(RelationQueryTelemetry.ActivitySourceName);
    }

    /// <summary>Registers the portable Process distribution activity source.</summary>
    /// <param name="builder">The native OpenTelemetry trace provider builder.</param>
    /// <returns><paramref name="builder"/> for fluent composition.</returns>
    public static TracerProviderBuilder AddCohesiveProcessDistributionInstrumentation(
        this TracerProviderBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddSource(ProcessDistributionTelemetry.ActivitySourceName);
    }
}
