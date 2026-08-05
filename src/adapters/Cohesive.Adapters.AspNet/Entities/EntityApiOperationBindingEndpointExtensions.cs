using Cohesive.Api;
using Cohesive.Execution;
using Cohesive.Storage;
using Cohesive.Transitions.Compilation;
using Cohesive.Transitions.Execution;
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
            Func<EntityApiRequestContext, object?, EntityConcurrencyToken?>? getExpectedConcurrencyToken = null
            ) =>
            EntityApiOperationBinding.Create(
                endpoint,
                createState,
                createResult,
                getExpectedConcurrencyToken);

        /// <summary>
        /// Creates an entity transition operation binding for this endpoint.
        /// </summary>
        /// <param name="plan">Compiled exact canonical Transition plan referenced by the endpoint operation.</param>
        /// <param name="createTransitionInput">Optional projection from HTTP request data to canonical Transition input.</param>
        /// <param name="createResult">Required projection from commit context and effective snapshot to an HTTP result.</param>
        /// <param name="getExpectedConcurrencyToken">Optional expected-concurrency override.</param>
        /// <param name="interactionContracts">
        /// Exact interaction-contract catalog used to link and validate emitted Transition intents.
        /// Required only when the Transition emits.
        /// </param>
        /// <param name="createEmissionPolicy">
        /// Explicit request-scoped identity, authority, delivery, provenance, and Request-target policy.
        /// Required only when the Transition emits.
        /// </param>
        /// <returns>A binding anchored to this endpoint and exact plan.</returns>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="plan"/> or <paramref name="createResult"/> is <see langword="null"/>.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// An emitting decision has no canonical interaction catalog or lowering policy, or canonical lowering fails.
        /// </exception>
        public EntityApiOperationBinding Transition(
            CompiledTransitionPlan plan,
            Func<EntityApiRequestContext, object?, object?>? createTransitionInput,
            Func<EntityApiCommitContext, EntitySnapshot, IResult> createResult,
            Func<EntityApiRequestContext, object?, EntityConcurrencyToken?>? getExpectedConcurrencyToken = null,
            InteractionContractCatalog? interactionContracts = null,
            Func<EntityApiCommitContext, TransitionEmissionLoweringPolicy>? createEmissionPolicy = null
            ) =>
            EntityApiOperationBinding.Transition(
                endpoint,
                plan,
                createTransitionInput,
                createResult,
                getExpectedConcurrencyToken,
                interactionContracts,
                createEmissionPolicy);
    }
}
