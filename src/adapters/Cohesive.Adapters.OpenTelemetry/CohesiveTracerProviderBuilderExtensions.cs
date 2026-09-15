using OpenTelemetry.Trace;

namespace Cohesive.Adapters.OpenTelemetry;

/// <summary>Registers Cohesive activity sources with the native OpenTelemetry trace pipeline.</summary>
public static class CohesiveTracerProviderBuilderExtensions
{
    /// <summary>Registers all core Cohesive activity sources.</summary>
    /// <param name="builder">The native OpenTelemetry trace provider builder.</param>
    /// <returns><paramref name="builder"/> for fluent composition.</returns>
    /// <remarks>
    /// This method registers only core scopes. Adapter-owned and provider-owned sources remain explicit opt-ins in
    /// the host so selecting core instrumentation never introduces another adapter dependency.
    /// </remarks>
    public static TracerProviderBuilder AddCohesiveCoreInstrumentation(this TracerProviderBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        foreach (CohesiveInstrumentationScope scope in CohesiveInstrumentationScopes.Core)
            builder.AddCohesiveInstrumentationScope(scope);

        return builder;
    }

    /// <summary>Registers the core execution activity source.</summary>
    /// <param name="builder">The native OpenTelemetry trace provider builder.</param>
    /// <returns><paramref name="builder"/> for fluent composition.</returns>
    public static TracerProviderBuilder AddCohesiveExecutionInstrumentation(this TracerProviderBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddCohesiveInstrumentationScope(CohesiveInstrumentationScopes.Execution);
    }

    /// <summary>Registers the canonical Relations activity source.</summary>
    /// <param name="builder">The native OpenTelemetry trace provider builder.</param>
    /// <returns><paramref name="builder"/> for fluent composition.</returns>
    public static TracerProviderBuilder AddCohesiveRelationsInstrumentation(this TracerProviderBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddCohesiveInstrumentationScope(CohesiveInstrumentationScopes.Relations);
    }

    /// <summary>Registers the portable Process distribution activity source.</summary>
    /// <param name="builder">The native OpenTelemetry trace provider builder.</param>
    /// <returns><paramref name="builder"/> for fluent composition.</returns>
    public static TracerProviderBuilder AddCohesiveProcessDistributionInstrumentation(
        this TracerProviderBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddCohesiveInstrumentationScope(CohesiveInstrumentationScopes.ProcessDistribution);
    }

    static TracerProviderBuilder AddCohesiveInstrumentationScope(
        this TracerProviderBuilder builder,
        CohesiveInstrumentationScope scope) =>
        builder.AddSource(scope.ActivitySourceName);
}
