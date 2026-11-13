using Cohesive.Api;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Storage;
using Cohesive.Transitions.Authoring;
using Cohesive.Transitions.Execution;
using Microsoft.AspNetCore.Http;

namespace Cohesive.Adapters.AspNet.Entities;

/// <summary>Optional preparation and commit policies for a typed entity transition command.</summary>
/// <typeparam name="TRequest">JSON request body type.</typeparam>
public sealed class EntityTransitionCommandPolicy<TRequest>
{
    /// <summary>Exact graph for transitions whose canonical contracts reference graph-qualified shapes.</summary>
    public ShapeGraph? ShapeGraph { get; init; }

    /// <summary>Optional request-specific concurrency fence. Null uses the loaded concurrency token.</summary>
    public Func<EntityApiRequestContext, TRequest, EntityConcurrencyToken?>? ExpectedConcurrencyToken { get; init; }

    /// <summary>Exact interaction contracts required when the transition emits.</summary>
    public InteractionContractCatalog? InteractionContracts { get; init; }

    /// <summary>Request-scoped policy for lowering transition emissions into the outbox.</summary>
    public Func<EntityApiCommitContext, TransitionEmissionLoweringPolicy>? CreateEmissionPolicy { get; init; }
}

/// <summary>Completes and binds a typed entity transition command.</summary>
public static class EntityTransitionCommandExtensions
{
    /// <summary>Declares a JSON body, pins the authored transition, and completes the command and its ASP.NET binding.</summary>
    /// <typeparam name="TResource">Entity resource exposed by the command builder.</typeparam>
    /// <typeparam name="TEntity">Entity authoring type.</typeparam>
    /// <typeparam name="TRequest">JSON request type declared by the input projection.</typeparam>
    /// <typeparam name="TInput">Canonical input type of the transition.</typeparam>
    /// <typeparam name="TOutcome">Canonical outcome type of the transition.</typeparam>
    /// <param name="command">Entity command declaration, including its HTTP route and any response contracts.</param>
    /// <param name="options">Registration options that will map this endpoint.</param>
    /// <param name="transition">Exact authored transition to compile at registration.</param>
    /// <param name="createInput">Projects a deserialized request into the transition input.</param>
    /// <param name="createResult">Projects the decision and effective snapshot into an HTTP result.</param>
    /// <param name="policy">Optional graph, concurrency, and emission settings.</param>
    /// <returns>The endpoint added to the API definition builder and bound by <paramref name="options"/>.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">The command declares an incompatible request contract.</exception>
    /// <exception cref="InvalidOperationException">The command cannot be finalized or pins another transition.</exception>
    /// <exception cref="TransitionApiPreparationException">The authored transition failed compilation.</exception>
    /// <exception cref="NotSupportedException">Canonical transition content contains an unsupported runtime value.</exception>
    /// <remarks>Complete the command before taking the API definition snapshot with <see cref="ApiBuilder.Build"/>.</remarks>
    public static ApiEndpoint BindTransition<TResource, TEntity, TRequest, TInput, TOutcome>(
        this OperationBuilder<EntityApiBuilder<TResource>> command,
        EntityApiEndpointOptions options,
        Transition<TEntity, TInput, TOutcome> transition,
        Func<EntityApiRequestContext, TRequest, TInput> createInput,
        Func<EntityApiCommitContext, EntitySnapshot, IResult> createResult,
        EntityTransitionCommandPolicy<TRequest>? policy = null)
        where TEntity : notnull
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(transition);
        ArgumentNullException.ThrowIfNull(createInput);
        ArgumentNullException.ThrowIfNull(createResult);
        var compilation = policy?.ShapeGraph is { } graph ? transition.Compile(graph) : transition.Compile();
        if (!compilation.IsSuccessful)
            throw new TransitionApiPreparationException(command.OperationName, compilation);

        var endpoint = command.BuildEntityTransition<TRequest>(compilation.Plan!.DefinitionReference);
        var expectedToken = policy?.ExpectedConcurrencyToken;
        options.Bind(EntityApiOperationBinding.Transition(endpoint, compilation.Plan,
            (context, request) => createInput(context, (TRequest)request!),
            createResult,
            expectedToken is null ? null : (context, request) => expectedToken(context, (TRequest)request!),
            policy?.InteractionContracts,
            policy?.CreateEmissionPolicy));
        return endpoint;
    }
}
