using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using Cohesive.Api;
using Cohesive.Api.Execution.Services;
using Cohesive.Adapters.AspNet.Processes;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Cohesive.Adapters.AspNet.Services;

public static partial class ServiceEndpointRouteBuilderExtensions
{
    /// <summary>Maps the native receipt and standard polling responses.</summary>
    public static void MapServiceAcceptedProcess<TRequest, TResult>(this IEndpointRouteBuilder routes,
        ServiceAcceptedProcessApi<TRequest, TResult> api, ServiceRuntime runtime,
        Func<HttpContext, TRequest, (ProcessControlCommandId Command, ProcessControlIdempotencyKey Idempotency,
            ProcessContinuationIdentity Continuation, ObservationValue Input)> bind,
        Func<PortableValue, TResult> project,
        AspNetAuthorizationPolicyResolver? authorizationPolicyResolver = null) where TRequest : class =>
        MapServiceAcceptedProcess(routes, api, runtime, bind, project, authorizationPolicyResolver, null);

    /// <summary>Maps admission and its independently authorized polling endpoint as one linked contract.</summary>
    /// <remarks>No worker is started here. Cancellation or ambiguous admission is not retried. The caller must retain
    /// command, idempotency, continuation and input across retries. Pending reads never dispatch the process.</remarks>
    /// <typeparam name="TRequest">Command body.</typeparam><typeparam name="TResult">Terminal result view.</typeparam>
    /// <param name="routes">Native route owner.</param><param name="api">Exact linked contract.</param>
    /// <param name="runtime">Complete runtime for the declared service, backed by its native execution providers.</param>
    /// <param name="bind">Pure projection of caller-owned intent and input, never authority.</param>
    /// <param name="project">Pure terminal value projection, invoked only after result authorization and validation.</param>
    /// <param name="authorizationPolicyResolver">Optional native policy mapping.</param>
    /// <exception cref="ServiceBindingValidationException">The runtime does not realize this exact service.</exception>
    /// <param name="projectReceipt">Optional public receipt projection with the exact opaque route token and local polling URL.</param>
    /// <param name="projectFailure">Optional response for verified terminal failures on polling or a bounded start wait.</param>
    public static void MapServiceAcceptedProcess<TRequest, TResult>(this IEndpointRouteBuilder routes,
        ServiceAcceptedProcessApi<TRequest, TResult> api, ServiceRuntime runtime,
        Func<HttpContext, TRequest, (ProcessControlCommandId Command, ProcessControlIdempotencyKey Idempotency,
            ProcessContinuationIdentity Continuation, ObservationValue Input)> bind,
        Func<PortableValue, TResult> project,
        AspNetAuthorizationPolicyResolver? authorizationPolicyResolver,
        Func<AcceptedProcessHttpReceipt, IResult>? projectReceipt,
        Func<ServiceProcessFailureKind, IResult>? projectFailure = null) where TRequest : class
    {
        ArgumentNullException.ThrowIfNull(routes); ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(runtime); ArgumentNullException.ThrowIfNull(bind); ArgumentNullException.ThrowIfNull(project);
        if (runtime.Declaration.Reference != api.Declaration.Reference)
            throw ServiceImplementationPlan.Error("services.api.processMismatch", "The linked endpoints require the exact declared service runtime.");
        if (api.Response.MaximumWait is not null) runtime.RequireProcessResultWaiting(api.ResultOperationId);
        routes.MapApiEndpoint(api.Start, async (OperationContext context, HttpContext http) =>
        {
            TRequest request;
            try
            {
                request = await ProcessApiRequestSupport.ReadRequestAsync<TRequest>(http, api.Start.Operation,
                    context.CancellationToken).ConfigureAwait(false)
                    ?? throw new BadHttpRequestException("A Process command body is required.");
            }
            catch (Exception error) when (error is System.Text.Json.JsonException or BadHttpRequestException { StatusCode: 400 })
            {
                if (Map(true, ApiResultKind.ValidationFailed) is { } invalid) return invalid;
                throw;
            }
            (ProcessControlCommandId Command, ProcessControlIdempotencyKey Idempotency, ProcessContinuationIdentity Continuation, ObservationValue Input) intent;
            try { intent = bind(http, request); }
            catch (BadHttpRequestException error) when (error.StatusCode == 400)
            {
                if (Map(true, ApiResultKind.ValidationFailed) is { } invalid) return invalid;
                throw;
            }
            var outcome = await runtime.StartAsync(context, api.StartOperationId, intent.Command, intent.Idempotency,
                intent.Continuation, intent.Input).ConfigureAwait(false);
            if (outcome.Kind != ApiResultKind.Accepted) return Map(true, outcome.Kind) ?? ProjectProcessOutcome(api.Start, outcome);
            var admission = outcome.Outcome ?? throw new InvalidOperationException("Accepted start requires admission evidence.");
            var instance = admission.Admission!.Continuation.ProcessInstanceId.Value;
            var token = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(instance));
            var location = http.Request.PathBase + api.Result.Operation.Http!.Route.Replace("{instanceId}",
                token, StringComparison.Ordinal);
            http.Response.Headers.Location = location;
            if (api.Response.MaximumWait is { } maximumWait)
            {
                var result = await runtime.ReadProcessResultAsync(context, api.ResultOperationId, new(instance), maximumWait).ConfigureAwait(false);
                if (result.Kind == ApiResultKind.Success)
                    return Results.Json(project(result.Outcome!), statusCode: api.Result.Operation.PrimaryResult.Http!.StatusCode);
                if (result.Kind != ApiResultKind.Accepted)
                    return ProjectRead(result.Kind, result.Diagnostics) ?? DefaultRead(result.Kind, result.Diagnostics);
            }
            http.Response.Headers.RetryAfter = "1";
            return Map(true, ApiResultKind.Accepted, new(token, location)) ?? projectReceipt?.Invoke(new(admission, token, location)) ?? Results.Json(new ServiceProcessReceipt(admission, location), statusCode: StatusCodes.Status202Accepted);
        }, authorizationPolicyResolver: authorizationPolicyResolver).WithMetadata(api.Declaration);
        MapResultCore(routes, api.Declaration, api.Result, _ => runtime,
            (service, context, instance) => service.ReadProcessResultAsync(context, api.ResultOperationId, instance),
            (_, value) => project(value), authorizationPolicyResolver, "instanceId", ProjectRead,
            DecodeInstance, () => Map(false, ApiResultKind.NotFound));

