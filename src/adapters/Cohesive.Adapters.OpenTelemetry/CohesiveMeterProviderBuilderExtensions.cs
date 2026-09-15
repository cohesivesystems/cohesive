using OpenTelemetry.Metrics;

namespace Cohesive.Adapters.OpenTelemetry;

/// <summary>Registers Cohesive meters with the native OpenTelemetry metric pipeline.</summary>
public static class CohesiveMeterProviderBuilderExtensions
{
    /// <summary>Registers all core Cohesive meters.</summary>
    /// <param name="builder">The native OpenTelemetry meter provider builder.</param>
    /// <returns><paramref name="builder"/> for fluent composition.</returns>
    /// <remarks>
    /// This method registers only core scopes. Adapter-owned and provider-owned meters remain explicit opt-ins in
    /// the host so selecting core instrumentation never introduces another adapter dependency.
    /// </remarks>
    public static MeterProviderBuilder AddCohesiveCoreInstrumentation(this MeterProviderBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        foreach (CohesiveInstrumentationScope scope in CohesiveInstrumentationScopes.Core)
            builder.AddCohesiveInstrumentationScope(scope);

        return builder;
    }

    /// <summary>Registers the core execution meter.</summary>
    /// <param name="builder">The native OpenTelemetry meter provider builder.</param>
    /// <returns><paramref name="builder"/> for fluent composition.</returns>
    public static MeterProviderBuilder AddCohesiveExecutionInstrumentation(this MeterProviderBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddCohesiveInstrumentationScope(CohesiveInstrumentationScopes.Execution);
    }

    /// <summary>Registers the canonical Relations meter.</summary>
    /// <param name="builder">The native OpenTelemetry meter provider builder.</param>
    /// <returns><paramref name="builder"/> for fluent composition.</returns>
    public static MeterProviderBuilder AddCohesiveRelationsInstrumentation(this MeterProviderBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddCohesiveInstrumentationScope(CohesiveInstrumentationScopes.Relations);
    }

    /// <summary>Registers the portable Process distribution meter.</summary>
    /// <param name="builder">The native OpenTelemetry meter provider builder.</param>
    /// <returns><paramref name="builder"/> for fluent composition.</returns>
    public static MeterProviderBuilder AddCohesiveProcessDistributionInstrumentation(
        this MeterProviderBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddCohesiveInstrumentationScope(CohesiveInstrumentationScopes.ProcessDistribution);
    }

    static MeterProviderBuilder AddCohesiveInstrumentationScope(
        this MeterProviderBuilder builder,
        CohesiveInstrumentationScope scope) =>
        builder.AddMeter(scope.MeterName);
}
