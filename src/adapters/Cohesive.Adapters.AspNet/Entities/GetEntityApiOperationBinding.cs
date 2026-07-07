using Cohesive.Api;
using Cohesive.Storage;
using Microsoft.AspNetCore.Http;

namespace Cohesive.Adapters.AspNet.Entities;

sealed class GetEntityApiOperationBinding : EntityApiOperationBinding
{
    readonly Func<EntityApiLoadedContext, EntitySnapshot, ValueTask<IResult>> createResult;
    readonly EntityReadOptions? readOptions;

    internal GetEntityApiOperationBinding(
        string operationName,
        Func<EntityApiLoadedContext, EntitySnapshot, IResult> createResult,
        EntityReadOptions? readOptions)
        : this(operationName, (context, snapshot) => ValueTask.FromResult(createResult(context, snapshot)), readOptions)
    {
    }

    internal GetEntityApiOperationBinding(
        string operationName,
        Func<EntityApiLoadedContext, EntitySnapshot, ValueTask<IResult>> createResult,
        EntityReadOptions? readOptions)
        : base(operationName)
    {
        this.createResult = createResult ?? throw new ArgumentNullException(nameof(createResult));
        this.readOptions = readOptions;
    }

    internal GetEntityApiOperationBinding(
        ApiEndpoint endpoint,
        Func<EntityApiLoadedContext, EntitySnapshot, IResult> createResult,
        EntityReadOptions? readOptions)
        : this(endpoint, (context, snapshot) => ValueTask.FromResult(createResult(context, snapshot)), readOptions)
    {
    }

    internal GetEntityApiOperationBinding(
        ApiEndpoint endpoint,
        Func<EntityApiLoadedContext, EntitySnapshot, ValueTask<IResult>> createResult,
        EntityReadOptions? readOptions)
        : base(endpoint)
    {
        this.createResult = createResult ?? throw new ArgumentNullException(nameof(createResult));
        this.readOptions = readOptions;
    }

    internal override Delegate CreateHandler(ApiOperation operation, EntityApiEndpointOptions options) =>
        async (OperationContext operationContext, HttpContext httpContext) =>
        {
            var repository = EntityApiRequestSupport.ResolveRepository(httpContext, options);
            var entityId = EntityApiRequestSupport.GetRequiredEntityId(httpContext, options);
            var requestContext = new EntityApiRequestContext(operationContext, httpContext, operation, options.Entity, repository, EntityId: entityId);
            var effectiveReadOptions = options.ResolveReadOptions(requestContext, readOptions);
            var snapshot = await repository.TryGet(operationContext, id: entityId, effectiveReadOptions).ConfigureAwait(false);
            if (snapshot is null)
                return Results.NotFound();

            return await createResult(
                new(operationContext, httpContext, operation, options.Entity, repository, EntityId: entityId),
                snapshot
                ).ConfigureAwait(false);
        };
}
