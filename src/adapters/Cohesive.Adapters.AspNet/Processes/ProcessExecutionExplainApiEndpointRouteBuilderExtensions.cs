using Cohesive.Api;
using Cohesive.Api.Execution;
using Cohesive.Execution;
using Cohesive.Processes.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;

namespace Cohesive.Adapters.AspNet.Processes;

/// <summary>Resolves the trusted authority address for one HTTP Process explanation read.</summary>
/// <remarks>
/// Implementations derive this value from authenticated server-side identity, authorization, and scope evidence.
/// They must not accept authority or tenant values from an untrusted request payload.
/// </remarks>
/// <param name="operationContext">Current Cohesive operation context with server-resolved identity evidence.</param>
/// <param name="httpContext">Current authorized ASP.NET request.</param>
/// <param name="processInstanceId">Logical Process identity projected from the declared route value.</param>
/// <returns>The exact trusted authority and optional tenant isolating the Process execution.</returns>
public delegate InteractionAuthorityScope ProcessExecutionExplainAuthorityScopeResolver(
    OperationContext operationContext,
    HttpContext httpContext,
    ProcessInstanceId processInstanceId);

/// <summary>ASP.NET binding for canonical Process execution explanations.</summary>
public static class ProcessExecutionExplainApiEndpointRouteBuilderExtensions
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
        ProcessExecutionExplainAuthorityScopeResolver authorityScopeResolver,
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
            CreateHandler(projected.Operation, authorityScopeResolver),
            configure,
            authorizationPolicyResolver);
    }

    static Delegate CreateHandler(
        ApiOperation operation,
        ProcessExecutionExplainAuthorityScopeResolver authorityScopeResolver) =>
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
                    "The Process explain authority-scope resolver returned no trusted address.");
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
            return new CanonicalExplainJsonResult(
                ExecutionExplainJsonSerializer.GetCanonicalBytes(artifact),
                success.Http!.StatusCode,
                success.Http.ContentType ?? "application/json");
        };

    static bool TryReadProcessInstanceId(
        HttpContext httpContext,
        out ProcessInstanceId processInstanceId)
    {
        processInstanceId = default;
        if (!httpContext.Request.RouteValues.TryGetValue(ProcessInstanceIdRouteParameter, out var value)
            || value is null)
        {
            return false;
        }

        var text = value.ToString();
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        processInstanceId = new(text.Trim());
        return true;
    }

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

    static IResult Problem(ApiOperation operation, ApiResultKind kind, string code)
    {
        var result = GetHttpResult(operation, kind);
        return Results.Json(
            new ExecutionApiProblem(code),
            options: null,
            contentType: result.Http!.ContentType ?? "application/json",
            statusCode: result.Http.StatusCode);
    }

    static ApiResultDefinition GetHttpResult(ApiOperation operation, ApiResultKind kind)
    {
        ApiResultDefinition? match = null;
        for (var i = 0; i < operation.Results.Count; i++)
        {
            var candidate = operation.Results[i];
            if (candidate.Kind != kind)
            {
                continue;
            }
            if (match is not null)
            {
                throw new InvalidOperationException(
                    $"Execution explain operation '{operation.Id}' declares multiple '{kind}' results.");
            }

            match = candidate;
        }

        if (match?.Http is null)
        {
            throw new InvalidOperationException(
                $"Execution explain operation '{operation.Id}' has no HTTP '{kind}' result projection.");
        }

        return match;
    }

    static void EnsureCanonicalExplainContract(ApiOperation operation)
    {
        var hasApiReference = operation.SemanticReferences.Any(static reference =>
            string.Equals(
                reference.Authority,
                ExecutionControlApiWireNames.SemanticAuthority,
                StringComparison.Ordinal)
            && reference.SchemaVersion == ExecutionControlApiCatalog.CurrentSchemaVersion
            && reference.Path == ExecutionControlApiWireNames.OperationPath(ExecutionExplainWireNames.Explain));
        var hasKernelReference = operation.SemanticReferences.Any(static reference =>
            string.Equals(
                reference.Authority,
                ExecutionExplainWireNames.SemanticAuthority,
                StringComparison.Ordinal)
            && reference.SchemaVersion == ExecutionExplainArtifact.CurrentSchemaVersion
            && reference.Path == ExecutionExplainWireNames.QueryPath);
        var hasAuthorization = operation.AuthorizationRequirements.Count == 1
            && string.Equals(
                operation.AuthorizationRequirements[0].Id,
                ExecutionControlApiWireNames.AuthorizationRequirement(ExecutionExplainWireNames.Explain),
                StringComparison.Ordinal);
        if (!string.Equals(operation.Name, ExecutionExplainWireNames.Explain, StringComparison.Ordinal)
            || operation.Kind != ApiOperationKind.Query
            || operation.RequestType != typeof(InspectProcessCommand)
            || operation.PrimaryResult.Kind != ApiResultKind.Success
            || operation.PrimaryResult.BodyType != typeof(ExecutionExplainArtifact)
            || !hasApiReference
            || !hasKernelReference
            || !hasAuthorization
            || !HasProblemResult(operation, ApiResultKind.NotFound)
            || !HasProblemResult(operation, ApiResultKind.ValidationFailed))
        {
            throw new InvalidOperationException(
                $"API endpoint '{operation.Id}' is not the canonical execution-control explain contract.");
        }
    }

    static bool HasProblemResult(ApiOperation operation, ApiResultKind kind) =>
        operation.Results.Count(result => result.Kind == kind
            && result.BodyType == typeof(ExecutionApiProblem)) == 1;

    static void EnsureProcessInstanceRoute(string route)
    {
        RoutePattern pattern;
        try
        {
            pattern = RoutePatternFactory.Parse(route);
        }
        catch (RoutePatternException error)
        {
            throw new ArgumentException("The Process explain HTTP route is malformed.", nameof(route), error);
        }

        if (!pattern.Parameters.Any(static parameter =>
                string.Equals(
                    parameter.Name,
                    ProcessInstanceIdRouteParameter,
                    StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                $"The Process explain HTTP route must contain '{{{ProcessInstanceIdRouteParameter}}}'.",
                nameof(route));
        }
    }

    sealed class CanonicalExplainJsonResult(
        byte[] content,
        int statusCode,
        string contentType) : IResult
    {
        readonly byte[] content = content ?? throw new ArgumentNullException(nameof(content));
        readonly string contentType = Guard.RequireNotNullOrWhiteSpace(contentType);

        public async Task ExecuteAsync(HttpContext httpContext)
        {
            ArgumentNullException.ThrowIfNull(httpContext);
            httpContext.Response.StatusCode = statusCode;
            httpContext.Response.ContentType = contentType;
            httpContext.Response.ContentLength = content.Length;
            await httpContext.Response.Body
                .WriteAsync(content, httpContext.RequestAborted)
                .ConfigureAwait(false);
        }
    }
}
