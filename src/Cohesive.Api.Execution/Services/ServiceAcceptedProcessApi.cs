using Cohesive.Api.Services;
using Cohesive.Execution;

namespace Cohesive.Api.Execution.Services;

/// <summary>A public admission receipt and the local address of its independently authorized result read.</summary>
/// <param name="Admission">Native accepted or replayed admission; never terminal execution evidence.</param>
/// <param name="ResultLocation">Local polling address for the admitted instance.</param>
public sealed record ServiceProcessReceipt(ProcessStartResult Admission, string ResultLocation);

/// <summary>A linked command/result API projected from two operations of one exact service declaration.</summary>
/// <remarks>Contains no executor. A native admission dispatcher and protected value repository must realize the
/// service. The result relationship remains visible on Result.Operation.ResultOf after API composition.</remarks>
/// <typeparam name="TRequest">Medium command input, including caller-owned retry identities.</typeparam>
/// <typeparam name="TResult">Public terminal result view.</typeparam>
public sealed class ServiceAcceptedProcessApi<TRequest, TResult> : IApiDeclaration where TRequest : class
{
    internal ServiceAcceptedProcessApi(ExecutionDefinitionDocument declaration, string startOperationId,
        string resultOperationId, string startRoute, string resultRoute, ServiceProcessResponse response)
    {
        Response = response;
        const string token = "{instanceId}";
        var remainder = resultRoute.Replace(token, "", StringComparison.Ordinal);
        if (!resultRoute.StartsWith('/') || resultRoute.StartsWith("//", StringComparison.Ordinal)
            || resultRoute.Length - remainder.Length != token.Length || remainder.Contains('{') || remainder.Contains('}')
            || remainder.Contains('?') || remainder.Contains('#') || remainder.Contains('\\') || remainder.Any(char.IsControl))
            throw ServiceImplementationPlan.Error("services.api.resultRouteInvalid", "The local result route requires exactly one {instanceId} parameter.");
        var operations = declaration.GetDefinition<ServiceDefinition>().Operations;
        if (operations.SingleOrDefault(item => item.Id == startOperationId) is not ServiceProcessOperation
            { Execution.Completion: ServiceProcessCompletion.Admission } start
            || operations.SingleOrDefault(item => item.Id == resultOperationId) is not ServiceProcessResultOperation result
            || start.Process != result.Process)
            throw ServiceImplementationPlan.Error("services.api.resultSourceMismatch", "Select explicit admission and result operations for the same exact process.");
        var input = ServiceApiProjection.ProjectProcessInput<TRequest>(declaration, startOperationId,
            new("POST", startRoute, [], new(typeof(TRequest)))).Operation;
        var output = ServiceApiProjection.ProjectProcessResult<TResult>(declaration, resultOperationId,
            new("GET", resultRoute, [new("instanceId", HttpParameterSource.Route, typeof(string))], body: null)).Operation;
        var alternatives = input.Results.Select(item => item.Kind == ApiResultKind.Accepted
            ? new ApiResultDefinition(ApiResultKind.Accepted, typeof(ServiceProcessReceipt), isPrimary: true,
                description: "Admitted to the declared execution lifetime; poll the linked result independently.") : item).ToList();
        if (response.MaximumWait is not null)
            alternatives.AddRange(output.Results.Where(item => item.Kind != ApiResultKind.Accepted)
                .Select(item => new ApiResultDefinition(item.Kind, item.BodyType, id: "result-" + item.Id,
                    description: item.Description, http: item.Http)));
        Start = new(new ApiOperation(input.Name, input.Kind, input.RequestType, typeof(ServiceProcessReceipt),
            input.Http, input.Id, authorizationRequirements: input.AuthorizationRequirements,
            results: alternatives, semanticReferences: input.SemanticReferences));
        Result = new(new ApiOperation(output.Name, output.Kind, output.RequestType, output.ResponseType,
            output.Http, output.Id, results: output.Results, authorizationRequirements: output.AuthorizationRequirements,
            semanticReferences: output.SemanticReferences, resultOf: Start.Id));
        Declaration = declaration;
        StartOperationId = startOperationId;
        ResultOperationId = resultOperationId;
        Definition = new ApiDefinition([Start, Result]);
    }
    /// <summary>HTTP response timing, independent of the execution provider lifetime.</summary>
    public ServiceProcessResponse Response { get; }
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

/// <summary>Authoring of admission and its linked polling contract.</summary>
public static class ServiceAcceptedProcessApiExtensions
{
    /// <summary>Projects two declared service operations without constructing any executor or storage.</summary>
    /// <typeparam name="TRequest">Command request.</typeparam><typeparam name="TResult">Terminal response view.</typeparam>
    /// <param name="service">Separate service declaration.</param><param name="startOperationId">Admission operation.</param>
    /// <param name="resultOperationId">Result operation referencing the same exact process.</param>
    /// <param name="startRoute">POST route.</param><param name="resultRoute">Local GET route with one {instanceId} parameter.</param>
    /// <param name="response">Acknowledge immediately by default, or wait boundedly while retaining the polling fallback.</param>
    /// <returns>The linked API contract.</returns>
    /// <exception cref="ServiceBindingValidationException">Operation selection, exact process link, or result route is invalid.</exception>
    public static ServiceAcceptedProcessApi<TRequest, TResult> AcceptedApi<TRequest, TResult>(this ServiceBuilder service,
        string startOperationId, string resultOperationId, string startRoute, string resultRoute, ServiceProcessResponse? response = null) where TRequest : class
    {
        ArgumentNullException.ThrowIfNull(service); ArgumentException.ThrowIfNullOrWhiteSpace(startRoute);
        ArgumentException.ThrowIfNullOrWhiteSpace(resultRoute);
        return new(service.Build(), startOperationId, resultOperationId, startRoute, resultRoute, response ?? ServiceProcessResponse.Acknowledge);
    }
}

/// <summary>Response timing for a linked process API; never selects durability or execution cancellation.</summary>
public sealed class ServiceProcessResponse
{
    ServiceProcessResponse(TimeSpan? maximumWait) => MaximumWait = maximumWait;
    /// <summary>Return the admission receipt without reading a result.</summary>
    public static ServiceProcessResponse Acknowledge { get; } = new(null);
    /// <summary>Maximum HTTP result wait; null means acknowledge immediately.</summary>
    public TimeSpan? MaximumWait { get; }
    /// <summary>Wait for a result, falling back to a receipt and polling link when the budget expires.</summary>
    /// <param name="maximumWait">Positive finite response budget; expiration does not cancel execution.</param>
    /// <returns>An immutable API response policy.</returns>
    public static ServiceProcessResponse WaitForResult(TimeSpan maximumWait)
    {
        if (maximumWait <= TimeSpan.Zero || maximumWait.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(maximumWait));
        return new(maximumWait);
    }
}
