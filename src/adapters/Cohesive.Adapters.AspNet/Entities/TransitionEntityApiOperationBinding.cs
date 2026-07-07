using Cohesive.Api;
using Cohesive.Model;
using Cohesive.Storage;
using Cohesive.Transitions.Authoring;
using Microsoft.AspNetCore.Http;

namespace Cohesive.Adapters.AspNet.Entities;

sealed class TransitionEntityApiOperationBinding : EntityApiOperationBinding
{
    readonly string transitionName;
    readonly Func<EntityApiRequestContext, object?, object?>? createTransitionInput;
    readonly Func<EntityApiCommitContext, EntitySnapshot, IResult> createResult;
    readonly Func<EntityApiRequestContext, object?, EntityConcurrencyToken?>? getExpectedConcurrencyToken;
    readonly Func<EntityApiCommitContext, IReadOnlyList<EntityOutboxMessage>>? createOutboxMessages;

    internal TransitionEntityApiOperationBinding(
        string operationName,
        string transitionName,
        Func<EntityApiRequestContext, object?, object?>? createTransitionInput,
        Func<EntityApiCommitContext, EntitySnapshot, IResult> createResult,
        Func<EntityApiRequestContext, object?, EntityConcurrencyToken?>? getExpectedConcurrencyToken,
        Func<EntityApiCommitContext, IReadOnlyList<EntityOutboxMessage>>? createOutboxMessages)
        : base(operationName)
    {
        this.transitionName = Guard.RequireNotNullOrWhiteSpace(transitionName);
        this.createTransitionInput = createTransitionInput;
        this.createResult = createResult ?? throw new ArgumentNullException(nameof(createResult));
        this.getExpectedConcurrencyToken = getExpectedConcurrencyToken;
        this.createOutboxMessages = createOutboxMessages;
    }

    internal TransitionEntityApiOperationBinding(
        ApiEndpoint endpoint,
        string transitionName,
        Func<EntityApiRequestContext, object?, object?>? createTransitionInput,
        Func<EntityApiCommitContext, EntitySnapshot, IResult> createResult,
        Func<EntityApiRequestContext, object?, EntityConcurrencyToken?>? getExpectedConcurrencyToken,
        Func<EntityApiCommitContext, IReadOnlyList<EntityOutboxMessage>>? createOutboxMessages)
        : base(endpoint)
    {
        this.transitionName = Guard.RequireNotNullOrWhiteSpace(transitionName);
        this.createTransitionInput = createTransitionInput;
        this.createResult = createResult ?? throw new ArgumentNullException(nameof(createResult));
        this.getExpectedConcurrencyToken = getExpectedConcurrencyToken;
        this.createOutboxMessages = createOutboxMessages;
    }

    internal override Delegate CreateHandler(ApiOperation operation, EntityApiEndpointOptions options) =>
        async (OperationContext operationContext, HttpContext httpContext) =>
        {
            var repository = EntityApiRequestSupport.ResolveRepository(httpContext, options);
            var entityId = EntityApiRequestSupport.GetRequiredEntityId(httpContext, options);
            var request = await EntityApiRequestSupport.ReadBodyRequestAsync(httpContext, operation, operationContext.CancellationToken).ConfigureAwait(false);
            var initialRequestContext = new EntityApiRequestContext(operationContext, httpContext, operation, options.Entity, repository, entityId);
            var readOptions = options.ResolveReadOptions(initialRequestContext, EntityReadOptions.Full);
            var snapshot = await repository.TryGet(operationContext, entityId, readOptions).ConfigureAwait(false);
            if (snapshot is null)
                return Results.NotFound();

            var requestContext = new EntityApiRequestContext(
                operationContext,
                httpContext,
                operation,
                options.Entity,
                repository,
                entityId
            )
            {
                Snapshot = snapshot
            };
            var state = options.Entity.CreateState(snapshot.Entity);
            var input = createTransitionInput?.Invoke(requestContext, request) ?? request;
            var runtime = new DeclarativeEntityRuntime(options.Entity);
            var transition = runtime.Apply(
                entityId: entityId,
                state: state,
                version: state.Version,
                transitionName: transitionName,
                input: input is null ? default : ObservationValue.FromObject(input)
            );
            var commitContext = new EntityApiCommitContext(
                operationContext,
                httpContext,
                operation,
                options.Entity,
                repository,
                entityId,
                request,
                OldSnapshot: snapshot,
                NewState: transition.NewState,
                Transition: transition
            );
            var expectedToken = getExpectedConcurrencyToken?.Invoke(requestContext, request) ?? snapshot.ConcurrencyToken;
            var updated = await EntityApiRequestSupport.CommitAsync(
                commitContext,
                options,
                expectedToken,
                createOutboxMessages
            ).ConfigureAwait(false);
            
            return createResult(commitContext, updated);
        };
}