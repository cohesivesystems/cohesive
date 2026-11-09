using System.Text.Json;
using Cohesive.Prelude;
using Cohesive.Processes.Execution;
using Cohesive.Storage;
using Cohesive.Storage.Processes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Cohesive.Adapters.AspNet.Machines;

/// <summary>HTTP realization of exact operation execution and retained receipt resolution.</summary>
public static class MachineReactionEndpoints
{
    /// <summary>Maps execution and receipt lookup with fresh target authorization on both paths.</summary>
    /// <param name="endpoints">Endpoint registry.</param>
    /// <param name="route">Base route; receipt lookup appends /receipt.</param>
    /// <param name="target">Authorized native target with atomic operation receipts.</param>
    /// <param name="createContext">Builds a trusted server context from authenticated request identity.</param>
    /// <returns>Route group for additional host authorization conventions.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    public static RouteGroupBuilder MapMachineReactionTarget(this IEndpointRouteBuilder endpoints, string route,
        MachineReactionTarget target, Func<HttpContext, OperationContext> createContext)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(createContext);
        var group = endpoints.MapGroup(route);
        group.MapPost("", async (HttpContext http) =>
        {
            try
            {
                var invocation = await http.Request.ReadFromJsonAsync<ProcessTransitionInvocation>(HttpMachineReactionTarget.Json, http.RequestAborted);
                if (invocation is null) return Results.BadRequest();
                var context = createContext(http) with { CancellationToken = http.RequestAborted };
                var result = await target.ExecuteAsync(context, invocation);
                if (result.ReceiptReference is not { } locator) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
                var receipt = await target.ResolveAsync(context, EntityTransitionReceiptReferences.Read(locator));
                return receipt is null ? Results.StatusCode(StatusCodes.Status503ServiceUnavailable)
                    : Results.Json(receipt, HttpMachineReactionTarget.Json);
            }
            catch (UnauthorizedAccessException) { return Results.StatusCode(StatusCodes.Status403Forbidden); }
            catch (MachineReactionProtocolException exception) { return ProtocolConflict(exception); }
            catch (JsonException) { return Results.BadRequest(); }
        });
        group.MapPost("/" + HttpMachineReactionTarget.ReceiptPath, async (HttpContext http) =>
        {
            try
            {
                var reference = await http.Request.ReadFromJsonAsync<EntityTransitionOperationReference>(HttpMachineReactionTarget.Json, http.RequestAborted);
                if (reference is null) return Results.BadRequest();
                var receipt = await target.ResolveAsync(createContext(http) with { CancellationToken = http.RequestAborted }, reference);
                return receipt is null ? Results.NotFound() : Results.Json(receipt, HttpMachineReactionTarget.Json);
            }
            catch (UnauthorizedAccessException) { return Results.StatusCode(StatusCodes.Status403Forbidden); }
            catch (MachineReactionProtocolException exception) { return ProtocolConflict(exception); }
            catch (JsonException) { return Results.BadRequest(); }
        });
        return group;
    }

    static IResult ProtocolConflict(MachineReactionProtocolException exception) => Results.Problem(
        statusCode: StatusCodes.Status409Conflict, title: "Reaction protocol conflict", detail: exception.Message,
        extensions: new Dictionary<string, object?> { ["code"] = exception.Code });
}
