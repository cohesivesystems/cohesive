using Cohesive.Processes;
using Cohesive.Processes.Model;
using Cohesive.Processes.Runtime;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cohesive.Adapters.AspNet.Processes;

sealed class ProcessEndpointStartCoreOptions<TRequest, TInput, TOutput>
{
    public object? ServiceKey { get; init; }

    public required Lazy<TypedProcessDefinition<TInput, TOutput>> ProcessDefinition { get; init; }

    public required string OperationName { get; init; }

    public required string OperationId { get; init; }

    public Func<ProcessEndpointStartExecutionContext<TRequest>, string>? CreateProcessId { get; init; }

    public required Func<ProcessEndpointStartExecutionContext<TRequest>, TInput> CreateInput { get; init; }

    public Func<ProcessEndpointStartExecutionContext<TRequest>, ProcessRunOptions?>? CreateRunOptions { get; init; }
}

sealed class ProcessEndpointStatusCoreOptions<TOutput>
{
    public object? ServiceKey { get; init; }

    public required string OperationName { get; init; }

    public required string OperationId { get; init; }

    public Func<ProcessEndpointStatusExecutionContext, bool>? ValidateStatusRequest { get; init; }

    public Func<IProcessEngine, OperationContext, string, ProcessExecutionState, Task<ProcessRunResult<TOutput>?>>? LoadCompletedRunAsync { get; init; }
}

sealed record ProcessEndpointStartExecutionContext<TRequest>(
    OperationContext OperationContext,
    HttpContext HttpContext,
    TRequest Request,
    string ProcessId
    );

sealed record ProcessEndpointStartExecutionResult<TOutput>(
    string ProcessId,
    ProcessStartResult<TOutput> Started
    );

sealed record ProcessEndpointStatusExecutionContext(
    OperationContext OperationContext,
    HttpContext HttpContext,
    string ProcessId
    );

sealed record ProcessEndpointStatusExecutionResult<TOutput>(
    string ProcessId,
    ProcessExecutionState Status,
    ProcessRunResult<TOutput>? CompletedRun,
    Exception? CompletedRunError
    );

static class ProcessEndpointCore
{
    public static async Task<ProcessEndpointStartExecutionResult<TOutput>> StartAsync<TRequest, TInput, TOutput>(
        OperationContext operationContext,
        HttpContext httpContext,
        TRequest request,
        ProcessEndpointStartCoreOptions<TRequest, TInput, TOutput> options
        )
    {
        ArgumentNullException.ThrowIfNull(operationContext);
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(options);

        var processId = options.CreateProcessId?.Invoke(new(
                operationContext,
                httpContext,
                request,
                ProcessId: string.Empty))
            ?? Guid.NewGuid().ToString("N");

        var requestContext = new ProcessEndpointStartExecutionContext<TRequest>(
            operationContext,
            httpContext,
            request,
            processId);
        var input = options.CreateInput(requestContext);
        var runOptions = NormalizeRunOptions(processId, options.CreateRunOptions?.Invoke(requestContext));
        var process = options.ProcessDefinition.Value;
        var engine = httpContext.RequestServices.ResolveProcessEngine(options.ServiceKey);
        var logger = CreateLogger(httpContext.RequestServices);

        logger.LogInformation(
            "Starting process '{ProcessName}' ({ProcessId}) for endpoint '{OperationName}' ({OperationId}).",
            process.Definition.Name,
            processId,
            options.OperationName,
            options.OperationId);

        ProcessStartResult<TOutput> started;
        try
        {
            started = await engine.StartAsync(
                operationContext,
                process,
                input,
                runOptions: runOptions
                ).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed to start process '{ProcessName}' ({ProcessId}) for endpoint '{OperationName}' ({OperationId}).",
                process.Definition.Name,
                processId,
                options.OperationName,
                options.OperationId);
            throw;
        }

        logger.LogInformation(
            "Started process '{ProcessName}' ({ProcessId}) for endpoint '{OperationName}' ({OperationId}).",
            started.ProcessName,
            started.ProcessId,
            options.OperationName,
            options.OperationId);

        return new(processId, started);
    }

    public static async Task<ProcessEndpointStatusExecutionResult<TOutput>?> GetStatusAsync<TOutput>(
        OperationContext operationContext,
        HttpContext httpContext,
        string processId,
        ProcessEndpointStatusCoreOptions<TOutput> options
        )
    {
        ArgumentNullException.ThrowIfNull(operationContext);
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentException.ThrowIfNullOrWhiteSpace(processId);
        ArgumentNullException.ThrowIfNull(options);

        var requestContext = new ProcessEndpointStatusExecutionContext(
            operationContext,
            httpContext,
            processId);
        if (options.ValidateStatusRequest?.Invoke(requestContext) == false)
            return null;

        var engine = httpContext.RequestServices.ResolveProcessEngine(options.ServiceKey);
        var logger = CreateLogger(httpContext.RequestServices);
        logger.LogDebug(
            "Reading process status for '{ProcessId}' via endpoint '{OperationName}' ({OperationId}).",
            processId,
            options.OperationName,
            options.OperationId);

        var executionState = await engine.GetStatusAsync(operationContext, processId).ConfigureAwait(false);
        if (executionState is null)
        {
            logger.LogInformation(
                "Process '{ProcessId}' was not found while serving endpoint '{OperationName}' ({OperationId}).",
                processId,
                options.OperationName,
                options.OperationId);
            return null;
        }

        logger.LogDebug(
            "Process '{ProcessId}' status is '{Status}' for endpoint '{OperationName}' ({OperationId}).",
            processId,
            executionState.Status,
            options.OperationName,
            options.OperationId);

        ProcessRunResult<TOutput>? completedRun = null;
        Exception? completedRunError = null;
        if (executionState.IsTerminal && options.LoadCompletedRunAsync is not null)
        {
            try
            {
                completedRun = await options.LoadCompletedRunAsync(
                    engine,
                    operationContext,
                    processId,
                    executionState).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                completedRunError = ex;
                logger.LogWarning(
                    ex,
                    "Failed to load completed process run for '{ProcessId}' while serving endpoint '{OperationName}' ({OperationId}).",
                    processId,
                    options.OperationName,
                    options.OperationId);
            }
        }

        return new(
            ProcessId: processId,
            Status: executionState,
            CompletedRun: completedRun,
            CompletedRunError: completedRunError
            );
    }

    static ProcessRunOptions NormalizeRunOptions(string processId, ProcessRunOptions? runOptions)
    {
        if (runOptions is null)
            return new() { ProcessId = processId };

        if (!string.IsNullOrWhiteSpace(runOptions.ProcessId) && !string.Equals(runOptions.ProcessId, processId, StringComparison.Ordinal))
            throw new InvalidOperationException($"Configured process id '{runOptions.ProcessId}' did not match the mapped process id '{processId}'.");

        return runOptions with { ProcessId = processId };
    }

    static ILogger CreateLogger(IServiceProvider services) =>
        services.GetService<ILoggerFactory>()?.CreateLogger(typeof(ProcessEndpointCore).FullName!)
        ?? NullLogger.Instance;
}
