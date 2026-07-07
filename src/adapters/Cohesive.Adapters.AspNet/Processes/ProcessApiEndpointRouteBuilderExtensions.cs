using Cohesive.Api;
using Cohesive.Processes;
using Cohesive.Processes.Model;
using Cohesive.Processes.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Cohesive.Adapters.AspNet.Processes;

/// <summary>
/// Mapped route builders for declared API operations bound to a process trigger and status lookup.
/// </summary>
public sealed record ProcessApiEndpointBuilders(
    RouteHandlerBuilder Start,
    RouteHandlerBuilder Status
    );

/// <summary>
/// ASP.NET endpoint helpers for binding declared API operations to typed process-engine operations.
/// </summary>
public static class ProcessApiEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps start and status API operations for a typed process definition.
    /// </summary>
    public static ProcessApiEndpointBuilders MapProcessApiDefinition<TProcess, TRequest, TInput, TOutput>(
        this IEndpointRouteBuilder endpoints,
        ApiEndpoint startEndpoint,
        ApiEndpoint statusEndpoint,
        ProcessApiEndpointOptions<TRequest, TInput, TOutput> options,
        Action<RouteHandlerBuilder, ApiOperation>? configure = null
        ) where TProcess : class, IProcessDefinition<TInput, TOutput>
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(startEndpoint);
        ArgumentNullException.ThrowIfNull(statusEndpoint);
        ArgumentNullException.ThrowIfNull(options);

        return MapProcessApiOperations<TProcess, TRequest, TInput, TOutput>(
            endpoints,
            startEndpoint,
            statusEndpoint,
            options,
            configure
            );
    }

    static ProcessApiEndpointBuilders MapProcessApiOperations<TProcess, TRequest, TInput, TOutput>(
        IEndpointRouteBuilder endpoints,
        ApiEndpoint startEndpoint,
        ApiEndpoint statusEndpoint,
        ProcessApiEndpointOptions<TRequest, TInput, TOutput> options,
        Action<RouteHandlerBuilder, ApiOperation>? configure
        ) where TProcess : class, IProcessDefinition<TInput, TOutput>
    {
        var processDefinition = new Lazy<TypedProcessDefinition<TInput, TOutput>>(
            () => options.ProcessDefinition ?? endpoints.ServiceProvider.ResolveProcessDefinition<TProcess, TInput, TOutput>(options.ProcessName),
            LazyThreadSafetyMode.ExecutionAndPublication
            );
        var start = endpoints.MapApiEndpoint(
            startEndpoint,
            CreateStartHandler(startEndpoint.Operation, options, processDefinition),
            configure
            );
        var status = endpoints.MapApiEndpoint(
            statusEndpoint,
            CreateStatusHandler(statusEndpoint.Operation, options),
            configure
            );
        return new(start, status);
    }

    static Delegate CreateStartHandler<TRequest, TInput, TOutput>(
        ApiOperation operation,
        ProcessApiEndpointOptions<TRequest, TInput, TOutput> options,
        Lazy<TypedProcessDefinition<TInput, TOutput>> processDefinition
        ) =>
        async (OperationContext operationContext, HttpContext httpContext) =>
        {
            ArgumentNullException.ThrowIfNull(operationContext);
            ArgumentNullException.ThrowIfNull(httpContext);

            var request = await ProcessApiRequestSupport.ReadRequestAsync<TRequest>(
                httpContext,
                operation,
                operationContext.CancellationToken
                ).ConfigureAwait(false);
            
            var result = await ProcessEndpointCore.StartAsync(
                operationContext,
                httpContext,
                request,
                CreateStartCoreOptions(operation, options, processDefinition)
                ).ConfigureAwait(false);

            return options.CreateStartResult(new(
                OperationContext: operationContext,
                HttpContext: httpContext,
                Operation: operation,
                Request: request,
                ProcessId: result.ProcessId,
                Started: result.Started
                )
            );
        };

    static Delegate CreateStatusHandler<TRequest, TInput, TOutput>(ApiOperation operation, ProcessApiEndpointOptions<TRequest, TInput, TOutput> options) =>
        async (OperationContext operationContext, HttpContext httpContext) =>
        {
            ArgumentNullException.ThrowIfNull(operationContext);
            ArgumentNullException.ThrowIfNull(httpContext);

            var processId = GetRequiredRouteValue(httpContext, options.ProcessIdRouteParameter);
            var result = await ProcessEndpointCore.GetStatusAsync(
                    operationContext,
                    httpContext,
                    processId,
                    CreateStatusCoreOptions(operation, options))
                .ConfigureAwait(false);
            if (result is null)
                return Results.NotFound();

            return options.CreateStatusResult(new(
                OperationContext: operationContext,
                HttpContext: httpContext,
                Operation: operation,
                ProcessId: result.ProcessId,
                Status: result.Status,
                CompletedRun: result.CompletedRun,
                CompletedRunError: result.CompletedRunError
                )
            );
        };

    static string GetRequiredRouteValue(HttpContext httpContext, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parameterName);
        if (httpContext.Request.RouteValues.TryGetValue<string>(parameterName, out var value) && !string.IsNullOrWhiteSpace(value))
            return value;
        throw new BadHttpRequestException($"Route value '{parameterName}' is required.");
    }

    static ProcessEndpointStartCoreOptions<TRequest, TInput, TOutput> CreateStartCoreOptions<TRequest, TInput, TOutput>(
        ApiOperation operation,
        ProcessApiEndpointOptions<TRequest, TInput, TOutput> options,
        Lazy<TypedProcessDefinition<TInput, TOutput>> processDefinition
        ) => new()
        {
            ServiceKey = options.ServiceKey,
            ProcessDefinition = processDefinition,
            OperationName = operation.Name,
            OperationId = operation.Id.Value,
            CreateProcessId = options.CreateProcessId is null
                ? null
                : context => options.CreateProcessId(new(
                    context.OperationContext,
                    context.HttpContext,
                    operation,
                    context.Request,
                    context.ProcessId)),
            CreateInput = context => options.CreateInput(new(
                context.OperationContext,
                context.HttpContext,
                operation,
                context.Request,
                context.ProcessId)),
            CreateRunOptions = options.CreateRunOptions is null
                ? null
                : context => options.CreateRunOptions(new(
                    context.OperationContext,
                    context.HttpContext,
                    operation,
                    context.Request,
                    context.ProcessId)),
        };

    static ProcessEndpointStatusCoreOptions<TOutput> CreateStatusCoreOptions<TRequest, TInput, TOutput>(
        ApiOperation operation,
        ProcessApiEndpointOptions<TRequest, TInput, TOutput> options
        ) => new()
        {
            ServiceKey = options.ServiceKey,
            OperationName = operation.Name,
            OperationId = operation.Id.Value,
            ValidateStatusRequest = options.ValidateStatusRequest is null
                ? null
                : context => options.ValidateStatusRequest(new(
                    context.OperationContext,
                    context.HttpContext,
                    operation,
                    context.ProcessId)),
            LoadCompletedRunAsync = options.LoadCompletedRunAsync
        };
}
