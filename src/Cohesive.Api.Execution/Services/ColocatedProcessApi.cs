using Cohesive.Execution;
using Cohesive.Api.Services;
using Cohesive.Processes.Authoring;

namespace Cohesive.Api.Execution.Services;

/// <summary>A linked API retaining its colocated service and generated dependency recipe for host binding.</summary>
/// <typeparam name="TRequest">Transport input.</typeparam><typeparam name="TInput">Process input.</typeparam>
/// <typeparam name="TResult">Process result.</typeparam>
public sealed class ColocatedProcessApi<TRequest, TInput, TResult> : ServiceAcceptedProcessApi<TRequest, TResult> where TRequest : class
{
    internal ColocatedProcessApi(ServiceBuilder service, ProcessModel<TInput, TResult> process,
        string startOperationId, string resultOperationId, string startRoute, string resultRoute, ServiceProcessResponse response)
        : base(service.Build(), startOperationId, resultOperationId, startRoute, resultRoute, response)
    {
        if (declarationMismatch())
            throw ServiceImplementationPlan.Error("services.api.processMismatch", "Colocated operations must reference the selected exact process.");
        Service = service; Process = process;

        bool declarationMismatch() => Declaration.GetDefinition<ServiceDefinition>().Operations.Any(operation => operation switch
        {
            ServiceProcessOperation start => start.Process != process.Definition.Reference,
            ServiceProcessResultOperation read => read.Process != process.Definition.Reference,
            _ => true
        });
    }
    /// <summary>The same service authoring contract used by separately declared services.</summary>
    public ServiceBuilder Service { get; }
    /// <summary>Generated process and dependency recipes; no executor is constructed.</summary>
    public ProcessModel<TInput, TResult> Process { get; }
}

/// <summary>Selects a generated process for colocated service/API authoring.</summary>
public static class ColocatedProcessApiExtensions
{
    /// <summary>Selects the process without creating runtime resources.</summary>
    /// <typeparam name="TInput">Process input.</typeparam><typeparam name="TResult">Process result.</typeparam>
    /// <param name="endpoints">Owning API.</param><param name="process">Generated process and dependencies.</param>
    /// <returns>A colocated service authoring builder.</returns>
    public static ColocatedProcessApiBuilder<TInput, TResult> Process<TInput, TResult>(this ApiBuilder endpoints, ProcessModel<TInput, TResult> process)
    {
        ArgumentNullException.ThrowIfNull(endpoints); ArgumentNullException.ThrowIfNull(process);
        return new(endpoints, process);
    }
}

/// <summary>Authors service policy locally while reusing the canonical service builder.</summary>
/// <typeparam name="TInput">Process input.</typeparam><typeparam name="TResult">Process result.</typeparam>
public sealed class ColocatedProcessApiBuilder<TInput, TResult>
{
    readonly ApiBuilder endpoints;
    readonly ProcessModel<TInput, TResult> process;
    internal ColocatedProcessApiBuilder(ApiBuilder endpoints, ProcessModel<TInput, TResult> process)
    { this.endpoints = endpoints; this.process = process; }
    /// <summary>Builds and atomically includes the admission and polling endpoints.</summary>
    /// <typeparam name="TRequest">Transport command, independently mapped to process input at hosting.</typeparam>
    /// <param name="id">Stable service identity, independent of CLR names.</param><param name="revision">Explicit service revision.</param>
    /// <param name="configure">Service operations and policies; generated dependencies are already registered.</param>
    /// <param name="startOperationId">Admission operation.</param><param name="resultOperationId">Result operation.</param>
    /// <param name="startRoute">POST route.</param><param name="resultRoute">GET route with {instanceId}.</param>
    /// <param name="response">Response timing, independent of execution lifetime.</param>
    /// <returns>The linked declaration and its reusable host recipe.</returns>
    public ColocatedProcessApi<TRequest, TInput, TResult> Accepted<TRequest>(ExecutionDefinitionId id, ExecutionRevisionId revision,
        Func<ServiceBuilder, ServiceBuilder> configure, string startOperationId, string resultOperationId,
        string startRoute, string resultRoute, ServiceProcessResponse? response = null) where TRequest : class
    {
        ArgumentNullException.ThrowIfNull(configure);
        var service = configure(Service.Define(id, revision, process.Provenance).Use(new ServiceDependencies().Process(process)));
        ArgumentNullException.ThrowIfNull(service);
        var pair = new ColocatedProcessApi<TRequest, TInput, TResult>(service, process, startOperationId, resultOperationId,
            startRoute, resultRoute, response ?? ServiceProcessResponse.Acknowledge);
        endpoints.Include(pair.Definition);
        return pair;
    }
}
