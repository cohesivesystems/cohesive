using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Cohesive.Api;

/// <summary>
/// Minimal API binding helpers for <see cref="ApiDefinition"/>.
/// </summary>
public static class ApiEndpointRouteBuilderExtensions
{
    extension(IEndpointRouteBuilder endpoints)
    {
        /// <summary>
        /// Maps every operation in a definition using the provided handler factory.
        /// </summary>
        public IReadOnlyList<RouteHandlerBuilder> MapApiDefinition(
            ApiDefinition definition,
            Func<ApiOperation, Delegate> handlerFactory,
            Action<RouteHandlerBuilder, ApiOperation>? configure = null
            )
        {
            ArgumentNullException.ThrowIfNull(endpoints);
            ArgumentNullException.ThrowIfNull(definition);
            ArgumentNullException.ThrowIfNull(handlerFactory);
            var builders = new RouteHandlerBuilder[definition.Endpoints.Count];
            for (var i = 0; i < definition.Endpoints.Count; i++)
            {
                var endpoint = definition.Endpoints[i];
                builders[i] = endpoints.MapApiEndpoint(endpoint, handlerFactory(endpoint.Operation), configure);
            }
            return builders;
        }

        /// <summary>
        /// Maps a single endpoint using a supplied Minimal API delegate.
        /// </summary>
        public RouteHandlerBuilder MapApiEndpoint(ApiEndpoint endpoint, Delegate handler, Action<RouteHandlerBuilder, ApiOperation>? configure = null) => 
            endpoints.MapApiEndpoint(endpoint, handler, endpointNameSelector: null, configure);

        /// <summary>
        /// Maps a single operation using a supplied Minimal API delegate and optional ASP.NET endpoint name selector.
        /// </summary>
        public RouteHandlerBuilder MapApiEndpoint(
            ApiEndpoint endpoint,
            Delegate handler,
            Func<ApiOperation, string>? endpointNameSelector,
            Action<RouteHandlerBuilder, ApiOperation>? configure = null
            )
        {
            ArgumentNullException.ThrowIfNull(endpoints);
            ArgumentNullException.ThrowIfNull(endpoint);
            ArgumentNullException.ThrowIfNull(handler);
            var operation = endpoint.Operation;
            var builder = endpoints.MapMethods(operation.Http.Route, [operation.Http.Method], handler);
            var endpointName = endpointNameSelector?.Invoke(operation) ?? operation.Name;
            ApplyMetadata(builder, operation, endpointName: endpointName);
            configure?.Invoke(builder, operation);
            return builder;
        }
    }

    static void ApplyMetadata(RouteHandlerBuilder builder, ApiOperation operation, string endpointName)
    {
        builder.WithName(Guard.RequireNotNullOrWhiteSpace(endpointName));
        builder.WithTags([..operation.Tags]);
        builder.WithSummary(operation.Summary ?? BuildDefaultSummary(operation));

        if (!string.IsNullOrWhiteSpace(operation.Description))
            builder.WithDescription(operation.Description);

        if (operation.Http.Body is not null)
            builder.Accepts(operation.Http.Body.BodyType, "application/json");

        if (operation.Http.Query is not null)
            builder.WithMetadata(operation.Http.Query);

        for (var i = 0; i < operation.Results.Count; i++)
        {
            var result = operation.Results[i];
            if (result.Http is not { } http)
                continue;

            if (result.BodyType == typeof(void))
                builder.Produces(http.StatusCode);
            else if (string.IsNullOrWhiteSpace(http.ContentType))
                builder.Produces(http.StatusCode, result.BodyType);
            else
                builder.Produces(http.StatusCode, result.BodyType, http.ContentType);
        }

        builder.WithMetadata(operation);
        builder.WithMetadata(operation.Http);

        for (var i = 0; i < operation.ScopePolicies.Count; i++)
            builder.WithMetadata(operation.ScopePolicies[i]);

        if (operation.Transition is { } transition)
            builder.WithMetadata(transition);
    }
    
    static string BuildDefaultSummary(ApiOperation operation)
    {
        if (operation.Entity is { } entity)
        {
            return operation.Kind switch
            {
                ApiOperationKind.Query => $"Query {entity.Value} via {operation.Http.Method} {operation.Http.Route}",
                ApiOperationKind.Command when operation.Transition is not null => $"Execute {operation.Transition.Name} for {entity.Value}",
                ApiOperationKind.Command => $"Command {entity.Value} via {operation.Http.Method} {operation.Http.Route}",
                _ => $"Operate on {entity.Value} via {operation.Http.Method} {operation.Http.Route}"
            };
        }

        return $"{operation.Kind} {operation.Name} via {operation.Http.Method} {operation.Http.Route}";
    }
}
