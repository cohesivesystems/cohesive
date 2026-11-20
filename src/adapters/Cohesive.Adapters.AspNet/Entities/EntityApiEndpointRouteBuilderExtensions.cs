using Cohesive.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace Cohesive.Adapters.AspNet.Entities;

/// <summary>
/// ASP.NET endpoint helpers for binding declared API operations to entity repositories.
/// </summary>
public static class EntityApiEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps the operations in <paramref name="definition"/> that have corresponding entity operation bindings.
    /// </summary>
    /// <param name="endpoints">ASP.NET endpoint builder receiving the mapped operations.</param>
    /// <param name="definition">API definition including every transition bound in <paramref name="options"/>.</param>
    /// <param name="options">Entity, repository, and operation bindings for this definition.</param>
    /// <param name="configure">Optional ASP.NET configuration for each mapped operation.</param>
    /// <returns>The mapped route handler builders in definition order.</returns>
    /// <exception cref="ArgumentNullException">A required argument or the entity definition is null.</exception>
    /// <exception cref="InvalidOperationException">A bound transition is missing from the definition or has a conflicting runtime contract.</exception>
    public static IReadOnlyList<RouteHandlerBuilder> MapEntityApiDefinition(
        this IEndpointRouteBuilder endpoints,
        ApiDefinition definition,
        EntityApiEndpointOptions options,
        Action<RouteHandlerBuilder, ApiOperation>? configure = null
        )
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Entity);
        options.ValidateTransitionBindings(definition);

        var builders = new List<RouteHandlerBuilder>();
        for (var i = 0; i < definition.Endpoints.Count; i++)
        {
            var endpoint = definition.Endpoints[i];
            if (options.OperationFilter is not null && !options.OperationFilter(endpoint.Operation))
                continue;

            if (!options.TryGetBinding(endpoint.Operation, out var binding))
                continue;

            var handler = binding.CreateHandler(endpoint.Operation, options);
            builders.Add(endpoints.MapApiEndpoint(endpoint, handler, options.EndpointNameSelector, configure));
        }

        return builders;
    }
}