        IResult? Map(bool start, ApiResultKind kind, AcceptedProcessLocation? receipt = null, bool failed = false)
        {
            if (!api.TryProject(start, kind, out var response, receipt, failed)) return null;
            return response.BodyType == typeof(void) ? Results.StatusCode(response.Status)
                : Results.Json(response.Body, statusCode: response.Status);
        }

        IResult DefaultRead(ApiResultKind kind, IReadOnlyList<DocumentValidationDiagnostic> diagnostics) =>
            ProjectServiceProblem(api.Result, kind, diagnostics, api.Result.Operation.Results.First(item => item.Kind == kind).Id);

        IResult? ProjectRead(ApiResultKind kind, IReadOnlyList<DocumentValidationDiagnostic> diagnostics)
        {
            if (diagnostics.FirstOrDefault()?.Code is "services.process.engineFailed" or "services.process.notCompleted")
                return Map(false, kind, failed: true) ?? ProjectFailure(diagnostics);
            return Map(false, kind);
        }

        IResult? ProjectFailure(IReadOnlyList<DocumentValidationDiagnostic> diagnostics) =>
            diagnostics.FirstOrDefault()?.Code switch
            {
                "services.process.engineFailed" => projectFailure?.Invoke(ServiceProcessFailureKind.EngineFailed),
                "services.process.notCompleted" => projectFailure?.Invoke(ServiceProcessFailureKind.UnsuccessfulOutcome),
                _ => null
            };
    }

    // Route segments use an opaque reversible token: ASP.NET deliberately leaves encoded slashes escaped.
    // Percent-decoding a second time would instead corrupt identities that contain a literal percent escape.
    static readonly UTF8Encoding InstanceEncoding = new(false, true);

    static ProcessInstanceId DecodeInstance(string token)
    {
        try
        {
            var bytes = WebEncoders.Base64UrlDecode(token);
            if (WebEncoders.Base64UrlEncode(bytes) != token) throw new FormatException();
            var instance = InstanceEncoding.GetString(bytes);
            if (string.IsNullOrWhiteSpace(instance)) throw new FormatException();
            return new(instance);
        }
        catch (Exception exception) when (exception is FormatException or DecoderFallbackException)
        {
            throw new BadHttpRequestException("The process instance token is invalid.", exception);
        }
    }
}


/// <summary>Verified terminal classifications exposed to accepted-process HTTP projections.</summary>
public enum ServiceProcessFailureKind
{
    /// <summary>The canonical process retained an unsuccessful terminal outcome.</summary>
    UnsuccessfulOutcome,
    /// <summary>The provider retained a failed or terminated execution without a canonical terminal result.</summary>
    EngineFailed
}

/// <summary>Accepted admission with its exact opaque route segment and PathBase-aware polling address.</summary>
/// <param name="Admission">Native accepted or replayed start.</param>
/// <param name="ResultToken">Opaque route parameter that identifies the retained result.</param>
/// <param name="ResultLocation">Local polling URL also emitted in the Location header.</param>
public sealed record AcceptedProcessHttpReceipt(ProcessStartResult Admission, string ResultToken, string ResultLocation);
