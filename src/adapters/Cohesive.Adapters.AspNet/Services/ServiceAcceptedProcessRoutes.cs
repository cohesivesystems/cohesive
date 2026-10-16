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
    /// <summary>Maps durable admission and its independently authorized polling endpoint as one linked contract.</summary>
    /// <remarks>No worker is started here. Cancellation or ambiguous admission is not retried. The caller must retain
    /// command, idempotency, continuation and input across retries. Pending reads never dispatch the process.</remarks>
    /// <typeparam name="TRequest">Command body.</typeparam><typeparam name="TResult">Terminal result view.</typeparam>
    /// <param name="routes">Native route owner.</param><param name="api">Exact linked contract.</param>
    /// <param name="runtime">Complete runtime for the declared service, backed by its native execution providers.</param>
    /// <param name="bind">Pure projection of caller-owned intent and input, never authority.</param>
    /// <param name="project">Pure terminal value projection, invoked only after result authorization and validation.</param>
    /// <param name="authorizationPolicyResolver">Optional native policy mapping.</param>
    /// <exception cref="ServiceBindingValidationException">The runtime does not realize this exact service.</exception>
    public static void MapServiceAcceptedProcess<TRequest, TResult>(this IEndpointRouteBuilder routes,
        ServiceAcceptedProcessApi<TRequest, TResult> api, ServiceRuntime runtime,
        Func<HttpContext, TRequest, (ProcessControlCommandId Command, ProcessControlIdempotencyKey Idempotency,
            ProcessContinuationIdentity Continuation, ObservationValue Input)> bind,
        Func<PortableValue, TResult> project,
        AspNetAuthorizationPolicyResolver? authorizationPolicyResolver = null) where TRequest : class
    {
        ArgumentNullException.ThrowIfNull(routes); ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(runtime); ArgumentNullException.ThrowIfNull(bind); ArgumentNullException.ThrowIfNull(project);
        if (runtime.Declaration.Reference != api.Declaration.Reference)
            throw ServiceImplementationPlan.Error("services.api.processMismatch", "The linked endpoints require the exact declared service runtime.");
        routes.MapApiEndpoint(api.Start, async (OperationContext context, HttpContext http) =>
        {
            var request = await ProcessApiRequestSupport.ReadRequestAsync<TRequest>(http, api.Start.Operation,
                context.CancellationToken).ConfigureAwait(false)
                ?? throw new BadHttpRequestException("A Process command body is required.");
            var intent = bind(http, request);
            var outcome = await runtime.StartAsync(context, api.StartOperationId, intent.Command, intent.Idempotency,
                intent.Continuation, intent.Input).ConfigureAwait(false);
            if (outcome.Kind != ApiResultKind.Accepted) return ProjectProcessOutcome(api.Start, outcome);
            var admission = outcome.Outcome ?? throw new InvalidOperationException("Accepted start requires admission evidence.");
            var instance = admission.Admission!.Continuation.ProcessInstanceId.Value;
            var location = http.Request.PathBase + api.Result.Operation.Http!.Route.Replace("{instanceId}",
                WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(instance)), StringComparison.Ordinal);
            http.Response.Headers.Location = location;
            http.Response.Headers.RetryAfter = "1";
            return Results.Json(new ServiceProcessReceipt(admission, location), statusCode: StatusCodes.Status202Accepted);
        }, authorizationPolicyResolver: authorizationPolicyResolver).WithMetadata(api.Declaration);
        MapResultCore(routes, api.Declaration, api.Result, _ => runtime,
            (service, context, instance) => service.ReadProcessResultAsync(context, api.ResultOperationId, DecodeInstance(instance.Value)),
            (_, value) => project(value), authorizationPolicyResolver, "instanceId");
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
