using Cohesive.Api;
using Cohesive.Api.Execution;
using Cohesive.Execution;
using Cohesive.Processes.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Cohesive.Adapters.AspNet.Processes;

public static partial class ProcessExecutionReadApiEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps the canonical execution-control inspect operation as one read-only HTTP endpoint.
    /// </summary>
    /// <remarks>
    /// The HTTP projection accepts only the logical Process identity from the route. The canonical
    /// <see cref="InspectProcessCommand"/> remains the transport-neutral request contract, but its authorization,
    /// issuance, and provenance fields are deliberately not accepted from HTTP callers. The supplied
    /// <paramref name="authorityScopeResolver"/> establishes the trusted repository address instead.
    /// </remarks>
    /// <param name="endpoints">ASP.NET endpoint route builder to update.</param>
    /// <param name="endpoint">
    /// Canonical route-neutral <see cref="ExecutionControlApiCatalog.Inspect"/> endpoint handle.
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
    /// <paramref name="endpoint"/> is not the canonical execution inspect contract.
    /// </exception>
    public static RouteHandlerBuilder MapProcessExecutionInspectApi(
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

        EnsureCanonicalInspectContract(endpoint.Operation);
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
            CreateInspectHandler(projected.Operation, authorityScopeResolver),
            configure,
            authorizationPolicyResolver);
    }

    static Delegate CreateInspectHandler(
        ApiOperation operation,
        ProcessExecutionAuthorityScopeResolver authorityScopeResolver) =>
        async (
            OperationContext operationContext,
            HttpContext httpContext,
            IProcessExecutionRepository repository) =>
        {
            ArgumentNullException.ThrowIfNull(operationContext);
            ArgumentNullException.ThrowIfNull(httpContext);
            ArgumentNullException.ThrowIfNull(repository);

            if (!TryReadProcessInstanceId(httpContext, out var processInstanceId))
            {
                return Problem(
                    operation,
                    ApiResultKind.NotFound,
                    ExecutionApiProblemCodes.NotFound);
            }

            var authorityScope = authorityScopeResolver(
                    operationContext,
                    httpContext,
                    processInstanceId)
                ?? throw new InvalidOperationException(
                    "The Process observation authority-scope resolver returned no trusted address.");
            var execution = await repository.GetAsync(
                    operationContext,
                    authorityScope,
                    processInstanceId)
                .ConfigureAwait(false);
            if (execution?.RuntimeStatus is not { } status)
            {
                return Problem(
                    operation,
                    ApiResultKind.NotFound,
                    ExecutionApiProblemCodes.NotFound);
            }

            EnsureStatusAffinity(status, processInstanceId);
            var success = GetHttpResult(operation, ApiResultKind.Success);
            return Results.Json(
                new ExecutionControlResult(ProcessControlDecisionDisposition.Inspected, status),
                options: null,
                contentType: success.Http!.ContentType ?? "application/json",
                statusCode: success.Http.StatusCode);
        };

    static void EnsureStatusAffinity(
        ExecutionStatus status,
        ProcessInstanceId processInstanceId)
    {
        if (status.ProcessInstanceId != processInstanceId)
        {
            throw new InvalidOperationException(
                "The Process execution repository returned runtime evidence for another logical Process instance.");
        }
    }

    static void EnsureCanonicalInspectContract(ApiOperation operation)
    {
        var hasApiReference = operation.SemanticReferences.Any(static reference =>
            string.Equals(
                reference.Authority,
                ExecutionControlApiWireNames.SemanticAuthority,
                StringComparison.Ordinal)
            && reference.SchemaVersion == ExecutionControlApiCatalog.CurrentSchemaVersion
            && reference.Path == ExecutionControlApiWireNames.OperationPath(ExecutionControlWireNames.Inspect));
        var hasKernelReference = operation.SemanticReferences.Any(static reference =>
            string.Equals(
                reference.Authority,
                ExecutionControlWireNames.SemanticAuthority,
                StringComparison.Ordinal)
            && reference.SchemaVersion == ProcessControlCommand.CurrentSchemaVersion
            && reference.Path == ExecutionControlWireNames.CommandPath(ExecutionControlWireNames.Inspect));
        var hasAuthorization = operation.AuthorizationRequirements.Count == 1
            && string.Equals(
                operation.AuthorizationRequirements[0].Id,
                ExecutionControlApiWireNames.AuthorizationRequirement(ExecutionControlWireNames.Inspect),
                StringComparison.Ordinal);
        if (!string.Equals(operation.Name, ExecutionControlWireNames.Inspect, StringComparison.Ordinal)
            || operation.Kind != ApiOperationKind.Query
            || operation.RequestType != typeof(InspectProcessCommand)
            || operation.PrimaryResult.Kind != ApiResultKind.Success
            || operation.PrimaryResult.BodyType != typeof(ExecutionControlResult)
            || !hasApiReference
            || !hasKernelReference
            || !hasAuthorization
            || !HasResult<ExecutionControlResult>(operation, ApiResultKind.PreconditionFailed)
            || !HasResult<ExecutionControlResult>(operation, ApiResultKind.Conflict)
            || !HasResult<ExecutionControlResult>(operation, ApiResultKind.ValidationFailed)
            || !HasProblemResult(operation, ApiResultKind.Forbidden)
            || !HasProblemResult(operation, ApiResultKind.NotFound))
        {
            throw new InvalidOperationException(
                $"API endpoint '{operation.Id}' is not the canonical execution-control inspect contract.");
        }
    }

    static bool HasResult<TResult>(ApiOperation operation, ApiResultKind kind) =>
        operation.Results.Count(result => result.Kind == kind
            && result.BodyType == typeof(TResult)) == 1;
}
