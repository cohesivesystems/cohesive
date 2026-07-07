using Cohesive.Api;
using Cohesive.Relations.Queries;
using Microsoft.AspNetCore.Http;

namespace Cohesive.Adapters.AspNet.Relations;

/// <summary>
/// Endpoint-anchored helpers for creating relation-query API operation bindings.
/// </summary>
public static class RelationQueryApiOperationBindingEndpointExtensions
{
    extension(ApiEndpoint endpoint)
    {
        /// <summary>
        /// Creates a relation-query binding for a fixed executable query.
        /// </summary>
        public RelationQueryApiOperationBinding RelationQuery(
            IExecutableQuery query,
            Func<RelationQueryApiResultContext, object?, IResult>? createResult = null
            ) =>
            RelationQueryApiOperationBinding.Query(endpoint, query, createResult);

        /// <summary>
        /// Creates a relation-query binding that translates the API request into an executable query.
        /// </summary>
        public RelationQueryApiOperationBinding RelationQuery(
            Func<RelationQueryApiRequestContext, object?, IExecutableQuery> createQuery,
            Func<RelationQueryApiResultContext, object?, IResult>? createResult = null
            ) =>
            RelationQueryApiOperationBinding.Query(endpoint, createQuery, createResult);

        /// <summary>
        /// Creates a relation-query binding that asynchronously translates the API request into an executable query.
        /// </summary>
        public RelationQueryApiOperationBinding RelationQuery(
            Func<RelationQueryApiRequestContext, object?, ValueTask<IExecutableQuery>> createQuery,
            Func<RelationQueryApiResultContext, object?, ValueTask<IResult>>? createResult = null
            ) =>
            RelationQueryApiOperationBinding.Query(endpoint, createQuery, createResult);

        /// <summary>
        /// Creates a strongly typed relation-query binding that translates the API request into an executable query.
        /// </summary>
        public RelationQueryApiOperationBinding RelationQuery<TResult>(
            Func<RelationQueryApiRequestContext, object?, ExecutableQuery<TResult>> createQuery,
            Func<RelationQueryApiResultContext, TResult, IResult> createResult
            ) =>
            RelationQueryApiOperationBinding.Query(endpoint, createQuery, createResult);
    }
}
