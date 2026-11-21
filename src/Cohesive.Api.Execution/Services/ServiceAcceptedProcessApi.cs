using System.Collections.Frozen;
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
/// <typeparam name="TReceipt">Public admission receipt.</typeparam>
public class ServiceAcceptedProcessApi<TRequest, TResult, TReceipt> : IApiDeclaration where TRequest : class
{
    internal ServiceAcceptedProcessApi(ExecutionDefinitionDocument declaration, string startOperationId,
        string resultOperationId, string startRoute, string resultRoute, ServiceProcessResponse response, AcceptedProcessResponses<TReceipt> outcomes)
    {
        Response = response;
        Outcomes = outcomes.Snapshot();
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
        Outcomes.Bind(input, output);
        var sharedAdmissionProblems = new Dictionary<ApiResultKind, ApiResultDefinition>();
        // A bounded POST can return a rejection from either admission or its result read.
        // Use the standard read problem for shared kinds before applying public overrides.
        var alternatives = input.Results.Select(item =>
        {
            var body = item.Kind == ApiResultKind.Accepted ? typeof(TReceipt) : item.BodyType;
            if (response.MaximumWait is not null && output.Results.SingleOrDefault(read => read.Kind == item.Kind) is { } shared
                && item.Kind != ApiResultKind.Accepted)
                body = shared.BodyType;
            var admission = Outcomes.Apply(AcceptedProcessRoute.Submit,
                new(item.Kind, body, item.IsPrimary, item.Id, item.Description, item.Http));
            if (body != item.BodyType && item.Kind != ApiResultKind.Accepted && Outcomes.Find(AcceptedProcessRoute.Submit, item.Kind) is null)
                sharedAdmissionProblems.Add(item.Kind, admission);
            return admission;
        }).ToList();
        AdmissionProblems = sharedAdmissionProblems.ToFrozenDictionary();
        var reads = output.Results.Select(item => Outcomes.Apply(AcceptedProcessRoute.Poll, item)).ToList();
        if (response.MaximumWait is not null)
            alternatives.AddRange(reads.Where(item => item.Kind != ApiResultKind.Accepted)
                .Select(item => new ApiResultDefinition(item.Kind, item.BodyType, id: "result-" + item.Id,
                    description: item.Description, http: item.Http)));
        Start = new(new ApiOperation(input.Name, input.Kind, input.RequestType, alternatives.Single(item => item.IsPrimary).BodyType,
            input.Http, input.Id, authorizationRequirements: input.AuthorizationRequirements,
            results: alternatives, semanticReferences: input.SemanticReferences));
        Result = new(new ApiOperation(output.Name, output.Kind, output.RequestType, output.ResponseType,
            output.Http, output.Id, results: reads, authorizationRequirements: output.AuthorizationRequirements,
            semanticReferences: output.SemanticReferences, resultOf: Start.Id));
        RequireOneBodyPerStatus(Start.Operation);
        RequireOneBodyPerStatus(Result.Operation);
        Declaration = declaration;
        StartOperationId = startOperationId;
        ResultOperationId = resultOperationId;
        Definition = new ApiDefinition([Start, Result]);
    }
    static void RequireOneBodyPerStatus(ApiOperation operation)
    {
        foreach (var group in operation.Results.GroupBy(result => result.Http!.StatusCode))
        {
            if (!group.Select(result => result.BodyType).Distinct().Skip(1).Any()) continue;
            var alternatives = string.Join(", ", group.Select(result => $"{result.Id} ({result.BodyType.Name})"));
            throw ServiceImplementationPlan.Error("services.api.responseBodyConflict",
                $"{operation.Http!.Method} {operation.Http.Route} declares different bodies for HTTP {group.Key}: {alternatives}.");
        }
    }

    /// <summary>Default admission problem contracts shared with reads on a bounded-wait POST, indexed before route composition.</summary>
    public IReadOnlyDictionary<ApiResultKind, ApiResultDefinition> AdmissionProblems { get; }

