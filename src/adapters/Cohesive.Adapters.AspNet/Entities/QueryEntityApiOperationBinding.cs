using Cohesive.Api;
using Cohesive.Relations.Queries;
using Cohesive.Storage;
using Microsoft.AspNetCore.Http;

namespace Cohesive.Adapters.AspNet.Entities;

sealed class QueryEntityApiOperationBinding : EntityApiOperationBinding
{
    readonly Func<EntityApiRequestContext, object?, EntityQuery> createQuery;
    readonly Func<EntityApiQueryResultContext, IReadOnlyList<EntitySnapshot>, ValueTask<IResult>> createResult;

    internal QueryEntityApiOperationBinding(
        string operationName,
        Func<EntityApiRequestContext, object?, EntityQuery> createQuery,
        Func<EntityApiQueryResultContext, IReadOnlyList<EntitySnapshot>, IResult> createResult)
        : this(operationName, createQuery, (context, snapshots) => ValueTask.FromResult(createResult(context, snapshots)))
    {
    }

    internal QueryEntityApiOperationBinding(
        string operationName,
        Func<EntityApiRequestContext, object?, EntityQuery> createQuery,
        Func<EntityApiQueryResultContext, IReadOnlyList<EntitySnapshot>, ValueTask<IResult>> createResult)
        : base(operationName)
    {
        this.createQuery = createQuery ?? throw new ArgumentNullException(nameof(createQuery));
        this.createResult = createResult ?? throw new ArgumentNullException(nameof(createResult));
    }

    internal QueryEntityApiOperationBinding(
        ApiEndpoint endpoint,
        Func<EntityApiRequestContext, object?, EntityQuery> createQuery,
        Func<EntityApiQueryResultContext, IReadOnlyList<EntitySnapshot>, IResult> createResult)
        : this(endpoint, createQuery, (context, snapshots) => ValueTask.FromResult(createResult(context, snapshots)))
    {
    }

    internal QueryEntityApiOperationBinding(
        ApiEndpoint endpoint,
        Func<EntityApiRequestContext, object?, EntityQuery> createQuery,
        Func<EntityApiQueryResultContext, IReadOnlyList<EntitySnapshot>, ValueTask<IResult>> createResult)
        : base(endpoint)
    {
        this.createQuery = createQuery ?? throw new ArgumentNullException(nameof(createQuery));
        this.createResult = createResult ?? throw new ArgumentNullException(nameof(createResult));
    }

    internal override Delegate CreateHandler(ApiOperation operation, EntityApiEndpointOptions options) =>
        async (OperationContext operationContext, HttpContext httpContext) =>
        {
            var repository = EntityApiRequestSupport.ResolveQueryRepository(httpContext, options);
            var request = EntityApiRequestSupport.ReadQueryRequest(httpContext, operation);
            var context = new EntityApiRequestContext(
                operationContext,
                httpContext,
                operation,
                options.Entity,
                repository,
                EntityId: null
            );
            var query = createQuery(context, request);
            var results = new List<EntitySnapshot>();
            await foreach (var snapshot in repository.QueryStream(operationContext, query).WithCancellation(operationContext.CancellationToken))
                results.Add(snapshot);
            return await createResult(
                new(operationContext, httpContext, operation, options.Entity, repository, request),
                results).ConfigureAwait(false);
        };
}
