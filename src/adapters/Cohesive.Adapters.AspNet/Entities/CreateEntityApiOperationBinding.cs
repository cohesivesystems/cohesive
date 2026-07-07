using Cohesive.Api;
using Cohesive.Storage;
using Cohesive.Transitions.Model;
using Microsoft.AspNetCore.Http;

namespace Cohesive.Adapters.AspNet.Entities;

sealed class CreateEntityApiOperationBinding : EntityApiOperationBinding
{
    readonly Func<EntityApiRequestContext, object?, EntityState> createState;
    readonly Func<EntityApiCommitContext, EntitySnapshot, IResult> createResult;
    readonly Func<EntityApiRequestContext, object?, EntityConcurrencyToken?>? getExpectedConcurrencyToken;
    readonly Func<EntityApiCommitContext, IReadOnlyList<EntityOutboxMessage>>? createOutboxMessages;

    internal CreateEntityApiOperationBinding(
        string operationName,
        Func<EntityApiRequestContext, object?, EntityState> createState,
        Func<EntityApiCommitContext, EntitySnapshot, IResult> createResult,
        Func<EntityApiRequestContext, object?, EntityConcurrencyToken?>? getExpectedConcurrencyToken,
        Func<EntityApiCommitContext, IReadOnlyList<EntityOutboxMessage>>? createOutboxMessages)
        : base(operationName)
    {
        this.createState = createState ?? throw new ArgumentNullException(nameof(createState));
        this.createResult = createResult ?? throw new ArgumentNullException(nameof(createResult));
        this.getExpectedConcurrencyToken = getExpectedConcurrencyToken;
        this.createOutboxMessages = createOutboxMessages;
    }

    internal CreateEntityApiOperationBinding(
        ApiEndpoint endpoint,
        Func<EntityApiRequestContext, object?, EntityState> createState,
        Func<EntityApiCommitContext, EntitySnapshot, IResult> createResult,
        Func<EntityApiRequestContext, object?, EntityConcurrencyToken?>? getExpectedConcurrencyToken,
        Func<EntityApiCommitContext, IReadOnlyList<EntityOutboxMessage>>? createOutboxMessages)
        : base(endpoint)
    {
        this.createState = createState ?? throw new ArgumentNullException(nameof(createState));
        this.createResult = createResult ?? throw new ArgumentNullException(nameof(createResult));
        this.getExpectedConcurrencyToken = getExpectedConcurrencyToken;
        this.createOutboxMessages = createOutboxMessages;
    }

    internal override Delegate CreateHandler(ApiOperation operation, EntityApiEndpointOptions options) =>
        async (OperationContext operationContext, HttpContext httpContext) =>
        {
            var repository = EntityApiRequestSupport.ResolveRepository(httpContext, options);
            var request = await EntityApiRequestSupport.ReadBodyRequestAsync(httpContext, operation, operationContext.CancellationToken).ConfigureAwait(false);
            var requestContext = new EntityApiRequestContext(
                operationContext,
                httpContext,
                operation,
                options.Entity,
                repository,
                EntityId: null
                );
            var state = createState(requestContext, request);
            var entityId = state.EntityId.Value;
            var commitContext = new EntityApiCommitContext(
                operationContext,
                httpContext,
                operation,
                options.Entity,
                repository,
                entityId,
                request,
                OldSnapshot: null,
                NewState: state,
                Transition: null
                );
            var expectedToken = getExpectedConcurrencyToken?.Invoke(requestContext, request);
            var snapshot = await EntityApiRequestSupport.CommitAsync(
                commitContext,
                options,
                expectedToken,
                createOutboxMessages
                ).ConfigureAwait(false);
            return createResult(commitContext, snapshot);
        };
}