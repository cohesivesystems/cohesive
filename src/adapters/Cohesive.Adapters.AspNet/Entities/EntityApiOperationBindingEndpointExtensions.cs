using Cohesive.Api;
using Cohesive.Storage;
using Cohesive.Transitions.Model;
using Microsoft.AspNetCore.Http;

namespace Cohesive.Adapters.AspNet.Entities;

/// <summary>
/// Endpoint-anchored helpers for creating entity API operation bindings.
/// </summary>
public static class EntityApiOperationBindingEndpointExtensions
{
    extension(ApiEndpoint endpoint)
    {
        /// <summary>
        /// Creates a read-by-id operation binding for this endpoint.
        /// </summary>
        public EntityApiOperationBinding Get(
            Func<EntityApiLoadedContext, EntitySnapshot, IResult> createResult,
            EntityReadOptions? readOptions = null
            ) =>
            EntityApiOperationBinding.Get(endpoint, createResult, readOptions);

        /// <summary>
        /// Creates a read-by-id operation binding with asynchronous response projection.
        /// </summary>
        public EntityApiOperationBinding Get(
            Func<EntityApiLoadedContext, EntitySnapshot, ValueTask<IResult>> createResult,
            EntityReadOptions? readOptions = null
            ) =>
            EntityApiOperationBinding.Get(endpoint, createResult, readOptions);

        /// <summary>
        /// Creates a read-by-id operation binding that also exposes the operation request payload.
        /// </summary>
        public EntityApiOperationBinding Load(Func<EntityApiLoadedRequestContext, EntitySnapshot, object?, IResult> createResult) =>
            EntityApiOperationBinding.Load(endpoint, createResult);

        /// <summary>
        /// Creates a read-by-id operation binding that also exposes the operation request payload.
        /// </summary>
        public EntityApiOperationBinding Load(
            Func<EntityApiLoadedRequestContext, EntitySnapshot, object?, ValueTask<IResult>> createResult
            ) =>
            EntityApiOperationBinding.Load(endpoint, createResult);

        /// <summary>
        /// Creates an entity create operation binding for this endpoint.
        /// </summary>
        public EntityApiOperationBinding Create(
            Func<EntityApiRequestContext, object?, EntityState> createState,
            Func<EntityApiCommitContext, EntitySnapshot, IResult> createResult,
            Func<EntityApiRequestContext, object?, EntityConcurrencyToken?>? getExpectedConcurrencyToken = null,
            Func<EntityApiCommitContext, IReadOnlyList<EntityOutboxMessage>>? createOutboxMessages = null
            ) =>
            EntityApiOperationBinding.Create(
                endpoint,
                createState,
                createResult,
                getExpectedConcurrencyToken,
                createOutboxMessages);

        /// <summary>
        /// Creates an entity transition operation binding for this endpoint.
        /// </summary>
        public EntityApiOperationBinding Transition(
            string transitionName,
            Func<EntityApiRequestContext, object?, object?>? createTransitionInput,
            Func<EntityApiCommitContext, EntitySnapshot, IResult> createResult,
            Func<EntityApiRequestContext, object?, EntityConcurrencyToken?>? getExpectedConcurrencyToken = null,
            Func<EntityApiCommitContext, IReadOnlyList<EntityOutboxMessage>>? createOutboxMessages = null
            ) =>
            EntityApiOperationBinding.Transition(
                endpoint,
                transitionName,
                createTransitionInput,
                createResult,
                getExpectedConcurrencyToken,
                createOutboxMessages);
    }
}