    /// <summary>HTTP response timing, independent of the execution provider lifetime.</summary>
    public ServiceProcessResponse Response { get; }
    /// <summary>HTTP projections frozen at API construction.</summary>
    AcceptedProcessResponses<TReceipt> Outcomes { get; }

    /// <summary>Materializes the configured outcome using the same mapping as the endpoint declaration.</summary>
    public bool TryProjectSubmission(ApiResultKind kind, out AcceptedProcessResponse response, AcceptedProcessLocation? receipt = null) =>
        Outcomes.TryProject(AcceptedProcessRoute.Submit, kind, out response, receipt);

    /// <summary>Projects a classified read outcome, including verified terminal failures.</summary>
    public bool TryProjectRead<T>(ServiceOperationResult<T> result, out AcceptedProcessResponse response) where T : class =>
        Outcomes.TryProject(AcceptedProcessRoute.Poll, result.Kind, out response);

    /// <summary>Projects a malformed polling token as a missing instance.</summary>
    public bool TryProjectMissing(out AcceptedProcessResponse response) => Outcomes.TryProject(AcceptedProcessRoute.Poll, ApiResultKind.NotFound, out response);

    /// <summary>Exact service authority; all its operations require runtime bindings.</summary>
    public ExecutionDefinitionDocument Declaration { get; }
    /// <summary>Selected admission operation, not inferred from the process identity.</summary>
    public string StartOperationId { get; }
    /// <summary>Selected independently authorized result operation.</summary>
    public string ResultOperationId { get; }
    /// <summary>Command with a primary 202 receipt response.</summary>
    public ApiEndpoint<TRequest, TReceipt> Start { get; }
    /// <summary>Read with terminal success, pending and failure alternatives.</summary>
    public ApiEndpoint<TResult> Result { get; }
    /// <summary>Both endpoint declarations, available for separate or partial application composition.</summary>
    public ApiDefinition Definition { get; }
}

/// <summary>Authoring of admission and its linked polling contract.</summary>
public static class ServiceAcceptedProcessApiExtensions
{
    /// <summary>Declares and registers a linked process start/result pair in the owning API builder.</summary>
    /// <typeparam name="TRequest">Command request.</typeparam><typeparam name="TResult">Terminal response view.</typeparam>
    /// <param name="endpoints">Owning API builder.</param><param name="service">Separate service authority.</param>
    /// <param name="startOperationId">Admission operation.</param><param name="resultOperationId">Linked result operation.</param>
    /// <param name="startRoute">POST route.</param><param name="resultRoute">GET route with one {instanceId} parameter.</param>
    /// <param name="response">Independent response policy; defaults to immediate acknowledgement.</param>
    /// <returns>The typed pair; both endpoints are already registered without reflection or member discovery.</returns>
    /// <exception cref="ServiceBindingValidationException">The pair is invalid.</exception>
    /// <exception cref="InvalidOperationException">The owner is frozen or either endpoint already exists; neither is added.</exception>
    public static ServiceAcceptedProcessApi<TRequest, TResult, ServiceProcessReceipt> AcceptedProcess<TRequest, TResult>(this ApiBuilder endpoints,
        ServiceBuilder service, string startOperationId, string resultOperationId, string startRoute, string resultRoute,
        ServiceProcessResponse? response = null) where TRequest : class
        => endpoints.AcceptedProcess<TRequest, TResult, ServiceProcessReceipt>(service, startOperationId,
            resultOperationId, startRoute, resultRoute, new(), response);

