using Cohesive.Api;
using Cohesive.Processes.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace Cohesive.Adapters.AspNet.Processes;

/// <summary>
/// ASP.NET endpoint helpers for binding declared API operations to process execution repository queries.
/// </summary>
public static class ProcessExecutionApiEndpointRouteBuilderExtensions
{
    // ReSharper disable once ClassNeverInstantiated.Local
    record ProcessExecutionApiEndpoints();
    
    /// <summary>
    /// Maps an API operation that queries process executions using <see cref="IProcessExecutionRepository"/>.
    /// </summary>
    public static RouteHandlerBuilder MapProcessExecutionQueryApiDefinition<TRequest>(
        this IEndpointRouteBuilder endpoints,
        ApiEndpoint endpoint,
        ProcessExecutionApiEndpointOptions<TRequest> options,
        Action<RouteHandlerBuilder, ApiOperation>? configure = null
        )
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(options);
        return endpoints.MapApiEndpoint(endpoint, CreateQueryHandler(endpoint.Operation, options), configure);
    }

    static Delegate CreateQueryHandler<TRequest>(ApiOperation operation, ProcessExecutionApiEndpointOptions<TRequest> options) =>
        async (OperationContext operationContext, HttpContext httpContext, IProcessExecutionRepository repository, ILogger<ProcessExecutionApiEndpoints> logger) =>
        {
            ArgumentNullException.ThrowIfNull(operationContext);
            ArgumentNullException.ThrowIfNull(httpContext);
            ArgumentNullException.ThrowIfNull(repository);
            var request = await ProcessApiRequestSupport.ReadRequestAsync<TRequest>(httpContext, operation, operationContext.CancellationToken).ConfigureAwait(false);
            var query = options.CreateQuery(new(
                OperationContext: operationContext,
                HttpContext: httpContext,
                Operation: operation,
                Request: request
            ));
            var result = await repository.QueryAsync(operationContext, query).ConfigureAwait(false);
            logger.LogDebug(
                "Process execution query for API Operation={OperationName} ({EndpointId}) ProcessName={ProcessName} ProcessIdPrefix={ProcessIdPrefix} returned {Count} execution(s); HasContinuationToken={HasContinuationToken}.",
                operation.Name,
                operation.Id,
                query.ProcessName ?? "<any>",
                query.ProcessIdPrefix ?? "<any>",
                result.Items.Count,
                !string.IsNullOrWhiteSpace(result.ContinuationToken)
                );
            return options.CreateResult(new(
                OperationContext: operationContext,
                HttpContext: httpContext,
                Operation: operation,
                Request: request,
                Query: query,
                Result: result
                )
            );
        };
}
