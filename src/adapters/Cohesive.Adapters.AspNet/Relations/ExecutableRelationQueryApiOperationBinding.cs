using Cohesive.Api;
using Cohesive.Relations.Queries;
using Microsoft.AspNetCore.Http;

namespace Cohesive.Adapters.AspNet.Relations;

sealed class ExecutableRelationQueryApiOperationBinding : RelationQueryApiOperationBinding
{
    readonly Func<RelationQueryApiRequestContext, object?, ValueTask<IExecutableQuery>> createQuery;
    readonly Func<RelationQueryApiResultContext, object?, ValueTask<IResult>> createResult;

    internal ExecutableRelationQueryApiOperationBinding(
        string operationName,
        Func<RelationQueryApiRequestContext, object?, ValueTask<IExecutableQuery>> createQuery,
        Func<RelationQueryApiResultContext, object?, ValueTask<IResult>> createResult
        )
        : base(operationName)
    {
        this.createQuery = createQuery ?? throw new ArgumentNullException(nameof(createQuery));
        this.createResult = createResult ?? throw new ArgumentNullException(nameof(createResult));
    }

    internal ExecutableRelationQueryApiOperationBinding(
        ApiEndpoint endpoint,
        Func<RelationQueryApiRequestContext, object?, ValueTask<IExecutableQuery>> createQuery,
        Func<RelationQueryApiResultContext, object?, ValueTask<IResult>> createResult
        )
        : base(endpoint)
    {
        this.createQuery = createQuery ?? throw new ArgumentNullException(nameof(createQuery));
        this.createResult = createResult ?? throw new ArgumentNullException(nameof(createResult));
    }

    internal override Delegate CreateHandler(ApiOperation operation, RelationQueryApiEndpointOptions options) =>
        async (OperationContext operationContext, HttpContext httpContext) =>
        {
            var repositoryRegistry = RelationQueryApiRequestSupport.ResolveRepositoryRegistry(httpContext, options);
            var request = await RelationQueryApiRequestSupport.ReadRequestAsync(httpContext, operation, operationContext.CancellationToken).ConfigureAwait(false);
            var requestContext = new RelationQueryApiRequestContext(
                operationContext,
                httpContext,
                operation,
                repositoryRegistry
                );
            var query = await createQuery(requestContext, request).ConfigureAwait(false) ?? throw new InvalidOperationException($"Relation query binding for operation '{operation.Name}' returned null.");
            var result = await query.ExecuteAsync(operationContext, repositoryRegistry).ConfigureAwait(false);
            return await createResult(
                    new(OperationContext: operationContext,
                        HttpContext: httpContext,
                        Operation: operation,
                        RepositoryRegistry: repositoryRegistry,
                        Request: request,
                        Query: query
                        ),
                    result
                ).ConfigureAwait(false);
        };
}
