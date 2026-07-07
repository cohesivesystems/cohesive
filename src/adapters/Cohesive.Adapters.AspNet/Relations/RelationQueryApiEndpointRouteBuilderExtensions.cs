using Cohesive.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace Cohesive.Adapters.AspNet.Relations;

/// <summary>
/// ASP.NET endpoint helpers for binding declared API operations to Cohesive relation queries.
/// </summary>
public static class RelationQueryApiEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps the operations in <paramref name="definition"/> that have corresponding relation-query bindings.
    /// </summary>
    public static IReadOnlyList<RouteHandlerBuilder> MapRelationQueryApiDefinition(
        this IEndpointRouteBuilder endpoints,
        ApiDefinition definition,
        RelationQueryApiEndpointOptions options,
        Action<RouteHandlerBuilder, ApiOperation>? configure = null
        )
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(options);

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