    /// <summary>Projects two declared service operations without constructing any executor or storage.</summary>
    /// <typeparam name="TRequest">Command request.</typeparam><typeparam name="TResult">Terminal response view.</typeparam>
    /// <param name="service">Separate service declaration.</param><param name="startOperationId">Admission operation.</param>
    /// <param name="resultOperationId">Result operation referencing the same exact process.</param>
    /// <param name="startRoute">POST route.</param><param name="resultRoute">Local GET route with one {instanceId} parameter.</param>
    /// <param name="response">Acknowledge immediately by default, or wait boundedly while retaining the polling fallback.</param>
    /// <returns>The linked API contract.</returns>
    /// <exception cref="ServiceBindingValidationException">Operation selection, exact process link, or result route is invalid.</exception>
    public static ServiceAcceptedProcessApi<TRequest, TResult, ServiceProcessReceipt> AcceptedApi<TRequest, TResult>(this ServiceBuilder service,
        string startOperationId, string resultOperationId, string startRoute, string resultRoute, ServiceProcessResponse? response = null) where TRequest : class
        => service.AcceptedApi<TRequest, TResult, ServiceProcessReceipt>(startOperationId, resultOperationId,
            startRoute, resultRoute, new(), response);
    /// <summary>Declares and registers a linked process start/result pair in the owning API builder.</summary>
    /// <typeparam name="TRequest">Command request.</typeparam><typeparam name="TResult">Terminal response view.</typeparam>
    /// <param name="endpoints">Owning API builder.</param><param name="service">Separate service authority.</param>
    /// <param name="startOperationId">Admission operation.</param><param name="resultOperationId">Linked result operation.</param>
    /// <param name="startRoute">POST route.</param><param name="resultRoute">GET route with one {instanceId} parameter.</param>
    /// <param name="outcomes">Optional typed HTTP outcome mappings, captured at declaration.</param>
    /// <param name="response">Independent response policy; defaults to immediate acknowledgement.</param>
    /// <returns>The typed pair; both endpoints are already registered without reflection or member discovery.</returns>
    /// <exception cref="ServiceBindingValidationException">The pair is invalid.</exception>
    /// <exception cref="InvalidOperationException">The owner is frozen or either endpoint already exists; neither is added.</exception>
    /// <typeparam name="TReceipt">Public admission receipt.</typeparam>
    public static ServiceAcceptedProcessApi<TRequest, TResult, TReceipt> AcceptedProcess<TRequest, TResult, TReceipt>(this ApiBuilder endpoints,
        ServiceBuilder service, string startOperationId, string resultOperationId, string startRoute, string resultRoute,
        AcceptedProcessResponses<TReceipt> outcomes, ServiceProcessResponse? response = null) where TRequest : class
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var pair = service.AcceptedApi<TRequest, TResult, TReceipt>(startOperationId, resultOperationId, startRoute, resultRoute, outcomes, response);
        endpoints.Include(pair.Definition);
        return pair;
    }

    /// <summary>Projects two declared service operations without constructing any executor or storage.</summary>
    /// <typeparam name="TRequest">Command request.</typeparam><typeparam name="TResult">Terminal response view.</typeparam>
    /// <param name="service">Separate service declaration.</param><param name="startOperationId">Admission operation.</param>
    /// <param name="resultOperationId">Result operation referencing the same exact process.</param>
    /// <param name="startRoute">POST route.</param><param name="resultRoute">Local GET route with one {instanceId} parameter.</param>
    /// <param name="outcomes">Optional typed HTTP outcome mappings, captured at declaration.</param>
    /// <param name="response">Acknowledge immediately by default, or wait boundedly while retaining the polling fallback.</param>
    /// <returns>The linked API contract.</returns>
    /// <exception cref="ServiceBindingValidationException">Operation selection, exact process link, or result route is invalid.</exception>
    /// <typeparam name="TReceipt">Public admission receipt.</typeparam>
    public static ServiceAcceptedProcessApi<TRequest, TResult, TReceipt> AcceptedApi<TRequest, TResult, TReceipt>(this ServiceBuilder service,
        string startOperationId, string resultOperationId, string startRoute, string resultRoute, AcceptedProcessResponses<TReceipt> outcomes, ServiceProcessResponse? response = null) where TRequest : class
    {
        ArgumentNullException.ThrowIfNull(outcomes);
        ArgumentNullException.ThrowIfNull(service); ArgumentException.ThrowIfNullOrWhiteSpace(startRoute);
        ArgumentException.ThrowIfNullOrWhiteSpace(resultRoute);
        return new(service.Build(), startOperationId, resultOperationId, startRoute, resultRoute, response ?? ServiceProcessResponse.Acknowledge, outcomes);
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
