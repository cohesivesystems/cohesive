using Cohesive.Api.Services;
using Cohesive.Execution;
using Cohesive.Processes.Authoring;

namespace Cohesive.Api.Execution.Services;

/// <summary>A typed endpoint retaining the exact service and process authorities that produced its contract.</summary>
/// <typeparam name="TRequest">HTTP input.</typeparam><typeparam name="TResponse">HTTP response.</typeparam>
public sealed class ServiceProcessEndpoint<TRequest, TResponse> : ApiEndpoint<TRequest, TResponse>
{
    internal ServiceProcessEndpoint(ApiOperation operation, ExecutionDefinitionDocument service, ExecutionDefinitionReference process, string operationId)
        : base(operation)
    { Service = service.Reference; Process = process; OperationId = operationId; }
    ServiceProcessEndpoint(ApiOperation operation, ExecutionDefinitionReference service, ExecutionDefinitionReference process, string operationId)
        : base(operation) { Service = service; Process = process; OperationId = operationId; }
    /// <summary>Attaches HTTP configuration while retaining the exact projection authorities.</summary>
    /// <param name="http">HTTP projection of this operation.</param><returns>The authority-preserving endpoint.</returns>
    public override ServiceProcessEndpoint<TRequest, TResponse> WithHttp(HttpBinding http) => new(Operation.WithHttp(http), Service, Process, OperationId);
    /// <summary>Exact operation selected within the service.</summary>
    public string OperationId { get; }
    /// <summary>Fingerprint-pinned service authority.</summary>
    public ExecutionDefinitionReference Service { get; }
    /// <summary>Fingerprint-pinned process authority.</summary>
    public ExecutionDefinitionReference Process { get; }
}

/// <summary>Fluent HTTP authoring over separately declared service and process contracts.</summary>
public static class ServiceProcessEndpointExtensions
{
    /// <summary>Selects the sole operation of a finite ephemeral service without constructing runtime bindings.</summary>
    /// <remarks>This host profile requires one operation. Multi-operation service declarations remain valid for other
    /// runtimes, but require complete binding coverage and cannot be projected through this finite-host builder.</remarks>
    /// <exception cref="ServiceBindingValidationException">The service contains more than one operation or the ID does not select its sole operation.</exception>
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
        var operations = service.Build().GetDefinition<ServiceDefinition>().Operations;
        if (operations.Length != 1 || operations[0].Id != operationId)
            throw ServiceImplementationPlan.Error("services.api.operationMismatch",
                "This finite endpoint profile requires one service operation, selected by its exact ID. Use complete multi-operation runtime bindings for larger services.");
        this.endpoints = endpoints; this.service = service; this.operationId = operationId; this.process = process;
    }
    /// <summary>Projects the declared terminal outcomes and includes the endpoint in its API.</summary>
    /// <param name="method">HTTP method.</param><param name="template">Route template.</param>
    /// <returns>The typed endpoint handle.</returns>
    public ServiceProcessEndpoint<TInput, TResult> Route(string method, string template) => endpoints.Include(
        ServiceApiProjection.ProjectEphemeralProcess(service.Build(), operationId, process.Definition,
            new HttpBinding(method, template, [], new(typeof(TInput)))));
}
