using Cohesive.Processes.Model;
using Cohesive.Processes.Runtime;
using Microsoft.AspNetCore.Http;

namespace Cohesive.Adapters.AspNet.Processes;

/// <summary>
/// Configuration for mapping typed process start/status endpoints.
/// </summary>
public sealed class ProcessEndpointOptions<TRequest, TInput, TOutput>
{
    /// <summary>
    /// Start-route pattern.
    /// </summary>
    public string StartPattern { get; init; } = "/processes";

    /// <summary>
    /// Status-route pattern.
    /// </summary>
    public string StatusPattern { get; init; } = "/processes/{processId}";

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
    /// Optional start endpoint name.
    /// </summary>
    public string? StartEndpointName { get; init; }

    /// <summary>
    /// Optional status endpoint name.
    /// </summary>
    public string? StatusEndpointName { get; init; }

    /// <summary>
    /// Optional process id factory. Defaults to a random guid.
    /// </summary>
    public Func<OperationContext, TRequest, string>? CreateProcessId { get; init; }

    /// <summary>
    /// Optional hook invoked for every start request before process execution begins.
    /// </summary>
    public Action<OperationContext, TRequest>? OnStartRequest { get; init; }

    /// <summary>
    /// Optional hook invoked for every status request before status is loaded.
    /// </summary>
    public Action<OperationContext, string>? OnStatusRequest { get; init; }

    /// <summary>
    /// Optional status-request guard. Return false to treat the process id as not found before querying the engine.
    /// </summary>
    public Func<ProcessEndpointStatusRequestContext, bool>? ValidateStatusRequest { get; init; }

    /// <summary>
    /// Creates the typed process input for a start request.
    /// </summary>
    public required Func<OperationContext, TRequest, string, TInput> CreateInput { get; init; }

    /// <summary>
    /// Optional run options factory. The mapped process id is always enforced.
    /// </summary>
    public Func<OperationContext, TRequest, string, ProcessRunOptions?>? CreateRunOptions { get; init; }

    /// <summary>
    /// Optional terminal-run loader used to enrich status responses.
    /// </summary>
    public Func<IProcessEngine, OperationContext, string, ProcessExecutionState, Task<ProcessRunResult<TOutput>?>>? LoadCompletedRunAsync { get; init; }

    /// <summary>
    /// Maps a successful start to the HTTP response.
    /// </summary>
    public required Func<ProcessEndpointStartContext<TRequest, TOutput>, IResult> CreateStartResult { get; init; }

    /// <summary>
    /// Maps a found status to the HTTP response.
    /// </summary>
    public required Func<ProcessEndpointStatusContext<TOutput>, IResult> CreateStatusResult { get; init; }
}
