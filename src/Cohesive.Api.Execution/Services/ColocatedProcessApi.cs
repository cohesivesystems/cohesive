using Cohesive.Execution;
using Cohesive.Api.Services;
using Cohesive.Processes.Authoring;

namespace Cohesive.Api.Execution.Services;

/// <summary>A linked API retaining its colocated service and generated dependency recipe for host binding.</summary>
/// <typeparam name="TRequest">Transport input.</typeparam><typeparam name="TInput">Process input.</typeparam>
/// <typeparam name="TResult">Process result.</typeparam>
/// <typeparam name="TReceipt">Public admission receipt.</typeparam>
public class ColocatedProcessApi<TRequest, TInput, TResult, TReceipt> : ServiceAcceptedProcessApi<TRequest, TResult, TReceipt> where TRequest : class
{
    internal ColocatedProcessApi(ServiceBuilder service, ProcessModel<TInput, TResult> process,
        string startOperationId, string resultOperationId, string startRoute, string resultRoute, ServiceProcessResponse response, AcceptedProcessResponses<TReceipt>? outcomes)
        : base(service.Build(), startOperationId, resultOperationId, startRoute, resultRoute, response, outcomes)
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

/// <summary>A colocated process with the standard admission receipt.</summary>
public sealed class ColocatedProcessApi<TRequest, TInput, TResult> : ColocatedProcessApi<TRequest, TInput, TResult, ServiceProcessReceipt> where TRequest : class
{
    internal ColocatedProcessApi(ServiceBuilder service, ProcessModel<TInput, TResult> process, string startOperationId,
        string resultOperationId, string startRoute, string resultRoute, ServiceProcessResponse response)
        : base(service, process, startOperationId, resultOperationId, startRoute, resultRoute, response, null) { }
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
        var service = Configure(configure, id, revision);
        var pair = new ColocatedProcessApi<TRequest, TInput, TResult>(service, process, startOperationId, resultOperationId,
            startRoute, resultRoute, response ?? ServiceProcessResponse.Acknowledge);
        endpoints.Include(pair.Definition);
        return pair;
    }

    /// <summary>Declares a colocated pair with a typed admission receipt.</summary>
    public ColocatedProcessApi<TRequest, TInput, TResult, TReceipt> Accepted<TRequest, TReceipt>(ExecutionDefinitionId id, ExecutionRevisionId revision,
        Func<ServiceBuilder, ServiceBuilder> configure, string startOperationId, string resultOperationId,
        string startRoute, string resultRoute, AcceptedProcessResponses<TReceipt> outcomes, ServiceProcessResponse? response = null) where TRequest : class
    {
        ArgumentNullException.ThrowIfNull(outcomes);
        var service = Configure(configure, id, revision);
        var pair = new ColocatedProcessApi<TRequest, TInput, TResult, TReceipt>(service, process, startOperationId, resultOperationId,
            startRoute, resultRoute, response ?? ServiceProcessResponse.Acknowledge, outcomes);
        endpoints.Include(pair.Definition);
        return pair;
    }

    ServiceBuilder Configure(Func<ServiceBuilder, ServiceBuilder> configure, ExecutionDefinitionId id, ExecutionRevisionId revision)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var service = configure(Service.Define(id, revision, process.Provenance).Use(new ServiceDependencies().Process(process)));
        return service ?? throw new InvalidOperationException("The configuration returned no service.");
    }
}
