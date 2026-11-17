using Cohesive.Execution;
using Cohesive.Storage.Processes;
using Cohesive.Transitions.Authoring;
using Cohesive.Transitions.Compilation;
using Cohesive.Transitions.Execution;

namespace Cohesive.Storage;

/// <summary>Repository-bound typed entry point for direct, receipt-backed entity Transitions.</summary>
/// <remarks>
/// Canonical Transition documents remain authoritative. This facade only prepares the existing direct executor
/// and projects CLR inputs using its exact input contract. Authorization, replay, concurrency and delivery
/// semantics are unchanged. Preparation is shared concurrently per document instance within this binding;
/// weak keys do not retain abandoned revisions. Failed preparation is retryable. Inputs are projected afresh
/// on every call and are never cached. Repositories and callbacks must support the host's concurrency. Call <see cref="Prepare{TEntity,TInput,TOutcome}"/> during host setup
/// to diagnose unsupported plans before accepting requests. Outcomes and receipts retain the canonical result contract.
/// </remarks>
public sealed class EntityTransitions
{
    static readonly InteractionContractCatalog EmptyContracts = CreateEmptyContracts();
    readonly IEntityRepository repository;
    readonly InteractionAuthorityScope authority;
    readonly Func<OperationContext, EntityTransitionOperationRequest, ValueTask<bool>> authorize;
    readonly InteractionContractCatalog contracts;
    readonly string? partitionKey;
    readonly ShapeGraph? shapeGraph;
    readonly Func<TransitionEmissionIntent, int, InteractionTarget?>? createRequestTarget;
    readonly WeakPreparationCache<ExecutionDefinitionDocument, (DirectTransitionExecutor Executor, ValueContract Input)> prepared = new();
    readonly Func<ExecutionDefinitionDocument, (DirectTransitionExecutor Executor, ValueContract Input)> prepare;

    /// <summary>Configures one repository's direct execution boundary without performing IO.</summary>
    /// <param name="repository">Authoritative entity repository; native atomic receipt capability is checked during preparation.</param>
    /// <param name="authority">Trusted authority used for all calls through this binding.</param>
    /// <param name="authorize">Mandatory fresh scope, resource and receipt-disclosure authorization, including on replay.</param>
    /// <param name="interactionContracts">Exact emission contracts; defaults to an empty catalog for transitions without emissions.</param>
    /// <param name="partitionKey">Trusted partition; defaults to the repository's receipt partition when a transition is prepared.</param>
    /// <param name="shapeGraph">Optional immutable graph used to resolve referenced contracts during compilation.</param>
    /// <param name="createRequestTarget">Optional response-target policy for request emissions, forwarded unchanged to the direct executor.</param>
    /// <exception cref="ArgumentNullException">The repository, authority or authorization callback is null.</exception>
    public EntityTransitions(IEntityRepository repository, InteractionAuthorityScope authority,
        Func<OperationContext, EntityTransitionOperationRequest, ValueTask<bool>> authorize,
        InteractionContractCatalog? interactionContracts = null, string? partitionKey = null,
        ShapeGraph? shapeGraph = null,
        Func<TransitionEmissionIntent, int, InteractionTarget?>? createRequestTarget = null)
    {
        this.repository = repository ?? throw new ArgumentNullException(nameof(repository));
        this.authority = authority ?? throw new ArgumentNullException(nameof(authority));
        this.authorize = authorize ?? throw new ArgumentNullException(nameof(authorize));
        contracts = interactionContracts ?? EmptyContracts;
        this.partitionKey = partitionKey;
        this.shapeGraph = shapeGraph;
        this.createRequestTarget = createRequestTarget;
        prepare = PrepareDocument;
    }

