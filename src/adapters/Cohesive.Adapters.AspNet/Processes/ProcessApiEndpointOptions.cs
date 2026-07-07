using Cohesive.Api;
using Cohesive.Processes.Model;
using Cohesive.Processes.Runtime;
using Microsoft.AspNetCore.Http;

namespace Cohesive.Adapters.AspNet.Processes;

/// <summary>
/// Configuration for binding declared API operations to a typed process trigger and status lookup.
/// </summary>
public sealed class ProcessApiEndpointOptions<TRequest, TInput, TOutput>
{
    /// <summary>
    /// Declared API endpoint that starts the process.
    /// </summary>
    public ApiEndpoint? StartEndpoint { get; init; }

    /// <summary>
    /// Declared API operation that starts the process.
    /// </summary>
    public string? StartOperationName { get; init; }

    /// <summary>
    /// Declared API endpoint that gets process status.
    /// </summary>
    public ApiEndpoint? StatusEndpoint { get; init; }

    /// <summary>
    /// Declared API operation that gets process status.
    /// </summary>
    public string? StatusOperationName { get; init; }

    /// <summary>
    /// Optional keyed service identity for resolving the process engine.
    /// </summary>
    public object? ServiceKey { get; init; }

    /// <summary>
    /// Optional process name override.
    /// </summary>
    public string? ProcessName { get; init; }

    /// <summary>
    /// Optional shared typed process definition instance.
    /// </summary>
    public TypedProcessDefinition<TInput, TOutput>? ProcessDefinition { get; init; }

    /// <summary>
    /// Route parameter that carries the process id on the status operation.
    /// </summary>
    public string ProcessIdRouteParameter { get; init; } = "processId";

    /// <summary>
    /// Optional status-request guard. Return false to treat the process id as not found before querying the engine.
    /// </summary>
    public Func<ProcessApiStatusRequestContext, bool>? ValidateStatusRequest { get; init; }

    /// <summary>
    /// Optional process id factory. Defaults to a random guid.
    /// </summary>
    public Func<ProcessApiStartRequestContext<TRequest>, string>? CreateProcessId { get; init; }

    /// <summary>
    /// Creates the typed process input for a start request.
    /// </summary>
    public required Func<ProcessApiStartRequestContext<TRequest>, TInput> CreateInput { get; init; }

    /// <summary>
    /// Optional run options factory. The mapped process id is always enforced.
    /// </summary>
    public Func<ProcessApiStartRequestContext<TRequest>, ProcessRunOptions?>? CreateRunOptions { get; init; }

    /// <summary>
    /// Optional terminal-run loader used to enrich status responses.
    /// </summary>
    public Func<IProcessEngine, OperationContext, string, ProcessExecutionState, Task<ProcessRunResult<TOutput>?>>? LoadCompletedRunAsync { get; init; }

    /// <summary>
    /// Maps a successful start to the HTTP response.
    /// </summary>
    public required Func<ProcessApiStartResultContext<TRequest, TOutput>, IResult> CreateStartResult { get; init; }

    /// <summary>
    /// Maps a found status to the HTTP response.
    /// </summary>
    public required Func<ProcessApiStatusContext<TOutput>, IResult> CreateStatusResult { get; init; }
}

/// <summary>
/// Start request context supplied to process input factories.
/// </summary>
public sealed record ProcessApiStartRequestContext<TRequest>(
    OperationContext OperationContext,
    HttpContext HttpContext,
    ApiOperation Operation,
    TRequest Request,
    string ProcessId
    );

/// <summary>
/// Start result context supplied to response mappers.
/// </summary>
public sealed record ProcessApiStartResultContext<TRequest, TOutput>(
    OperationContext OperationContext,
    HttpContext HttpContext,
    ApiOperation Operation,
    TRequest Request,
    string ProcessId,
    ProcessStartResult<TOutput> Started
    );

/// <summary>
/// Status request context supplied to process id guards.
/// </summary>
public sealed record ProcessApiStatusRequestContext(
    OperationContext OperationContext,
    HttpContext HttpContext,
    ApiOperation Operation,
    string ProcessId
    );

/// <summary>
/// Status endpoint context supplied to response mappers.
/// </summary>
public sealed record ProcessApiStatusContext<TOutput>(
    OperationContext OperationContext,
    HttpContext HttpContext,
    ApiOperation Operation,
    string ProcessId,
    ProcessExecutionState Status,
    ProcessRunResult<TOutput>? CompletedRun,
    Exception? CompletedRunError
    );
