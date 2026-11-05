using Cohesive.Configuration;
using LaunchDarkly.Sdk;
using LaunchDarkly.Sdk.Server.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Cohesive.Adapters.LaunchDarkly;

/// <summary>Attaches an evaluator to the host's existing native client.</summary>
public static class LaunchDarklyServiceCollectionExtensions
{
    /// <summary>Registers contextual evaluation without creating or owning a native client.</summary>
    /// <typeparam name="TContext">Explicit targeting contract.</typeparam>
    /// <param name="services">Host registrations containing ILdClient.</param>
    /// <param name="targeting">Projects the contract into native targeting attributes.</param>
    /// <returns>The same collection; existing evaluator registrations are retained.</returns>
    public static IServiceCollection AddCohesiveLaunchDarkly<TContext>(this IServiceCollection services, Func<TContext, Context> targeting)
    {
        ArgumentNullException.ThrowIfNull(services); ArgumentNullException.ThrowIfNull(targeting);
        services.TryAddSingleton<IFeatureFlagEvaluator<TContext>>(provider =>
            new LaunchDarklyFeatureFlagEvaluator<TContext>(provider.GetRequiredService<ILdClient>(), targeting));
        return services;
    }
}
