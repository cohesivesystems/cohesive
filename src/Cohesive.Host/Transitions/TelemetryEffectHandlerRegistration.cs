using Cohesive.Transitions.Authoring;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Cohesive.Host.Transitions;

/// <summary>
/// Service registration helpers for telemetry-wrapped effect handlers.
/// </summary>
public static class TelemetryEffectHandlerRegistration
{
    /// <summary>
    /// Adds a telemetry-wrapped effect handler to the service collection.
    /// Registers a singleton instance of the handler <typeparamref name="THandler"/> unless it is already registered.
    /// Also registers a singleton instance of <see cref="IEffectHandler{TRequest, TResult}"/> wrapping the handler.
    /// </summary>
    /// <param name="services"></param>
    /// <param name="configure">Configuration telemetry parameters for this handler.</param>
    /// <typeparam name="THandler"></typeparam>
    /// <typeparam name="TRequest"></typeparam>
    /// <typeparam name="TResult"></typeparam>
    /// <returns></returns>
    /// <exception cref="ArgumentNullException"></exception>
    public static IServiceCollection AddTelemetryEffectHandler<THandler, TRequest, TResult>(this IServiceCollection services, Action<TelemetryEffectHandlerWrapperOptions<TRequest, TResult>> configure)
        where THandler : class, IEffectHandler<TRequest, TResult>
        where TRequest : class, IEffectRequest<TResult>
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        
        services.TryAddSingleton<THandler>();
        services.AddSingleton<IEffectHandler<TRequest, TResult>>(sp =>
        {
            var options = new TelemetryEffectHandlerWrapperOptions<TRequest, TResult>();
            configure(options);
            options.Logger ??= sp.GetService<ILogger<THandler>>();
            return new TelemetryEffectHandlerWrapper<TRequest, TResult>(sp.GetRequiredService<THandler>(), options);
        });

        return services;
    }
}
