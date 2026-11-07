using System.Collections.Frozen;
using Cohesive.Api;
using Cohesive.Api.Execution.Services;
using Cohesive.Model;
using Cohesive.Execution;
using Cohesive.Api.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Cohesive.Adapters.AspNet.Services;

public static partial class ServiceEndpointRouteBuilderExtensions
{
    /// <summary>Maps authorized entity transition inspection using complete typed commands.</summary>
    /// <typeparam name="TInput">Shared input contract validated against the entity's transitions.</typeparam>
    /// <param name="endpoints">HTTP route builder.</param>
    /// <param name="runtime">Service runtime owning authorization and repository bindings.</param>
    /// <param name="entity">Host-selected entity authority.</param>
    /// <param name="route">POST route containing the subject parameter.</param>
    /// <param name="subjectParameter">Route parameter carrying the entity identity.</param>
    /// <returns>The route with generated API metadata.</returns>
    /// <exception cref="ArgumentNullException">The route builder or runtime is null.</exception>
    /// <exception cref="ArgumentException">The command projection or route parameters are invalid.</exception>
    public static RouteHandlerBuilder MapServiceTransitionInspection<TInput>(this IEndpointRouteBuilder endpoints,
        ServiceRuntime runtime, QualifiedShapeId entity, string route, string subjectParameter = "id")
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectParameter);
        var projection = runtime.ProjectTransitionInspection<TInput>(entity, new("POST", route,
            [new(subjectParameter, HttpParameterSource.Route, typeof(string))], new(typeof(ServiceTransitionInspection<TInput>))));
        var declared = runtime.Declaration.GetDefinition<ServiceDefinition>().Operations
            .OfType<ServiceTransitionOperation>().Where(op => op.Entity == entity)
            .ToFrozenDictionary(op => op.Id, op => runtime.InputContract(op.Id), StringComparer.Ordinal);
        return endpoints.MapApiEndpoint(projection, async (OperationContext context, HttpContext http) =>
        {
            var subject = http.Request.RouteValues[subjectParameter]?.ToString();
            if (string.IsNullOrWhiteSpace(subject)) return Results.BadRequest();
            var body = (ServiceTransitionInspection<TInput>?)await HttpRequestBindingSupport.ReadOperationBodyAsync(
                http, projection.Operation, context.CancellationToken).ConfigureAwait(false);
            // Resolve contracts only for declared operations of this entity. Unknown input keys are rejected
            // before inspection, and every operation still passes through runtime authorization.
            Dictionary<string, PortableValue>? inputs = null;
            if (body?.Inputs is { } commands)
            {
                inputs = [];
                foreach (var (id, command) in commands)
                {
                    if (!declared.TryGetValue(id, out var contract)) return Results.BadRequest(new ApiValidationProblem(
                        "services.inspection.operationUnknown", "The command does not belong to this entity.", []));
                    var value = ObservationValue.FromObject(command);
                    inputs.Add(id, value.Kind == ObservationValueKind.Null
                        ? PortableValue.Null(contract) : PortableValue.Concrete(contract, value));
                }
            }
            var actions = await runtime.InspectTransitionsAsync(context, entity, subject, inputs).ConfigureAwait(false);
            return Results.Json(actions.Select(runtime.ProjectTransitionAction).ToArray(), OutcomeJson);
        }).WithMetadata(runtime.Declaration);
    }
}
