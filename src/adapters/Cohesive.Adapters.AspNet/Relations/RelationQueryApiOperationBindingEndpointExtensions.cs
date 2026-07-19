using Cohesive.Api;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.Execution;
using Microsoft.AspNetCore.Http;

namespace Cohesive.Adapters.AspNet.Relations;

/// <summary>Endpoint-anchored helpers for canonical relation/query API operation bindings.</summary>
public static class RelationQueryApiOperationBindingEndpointExtensions
{
    extension(ApiEndpoint endpoint)
    {
        /// <summary>
        /// Creates a binding that authors a fresh canonical evaluation for every request and explicitly maps its
        /// complete outcome to an HTTP result.
        /// </summary>
        /// <param name="createEvaluation">
        /// Per-request factory that must assign the request context evaluation identity to the returned evaluation.
        /// </param>
        /// <param name="createResult">Explicit projection from the complete canonical outcome to the HTTP response.</param>
        /// <returns>A binding anchored to this endpoint.</returns>
        /// <exception cref="ArgumentNullException">A required delegate is <see langword="null"/>.</exception>
        public RelationQueryApiOperationBinding RelationQuery(
            Func<RelationQueryApiRequestContext, object?, RelationQueryEvaluation> createEvaluation,
            Func<RelationQueryApiResultContext, RelationQueryEvaluationOutcome, IResult> createResult) =>
            RelationQueryApiOperationBinding.Evaluate(endpoint, createEvaluation, createResult);

        /// <summary>
        /// Creates a binding that asynchronously authors a fresh canonical evaluation for every request and
        /// explicitly maps its complete outcome to an HTTP result.
        /// </summary>
        /// <param name="createEvaluation">Asynchronous per-request canonical evaluation factory.</param>
        /// <param name="createResult">Asynchronous explicit outcome-to-response projection.</param>
        /// <returns>A binding anchored to this endpoint.</returns>
        /// <exception cref="ArgumentNullException">A required delegate is <see langword="null"/>.</exception>
        public RelationQueryApiOperationBinding RelationQuery(
            Func<RelationQueryApiRequestContext, object?, ValueTask<RelationQueryEvaluation>> createEvaluation,
            Func<RelationQueryApiResultContext, RelationQueryEvaluationOutcome, ValueTask<IResult>> createResult) =>
            RelationQueryApiOperationBinding.Evaluate(endpoint, createEvaluation, createResult);
    }
}
