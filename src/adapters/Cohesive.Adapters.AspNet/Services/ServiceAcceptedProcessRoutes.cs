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
    /// <typeparam name="TReceipt">Public admission receipt.</typeparam>
    public static void MapServiceAcceptedProcess<TRequest, TResult, TReceipt>(this IEndpointRouteBuilder routes,
        ServiceAcceptedProcessApi<TRequest, TResult, TReceipt> api, ServiceRuntime runtime,
        Func<HttpContext, TRequest, (ProcessControlCommandId Command, ProcessControlIdempotencyKey Idempotency,
            ProcessContinuationIdentity Continuation, ObservationValue Input)> bind,
        Func<PortableValue, TResult> project,
        AspNetAuthorizationPolicyResolver? authorizationPolicyResolver = null) where TRequest : class
    {
        ArgumentNullException.ThrowIfNull(routes); ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(runtime); ArgumentNullException.ThrowIfNull(bind); ArgumentNullException.ThrowIfNull(project);
        if (runtime.Declaration.Reference != api.Declaration.Reference)
            throw ServiceImplementationPlan.Error("services.api.processMismatch", "The linked endpoints require the exact declared service runtime.");
        if (api.Response.MaximumWait is not null) runtime.RequireProcessResultWaiting(api.ResultOperationId);
        routes.MapApiEndpoint(api.Start, async (OperationContext context, HttpContext http) =>
        {
            (ProcessControlCommandId Command, ProcessControlIdempotencyKey Idempotency, ProcessContinuationIdentity Continuation, ObservationValue Input) intent;
            TRequest? request = null;
            try
            {
                request = await ProcessApiRequestSupport.ReadRequestAsync<TRequest>(http, api.Start.Operation, context.CancellationToken)
                    .ConfigureAwait(false) ?? throw new BadHttpRequestException("A Process command body is required.");
                intent = bind(http, request);
            }
            catch (Exception error) when (error is BadHttpRequestException { StatusCode: 400 }
                || request is null && error is System.Text.Json.JsonException)
            {
                if (Submission(ApiResultKind.ValidationFailed) is { } invalid) return invalid;
                if (error is System.Text.Json.JsonException) throw new BadHttpRequestException("Invalid process command JSON.", error);
                throw;
            }
            var outcome = await runtime.StartAsync(context, api.StartOperationId, intent.Command, intent.Idempotency,
                intent.Continuation, intent.Input).ConfigureAwait(false);
            if (outcome.Kind != ApiResultKind.Accepted) return Submission(outcome.Kind) ?? ProjectProcessOutcome(api.Start, outcome);
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
                    return ProjectRead(result) ?? ProjectServiceProblem(api.Result, result.Kind, result.Diagnostics);
            }
            http.Response.Headers.RetryAfter = "1";
            return Submission(ApiResultKind.Accepted, new(token, location)) ?? Results.Json(new ServiceProcessReceipt(admission, location), statusCode: StatusCodes.Status202Accepted);
        }, authorizationPolicyResolver: authorizationPolicyResolver).WithMetadata(api.Declaration);
        MapResultCore(routes, api.Declaration, api.Result, _ => runtime,
            (service, context, instance) => service.ReadProcessResultAsync(context, api.ResultOperationId, instance),
            (_, value) => project(value), authorizationPolicyResolver, "instanceId", ProjectRead,
            DecodeInstance, () => api.TryProjectMissing(out var missing) ? Render(missing) : null);

        IResult? Submission(ApiResultKind kind, AcceptedProcessLocation? location = null) =>
            api.TryProjectSubmission(kind, out var response, location) ? Render(response) : null;

        IResult? ProjectRead(ServiceOperationResult<PortableValue> result) =>
            api.TryProjectRead(result, out var response) ? Render(response) : null;

        static IResult Render(AcceptedProcessResponse response) => response.BodyType == typeof(void)
            ? Results.StatusCode(response.Status) : Results.Json(response.Body, statusCode: response.Status);
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
