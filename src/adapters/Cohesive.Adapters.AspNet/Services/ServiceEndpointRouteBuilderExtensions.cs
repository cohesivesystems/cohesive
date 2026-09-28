using System.Text.Json;
using System.Text.Json.Serialization;
using Cohesive.Api;
using Cohesive.Api.Execution;
using Cohesive.Adapters.AspNet.Processes;
using Cohesive.Api.Execution.Services;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Cohesive.Adapters.AspNet.Services;

/// <summary>HTTP projection of shared service invocation; domain loading and authority remain runtime-owned.</summary>
public static class ServiceEndpointRouteBuilderExtensions
{
    static readonly JsonSerializerOptions OutcomeJson = CreateJsonOptions();

    /// <summary>Maps a declared Transition using a route subject, opaque expected-token header and typed body.</summary>
    /// <remarks>
    /// The body is the declared Transition input. The token header carries the repository token verbatim, avoiding
    /// HTTP entity-tag quoting conventions. The response returns the typed Transition outcome, not entity contents.
    /// The existing API metadata and optional native authorization policies remain attached to the route.
    /// </remarks>
    /// <exception cref="ArgumentException">The CLR projection or route inputs are invalid.</exception>
    /// <exception cref="InvalidOperationException">Required native policy associations are missing.</exception>
    public static RouteHandlerBuilder MapServiceTransition<TInput, TOutcome>(this IEndpointRouteBuilder endpoints,
        ServiceRuntime runtime, string operationId, string route,
        AspNetAuthorizationPolicyResolver? authorizationPolicyResolver = null,
        string subjectParameter = "id", string tokenHeader = "X-Expected-Concurrency-Token")
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectParameter);
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenHeader);
        var projection = runtime.Project<TInput, TOutcome>(operationId, new("POST", route,
            [new(subjectParameter, HttpParameterSource.Route, typeof(string)),
             new(tokenHeader, HttpParameterSource.Header, typeof(string))], new(typeof(TInput))));
        var contract = runtime.InputContract(operationId);
        return endpoints.MapApiEndpoint(projection, async (OperationContext context, HttpContext http) =>
        {
            var subject = http.Request.RouteValues[subjectParameter]?.ToString();
            var tokens = http.Request.Headers[tokenHeader];
            if (string.IsNullOrWhiteSpace(subject) || tokens.Count != 1 || string.IsNullOrWhiteSpace(tokens[0]))
                return Results.BadRequest();
            var body = await HttpRequestBindingSupport.ReadOperationBodyAsync(http, projection.Operation, context.CancellationToken)
                .ConfigureAwait(false);
            var observed = ObservationValue.FromObject(body);
            var input = observed.Kind switch
            {
                ObservationValueKind.Null => PortableValue.Null(contract),
                ObservationValueKind.Undefined => PortableValue.Absent(contract),
                _ => PortableValue.Concrete(contract, observed)
            };
            var result = await runtime.InvokeAsync(context, operationId, subject, new(tokens[0]!),
                new($"aspnet/request/{Uri.EscapeDataString(http.TraceIdentifier)}/operation/{Uri.EscapeDataString(projection.Id.Value)}"), input)
                .ConfigureAwait(false);
            if (result.ConcurrencyToken is { } nextToken)
                http.Response.Headers[tokenHeader] = nextToken.Value;
            var status = projection.Operation.Results.Single(candidate => candidate.Kind == result.Kind).Http!.StatusCode;
            if (result.Kind is ApiResultKind.Success or ApiResultKind.DomainError)
                return Results.Json(result.Outcome?.Value is { } value
                    ? value.Deserialize<TOutcome>(OutcomeJson, ObservationBytesJsonEncoding.Base64String)
                    : default, statusCode: status);
            if (result.Kind == ApiResultKind.ValidationFailed)
                return Results.Json(new ApiValidationProblem("services.validation.failed", "Invocation validation failed.",
                    result.Diagnostics.Select(d => new ApiValidationIssue(d.Location, d.Code, d.Message)).ToArray()), statusCode: status);
            var diagnostic = result.Diagnostics.FirstOrDefault();
            return Results.Json(new ApiProblem(diagnostic?.Code ?? "services.invocation.failed",
                diagnostic?.Message ?? "Invocation failed.", diagnostic?.Location), statusCode: status);
        }, authorizationPolicyResolver: authorizationPolicyResolver).WithMetadata(runtime.Declaration);
    }

    /// <summary>Maps a declared committed-entity result read through shared authorization and receipt resolution.</summary>
    /// <typeparam name="TResponse">Public response contract attached at the HTTP boundary.</typeparam>
    /// <param name="endpoints">Native endpoint builder.</param>
    /// <param name="runtime">Prepared service runtime.</param>
    /// <param name="operationId">Declared result-read operation.</param>
    /// <param name="route">GET route containing the logical Process instance parameter.</param>
    /// <param name="project">Pure synchronous response projection of the authorized exact snapshot; no reads or writes.</param>
    /// <param name="authorizationPolicyResolver">Optional native policy associations.</param>
    /// <param name="instanceParameter">Route parameter naming the logical Process instance.</param>
    /// <param name="tokenHeader">Response header retaining the original opaque concurrency token verbatim.</param>
    /// <returns>The native route builder with API and service metadata attached.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">A declaration or medium binding is invalid.</exception>
    public static RouteHandlerBuilder MapServiceProcessEntityResult<TResponse>(this IEndpointRouteBuilder endpoints,
        ServiceRuntime runtime, string operationId, string route, Func<EntitySnapshot, TResponse> project,
        AspNetAuthorizationPolicyResolver? authorizationPolicyResolver = null,
        string instanceParameter = "instanceId", string tokenHeader = "X-Concurrency-Token")
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceParameter);
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenHeader);
        var projection = runtime.ProjectCommittedEntityResult<TResponse>(operationId, new("GET", route,
            [new(instanceParameter, HttpParameterSource.Route, typeof(string))], body: null));
        return MapResult(endpoints, runtime.Declaration, projection, _ => runtime, operationId, project,
            authorizationPolicyResolver, instanceParameter, tokenHeader);
    }

    /// <summary>Registers a declaration-derived result endpoint, resolving its exact runtime only on invocation.</summary>
    /// <remarks>The resolver must return the runtime for the same service identity, revision and fingerprint.
    /// Repository and dispatcher construction are deferred; declaration validation remains registration-time work.
    /// Supplied scope policies are preserved in API and endpoint metadata for host scope binding.</remarks>
    /// <exception cref="ArgumentException">The declaration, result operation or route parameters are invalid.</exception>
    /// <exception cref="InvalidOperationException">Invocation resolves a runtime for a different service declaration.</exception>
    public static RouteHandlerBuilder MapServiceProcessEntityResult<TResponse>(this IEndpointRouteBuilder endpoints,
        ExecutionDefinitionDocument declaration, Func<IServiceProvider, ServiceRuntime> resolveRuntime,
        string operationId, string route, Func<EntitySnapshot, TResponse> project,
        AspNetAuthorizationPolicyResolver? authorizationPolicyResolver = null,
        string instanceParameter = "instanceId", string tokenHeader = "X-Concurrency-Token",
        IReadOnlyList<ApiScopePolicy>? scopePolicies = null)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(resolveRuntime);
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceParameter);
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenHeader);
        var projection = ServiceApiProjection.ProjectCommittedEntityResult<TResponse>(declaration, operationId,
            new("GET", route, [new(instanceParameter, HttpParameterSource.Route, typeof(string))], body: null), scopePolicies);
        return MapResult(endpoints, declaration, projection, resolveRuntime, operationId, project,
            authorizationPolicyResolver, instanceParameter, tokenHeader);
    }

    static RouteHandlerBuilder MapResult<TResponse>(IEndpointRouteBuilder endpoints,
        ExecutionDefinitionDocument declaration, ApiEndpoint projection, Func<IServiceProvider, ServiceRuntime> resolveRuntime,
        string operationId, Func<EntitySnapshot, TResponse> project, AspNetAuthorizationPolicyResolver? authorizationPolicyResolver,
        string instanceParameter, string tokenHeader)
    {
        return endpoints.MapApiEndpoint(projection, async (OperationContext context, HttpContext http) =>
        {
            var instance = http.Request.RouteValues[instanceParameter]?.ToString();
            if (string.IsNullOrWhiteSpace(instance))
                return Results.BadRequest(new ApiValidationProblem("services.result.instanceRequired", "A Process instance is required.",
                    [new(instanceParameter, "services.result.instanceRequired", "Supply the logical Process instance identity.")]));
            var runtime = resolveRuntime(http.RequestServices)
                ?? throw new InvalidOperationException("The result runtime resolver returned null.");
            if (runtime.Declaration.Metadata.DefinitionId != declaration.Metadata.DefinitionId
                || runtime.Declaration.Metadata.RevisionId != declaration.Metadata.RevisionId
                || runtime.Declaration.Metadata.Fingerprint != declaration.Metadata.Fingerprint)
                throw new InvalidOperationException("The resolved runtime does not realize the registered service declaration.");
            var result = await runtime.ReadCommittedEntityAsync(context, operationId, new(instance)).ConfigureAwait(false);
            var status = projection.Operation.Results.Single(item => item.Kind == result.Kind).Http!.StatusCode;
            if (result.Kind == ApiResultKind.Success)
            {
                var snapshot = result.Outcome ?? throw new InvalidOperationException("Successful receipt resolution returned no snapshot.");
                http.Response.Headers[tokenHeader] = snapshot.ConcurrencyToken.Value;
                return Results.Json(project(snapshot), statusCode: status);
            }
            return ProjectServiceProblem(projection, result.Kind, result.Diagnostics);
        }, authorizationPolicyResolver: authorizationPolicyResolver).WithMetadata(declaration);
    }

    /// <summary>Projects a rejected or pending service outcome using its declared HTTP status and problem contract.</summary>
    /// <param name="endpoint">Canonical endpoint containing the result alternative.</param>
    /// <param name="kind">Rejected or pending service outcome.</param>
    /// <param name="diagnostics">Safe diagnostics produced by service admission or declared result classification.</param>
    /// <returns>A JSON problem with the declared status; validation retains all issue locations and codes.</returns>
    /// <exception cref="InvalidOperationException">The result is undeclared or does not use a standard API problem contract.</exception>
    public static IResult ProjectServiceProblem(ApiEndpoint endpoint, ApiResultKind kind,
        IEnumerable<DocumentValidationDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(diagnostics);
        var result = endpoint.Operation.Results.Single(candidate => candidate.Kind == kind);
        var status = result.Http?.StatusCode
            ?? throw new InvalidOperationException("A service problem requires a declared HTTP status.");
        var issues = diagnostics.ToArray();
        var first = issues.FirstOrDefault();
        var code = first?.Code ?? "services.result.unavailable";
        var message = first?.Message ?? "The operation result is unavailable.";
        object problem;
        if (result.BodyType == typeof(ApiValidationProblem))
            problem = new ApiValidationProblem(code, message,
                issues.Select(item => new ApiValidationIssue(item.Location, item.Code, item.Message)).ToArray());
        else if (result.BodyType == typeof(ApiConflictProblem))
            problem = new ApiConflictProblem(code, message);
        else if (result.BodyType == typeof(ApiProblem))
            problem = new ApiProblem(code, message, first?.Location);
        else
            throw new InvalidOperationException("The declared result is not a standard API problem contract.");
        return Results.Json(problem, statusCode: status);
    }

    /// <summary>Maps a declared Process entry using its native start request and admission result.</summary>
    /// <remarks>Authority, exact definition and portable input admission remain in the shared service runtime.</remarks>
    /// <exception cref="ArgumentException">The operation or route is invalid.</exception>
    public static RouteHandlerBuilder MapServiceProcessStart(this IEndpointRouteBuilder endpoints,
        ServiceRuntime runtime, string operationId, string route,
        AspNetAuthorizationPolicyResolver? authorizationPolicyResolver = null) =>
        MapProcess<ProcessStartRequest, ProcessStartResult>(endpoints, runtime, operationId, route,
            (context, request) => runtime.StartAsync(context, operationId, request), authorizationPolicyResolver);

    /// <summary>Maps a declared lifecycle control with its exact native command type and safe native result.</summary>
    /// <typeparam name="TCommand">Native command type required by the declaration, such as PauseProcessCommand.</typeparam>
    /// <remarks>Request type mismatch fails mapping; canonical control and replay remain dispatcher-owned.</remarks>
    /// <exception cref="ArgumentException">The request type, operation or route is invalid.</exception>
    public static RouteHandlerBuilder MapServiceProcessControl<TCommand>(this IEndpointRouteBuilder endpoints,
        ServiceRuntime runtime, string operationId, string route,
        AspNetAuthorizationPolicyResolver? authorizationPolicyResolver = null)
        where TCommand : ProcessControlCommand =>
        MapProcess<TCommand, ExecutionControlResult>(endpoints, runtime, operationId, route,
            (context, request) => runtime.ControlAsync(context, operationId, request), authorizationPolicyResolver);

    static RouteHandlerBuilder MapProcess<TRequest, TOutcome>(IEndpointRouteBuilder endpoints,
        ServiceRuntime runtime, string operationId, string route,
        Func<OperationContext, TRequest, ValueTask<ServiceOperationResult<TOutcome>>> invoke,
        AspNetAuthorizationPolicyResolver? authorizationPolicyResolver)
        where TRequest : class where TOutcome : class
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentException.ThrowIfNullOrWhiteSpace(route);
        var projection = runtime.ProjectProcess<TRequest>(operationId, new("POST", route, [], new(typeof(TRequest))));
        return endpoints.MapApiEndpoint(projection, async (OperationContext context, HttpContext http) =>
        {
            var request = await ProcessApiRequestSupport.ReadRequestAsync<TRequest>(http, projection.Operation,
                context.CancellationToken).ConfigureAwait(false)
                ?? throw new BadHttpRequestException("A native Process request body is required.");
            var result = await invoke(context, request).ConfigureAwait(false);
            object response = result.Outcome is { } outcome
                ? outcome
                : new ExecutionApiProblem(result.Diagnostics.FirstOrDefault()?.Code ?? "services.invocation.failed");
            var projectedResult = ProcessExecutionCommandApiEndpointRouteBuilderExtensions.GetProjectedResult(
                projection.Operation, result.Kind, response.GetType());
            return Results.Json(response, options: null,
                contentType: projectedResult.Http!.ContentType ?? "application/json",
                statusCode: projectedResult.Http.StatusCode);
        }, authorizationPolicyResolver: authorizationPolicyResolver).WithMetadata(runtime.Declaration);
    }

    static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        options.Converters.Add(new ObservationValueJsonConverter(ObservationBytesJsonEncoding.Base64String));
        options.Converters.Add(new JsonStringEnumConverter());
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
