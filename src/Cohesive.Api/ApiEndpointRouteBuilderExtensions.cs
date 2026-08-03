using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Cohesive.Api;

/// <summary>
/// Resolves one transport-neutral API authorization requirement to an ASP.NET authorization policy name.
/// </summary>
/// <param name="operation">Operation that declares the semantic authorization requirement.</param>
/// <param name="requirement">Exact semantic requirement being projected.</param>
/// <returns>
/// A non-empty ASP.NET authorization policy name. Returning <see langword="null"/>, an empty string, or white
/// space causes endpoint mapping to fail closed.
/// </returns>
public delegate string? AspNetAuthorizationPolicyResolver(
    ApiOperation operation,
    ApiAuthorizationRequirement requirement);

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
        /// <param name="definition">API definition whose HTTP-projected endpoints are mapped.</param>
        /// <param name="handlerFactory">Factory that supplies one handler for each operation.</param>
        /// <param name="configure">Optional callback that configures each mapped endpoint.</param>
        /// <param name="authorizationPolicyResolver">
        /// Optional interpreter from semantic requirements to ASP.NET policy names. It is required when any mapped
        /// operation declares an authorization requirement.
        /// </param>
        /// <returns>Mapped route builders in definition order.</returns>
        /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
        /// <exception cref="InvalidOperationException">
        /// The definition contains an operation without an HTTP projection, a secured operation has no
        /// <paramref name="authorizationPolicyResolver"/>, or the resolver returns an invalid policy name.
        /// </exception>
        public IReadOnlyList<RouteHandlerBuilder> MapApiDefinition(
            ApiDefinition definition,
            Func<ApiOperation, Delegate> handlerFactory,
            Action<RouteHandlerBuilder, ApiOperation>? configure = null,
            AspNetAuthorizationPolicyResolver? authorizationPolicyResolver = null)
        {
            ArgumentNullException.ThrowIfNull(endpoints);
            ArgumentNullException.ThrowIfNull(definition);
            ArgumentNullException.ThrowIfNull(handlerFactory);

            var authorizationPolicies = new IReadOnlyList<string>[definition.Endpoints.Count];
            for (var i = 0; i < definition.Endpoints.Count; i++)
            {
                var endpoint = definition.Endpoints[i];
                if (endpoint.Operation.Http is null)
                {
                    throw new InvalidOperationException(
                        $"API endpoint '{endpoint.Id}' does not declare an HTTP projection and cannot be mapped to ASP.NET.");
                }

                authorizationPolicies[i] = ResolveAuthorizationPolicies(
                    endpoint.Operation,
                    authorizationPolicyResolver);
            }

            var builders = new RouteHandlerBuilder[definition.Endpoints.Count];
            for (var i = 0; i < definition.Endpoints.Count; i++)
            {
                var endpoint = definition.Endpoints[i];
                builders[i] = MapApiEndpointCore(
                    endpoints,
                    endpoint,
                    handlerFactory(endpoint.Operation),
                    endpointNameSelector: null,
                    configure,
                    authorizationPolicies[i]);
            }
            return builders;
        }

        /// <summary>
        /// Maps a single endpoint using a supplied Minimal API delegate.
        /// </summary>
        /// <param name="endpoint">Endpoint whose HTTP projection is mapped.</param>
        /// <param name="handler">Minimal API handler.</param>
        /// <param name="configure">Optional callback that configures the mapped endpoint.</param>
        /// <param name="authorizationPolicyResolver">
        /// Optional interpreter from semantic requirements to ASP.NET policy names. It is required when the
        /// operation declares an authorization requirement.
        /// </param>
        /// <returns>The mapped route builder.</returns>
        /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
        /// <exception cref="InvalidOperationException">
        /// <paramref name="endpoint"/> does not declare an HTTP projection, has authorization requirements without
        /// an <paramref name="authorizationPolicyResolver"/>, or the resolver returns an invalid policy name.
        /// </exception>
        public RouteHandlerBuilder MapApiEndpoint(
            ApiEndpoint endpoint,
            Delegate handler,
            Action<RouteHandlerBuilder, ApiOperation>? configure = null,
            AspNetAuthorizationPolicyResolver? authorizationPolicyResolver = null) =>
            endpoints.MapApiEndpoint(
                endpoint,
                handler,
                endpointNameSelector: null,
                configure,
                authorizationPolicyResolver);

        /// <summary>
        /// Maps a single operation using a supplied Minimal API delegate and optional ASP.NET endpoint name selector.
        /// </summary>
        /// <param name="endpoint">Endpoint whose HTTP projection is mapped.</param>
        /// <param name="handler">Minimal API handler.</param>
        /// <param name="endpointNameSelector">Optional selector for the ASP.NET endpoint name.</param>
        /// <param name="configure">Optional callback that configures the mapped endpoint.</param>
        /// <param name="authorizationPolicyResolver">
        /// Optional interpreter from semantic requirements to ASP.NET policy names. It is required when the
        /// operation declares an authorization requirement.
        /// </param>
        /// <returns>The mapped route builder.</returns>
        /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
        /// <exception cref="InvalidOperationException">
        /// <paramref name="endpoint"/> does not declare an HTTP projection, has authorization requirements without
        /// an <paramref name="authorizationPolicyResolver"/>, or the resolver returns an invalid policy name.
        /// </exception>
        public RouteHandlerBuilder MapApiEndpoint(
            ApiEndpoint endpoint,
            Delegate handler,
            Func<ApiOperation, string>? endpointNameSelector,
            Action<RouteHandlerBuilder, ApiOperation>? configure = null,
            AspNetAuthorizationPolicyResolver? authorizationPolicyResolver = null)
        {
            ArgumentNullException.ThrowIfNull(endpoints);
            ArgumentNullException.ThrowIfNull(endpoint);
            ArgumentNullException.ThrowIfNull(handler);
            var policies = ResolveAuthorizationPolicies(endpoint.Operation, authorizationPolicyResolver);
            return MapApiEndpointCore(
                endpoints,
                endpoint,
                handler,
                endpointNameSelector,
                configure,
                policies);
        }
    }

    static RouteHandlerBuilder MapApiEndpointCore(
        IEndpointRouteBuilder endpoints,
        ApiEndpoint endpoint,
        Delegate handler,
        Func<ApiOperation, string>? endpointNameSelector,
        Action<RouteHandlerBuilder, ApiOperation>? configure,
        IReadOnlyList<string> authorizationPolicies)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var operation = endpoint.Operation;
        var http = operation.Http ?? throw new InvalidOperationException(
            $"API endpoint '{endpoint.Id}' does not declare an HTTP projection and cannot be mapped to ASP.NET.");
        var builder = endpoints.MapMethods(http.Route, [http.Method], handler);
        var endpointName = endpointNameSelector?.Invoke(operation) ?? operation.Name;
        ApplyMetadata(builder, operation, http, endpointName: endpointName);
        configure?.Invoke(builder, operation);

        for (var i = 0; i < authorizationPolicies.Count; i++)
            builder.RequireAuthorization(authorizationPolicies[i]);

        return builder;
    }

    static IReadOnlyList<string> ResolveAuthorizationPolicies(
        ApiOperation operation,
        AspNetAuthorizationPolicyResolver? resolver)
    {
        var requirements = operation.AuthorizationRequirements;
        if (requirements.Count == 0)
            return [];
        if (resolver is null)
        {
            throw new InvalidOperationException(
                $"API operation '{operation.Id}' declares authorization requirements but no ASP.NET " +
                "authorization policy resolver was supplied; endpoint mapping was rejected.");
        }

        var policies = new List<string>(requirements.Count);
        HashSet<string> seen = new(StringComparer.Ordinal);
        for (var i = 0; i < requirements.Count; i++)
        {
            var requirement = requirements[i];
            var policy = resolver(operation, requirement);
            if (string.IsNullOrWhiteSpace(policy))
            {
                throw new InvalidOperationException(
                    $"ASP.NET authorization policy resolver returned no policy for requirement " +
                    $"'{requirement.Id}' on API operation '{operation.Id}'; endpoint mapping was rejected.");
            }

            policy = policy.Trim();
            if (seen.Add(policy))
                policies.Add(policy);
        }

        return policies;
    }

    static void ApplyMetadata(
        RouteHandlerBuilder builder,
        ApiOperation operation,
        HttpBinding http,
        string endpointName)
    {
        builder.WithName(Guard.RequireNotNullOrWhiteSpace(endpointName));
        builder.WithTags([..operation.Tags]);
        builder.WithSummary(operation.Summary ?? BuildDefaultSummary(operation, http));

        if (!string.IsNullOrWhiteSpace(operation.Description))
            builder.WithDescription(operation.Description);

        if (http.Body is not null)
            builder.Accepts(http.Body.BodyType, "application/json");

        if (http.Query is not null)
            builder.WithMetadata(http.Query);

        for (var i = 0; i < operation.Results.Count; i++)
        {
            var result = operation.Results[i];
            if (result.Http is not { } resultHttp)
                continue;

            if (result.BodyType == typeof(void))
                builder.Produces(resultHttp.StatusCode);
            else if (string.IsNullOrWhiteSpace(resultHttp.ContentType))
                builder.Produces(resultHttp.StatusCode, result.BodyType);
            else
                builder.Produces(resultHttp.StatusCode, result.BodyType, resultHttp.ContentType);
        }

        builder.WithMetadata(operation);
        builder.WithMetadata(http);

        for (var i = 0; i < operation.ScopePolicies.Count; i++)
            builder.WithMetadata(operation.ScopePolicies[i]);

        for (var i = 0; i < operation.AuthorizationRequirements.Count; i++)
            builder.WithMetadata(operation.AuthorizationRequirements[i]);

        for (var i = 0; i < operation.SemanticReferences.Count; i++)
            builder.WithMetadata(operation.SemanticReferences[i]);

        if (operation.TransitionReference is { } transitionReference)
            builder.WithMetadata(transitionReference);
    }
    
    static string BuildDefaultSummary(ApiOperation operation, HttpBinding http)
    {
        if (operation.Entity is { } entity)
        {
            return operation.Kind switch
            {
                ApiOperationKind.Query => $"Query {entity.Value} via {http.Method} {http.Route}",
                ApiOperationKind.Command when operation.TransitionReference is not null =>
                    $"Execute {operation.TransitionReference.DefinitionId.Value} for {entity.Value}",
                ApiOperationKind.Command => $"Command {entity.Value} via {http.Method} {http.Route}",
                _ => $"Operate on {entity.Value} via {http.Method} {http.Route}"
            };
        }

        return $"{operation.Kind} {operation.Name} via {http.Method} {http.Route}";
    }
}
