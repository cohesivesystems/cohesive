using System.Globalization;
using Cohesive.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Cohesive.Adapters.AspNet;

/// <summary>Fluent typed binding of a prepared query to a separately declared bodyless API endpoint.</summary>
public static class ApiQueryBindingExtensions
{
    /// <summary>Starts binding an existing typed query operation; does not execute or compile it.</summary>
    /// <typeparam name="TInput">Prepared query input.</typeparam>
    /// <typeparam name="TResult">Declared API response type.</typeparam>
    /// <param name="endpoints">Native endpoint builder.</param>
    /// <param name="endpoint">Portable query endpoint declaration.</param>
    /// <param name="execute">Prepared query invocation; receives request cancellation.</param>
    /// <param name="authorizationPolicyResolver">Required when semantic authorization is declared.</param>
    /// <returns>A binding completed by route input and response policy.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">Operation kind, body or response contract differs.</exception>
    public static ApiQueryBinding<TInput, TResult> MapApiQuery<TInput, TResult>(this IEndpointRouteBuilder endpoints,
        ApiEndpoint endpoint, Func<TInput, CancellationToken, Task<TResult>> execute,
        AspNetAuthorizationPolicyResolver? authorizationPolicyResolver = null) =>
        new(endpoints, endpoint, execute, authorizationPolicyResolver);
}

/// <summary>Registration-local query binding. Complete once; it is not safe for concurrent mutation.</summary>
/// <typeparam name="TInput">Query input type.</typeparam>
/// <typeparam name="TResult">API response type.</typeparam>
public sealed class ApiQueryBinding<TInput, TResult>
{
    readonly IEndpointRouteBuilder endpoints;
    readonly ApiEndpoint endpoint;
    readonly Func<TInput, CancellationToken, Task<TResult>> execute;
    readonly AspNetAuthorizationPolicyResolver? authorizationPolicyResolver;
    Func<HttpContext, TInput>? input;
    bool completed;

    internal ApiQueryBinding(IEndpointRouteBuilder endpoints, ApiEndpoint endpoint,
        Func<TInput, CancellationToken, Task<TResult>> execute, AspNetAuthorizationPolicyResolver? authorizationPolicyResolver)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(execute);
        if (endpoint.Operation.Kind != ApiOperationKind.Query || endpoint.Operation.RequestType != typeof(void) || endpoint.Operation.Http?.Body is not null
            || endpoint.Operation.ResponseType != typeof(TResult) || endpoint.Operation.PrimaryResult.Kind != ApiResultKind.Success)
            throw new ArgumentException("Query kind, body and primary response type must match the typed binding.", nameof(endpoint));
        this.endpoints = endpoints;
        this.endpoint = endpoint;
        this.execute = execute;
        this.authorizationPolicyResolver = authorizationPolicyResolver;
    }

    /// <summary>Parses one route value and maps it to the query input.</summary>
    /// <typeparam name="TValue">Invariantly parsable route type.</typeparam>
    /// <param name="name">Route parameter name declared in the endpoint.</param>
    /// <param name="map">Pure input conversion.</param>
    /// <returns>This binding.</returns>
    /// <exception cref="ArgumentException">The route name is empty or not declared by the endpoint.</exception>
    /// <exception cref="ArgumentNullException">The input conversion is null.</exception>
    /// <exception cref="InvalidOperationException">The binding is complete or input is already assigned.</exception>
    public ApiQueryBinding<TInput, TResult> FromRoute<TValue>(string name, Func<TValue, TInput> map) where TValue : IParsable<TValue>
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(map);
        if (completed || input is not null) throw new InvalidOperationException("Query input is already configured or bound.");
        if (endpoint.Operation.Http is not { } http
            || !http.Parameters.Any(parameter => parameter.Name == name && parameter.Source == HttpParameterSource.Route))
            throw new ArgumentException("The route parameter is not declared by the endpoint.", nameof(name));
        input = context => TValue.TryParse(context.Request.RouteValues[name]?.ToString(), CultureInfo.InvariantCulture, out var value)
            ? map(value) : throw new BadHttpRequestException($"Invalid route parameter '{name}'.");
        return this;
    }

    /// <summary>Completes the binding: a null query result returns the declared 404; other results return typed 200 JSON.</summary>
    /// <returns>The native route handler for further ASP.NET configuration.</returns>
    /// <exception cref="InvalidOperationException">Input is absent, binding already completed, or 404 is not declared.</exception>
    public RouteHandlerBuilder OkOrNotFound()
    {
        if (completed || input is null || !endpoint.Operation.Results.Any(result => result.Kind == ApiResultKind.NotFound && result.BodyType == typeof(void)))
            throw new InvalidOperationException("An unbound query, configured input and declared NotFound result are required.");
        var readInput = input;
        var route = endpoints.MapApiEndpoint(endpoint, async (HttpContext context) =>
        {
            var result = await execute(readInput(context), context.RequestAborted).ConfigureAwait(false);
            return result is null ? Results.NotFound() : Results.Ok(result);
        }, authorizationPolicyResolver: authorizationPolicyResolver);
        completed = true;
        return route;
    }
}
