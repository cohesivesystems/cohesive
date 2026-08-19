using Cohesive.Api;
using Cohesive.Api.Execution;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Cohesive.Adapters.AspNet.Processes;

/// <summary>Resolves trusted invocation evidence for one canonical execution-control HTTP command.</summary>
/// <param name="context">Explicit cancellation, time, identity, and tracing context.</param>
/// <param name="httpContext">Current authenticated ASP.NET request.</param>
/// <param name="operation">Canonical execution-control operation being invoked.</param>
/// <returns>Trusted authorization, timing, provenance, and grant evidence.</returns>
/// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
/// <exception cref="OperationCanceledException">Resolution is cancelled.</exception>
public delegate ValueTask<ExecutionApiInvocationContext> ExecutionApiInvocationContextResolver(
    OperationContext context,
    HttpContext httpContext,
    ApiOperation operation);

/// <summary>ASP.NET projections for canonical execution-control command endpoints.</summary>
public static class ProcessExecutionCommandApiEndpointRouteBuilderExtensions
{
    /// <summary>Maps one canonical execution-control command without introducing a transport-specific DTO.</summary>
    /// <typeparam name="TRequest">Exact canonical request type declared by <paramref name="endpoint"/>.</typeparam>
    /// <param name="endpoints">ASP.NET endpoint route builder to update.</param>
    /// <param name="catalog">Canonical execution-control API catalog owning the endpoint.</param>
    /// <param name="endpoint">Exact canonical command endpoint handle.</param>
    /// <param name="route">HTTP POST route for this projection.</param>
    /// <param name="invocationContextResolver">Server-side trusted invocation-context resolver.</param>
    /// <param name="authorizationPolicyResolver">
    /// Projection from semantic authorization requirements to ASP.NET policies.
    /// </param>
    /// <param name="configure">Optional callback applied to the mapped endpoint.</param>
    /// <returns>The mapped route handler builder.</returns>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="route"/> is empty.</exception>
    /// <exception cref="InvalidOperationException">
    /// The endpoint is not owned by <paramref name="catalog"/>, is not a command, or declares another request type.
    /// </exception>
    public static RouteHandlerBuilder MapProcessExecutionCommandApi<TRequest>(
        this IEndpointRouteBuilder endpoints,
        ExecutionControlApiCatalog catalog,
        ApiEndpoint endpoint,
        string route,
        ExecutionApiInvocationContextResolver invocationContextResolver,
        AspNetAuthorizationPolicyResolver authorizationPolicyResolver,
        Action<RouteHandlerBuilder, ApiOperation>? configure = null)
        where TRequest : class
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(route);
        ArgumentNullException.ThrowIfNull(invocationContextResolver);
        ArgumentNullException.ThrowIfNull(authorizationPolicyResolver);

        ApiOperation operation;
        try
        {
            operation = catalog.Definition.GetOperation(endpoint);
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidOperationException(
                "The execution-control command endpoint is not owned by the supplied canonical catalog.",
                exception);
        }
        if (!ReferenceEquals(operation, endpoint.Operation))
        {
            throw new InvalidOperationException(
                "The execution-control command endpoint is not owned by the supplied canonical catalog.");
        }

        if (operation.Kind != ApiOperationKind.Command || operation.RequestType != typeof(TRequest))
        {
            throw new InvalidOperationException(
                $"Execution-control endpoint '{operation.Id}' does not declare command request '{typeof(TRequest).FullName}'.");
        }

        var projected = endpoint.WithHttp(new HttpBinding(
            method: HttpMethods.Post,
            route: route,
            parameters: [],
            body: new HttpBodyBinding(typeof(TRequest))));
        return endpoints.MapApiEndpoint(
            projected,
            CreateHandler<TRequest>(endpoint, projected.Operation, invocationContextResolver),
            configure,
            authorizationPolicyResolver);
    }

    static Delegate CreateHandler<TRequest>(
        ApiEndpoint endpoint,
        ApiOperation projectedOperation,
        ExecutionApiInvocationContextResolver invocationContextResolver)
        where TRequest : class =>
        async (
            OperationContext operationContext,
            HttpContext httpContext,
            IExecutionControlApiDispatcher dispatcher) =>
        {
            ArgumentNullException.ThrowIfNull(operationContext);
            ArgumentNullException.ThrowIfNull(httpContext);
            ArgumentNullException.ThrowIfNull(dispatcher);
            var request = await ProcessApiRequestSupport.ReadRequestAsync<TRequest>(
                    httpContext,
                    projectedOperation,
                    operationContext.CancellationToken)
                .ConfigureAwait(false);
            if (request is null)
            {
                throw new BadHttpRequestException(
                    $"Request body for execution-control operation '{projectedOperation.Name}' is required.");
            }
            var invocation = await invocationContextResolver(
                    operationContext,
                    httpContext,
                    projectedOperation)
                .ConfigureAwait(false);
            var dispatched = await dispatcher.DispatchAsync(
                    operationContext,
                    endpoint,
                    request,
                    invocation)
                .ConfigureAwait(false);
            var result = GetProjectedResult(
                projectedOperation,
                dispatched.Result.Kind,
                dispatched.Result.BodyType);
            return result.BodyType == typeof(void)
                ? Results.StatusCode(result.Http!.StatusCode)
                : Results.Json(
                    dispatched.Body,
                    options: null,
                    contentType: result.Http!.ContentType ?? "application/json",
                    statusCode: result.Http.StatusCode);
        };

    static ApiResultDefinition GetProjectedResult(
        ApiOperation operation,
        ApiResultKind kind,
        Type bodyType)
    {
        ApiResultDefinition? match = null;
        for (var index = 0; index < operation.Results.Count; index++)
        {
            var candidate = operation.Results[index];
            if (candidate.Kind != kind || candidate.BodyType != bodyType)
            {
                continue;
            }

            if (match is not null)
            {
                throw new InvalidOperationException(
                    $"Execution-control operation '{operation.Id}' declares multiple matching HTTP results.");
            }
            match = candidate;
        }

        if (match?.Http is null)
        {
            throw new InvalidOperationException(
                $"Execution-control operation '{operation.Id}' has no HTTP result for '{kind}/{bodyType.FullName}'.");
        }
        return match;
    }
}
