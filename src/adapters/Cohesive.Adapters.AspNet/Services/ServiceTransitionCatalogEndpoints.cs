using Cohesive.Api;
using Cohesive.Api.Execution.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace Cohesive.Adapters.AspNet.Services;

public static partial class ServiceEndpointRouteBuilderExtensions
{
    /// <summary>Maps a declared catalog of service transitions with shared input and outcome contracts.</summary>
    /// <typeparam name="TInput">Exact CLR input projection for every supplied operation.</typeparam>
    /// <typeparam name="TOutcome">Exact CLR outcome projection for every supplied operation.</typeparam>
    /// <param name="endpoints">Native route builder.</param>
    /// <param name="runtime">Runtime owning the declared transitions and their authorization.</param>
    /// <param name="definition">Transition-only API catalog; its routes, results and metadata are retained.</param>
    /// <param name="authorizationPolicyResolver">Native policy associations for declared requirements.</param>
    /// <param name="subjectParameter">Required string route parameter carrying entity identity.</param>
    /// <param name="tokenHeader">Required string header carrying the expected concurrency token.</param>
    /// <returns>Mapped route builders in declaration order.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">A catalog entry does not match the runtime or supported HTTP binding.</exception>
    /// <exception cref="InvalidOperationException">Required native policy associations are missing.</exception>
    /// <remarks>All contracts are checked before registering any endpoint. Supports POST with a typed body,
    /// one subject route parameter and one token header. Mixed CLR contracts require separate catalogs.</remarks>
    public static IReadOnlyList<RouteHandlerBuilder> MapServiceTransitions<TInput, TOutcome>(
        this IEndpointRouteBuilder endpoints, ServiceRuntime runtime, ApiDefinition definition,
        AspNetAuthorizationPolicyResolver? authorizationPolicyResolver = null,
        string subjectParameter = "id", string tokenHeader = "X-Expected-Concurrency-Token")
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectParameter);
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenHeader);
        if (definition.Endpoints.Count != definition.Operations.Count)
            throw new ArgumentException("Every transition must declare an HTTP endpoint.", nameof(definition));
        foreach (var endpoint in definition.Endpoints)
        {
            var operation = endpoint.Operation;
            var http = operation.Http!;
            var expected = runtime.Project<TInput, TOutcome>(operation.Name, http).Operation;
            if (operation.Id != expected.Id || operation.Entity != expected.Entity
                || operation.Kind != expected.Kind || operation.TransitionReference != expected.TransitionReference
                || operation.RequestType != expected.RequestType || operation.ResponseType != expected.ResponseType
                || !operation.AuthorizationRequirements.SequenceEqual(expected.AuthorizationRequirements)
                || operation.Results.Count != expected.Results.Count
                || expected.Results.Any(result => !operation.Results.Any(candidate => candidate.Kind == result.Kind && candidate.BodyType == result.BodyType)))
                throw new ArgumentException($"Operation '{operation.Name}' does not match its service transition projection.", nameof(definition));
            if (http.Method != "POST" || http.Body?.BodyType != typeof(TInput) || http.Query is not null || http.Parameters.Count != 2
                || !HasParameter(subjectParameter, HttpParameterSource.Route)
                || !HasParameter(tokenHeader, HttpParameterSource.Header))
                throw new ArgumentException($"Operation '{operation.Name}' requires a subject route parameter and token header.", nameof(definition));

            bool HasParameter(string name, HttpParameterSource source) => http.Parameters.Any(parameter =>
                parameter.Name == name && parameter.Source == source && parameter.Type == typeof(string) && !parameter.IsOptional);
        }
        var builders = new List<RouteHandlerBuilder>(definition.Endpoints.Count);
        foreach (var endpoint in definition.Endpoints)
            builders.Add(MapTransition<TInput, TOutcome>(endpoints, runtime, endpoint.Operation.Name, endpoint,
                authorizationPolicyResolver, subjectParameter, tokenHeader));
        return builders;
    }
}
