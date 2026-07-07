using Cohesive.Processes;
using Cohesive.Processes.Model;
using Cohesive.Processes.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Cohesive.Adapters.AspNet.Processes;

/// <summary>
/// Mapped route builders for a process start/status endpoint pair.
/// </summary>
public sealed record ProcessEndpointBuilders(
    RouteHandlerBuilder Start,
    RouteHandlerBuilder Status
    );

/// <summary>
/// Start endpoint context supplied to response mappers.
/// </summary>
public sealed record ProcessEndpointStartContext<TRequest, TOutput>(
    OperationContext OperationContext,
    TRequest Request,
    string ProcessId,
    ProcessStartResult<TOutput> Started
    );

/// <summary>
/// Status endpoint context supplied to response mappers.
/// </summary>
public sealed record ProcessEndpointStatusContext<TOutput>(
    OperationContext OperationContext,
    string ProcessId,
    ProcessExecutionState Status,
    ProcessRunResult<TOutput>? CompletedRun,
    Exception? CompletedRunError
    );

/// <summary>
/// Status request context supplied to process id guards.
/// </summary>
public sealed record ProcessEndpointStatusRequestContext(
    OperationContext OperationContext,
    HttpContext HttpContext,
    string ProcessId
    );

/// <summary>
/// ASP.NET endpoint helpers for exposing typed process-engine operations.
/// </summary>
public static class ProcessEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps start and status endpoints for one typed process definition.
    /// </summary>
    public static ProcessEndpointBuilders MapProcessEndpoints<TProcess, TRequest, TInput, TOutput>(this IEndpointRouteBuilder endpoints, ProcessEndpointOptions<TRequest, TInput, TOutput> options)
        where TProcess : class, IProcessDefinition<TInput, TOutput>
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(options);

        var processDefinition = new Lazy<TypedProcessDefinition<TInput, TOutput>>(
            () => options.ProcessDefinition ?? endpoints.ServiceProvider.ResolveProcessDefinition<TProcess, TInput, TOutput>(processName: options.ProcessName),
            LazyThreadSafetyMode.ExecutionAndPublication
            );
        var start = endpoints.MapPost(options.StartPattern, async (
            TRequest request,
            OperationContext operationContext,
            HttpContext httpContext
            ) =>
            {
                ArgumentNullException.ThrowIfNull(request);
                ArgumentNullException.ThrowIfNull(operationContext);
                ArgumentNullException.ThrowIfNull(httpContext);
                
                options.OnStartRequest?.Invoke(operationContext, request);
                var result = await ProcessEndpointCore.StartAsync(
                        operationContext,
                        httpContext,
                        request,
                        CreateStartCoreOptions(options, processDefinition, options.StartEndpointName ?? options.StartPattern, options.StartPattern))
                    .ConfigureAwait(false);
                
                return options.CreateStartResult(new(
                    OperationContext: operationContext,
                    Request: request,
                    ProcessId: result.ProcessId,
                    Started: result.Started
                    ));
            });

        if (!string.IsNullOrWhiteSpace(options.StartEndpointName))
            start.WithName(options.StartEndpointName);

        var status = endpoints.MapGet(options.StatusPattern, async (
            string processId,
            OperationContext operationContext,
            HttpContext httpContext
            ) =>
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(processId);
                ArgumentNullException.ThrowIfNull(operationContext);
                ArgumentNullException.ThrowIfNull(httpContext);

                options.OnStatusRequest?.Invoke(operationContext, processId);
                var result = await ProcessEndpointCore.GetStatusAsync(
                        operationContext,
                        httpContext,
                        processId,
                        CreateStatusCoreOptions(options, options.StatusEndpointName ?? options.StatusPattern, options.StatusPattern))
                    .ConfigureAwait(false);
                if (result is null)
                    return Results.NotFound();

                return options.CreateStatusResult(new(
                    OperationContext: operationContext,
                    ProcessId: result.ProcessId,
                    Status: result.Status,
                    CompletedRun: result.CompletedRun,
                    CompletedRunError: result.CompletedRunError
                    ));
            });

        if (!string.IsNullOrWhiteSpace(options.StatusEndpointName))
            status.WithName(options.StatusEndpointName);

        return new(start, status);
    }

    static ProcessEndpointStartCoreOptions<TRequest, TInput, TOutput> CreateStartCoreOptions<TRequest, TInput, TOutput>(
        ProcessEndpointOptions<TRequest, TInput, TOutput> options,
        Lazy<TypedProcessDefinition<TInput, TOutput>> processDefinition,
        string operationName,
        string operationId
        ) => new()
        {
            ServiceKey = options.ServiceKey,
            ProcessDefinition = processDefinition,
            OperationName = operationName,
            OperationId = operationId,
            CreateProcessId = options.CreateProcessId is null
                ? null
                : context => options.CreateProcessId(context.OperationContext, context.Request),
            CreateInput = context => options.CreateInput(context.OperationContext, context.Request, context.ProcessId),
            CreateRunOptions = options.CreateRunOptions is null
                ? null
                : context => options.CreateRunOptions(context.OperationContext, context.Request, context.ProcessId)
        };

    static ProcessEndpointStatusCoreOptions<TOutput> CreateStatusCoreOptions<TRequest, TInput, TOutput>(
        ProcessEndpointOptions<TRequest, TInput, TOutput> options,
        string operationName,
        string operationId
        ) => new()
        {
            ServiceKey = options.ServiceKey,
            OperationName = operationName,
            OperationId = operationId,
            ValidateStatusRequest = options.ValidateStatusRequest is null
                ? null
                : context => options.ValidateStatusRequest(new(
                    context.OperationContext,
                    context.HttpContext,
                    context.ProcessId)),
            LoadCompletedRunAsync = options.LoadCompletedRunAsync
        };
}
