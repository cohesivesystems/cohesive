using Cohesive.Api;
using Cohesive.Api.Execution;
using Cohesive.Processes.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Cohesive.Adapters.AspNet.Processes;

public static partial class ProcessExecutionReadApiEndpointRouteBuilderExtensions
{
    /// <summary>Maps the canonical retained Process trace query as one read-only HTTP endpoint.</summary>
    /// <remarks>
    /// The HTTP projection accepts only logical Process identity from the route. It resolves authority and tenant
    /// address from trusted server-side context, reads the provider-neutral trace repository, and writes available
    /// artifacts through <see cref="ProcessExecutionTraceJsonSerializer"/> without reserialization.
    /// </remarks>
    /// <param name="endpoints">ASP.NET endpoint route builder to update.</param>
    /// <param name="endpoint">Canonical route-neutral <see cref="ExecutionControlApiCatalog.Traces"/> handle.</param>
    /// <param name="route">
    /// HTTP GET route containing a <c>{processInstanceId}</c> route parameter, optionally with ASP.NET constraints.
    /// </param>
    /// <param name="authorityScopeResolver">Server-side resolver for the trusted authority and tenant boundary.</param>
    /// <param name="authorizationPolicyResolver">
    /// Required projection from the catalog authorization requirement to an ASP.NET policy.
    /// </param>
    /// <param name="configure">Optional callback applied to the mapped endpoint.</param>
    /// <returns>The mapped route handler builder.</returns>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="route"/> is empty, malformed, or omits the conventional Process identity parameter.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="endpoint"/> is not the canonical retained-trace contract.
    /// </exception>
    public static RouteHandlerBuilder MapProcessExecutionTracesApi(
        this IEndpointRouteBuilder endpoints,
        ApiEndpoint endpoint,
        string route,
        ProcessExecutionAuthorityScopeResolver authorityScopeResolver,
        AspNetAuthorizationPolicyResolver authorizationPolicyResolver,
        Action<RouteHandlerBuilder, ApiOperation>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(route);
        ArgumentNullException.ThrowIfNull(authorityScopeResolver);
        ArgumentNullException.ThrowIfNull(authorizationPolicyResolver);

        EnsureCanonicalTraceContract(endpoint.Operation);
        EnsureProcessInstanceRoute(route);

        var projected = endpoint.WithHttp(new HttpBinding(
            method: HttpMethods.Get,
            route,
            parameters:
            [
                new(
                    ProcessInstanceIdRouteParameter,
                    HttpParameterSource.Route,
                    typeof(string))
            ],
            body: null));
        return endpoints.MapApiEndpoint(
            projected,
            CreateTraceHandler(projected.Operation, authorityScopeResolver),
            configure,
            authorizationPolicyResolver);
    }

    static Delegate CreateTraceHandler(
        ApiOperation operation,
        ProcessExecutionAuthorityScopeResolver authorityScopeResolver) =>
        async (
            OperationContext operationContext,
            HttpContext httpContext,
            IProcessExecutionTraceRepository repository) =>
        {
            ArgumentNullException.ThrowIfNull(operationContext);
            ArgumentNullException.ThrowIfNull(httpContext);
            ArgumentNullException.ThrowIfNull(repository);

            if (!TryReadProcessInstanceId(httpContext, out var processInstanceId))
            {
                return Problem(
                    operation,
                    ApiResultKind.ValidationFailed,
                    ExecutionApiProblemCodes.InvalidRequest);
            }

            var authorityScope = authorityScopeResolver(
                    operationContext,
                    httpContext,
                    processInstanceId)
                ?? throw new InvalidOperationException(
                    "The Process observation authority-scope resolver returned no trusted address.");
            var read = await repository.GetTracesAsync(
                    operationContext,
                    authorityScope,
                    processInstanceId)
                .ConfigureAwait(false);
            var resultKind = ExecutionControlApiCatalog.TraceResultKind(read.State);
            if (read.State != ProcessExecutionTraceReadState.Available)
            {
                return Problem(
                    operation,
                    resultKind,
                    ExecutionApiProblemCodes.ForTraceReadState(read.State));
            }

            var artifact = read.Artifact!;
            if (artifact.ProcessInstanceId != processInstanceId)
            {
                throw new InvalidOperationException(
                    "The Process trace repository returned evidence for another logical Process instance.");
            }

            var success = GetHttpResult(operation, resultKind);
            return new CanonicalProcessExecutionJsonResult(
                ProcessExecutionTraceJsonSerializer.GetCanonicalBytes(artifact),
                success.Http!.StatusCode,
                success.Http.ContentType ?? "application/json");
        };

    static void EnsureCanonicalTraceContract(ApiOperation operation)
    {
        if (!HasCanonicalObservationContract(
                operation,
                ProcessExecutionTraceWireNames.Read,
                typeof(ProcessExecutionTraceArtifact),
                ProcessExecutionTraceWireNames.SemanticAuthority,
                ProcessExecutionTraceArtifact.CurrentSchemaVersion,
                ProcessExecutionTraceWireNames.QueryPath)
            || !HasProblemResult(operation, ApiResultKind.Conflict)
            || !HasProblemResult(operation, ApiResultKind.PreconditionFailed)
            || !HasProblemResult(operation, ApiResultKind.Forbidden)
            || !HasProblemResult(operation, ApiResultKind.NotFound)
            || !HasProblemResult(operation, ApiResultKind.ValidationFailed))
        {
            throw new InvalidOperationException(
                $"API endpoint '{operation.Id}' is not the canonical execution-control retained-trace contract.");
        }
    }
}
