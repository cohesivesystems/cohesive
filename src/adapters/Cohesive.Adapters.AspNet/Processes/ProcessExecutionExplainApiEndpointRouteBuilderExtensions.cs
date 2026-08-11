using Cohesive.Api;
using Cohesive.Api.Execution;
using Cohesive.Execution;
using Cohesive.Processes.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Cohesive.Adapters.AspNet.Processes;

/// <summary>Resolves the trusted authority address for one HTTP Process observation read.</summary>
/// <remarks>
/// Implementations derive this value from authenticated server-side identity, authorization, and scope evidence.
/// They must not accept authority or tenant values from an untrusted request payload.
/// </remarks>
/// <param name="operationContext">Current Cohesive operation context with server-resolved identity evidence.</param>
/// <param name="httpContext">Current authorized ASP.NET request.</param>
/// <param name="processInstanceId">Logical Process identity projected from the declared route value.</param>
/// <returns>The exact trusted authority and optional tenant isolating the Process execution.</returns>
public delegate InteractionAuthorityScope ProcessExecutionAuthorityScopeResolver(
    OperationContext operationContext,
    HttpContext httpContext,
    ProcessInstanceId processInstanceId);

/// <summary>ASP.NET bindings for canonical Process execution observations.</summary>
public static partial class ProcessExecutionReadApiEndpointRouteBuilderExtensions
{
    /// <summary>Conventional route parameter that carries the logical Process instance identity.</summary>
    public const string ProcessInstanceIdRouteParameter = "processInstanceId";

    /// <summary>
    /// Maps the canonical execution-control explain operation as one read-only HTTP endpoint.
    /// </summary>
    /// <remarks>
    /// The HTTP projection accepts only the logical Process identity from the route. The canonical
    /// <see cref="InspectProcessCommand"/> remains the transport-neutral request contract, but its authorization,
    /// issuance, and provenance fields are deliberately not accepted from HTTP callers. The supplied
    /// <paramref name="authorityScopeResolver"/> establishes the trusted repository address instead.
    /// </remarks>
    /// <param name="endpoints">ASP.NET endpoint route builder to update.</param>
    /// <param name="endpoint">
    /// Canonical route-neutral <see cref="ExecutionControlApiCatalog.Explain"/> endpoint handle.
    /// </param>
    /// <param name="route">
    /// HTTP GET route containing a <c>{processInstanceId}</c> route parameter, optionally with ASP.NET route
    /// constraints.
    /// </param>
    /// <param name="authorityScopeResolver">
    /// Server-side resolver for the trusted authority and optional tenant boundary.
    /// </param>
    /// <param name="authorizationPolicyResolver">
    /// Required projection from the catalog's semantic authorization requirement to an ASP.NET policy.
    /// </param>
    /// <param name="configure">Optional callback applied to the mapped endpoint.</param>
    /// <returns>The mapped route handler builder.</returns>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="route"/> is empty, malformed, or does not contain the conventional Process identity
    /// parameter.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="endpoint"/> is not the canonical execution explain contract.
    /// </exception>
    public static RouteHandlerBuilder MapProcessExecutionExplainApi(
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

        EnsureCanonicalExplainContract(endpoint.Operation);
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
            CreateExplainHandler(projected.Operation, authorityScopeResolver),
            configure,
            authorizationPolicyResolver);
    }

    static Delegate CreateExplainHandler(
        ApiOperation operation,
        ProcessExecutionAuthorityScopeResolver authorityScopeResolver) =>
        async (
            OperationContext operationContext,
            HttpContext httpContext,
            IProcessExecutionExplainRepository repository) =>
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
            var artifact = await repository.GetExplainAsync(
                    operationContext,
                    authorityScope,
                    processInstanceId)
                .ConfigureAwait(false);
            if (artifact is null)
            {
                return Problem(
                    operation,
                    ApiResultKind.NotFound,
                    ExecutionApiProblemCodes.NotFound);
            }

            EnsureArtifactAffinity(artifact, processInstanceId);
            var success = GetHttpResult(operation, ApiResultKind.Success);
            return new CanonicalProcessExecutionJsonResult(
                ExecutionExplainJsonSerializer.GetCanonicalBytes(artifact),
                success.Http!.StatusCode,
                success.Http.ContentType ?? "application/json");
        };

    static void EnsureArtifactAffinity(
        ExecutionExplainArtifact artifact,
        ProcessInstanceId processInstanceId)
    {
        if (artifact.RuntimeStatus is { } status
            && status.ProcessInstanceId != processInstanceId)
        {
            throw new InvalidOperationException(
                "The Process explain repository returned runtime evidence for another logical Process instance.");
        }

        if (artifact.Trace?.Continuation is { } continuation
            && continuation.ProcessInstanceId != processInstanceId)
        {
            throw new InvalidOperationException(
                "The Process explain repository returned trace evidence for another logical Process instance.");
        }
    }

    static void EnsureCanonicalExplainContract(ApiOperation operation)
    {
        if (!HasCanonicalObservationContract(
                operation,
                ExecutionExplainWireNames.Explain,
                typeof(ExecutionExplainArtifact),
                ExecutionExplainWireNames.SemanticAuthority,
                ExecutionExplainArtifact.CurrentSchemaVersion,
                ExecutionExplainWireNames.QueryPath)
            || !HasProblemResult(operation, ApiResultKind.NotFound)
            || !HasProblemResult(operation, ApiResultKind.ValidationFailed))
        {
            throw new InvalidOperationException(
                $"API endpoint '{operation.Id}' is not the canonical execution-control explain contract.");
        }
    }
}
