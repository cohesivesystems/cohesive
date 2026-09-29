using Cohesive.Adapters.AspNet.Relations;
using Cohesive.Relations.Execution;
using Cohesive.Relations.IR;
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

    /// <summary>Maps an independently authorized terminal Process value through a declaration-derived HTTP endpoint.</summary>
    /// <remarks>The runtime is resolved only on invocation and must realize the exact declaration.
    /// The projection receives an authorized, contract-validated value and must perform no reads or writes.
    /// Scope policies select the host scope; they do not grant access. No entity concurrency token is emitted.</remarks>
    /// <exception cref="ArgumentException">The declaration, operation or route binding is invalid.</exception>
    /// <exception cref="InvalidOperationException">The resolver returns a different service declaration.</exception>
    public static RouteHandlerBuilder MapServiceProcessResult<TResponse>(this IEndpointRouteBuilder endpoints,
        ExecutionDefinitionDocument declaration, Func<IServiceProvider, ServiceRuntime> resolveRuntime,
        string operationId, string route, Func<PortableValue, TResponse> project,
        AspNetAuthorizationPolicyResolver? authorizationPolicyResolver = null,
        string instanceParameter = "instanceId", IReadOnlyList<ApiScopePolicy>? scopePolicies = null)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(resolveRuntime);
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceParameter);
        var projection = ServiceApiProjection.ProjectProcessResult<TResponse>(declaration, operationId,
            new("GET", route, [new(instanceParameter, HttpParameterSource.Route, typeof(string))], body: null), scopePolicies);
        return MapResultCore(endpoints, declaration, projection, resolveRuntime,
            (runtime, context, instance) => runtime.ReadProcessResultAsync(context, operationId, instance),
            (_, value) => project(value), authorizationPolicyResolver, instanceParameter);
    }

    static RouteHandlerBuilder MapResult<TResponse>(IEndpointRouteBuilder endpoints,
        ExecutionDefinitionDocument declaration, ApiEndpoint projection, Func<IServiceProvider, ServiceRuntime> resolveRuntime,
        string operationId, Func<EntitySnapshot, TResponse> project, AspNetAuthorizationPolicyResolver? authorizationPolicyResolver,
        string instanceParameter, string tokenHeader)
        => MapResultCore(endpoints, declaration, projection, resolveRuntime,
            (runtime, context, instance) => runtime.ReadCommittedEntityAsync(context, operationId, instance),
            (http, snapshot) =>
            {
                http.Response.Headers[tokenHeader] = snapshot.ConcurrencyToken.Value;
                return project(snapshot);
            }, authorizationPolicyResolver, instanceParameter);

    static RouteHandlerBuilder MapResultCore<TValue, TResponse>(IEndpointRouteBuilder endpoints,
        ExecutionDefinitionDocument declaration, ApiEndpoint projection, Func<IServiceProvider, ServiceRuntime> resolveRuntime,
        Func<ServiceRuntime, OperationContext, ProcessInstanceId, ValueTask<ServiceOperationResult<TValue>>> read,
        Func<HttpContext, TValue, TResponse> project, AspNetAuthorizationPolicyResolver? authorizationPolicyResolver,
        string instanceParameter) where TValue : class
    {
        return endpoints.MapApiEndpoint(projection, async (OperationContext context, HttpContext http) =>
        {
            var instance = http.Request.RouteValues[instanceParameter]?.ToString();
            if (string.IsNullOrWhiteSpace(instance))
                return Results.BadRequest(new ApiValidationProblem("services.result.instanceRequired", "A Process instance is required.",
                    [new(instanceParameter, "services.result.instanceRequired", "Supply the logical Process instance identity.")]));
            var runtime = ResolveRuntime(declaration, resolveRuntime, http.RequestServices);
            var result = await read(runtime, context, new(instance)).ConfigureAwait(false);
            var status = projection.Operation.Results.Single(item => item.Kind == result.Kind).Http!.StatusCode;
            if (result.Kind == ApiResultKind.Success)
            {
                var value = result.Outcome ?? throw new InvalidOperationException("Successful result resolution returned no value.");
                return Results.Json(project(http, value), statusCode: status);
            }
            return ProjectServiceProblem(projection, result.Kind, result.Diagnostics);
        }, authorizationPolicyResolver: authorizationPolicyResolver).WithMetadata(declaration);
    }

    /// <summary>Projects a rejected or pending service outcome using its declared HTTP status and problem contract.</summary>
    /// <param name="endpoint">Canonical endpoint containing the result alternative.</param>
    /// <param name="kind">Rejected or pending service outcome.</param>
    /// <param name="diagnostics">Safe diagnostics produced by service admission or declared result classification.</param>
    /// <param name="resultId">Optional declared alternative when multiple results share the same kind.</param>
    /// <returns>A JSON problem with the declared status; validation retains all issue locations and codes.</returns>
    /// <exception cref="InvalidOperationException">The result is undeclared or does not use a standard API problem contract.</exception>
    public static IResult ProjectServiceProblem(ApiEndpoint endpoint, ApiResultKind kind,
        IEnumerable<DocumentValidationDiagnostic> diagnostics, string? resultId = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(diagnostics);
        var result = endpoint.Operation.Results.Single(candidate => candidate.Kind == kind && (resultId is null || candidate.Id == resultId));
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

    /// <summary>Maps a declared native start without resolving its runtime during endpoint registration.</summary>
    /// <remarks>The resolver must return the exact registered service. Native request validation and service
    /// authority admission run on invocation; supplied scope policies only select the host scope.</remarks>
    public static RouteHandlerBuilder MapServiceProcessStart(this IEndpointRouteBuilder endpoints,
        ExecutionDefinitionDocument declaration, Func<IServiceProvider, ServiceRuntime> resolveRuntime,
        string operationId, string route, AspNetAuthorizationPolicyResolver? authorizationPolicyResolver = null,
        IReadOnlyList<ApiScopePolicy>? scopePolicies = null) =>
        MapProcess<ProcessStartRequest, ProcessStartResult>(endpoints, declaration, resolveRuntime, operationId, route,
            (runtime, context, request) => runtime.StartAsync(context, operationId, request), authorizationPolicyResolver, scopePolicies);

    /// <summary>Maps a declared native lifecycle control with lazy, exact runtime resolution.</summary>
    /// <remarks>Native command contracts and authorization requirements derive from the service declaration.</remarks>
    public static RouteHandlerBuilder MapServiceProcessControl<TCommand>(this IEndpointRouteBuilder endpoints,
        ExecutionDefinitionDocument declaration, Func<IServiceProvider, ServiceRuntime> resolveRuntime,
        string operationId, string route, AspNetAuthorizationPolicyResolver? authorizationPolicyResolver = null,
        IReadOnlyList<ApiScopePolicy>? scopePolicies = null) where TCommand : ProcessControlCommand =>
        MapProcess<TCommand, ExecutionControlResult>(endpoints, declaration, resolveRuntime, operationId, route,
            (runtime, context, request) => runtime.ControlAsync(context, operationId, request), authorizationPolicyResolver, scopePolicies);

    /// <summary>Maps a terminal ephemeral Process operation using its inferred typed input and output.</summary>
    /// <remarks>Registration resolves no host. Successful completion returns the public Process output, never
    /// internal host evidence. Failures report possible prior effects without exposing receipts or private values.
    /// No background admission or mutation retry occurs. Request abort propagates; execution deadlines return an
    /// infrastructure problem after the host stops. The budget covers execution, not request parsing/admission.</remarks>
    public static RouteHandlerBuilder MapServiceEphemeralProcess<TInput, TOutput>(this IEndpointRouteBuilder endpoints,
        ExecutionDefinitionDocument declaration, Func<IServiceProvider, ServiceRuntime> resolveRuntime,
        string operationId, Cohesive.Processes.Authoring.Process<TInput, TOutput> process, HttpBinding http,
        AspNetAuthorizationPolicyResolver? authorizationPolicyResolver = null,
        IReadOnlyList<ApiScopePolicy>? scopePolicies = null)
    {
        return endpoints.MapServiceEphemeralProcess<TInput, TOutput, TInput, TOutput>(declaration, resolveRuntime,
            operationId, process, http, static (_, input) => input, static output => output,
            authorizationPolicyResolver, scopePolicies);
    }

    /// <summary>Maps an ephemeral Process through pure medium request and public response projections.</summary>
    /// <remarks>Registration resolves no runtime. Projections must perform no reads, writes or orchestration.
    /// The runtime validates the projected canonical input and owns authorization and bounded execution.
    /// Only successful public terminal output reaches the response projection; private execution evidence does not.
    /// Cancellation, deadline, and uncertain-effect behavior match the inferred-contract overload.</remarks>
    /// <param name="endpoints">Endpoint route builder.</param>
    /// <param name="declaration">Canonical service declaration.</param>
    /// <param name="resolveRuntime">Deferred request-time runtime resolver.</param>
    /// <param name="operationId">Declared ephemeral terminal operation.</param>
    /// <param name="process">Exact Process input and output authority.</param>
    /// <param name="http">Medium route and request body binding.</param>
    /// <param name="bind">Pure mapping of route and body fields into Process input.</param>
    /// <param name="project">Pure mapping of public Process output into the response body.</param>
    /// <param name="authorizationPolicyResolver">Optional native authorization policy resolver.</param>
    /// <param name="scopePolicies">Optional medium scope policies.</param>
    /// <returns>The mapped endpoint with its canonical declaration metadata.</returns>
    /// <exception cref="ArgumentException">The declaration, Process, or HTTP body is incompatible.</exception>
    /// <exception cref="BadHttpRequestException">Request parsing or input projection rejects transport fields.</exception>
    public static RouteHandlerBuilder MapServiceEphemeralProcess<TRequest, TResponse, TInput, TOutput>(
        this IEndpointRouteBuilder endpoints, ExecutionDefinitionDocument declaration,
        Func<IServiceProvider, ServiceRuntime> resolveRuntime, string operationId,
        Cohesive.Processes.Authoring.Process<TInput, TOutput> process, HttpBinding http,
        Func<HttpContext, TRequest, TInput> bind, Func<TOutput, TResponse> project,
        AspNetAuthorizationPolicyResolver? authorizationPolicyResolver = null,
        IReadOnlyList<ApiScopePolicy>? scopePolicies = null)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(resolveRuntime);
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(bind);
        ArgumentNullException.ThrowIfNull(project);
        var projection = ServiceApiProjection.ProjectEphemeralProcess<TRequest, TResponse, TInput, TOutput>(
            declaration, operationId, process, http, scopePolicies);
        return endpoints.MapApiEndpoint(projection, async (OperationContext context, HttpContext request) =>
        {
            var body = await HttpRequestBindingSupport.ReadOperationBodyAsync(request, projection.Operation, context.CancellationToken).ConfigureAwait(false);
            var input = bind(request, (TRequest)body!);
            var runtime = ResolveRuntime(declaration, resolveRuntime, request.RequestServices);
            try
            {
                var result = await runtime.ExecuteProcessAsync(context, operationId,
                    new(new($"http/{request.TraceIdentifier}"), new("attempt/1")), ObservationValue.FromObject(input)).ConfigureAwait(false);
                if (result.Kind == ApiResultKind.Success)
                {
                    var value = result.Outcome?.Decision.State.Terminal.Detail?.Value?.Value
                        ?? throw new InvalidOperationException("Completed Process returned no public value.");
                    return Results.Json(project(value.Deserialize<TOutput>(OutcomeJson, ObservationBytesJsonEncoding.Base64String)!),
                        statusCode: projection.Operation.Results.Single(item => item.Kind == ApiResultKind.Success).Http!.StatusCode);
                }
                if (result.Kind == ApiResultKind.DomainError)
                    return Uncertain(ApiResultKind.DomainError);
                return ProjectServiceProblem(projection, result.Kind, result.Diagnostics);
            }
            catch (Cohesive.Processes.Execution.EphemeralProcessInterruptedException) when (!context.CancellationToken.IsCancellationRequested)
            {
                return Uncertain(ApiResultKind.InfrastructureError);
            }
            catch (Cohesive.Processes.Execution.EphemeralProcessExecutionException)
            {
                return Uncertain(ApiResultKind.InfrastructureError);
            }

            IResult Uncertain(ApiResultKind kind) => ProjectServiceProblem(projection, kind,
                [new("services.process.incomplete", DiagnosticSeverity.Error,
                    "The operation did not complete successfully. Earlier changes may have committed; do not automatically retry.")]);
        }, authorizationPolicyResolver: authorizationPolicyResolver).WithMetadata(declaration);
    }

    /// <summary>Maps typed domain input and caller-owned retry identities to a declared Process start.</summary>
    /// <remarks>The synchronous projection must only bind request data, without reads or writes. It must preserve
    /// retry identities and input across retries. Throw BadHttpRequestException for malformed transport fields.
    /// The runtime owns authority, exact Process selection, portable input validation and native admission.</remarks>
    public static RouteHandlerBuilder MapServiceProcessInput<TRequest>(this IEndpointRouteBuilder endpoints,
        ExecutionDefinitionDocument declaration, Func<IServiceProvider, ServiceRuntime> resolveRuntime,
        string operationId, HttpBinding http,
        Func<HttpContext, TRequest, (ProcessControlCommandId Command, ProcessControlIdempotencyKey Idempotency,
            ProcessContinuationIdentity Continuation, ObservationValue Input)> bind,
        AspNetAuthorizationPolicyResolver? authorizationPolicyResolver = null,
        IReadOnlyList<ApiScopePolicy>? scopePolicies = null) where TRequest : class
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(resolveRuntime);
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(bind);
        var projection = ServiceApiProjection.ProjectProcessInput<TRequest>(declaration, operationId, http, scopePolicies);
        return endpoints.MapApiEndpoint(projection, async (OperationContext context, HttpContext requestHttp) =>
        {
            var request = await ProcessApiRequestSupport.ReadRequestAsync<TRequest>(requestHttp, projection.Operation,
                context.CancellationToken).ConfigureAwait(false)
                ?? throw new BadHttpRequestException("A Process command body is required.");
            var input = bind(requestHttp, request);
            var runtime = ResolveRuntime(declaration, resolveRuntime, requestHttp.RequestServices);
            var result = await runtime.StartAsync(context, operationId, input.Command, input.Idempotency,
                input.Continuation, input.Input).ConfigureAwait(false);
            return ProjectProcessOutcome(projection, result);
        }, authorizationPolicyResolver: authorizationPolicyResolver).WithMetadata(declaration);
    }

    /// <summary>Maps a Process command with an independently authorized committed-entity response.</summary>
    /// <remarks>Request and response delegates are pure medium projections. The native runtime owns admission,
    /// bounded waiting, result authorization and receipt resolution. A successful start does not imply completion.
    /// Pending responses retain the caller's logical instance identity in Location. Cancellation and unexpected
    /// failures propagate without retrying admission. Registration resolves no runtime or repository.</remarks>
    /// <exception cref="ArgumentException">The declarations, body, or result route are incompatible.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The optional wait is not a positive finite timer duration.</exception>
    public static RouteHandlerBuilder MapServiceProcessEntityCommand<TRequest, TResponse, TPending>(
        this IEndpointRouteBuilder endpoints, ExecutionDefinitionDocument declaration,
        Func<IServiceProvider, ServiceRuntime> resolveRuntime, string startOperationId, string resultOperationId,
        HttpBinding http, string resultRoute,
        Func<HttpContext, TRequest, (ProcessControlCommandId Command, ProcessControlIdempotencyKey Idempotency,
            ProcessContinuationIdentity Continuation, ObservationValue Input)> bind,
        Func<EntitySnapshot, TResponse> project, Func<ProcessStartResult, string, TPending> projectPending,
        TimeSpan? maximumWait = null, AspNetAuthorizationPolicyResolver? authorizationPolicyResolver = null,
        IReadOnlyList<ApiScopePolicy>? scopePolicies = null) where TRequest : class
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(resolveRuntime);
        ArgumentNullException.ThrowIfNull(bind);
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(projectPending);
        ArgumentException.ThrowIfNullOrWhiteSpace(resultRoute);
        const string instanceToken = "{instanceId}";
        var routeWithoutInstance = resultRoute.Replace(instanceToken, "", StringComparison.Ordinal);
        if (!resultRoute.StartsWith('/') || resultRoute.StartsWith("//", StringComparison.Ordinal)
            || resultRoute.Length - routeWithoutInstance.Length != instanceToken.Length
            || routeWithoutInstance.Contains('{') || routeWithoutInstance.Contains('}'))
            throw new ArgumentException("The local result route must contain exactly one {instanceId} parameter.", nameof(resultRoute));
        if (maximumWait is { } duration && (duration <= TimeSpan.Zero || duration.TotalMilliseconds > uint.MaxValue - 1))
            throw new ArgumentOutOfRangeException(nameof(maximumWait), "A positive finite timer duration is required.");
        var projection = ServiceApiProjection.ProjectProcessEntityCommand<TRequest, TResponse, TPending>(
            declaration, startOperationId, resultOperationId, http, scopePolicies);
        return endpoints.MapApiEndpoint(projection, async (OperationContext context, HttpContext requestHttp) =>
        {
            var request = await ProcessApiRequestSupport.ReadRequestAsync<TRequest>(requestHttp, projection.Operation,
                context.CancellationToken).ConfigureAwait(false)
                ?? throw new BadHttpRequestException("A Process command body is required.");
            var input = bind(requestHttp, request);
            var runtime = ResolveRuntime(declaration, resolveRuntime, requestHttp.RequestServices);
            var start = await runtime.StartAsync(context, startOperationId, input.Command, input.Idempotency,
                input.Continuation, input.Input).ConfigureAwait(false);
            if (start.Kind != ApiResultKind.Success)
                return ProjectServiceProblem(projection, start.Kind, start.Diagnostics);
            var instance = input.Continuation.ProcessInstanceId;
            var result = await runtime.ReadCommittedEntityAsync(context, resultOperationId, instance, maximumWait).ConfigureAwait(false);
            if (result.Kind == ApiResultKind.Success)
            {
                var snapshot = result.Outcome ?? throw new InvalidOperationException("Successful result resolution returned no entity.");
                requestHttp.Response.Headers["X-Concurrency-Token"] = snapshot.ConcurrencyToken.Value;
                return Results.Json(project(snapshot), statusCode: projection.Operation.Results.Single(item => item.Kind == ApiResultKind.Success).Http!.StatusCode);
            }
            if (result.Kind != ApiResultKind.Accepted)
                return ProjectServiceProblem(projection, result.Kind, result.Diagnostics);
            var location = requestHttp.Request.PathBase + resultRoute.Replace(instanceToken, Uri.EscapeDataString(instance.Value), StringComparison.Ordinal);
            requestHttp.Response.Headers.Location = location;
            requestHttp.Response.Headers.RetryAfter = "1";
            return Results.Json(projectPending(start.Outcome ?? throw new InvalidOperationException("Successful start returned no admission result."), location),
                statusCode: projection.Operation.Results.Single(item => item.Kind == ApiResultKind.Accepted).Http!.StatusCode);
        }, authorizationPolicyResolver: authorizationPolicyResolver).WithMetadata(declaration);
    }

    static RouteHandlerBuilder MapProcess<TRequest, TOutcome>(IEndpointRouteBuilder endpoints,
        ServiceRuntime runtime, string operationId, string route,
        Func<OperationContext, TRequest, ValueTask<ServiceOperationResult<TOutcome>>> invoke,
        AspNetAuthorizationPolicyResolver? authorizationPolicyResolver)
        where TRequest : class where TOutcome : class
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentException.ThrowIfNullOrWhiteSpace(route);
        return MapProcess<TRequest, TOutcome>(endpoints, runtime.Declaration, _ => runtime, operationId, route,
            (_, context, request) => invoke(context, request), authorizationPolicyResolver);
    }

    static RouteHandlerBuilder MapProcess<TRequest, TOutcome>(IEndpointRouteBuilder endpoints,
        ExecutionDefinitionDocument declaration, Func<IServiceProvider, ServiceRuntime> resolveRuntime,
        string operationId, string route,
        Func<ServiceRuntime, OperationContext, TRequest, ValueTask<ServiceOperationResult<TOutcome>>> invoke,
        AspNetAuthorizationPolicyResolver? authorizationPolicyResolver, IReadOnlyList<ApiScopePolicy>? scopePolicies = null)
        where TRequest : class where TOutcome : class
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(resolveRuntime);
        ArgumentException.ThrowIfNullOrWhiteSpace(route);
        var projection = ServiceApiProjection.ProjectProcess<TRequest>(declaration, operationId,
            new("POST", route, [], new(typeof(TRequest))), scopePolicies);
        return MapProcessCore<TRequest, TOutcome>(endpoints, declaration, projection, resolveRuntime,
            (runtime, context, request, _) => invoke(runtime, context, request), authorizationPolicyResolver);
    }

    static RouteHandlerBuilder MapProcessCore<TRequest, TOutcome>(IEndpointRouteBuilder endpoints,
        ExecutionDefinitionDocument declaration, ApiEndpoint projection, Func<IServiceProvider, ServiceRuntime> resolveRuntime,
        Func<ServiceRuntime, OperationContext, TRequest, HttpContext, ValueTask<ServiceOperationResult<TOutcome>>> invoke,
        AspNetAuthorizationPolicyResolver? authorizationPolicyResolver) where TRequest : class where TOutcome : class
    {
        return endpoints.MapApiEndpoint(projection, async (OperationContext context, HttpContext http) =>
        {
            var request = await ProcessApiRequestSupport.ReadRequestAsync<TRequest>(http, projection.Operation,
                context.CancellationToken).ConfigureAwait(false)
                ?? throw new BadHttpRequestException("A native Process request body is required.");
            var runtime = ResolveRuntime(declaration, resolveRuntime, http.RequestServices);
            var result = await invoke(runtime, context, request, http).ConfigureAwait(false);
            return ProjectProcessOutcome(projection, result);
        }, authorizationPolicyResolver: authorizationPolicyResolver).WithMetadata(declaration);
    }

    static IResult ProjectProcessOutcome<TOutcome>(ApiEndpoint projection, ServiceOperationResult<TOutcome> result)
        where TOutcome : class
    {
        object response = result.Outcome is { } outcome
            ? outcome
            : new ExecutionApiProblem(result.Diagnostics.FirstOrDefault()?.Code ?? "services.invocation.failed");
        var projectedResult = ProcessExecutionCommandApiEndpointRouteBuilderExtensions.GetProjectedResult(
            projection.Operation, result.Kind, response.GetType());
        return Results.Json(response, options: null,
            contentType: projectedResult.Http!.ContentType ?? "application/json",
            statusCode: projectedResult.Http.StatusCode);
    }

    /// <summary>Maps an authorized declared query without resolving its evaluator during registration.</summary>
    /// <remarks>Both projections are synchronous and must perform no reads or writes. The input binder supplies
    /// caller parameters only; the runtime injects the declared trusted scope. The response projection receives
    /// the complete native outcome for success and evaluation failure, preserving diagnostics at the medium seam.
    /// Evaluation identity follows the existing relation-query HTTP convention. Provider exceptions and cancellation propagate.</remarks>
    public static RouteHandlerBuilder MapServiceQuery<TRequest, TResponse>(this IEndpointRouteBuilder endpoints,
        ExecutionDefinitionDocument declaration, Func<IServiceProvider, ServiceRuntime> resolveRuntime,
        string operationId, HttpBinding http, Func<TRequest, IReadOnlyDictionary<QueryParameterId, ObservationValue>> bind,
        Func<RelationQueryEvaluationOutcome, TResponse> project,
        AspNetAuthorizationPolicyResolver? authorizationPolicyResolver = null,
        IReadOnlyList<ApiScopePolicy>? scopePolicies = null) where TRequest : class
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(resolveRuntime);
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(bind);
        ArgumentNullException.ThrowIfNull(project);
        var projection = ServiceApiProjection.ProjectQuery<TRequest, TResponse>(declaration, operationId, http, scopePolicies);
        return endpoints.MapApiEndpoint(projection, async (OperationContext context, HttpContext requestHttp) =>
        {
            var request = await ProcessApiRequestSupport.ReadRequestAsync<TRequest>(requestHttp, projection.Operation,
                context.CancellationToken).ConfigureAwait(false)
                ?? throw new BadHttpRequestException("A query request is required.");
            var runtime = ResolveRuntime(declaration, resolveRuntime, requestHttp.RequestServices);
            var result = await runtime.EvaluateAsync(context, operationId,
                RelationQueryApiEndpointOptions.CreateConventionalEvaluationId(requestHttp, projection.Operation), bind(request)).ConfigureAwait(false);
            if (result.Outcome is { } outcome)
            {
                var alternative = projection.Operation.Results.Single(item => outcome.IsSuccessful
                    ? item.IsPrimary : item.Id == "queryEvaluationFailed");
                return Results.Json(project(outcome), statusCode: alternative.Http!.StatusCode);
            }
            return ProjectServiceProblem(projection, result.Kind, result.Diagnostics,
                result.Kind == ApiResultKind.ValidationFailed ? "admissionValidationFailed" : null);
        }, authorizationPolicyResolver: authorizationPolicyResolver).WithMetadata(declaration);
    }

    static ServiceRuntime ResolveRuntime(ExecutionDefinitionDocument declaration,
        Func<IServiceProvider, ServiceRuntime> resolveRuntime, IServiceProvider services)
    {
        var runtime = resolveRuntime(services)
            ?? throw new InvalidOperationException("The service runtime resolver returned null.");
        if (runtime.Declaration.Metadata.DefinitionId != declaration.Metadata.DefinitionId
            || runtime.Declaration.Metadata.RevisionId != declaration.Metadata.RevisionId
            || runtime.Declaration.Metadata.Fingerprint != declaration.Metadata.Fingerprint)
            throw new InvalidOperationException("The resolved runtime does not realize the registered service declaration.");
        return runtime;
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
