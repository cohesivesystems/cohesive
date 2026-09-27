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