    /// <summary>Compiles and validates a typed transition's physical binding once, without authorization or IO.</summary>
    /// <typeparam name="TEntity">Authored entity type.</typeparam>
    /// <typeparam name="TInput">Authored input type.</typeparam>
    /// <typeparam name="TOutcome">Authored outcome type.</typeparam>
    /// <param name="transition">Exact immutable authored transition revision.</param>
    /// <exception cref="ArgumentNullException">The transition is null.</exception>
    /// <exception cref="ArgumentException">The configured partition contradicts repository placement.</exception>
    /// <exception cref="ProcessTransitionBindingException">Compilation fails or the repository lacks the exact shape or atomic receipt capability. Validation retains the original diagnostics.</exception>
    /// <exception cref="InvalidOperationException">The plan creates a subject, changes the entity-machine binding, or has no stable canonical representation.</exception>
    /// <exception cref="NotSupportedException">The document contains an unsupported runtime value.</exception>
    public void Prepare<TEntity, TInput, TOutcome>(Transition<TEntity, TInput, TOutcome> transition) where TEntity : notnull
    {
        ArgumentNullException.ThrowIfNull(transition);
        _ = prepared.Get(transition.Document, prepare);
    }

    /// <summary>Executes or replays a typed command through the existing direct executor.</summary>
    /// <typeparam name="TEntity">Authored entity type.</typeparam>
    /// <typeparam name="TInput">Command type, determined by the typed transition handle.</typeparam>
    /// <typeparam name="TOutcome">Authored outcome type; the returned evidence retains its canonical contract.</typeparam>
    /// <param name="transition">Exact authored transition; prepared on first use if not prepared at setup.</param>
    /// <param name="subjectId">Entity identity within the bound repository.</param>
    /// <param name="input">Caller-owned command, projected afresh; do not mutate it concurrently with projection. Null is allowed only by the transition's input contract.</param>
    /// <param name="operationId">Stable caller-assigned identity, unique within the repository and reused unchanged on retry.</param>
    /// <param name="context">Caller identity, time, tracing and cancellation; not an authorization grant.</param>
    /// <param name="expectedConcurrencyToken">Optional current-state fence; exact receipt replay precedes its check.</param>
    /// <returns>The existing canonical outcome, diagnostics and atomic receipt evidence, with no extra commit or retry.</returns>
    /// <exception cref="ArgumentNullException">The transition or context is null.</exception>
    /// <exception cref="ArgumentException">An identity is empty, the projected command violates its exact contract, or the partition is inconsistent.</exception>
    /// <exception cref="ProcessTransitionBindingException">Compilation or native binding validation fails.</exception>
    /// <exception cref="InvalidOperationException">The plan creates a subject, changes the entity-machine binding, or cannot be represented canonically.</exception>
    /// <exception cref="NotSupportedException">The document or CLR command cannot be projected through the supported portable representation.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is requested before a physical boundary.</exception>
    public ValueTask<EntityTransitionExecutionResult> ExecuteAsync<TEntity, TInput, TOutcome>(
        Transition<TEntity, TInput, TOutcome> transition, string subjectId, TInput input,
        ActivationId operationId, OperationContext context, EntityConcurrencyToken? expectedConcurrencyToken = null)
        where TEntity : notnull
    {
        ArgumentNullException.ThrowIfNull(transition);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectId);
        context.ThrowIfCancellationRequested();
        var operation = new DirectTransitionOperation(operationId);
        var binding = prepared.Get(transition.Document, prepare);
        var value = input is null ? PortableValue.Null(binding.Input)
            : PortableValue.Concrete(binding.Input, ObservationValue.FromObject(input));
        return binding.Executor.ExecuteAsync(context, operation,
            new(new(repository.EntityType), new(subjectId)), value, expectedConcurrencyToken);
    }

    (DirectTransitionExecutor Executor, ValueContract Input) PrepareDocument(ExecutionDefinitionDocument document)
    {
        var compilation = TransitionStaticCompiler.Compile(document, shapeGraph);
        if (!compilation.IsSuccessful) throw new ProcessTransitionBindingException(compilation.Validation);
        var plan = compilation.Plan!;
        return (new(plan, repository, contracts, authority, authorize, partitionKey, createRequestTarget), plan.Definition.Input);
    }

    static InteractionContractCatalog CreateEmptyContracts()
    {
        var validation = InteractionContractCatalog.TryCreate([], out var catalog);
        if (!validation.IsValid) throw new ProcessTransitionBindingException(validation);
        return catalog!;
    }
}
