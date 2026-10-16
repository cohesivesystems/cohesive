using Cohesive.Api.Services;
using Cohesive.Execution;

namespace Cohesive.Api.Execution.Services;

/// <summary>A public admission receipt and the local address of its independently authorized result read.</summary>
/// <param name="Admission">Native accepted or replayed admission; never terminal execution evidence.</param>
/// <param name="ResultLocation">Local polling address for the admitted instance.</param>
public sealed record ServiceProcessReceipt(ProcessStartResult Admission, string ResultLocation);

/// <summary>A linked command/result API projected from two operations of one exact service declaration.</summary>
/// <remarks>Contains no executor. A native durable dispatcher and protected value repository must realize the
/// service. The result relationship remains visible on Result.Operation.ResultOf after API composition.</remarks>
/// <typeparam name="TRequest">Medium command input, including caller-owned retry identities.</typeparam>
/// <typeparam name="TResult">Public terminal result view.</typeparam>
public sealed class ServiceAcceptedProcessApi<TRequest, TResult> : IApiDeclaration where TRequest : class
{
    internal ServiceAcceptedProcessApi(ExecutionDefinitionDocument declaration, string startOperationId,
        string resultOperationId, string startRoute, string resultRoute)
    {
        const string token = "{instanceId}";
        var remainder = resultRoute.Replace(token, "", StringComparison.Ordinal);
        if (!resultRoute.StartsWith('/') || resultRoute.StartsWith("//", StringComparison.Ordinal)
            || resultRoute.Length - remainder.Length != token.Length || remainder.Contains('{') || remainder.Contains('}')
            || remainder.Contains('?') || remainder.Contains('#') || remainder.Contains('\\') || remainder.Any(char.IsControl))
            throw ServiceImplementationPlan.Error("services.api.resultRouteInvalid", "The local result route requires exactly one {instanceId} parameter.");
        var operations = declaration.GetDefinition<ServiceDefinition>().Operations;
        if (operations.SingleOrDefault(item => item.Id == startOperationId) is not ServiceProcessOperation
            { Execution.Lifetime: ProcessExecutionLifetime.Durable, Execution.Completion: ServiceProcessCompletion.Admission } start
            || operations.SingleOrDefault(item => item.Id == resultOperationId) is not ServiceProcessResultOperation result
            || start.Process != result.Process)
            throw ServiceImplementationPlan.Error("services.api.resultSourceMismatch", "Select explicit durable admission and result operations for the same exact process.");
        var input = ServiceApiProjection.ProjectProcessInput<TRequest>(declaration, startOperationId,
            new("POST", startRoute, [], new(typeof(TRequest)))).Operation;
        Start = new(new ApiOperation(input.Name, input.Kind, input.RequestType, typeof(ServiceProcessReceipt),
            input.Http, input.Id, authorizationRequirements: input.AuthorizationRequirements,
            results: input.Results.Select(item => item.Kind == ApiResultKind.Accepted
                ? new ApiResultDefinition(ApiResultKind.Accepted, typeof(ServiceProcessReceipt), isPrimary: true,
                    description: "Durably admitted; poll the linked result independently. This does not promise completion.") : item).ToArray(),
            semanticReferences: input.SemanticReferences));
        var output = ServiceApiProjection.ProjectProcessResult<TResult>(declaration, resultOperationId,
            new("GET", resultRoute, [new("instanceId", HttpParameterSource.Route, typeof(string))], body: null)).Operation;
        Result = new(new ApiOperation(output.Name, output.Kind, output.RequestType, output.ResponseType,
            output.Http, output.Id, results: output.Results, authorizationRequirements: output.AuthorizationRequirements,
            semanticReferences: output.SemanticReferences, resultOf: Start.Id));
        Declaration = declaration;
        StartOperationId = startOperationId;
        ResultOperationId = resultOperationId;
        Definition = new ApiDefinition([Start, Result]);
    }
    /// <summary>Exact service authority; all its operations require runtime bindings.</summary>
    public ExecutionDefinitionDocument Declaration { get; }
    /// <summary>Selected admission operation, not inferred from the process identity.</summary>
    public string StartOperationId { get; }
    /// <summary>Selected independently authorized result operation.</summary>
    public string ResultOperationId { get; }
    /// <summary>Command with a primary 202 receipt response.</summary>
    public ApiEndpoint<TRequest, ServiceProcessReceipt> Start { get; }
    /// <summary>Read with terminal success, pending and failure alternatives.</summary>
    public ApiEndpoint<TResult> Result { get; }
    /// <summary>Both endpoint declarations, available for separate or partial application composition.</summary>
    public ApiDefinition Definition { get; }
}

/// <summary>Authoring of a durable admission and its linked polling contract.</summary>
public static class ServiceAcceptedProcessApiExtensions
{
    /// <summary>Projects two declared service operations without constructing any executor or storage.</summary>
    /// <typeparam name="TRequest">Command request.</typeparam><typeparam name="TResult">Terminal response view.</typeparam>
    /// <param name="service">Separate service declaration.</param><param name="startOperationId">Durable admission operation.</param>
    /// <param name="resultOperationId">Result operation referencing the same exact process.</param>
    /// <param name="startRoute">POST route.</param><param name="resultRoute">Local GET route with one {instanceId} parameter.</param>
    /// <returns>The linked API contract.</returns>
    /// <exception cref="ServiceBindingValidationException">Operation selection, exact process link, or result route is invalid.</exception>
    public static ServiceAcceptedProcessApi<TRequest, TResult> AcceptedApi<TRequest, TResult>(this ServiceBuilder service,
        string startOperationId, string resultOperationId, string startRoute, string resultRoute) where TRequest : class
    {
        ArgumentNullException.ThrowIfNull(service); ArgumentException.ThrowIfNullOrWhiteSpace(startRoute);
        ArgumentException.ThrowIfNullOrWhiteSpace(resultRoute);
        return new(service.Build(), startOperationId, resultOperationId, startRoute, resultRoute);
    }
}
