using Cohesive.Api;
using Cohesive.Api.Execution;
using Cohesive.Execution;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing.Patterns;

namespace Cohesive.Adapters.AspNet.Processes;

public static partial class ProcessExecutionReadApiEndpointRouteBuilderExtensions
{
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
                    $"Execution observation operation '{operation.Id}' declares multiple '{kind}' results.");
            }

            match = candidate;
        }

        if (match?.Http is null)
        {
            throw new InvalidOperationException(
                $"Execution observation operation '{operation.Id}' has no HTTP '{kind}' result projection.");
        }

        return match;
    }

    static bool HasCanonicalObservationContract(
        ApiOperation operation,
        string action,
        Type responseType,
        string kernelAuthority,
        ExecutionIrSchemaVersion kernelSchemaVersion,
        ExecutionSemanticPath kernelPath)
    {
        if (!string.Equals(operation.Name, action, StringComparison.Ordinal)
            || operation.Kind != ApiOperationKind.Query
            || operation.RequestType != typeof(InspectProcessCommand)
            || operation.PrimaryResult.Kind != ApiResultKind.Success
            || operation.PrimaryResult.BodyType != responseType
            || operation.AuthorizationRequirements.Count != 1
            || !string.Equals(
                operation.AuthorizationRequirements[0].Id,
                ExecutionControlApiWireNames.AuthorizationRequirement(action),
                StringComparison.Ordinal)
            || operation.SemanticReferences.Count != 2)
        {
            return false;
        }

        return HasSingleSemanticReference(
                operation,
                ExecutionControlApiWireNames.SemanticAuthority,
                ExecutionControlApiCatalog.CurrentSchemaVersion,
                ExecutionControlApiWireNames.OperationPath(action))
            && HasSingleSemanticReference(
                operation,
                kernelAuthority,
                kernelSchemaVersion,
                kernelPath);
    }

    static bool HasSingleSemanticReference(
        ApiOperation operation,
        string authority,
        ExecutionIrSchemaVersion schemaVersion,
        ExecutionSemanticPath path) =>
        operation.SemanticReferences.Count(reference =>
            string.Equals(reference.Authority, authority, StringComparison.Ordinal)
            && reference.SchemaVersion == schemaVersion
            && reference.Path == path) == 1;

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
            throw new ArgumentException("The Process observation HTTP route is malformed.", nameof(route), error);
        }

        if (!pattern.Parameters.Any(static parameter =>
                string.Equals(
                    parameter.Name,
                    ProcessInstanceIdRouteParameter,
                    StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                $"The Process observation HTTP route must contain '{{{ProcessInstanceIdRouteParameter}}}'.",
                nameof(route));
        }
    }
}
