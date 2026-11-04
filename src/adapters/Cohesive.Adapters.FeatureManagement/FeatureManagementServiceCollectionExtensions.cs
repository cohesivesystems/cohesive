using Cohesive.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.FeatureManagement;

namespace Cohesive.Adapters.FeatureManagement;

/// <summary>Registers the native feature manager and its Cohesive evaluator without choosing a read backend.</summary>
public static class FeatureManagementServiceCollectionExtensions
{
    /// <summary>Registers one concurrent evaluator for an explicit targeting type.</summary>
    /// <typeparam name="TContext">Targeting contract independent of query input.</typeparam>
    /// <param name="services">Host-owned registrations. Native filters and configuration remain configurable.</param>
    /// <returns>The same collection; existing evaluator registrations are retained.</returns>
    public static IServiceCollection AddCohesiveFeatureManagement<TContext>(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddFeatureManagement();
        services.TryAddSingleton<IFeatureFlagEvaluator<TContext>, FeatureManagementEvaluator<TContext>>();
        return services;
    }
}
