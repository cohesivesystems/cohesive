using Cohesive.Api;
using Cohesive.Storage;
using Microsoft.AspNetCore.Http;

namespace Cohesive.Adapters.AspNet.Entities;

sealed class LoadEntityApiOperationBinding : EntityApiOperationBinding
{
    readonly Func<EntityApiLoadedRequestContext, EntitySnapshot, object?, ValueTask<IResult>> createResult;

    internal LoadEntityApiOperationBinding(
        string operationName,
        Func<EntityApiLoadedRequestContext, EntitySnapshot, object?, ValueTask<IResult>> createResult)
        : base(operationName)
    {
        this.createResult = createResult ?? throw new ArgumentNullException(nameof(createResult));
    }

    internal LoadEntityApiOperationBinding(
        ApiEndpoint endpoint,
        Func<EntityApiLoadedRequestContext, EntitySnapshot, object?, ValueTask<IResult>> createResult)
        : base(endpoint)
    {
        this.createResult = createResult ?? throw new ArgumentNullException(nameof(createResult));
    }

    internal override Delegate CreateHandler(ApiOperation operation, EntityApiEndpointOptions options) =>
        async (OperationContext operationContext, HttpContext httpContext) =>
        {
            var repository = EntityApiRequestSupport.ResolveRepository(httpContext, options);
            var entityId = EntityApiRequestSupport.GetRequiredEntityId(httpContext, options);
            var request = await EntityApiRequestSupport.ReadBodyRequestAsync(httpContext, operation, operationContext.CancellationToken).ConfigureAwait(false);
            var requestContext = new EntityApiRequestContext(operationContext, httpContext, operation, options.Entity, repository, entityId);
            var readOptions = options.ResolveReadOptions(requestContext, EntityReadOptions.Full);
            var snapshot = await repository.TryGet(operationContext, entityId, readOptions).ConfigureAwait(false);
            if (snapshot is null)
                return Results.NotFound();

            return await createResult(
                new(operationContext, httpContext, operation, options.Entity, repository, entityId, request),
                snapshot,
                request).ConfigureAwait(false);
        };
}