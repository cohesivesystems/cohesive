using Cohesive.Execution;
using Cohesive.Processes.Authoring;

namespace Cohesive.Api.Execution.Services;

/// <summary>A typed endpoint retaining the exact service and process authorities that produced its contract.</summary>
/// <typeparam name="TRequest">HTTP input.</typeparam><typeparam name="TResponse">HTTP response.</typeparam>
public sealed class ServiceProcessEndpoint<TRequest, TResponse> : ApiEndpoint<TRequest, TResponse>
{
    internal ServiceProcessEndpoint(ApiOperation operation, ExecutionDefinitionDocument service, ExecutionDefinitionReference process)
        : base(operation)
    { Service = new(service.Metadata.DefinitionId, service.Metadata.RevisionId, service.Metadata.Fingerprint); Process = process; }
    ServiceProcessEndpoint(ApiOperation operation, ExecutionDefinitionReference service, ExecutionDefinitionReference process)
        : base(operation) { Service = service; Process = process; }
    /// <summary>Attaches HTTP configuration while retaining the exact projection authorities.</summary>
    /// <param name="http">HTTP projection of this operation.</param><returns>The authority-preserving endpoint.</returns>
    public override ServiceProcessEndpoint<TRequest, TResponse> WithHttp(HttpBinding http) => new(Operation.WithHttp(http), Service, Process);
    /// <summary>Fingerprint-pinned service authority.</summary>
    public ExecutionDefinitionReference Service { get; }
    /// <summary>Fingerprint-pinned process authority.</summary>
    public ExecutionDefinitionReference Process { get; }
}

/// <summary>Fluent HTTP authoring over separately declared service and process contracts.</summary>
public static class ServiceProcessEndpointExtensions
{
    /// <summary>Selects a terminal ephemeral service operation without constructing runtime bindings.</summary>
    /// <typeparam name="TInput">Process input.</typeparam><typeparam name="TResult">Process output.</typeparam>
    /// <param name="endpoints">Owning API builder.</param><param name="service">Service declaration.</param>
    /// <param name="operationId">Exact operation in that service.</param><param name="process">Typed process authority.</param>
    /// <returns>The HTTP attachment phase.</returns>
    public static ServiceProcessEndpointBuilder<TInput, TResult> Process<TInput, TResult>(this ApiBuilder endpoints,
        ServiceBuilder service, string operationId, ProcessModel<TInput, TResult> process) => new(endpoints, service, operationId, process);
}

/// <summary>Attaches a native route to one terminal process projection.</summary>
/// <typeparam name="TInput">Process input.</typeparam><typeparam name="TResult">Process result.</typeparam>
public sealed class ServiceProcessEndpointBuilder<TInput, TResult>
{
    readonly ApiBuilder endpoints;
    readonly ServiceBuilder service;
    readonly string operationId;
    readonly ProcessModel<TInput, TResult> process;
    internal ServiceProcessEndpointBuilder(ApiBuilder endpoints, ServiceBuilder service, string operationId, ProcessModel<TInput, TResult> process)
    {
        ArgumentNullException.ThrowIfNull(endpoints); ArgumentNullException.ThrowIfNull(service); ArgumentNullException.ThrowIfNull(process);
        this.endpoints = endpoints; this.service = service; this.operationId = operationId; this.process = process;
    }
    /// <summary>Projects the declared terminal outcomes and includes the endpoint in its API.</summary>
    /// <param name="method">HTTP method.</param><param name="template">Route template.</param>
    /// <returns>The typed endpoint handle.</returns>
    public ApiEndpoint<TInput, TResult> Route(string method, string template) => endpoints.Include(
        ServiceApiProjection.ProjectEphemeralProcess(service.Build(), operationId, process.Definition,
            new HttpBinding(method, template, [], new(typeof(TInput)))));
}
