using Cohesive.Api;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Relations.Model;
using Cohesive.Storage;
using Cohesive.Transitions.Compilation;
using Cohesive.Transitions.Execution;
using Cohesive.Transitions.Model;
using Microsoft.AspNetCore.Http;

namespace Cohesive.Adapters.AspNet.Entities;

sealed class TransitionEntityApiOperationBinding : EntityApiOperationBinding
{
    readonly CompiledTransitionPlan plan;
    readonly Func<EntityApiRequestContext, object?, object?>? createTransitionInput;
    readonly Func<EntityApiCommitContext, EntitySnapshot, IResult> createResult;
    readonly Func<EntityApiRequestContext, object?, EntityConcurrencyToken?>? getExpectedConcurrencyToken;
    readonly Func<EntityApiCommitContext, IReadOnlyList<EntityOutboxMessage>>? createOutboxMessages;

    internal TransitionEntityApiOperationBinding(
        string operationName,
        CompiledTransitionPlan plan,
        Func<EntityApiRequestContext, object?, object?>? createTransitionInput,
        Func<EntityApiCommitContext, EntitySnapshot, IResult> createResult,
        Func<EntityApiRequestContext, object?, EntityConcurrencyToken?>? getExpectedConcurrencyToken,
        Func<EntityApiCommitContext, IReadOnlyList<EntityOutboxMessage>>? createOutboxMessages)
        : base(operationName)
    {
        this.plan = plan ?? throw new ArgumentNullException(nameof(plan));
        this.createTransitionInput = createTransitionInput;
        this.createResult = createResult ?? throw new ArgumentNullException(nameof(createResult));
        this.getExpectedConcurrencyToken = getExpectedConcurrencyToken;
        this.createOutboxMessages = createOutboxMessages;
    }

    internal TransitionEntityApiOperationBinding(
        ApiEndpoint endpoint,
        CompiledTransitionPlan plan,
        Func<EntityApiRequestContext, object?, object?>? createTransitionInput,
        Func<EntityApiCommitContext, EntitySnapshot, IResult> createResult,
        Func<EntityApiRequestContext, object?, EntityConcurrencyToken?>? getExpectedConcurrencyToken,
        Func<EntityApiCommitContext, IReadOnlyList<EntityOutboxMessage>>? createOutboxMessages)
        : base(endpoint)
    {
        this.plan = plan ?? throw new ArgumentNullException(nameof(plan));
        this.createTransitionInput = createTransitionInput;
        this.createResult = createResult ?? throw new ArgumentNullException(nameof(createResult));
        this.getExpectedConcurrencyToken = getExpectedConcurrencyToken;
        this.createOutboxMessages = createOutboxMessages;
    }

    internal override Delegate CreateHandler(ApiOperation operation, EntityApiEndpointOptions options)
    {
        if (operation.TransitionReference != plan.DefinitionReference)
        {
            throw new InvalidOperationException(
                $"API operation '{operation.Id.Value}' must reference the exact canonical Transition plan "
                + $"'{plan.DefinitionReference.DefinitionId.Value}' at revision "
                + $"'{plan.DefinitionReference.RevisionId.Value}'.");
        }

        return async (OperationContext operationContext, HttpContext httpContext) =>
        {
            var repository = EntityApiRequestSupport.ResolveRepository(httpContext, options);
            var entityId = EntityApiRequestSupport.GetRequiredEntityId(httpContext, options);
            var request = await EntityApiRequestSupport.ReadBodyRequestAsync(
                    httpContext,
                    operation,
                    operationContext.CancellationToken)
                .ConfigureAwait(false);
            var initialRequestContext = new EntityApiRequestContext(
                operationContext,
                httpContext,
                operation,
                options.Entity,
                repository,
                entityId);
            var readOptions = options.ResolveReadOptions(initialRequestContext, EntityReadOptions.Full);
            var snapshot = await repository.TryGet(operationContext, entityId, readOptions).ConfigureAwait(false);
            if (snapshot is null)
            {
                return Results.NotFound();
            }

            var requestContext = initialRequestContext with { Snapshot = snapshot };
            var state = options.Entity.CreateState(snapshot.Entity);
            var input = createTransitionInput is null
                ? request
                : createTransitionInput(requestContext, request);
            var decision = TransitionReferenceInterpreter.DecideFullState(
                plan,
                options.CreateActivationId(httpContext, operation),
                ToPortableValue(
                    input,
                    plan.Definition.Input,
                    isSupplied: createTransitionInput is not null || operation.RequestType != typeof(void)),
                PortableValue.Concrete(
                    plan.Definition.Observation,
                    ObservationValue.FromObject(state.Fields)));

            var newState = CreateCandidateState(options, snapshot, state, decision);
            var commitContext = new EntityApiCommitContext(
                operationContext,
                httpContext,
                operation,
                options.Entity,
                repository,
                entityId,
                request,
                OldSnapshot: snapshot,
                NewState: newState,
                Decision: decision);

            if (!decision.GuaranteeDemands.CommitRequired)
            {
                return createResult(commitContext, snapshot);
            }

            if (!decision.Emissions.IsDefaultOrEmpty && createOutboxMessages is null)
            {
                throw new InvalidOperationException(
                    $"Canonical Transition '{plan.DefinitionReference.DefinitionId.Value}' emitted interaction "
                    + "intents, but the ASP.NET binding has no explicit outbox projection.");
            }

            var expectedToken = getExpectedConcurrencyToken?.Invoke(requestContext, request) ?? snapshot.ConcurrencyToken;
            var updated = await EntityApiRequestSupport.CommitAsync(
                    commitContext,
                    options,
                    expectedToken,
                    createOutboxMessages)
                .ConfigureAwait(false);
            return createResult(commitContext, updated);
        };
    }

    static EntityState CreateCandidateState(
        EntityApiEndpointOptions options,
        EntitySnapshot snapshot,
        EntityState state,
        TransitionDecision decision)
    {
        if (!decision.GuaranteeDemands.CommitRequired)
        {
            return state;
        }

        var projected = TransitionStateProjector.Apply(
            ObservationValue.FromObject(state.Fields),
            decision);
        var observation = new Observation(
            shapeId: snapshot.Entity.ShapeId,
            id: snapshot.Entity.Id,
            fields: projected.Fields!,
            version: snapshot.Entity.Version + 1,
            lineage: snapshot.Entity.Lineage);
        return options.Entity.CreateState(observation);
    }

    static PortableValue ToPortableValue(object? value, ValueContract contract, bool isSupplied)
    {
        if (!isSupplied)
        {
            return PortableValue.Missing(contract);
        }

        if (value is null)
        {
            return PortableValue.Null(contract);
        }

        var observation = ObservationValue.FromObject(value);
        return observation.Kind switch
        {
            ObservationValueKind.Undefined => PortableValue.Absent(contract),
            ObservationValueKind.Null => PortableValue.Null(contract),
            _ => PortableValue.Concrete(contract, observation)
        };
    }
}
