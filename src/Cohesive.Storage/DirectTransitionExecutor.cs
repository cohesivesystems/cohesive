using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Execution;
using Cohesive.Storage.Processes;
using Cohesive.Transitions.Compilation;
using Cohesive.Transitions.Execution;

namespace Cohesive.Storage;

/// <summary>Outcome of entity Transition execution, including atomic receipt evidence when committed or replayed.</summary>
/// <param name="Result">Typed outcome and envelopes, or a structured execution failure.</param>
/// <param name="Operation">Trusted repository disposition and receipt; null when no entity commit was attempted. Diagnostics
/// can contain private storage details and must not be serialized directly to untrusted clients.</param>
public sealed record EntityTransitionExecutionResult(ProcessOperationResult Result, EntityTransitionOperationResult? Operation)
{
    /// <summary>Immutable state, decision and emission evidence; null when no receipt was committed or replayed.</summary>
    public EntityTransitionOperationReceipt? Receipt => Operation?.Receipt;
}

/// <summary>Executes an existing-entity Transition through the atomic state-and-receipt protocol.</summary>
/// <remarks>
/// Authorization is host-owned and mandatory on every call, including replay. The supplied callback must check
/// both authority scope and resource access, including disclosure of retained snapshots, before any executor repository access. Authority identifiers are not
/// credentials. This executor neither publishes envelopes nor creates a Process; the caller owns delivery from
/// retained receipts. Rejected existing-subject decisions are durable. Creation is deliberately unsupported:
/// rejected creation requires a separate durable operation ledger when there is no entity to fence.
/// </remarks>
public sealed class DirectTransitionExecutor
{
    readonly CompiledTransitionPlan plan;
    readonly IEntityRepository repository;
    readonly InteractionContractCatalog contracts;
    readonly InteractionAuthorityScope authority;
    readonly EntityReadOptions readOptions;
    readonly Func<OperationContext, EntityTransitionOperationRequest, ValueTask<bool>> authorize;
    readonly Func<TransitionEmissionIntent, int, InteractionTarget?>? createRequestTarget;

    /// <summary>Binds one exact Transition to its authorized physical execution boundary.</summary>
    /// <param name="plan">Exact compiled Transition, including entity-machine binding and provenance.</param>
    /// <param name="repository">Repository supporting atomic state and receipt commits.</param>
    /// <param name="contracts">Exact interaction catalog for emitted events and requests.</param>
    /// <param name="authority">Trusted authority for operation identity and emitted interactions.</param>
    /// <param name="authorize">Fresh scope and resource authorization, evaluated before replay lookup or execution.</param>
    /// <param name="partitionKey">Trusted partition; defaults to the repository's declared receipt partition.</param>
    /// <param name="createRequestTarget">Optional response-target policy for request emissions; never used for events.</param>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">The partition contradicts repository placement.</exception>
    /// <exception cref="InvalidOperationException">The entity-machine binding differs or the plan creates a subject.</exception>
    /// <exception cref="ProcessTransitionBindingException">The repository lacks atomic receipts or its shape differs.</exception>
    public DirectTransitionExecutor(CompiledTransitionPlan plan, IEntityRepository repository,
        InteractionContractCatalog contracts, InteractionAuthorityScope authority,
        Func<OperationContext, EntityTransitionOperationRequest, ValueTask<bool>> authorize,
        string? partitionKey = null,
        Func<TransitionEmissionIntent, int, InteractionTarget?>? createRequestTarget = null)
    {
        this.plan = plan ?? throw new ArgumentNullException(nameof(plan));
        this.repository = repository ?? throw new ArgumentNullException(nameof(repository));
        this.contracts = contracts ?? throw new ArgumentNullException(nameof(contracts));
        this.authority = authority ?? throw new ArgumentNullException(nameof(authority));
        this.authorize = authorize ?? throw new ArgumentNullException(nameof(authorize));
        this.createRequestTarget = createRequestTarget;
        if (plan.Definition.SubjectCreation is not null)
            throw new InvalidOperationException("Direct receipt-backed execution currently requires an existing subject.");
        readOptions = EntityTransitionExecutor.ReadOptions(plan, repository, partitionKey);
        var validation = EntityTransitionExecutor.ValidateBinding(plan, repository);
        if (!validation.IsValid) throw new ProcessTransitionBindingException(validation);
    }

    /// <summary>Executes or replays a caller-assigned operation with typed materialized input.</summary>
    /// <param name="context">Identity, cancellation, time and tracing; not an implicit authorization grant.</param>
    /// <param name="operation">Stable direct identity reused unchanged after timeout or lost acknowledgement.</param>
    /// <param name="subject">Exact entity subject, matching the bound repository.</param>
    /// <param name="input">Materialized input matching the compiled Transition contract.</param>
    /// <param name="expectedConcurrencyToken">Optional fresh-read fence; exact receipt replay precedes this check.</param>
    /// <returns>Durable decision and receipt, or structured execution/conflict evidence. Replay never re-decides.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">The subject or input does not match the binding.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is requested before a physical boundary.</exception>
    public async ValueTask<EntityTransitionExecutionResult> ExecuteAsync(OperationContext context,
        DirectTransitionOperation operation, InteractionEntityReference subject, PortableValue input,
        EntityConcurrencyToken? expectedConcurrencyToken = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(input);
        context.ThrowIfCancellationRequested();
        if (subject.EntityType.Value != repository.EntityType)
            throw new ArgumentException("Subject type differs from the bound repository.", nameof(subject));
        if (input.Contract != plan.Definition.Input || !PortableExecutionValidator.Validate(input).IsValid)
            throw new ArgumentException("Input must satisfy the exact compiled Transition contract.", nameof(input));
        var request = new EntityTransitionOperationRequest(operation, authority, plan.DefinitionReference, subject, input);
        if (!await authorize(context, request).ConfigureAwait(false))
            return new(ProcessOperationResult.Failed(new(EntityTransitionOperationDiagnosticCodes.AuthorizationDenied, DiagnosticSeverity.Error,
                "The direct transition operation is not authorized.")), null);
        context.ThrowIfCancellationRequested();
        var execution = await EntityTransitionExecutor.ExecuteAsync(context, repository, plan, readOptions, request,
            expectedConcurrencyToken, decision =>
            {
                var outcome = decision.Evidence.Trace.Last(trace => trace.Kind == TransitionTraceEventKind.OutcomeReturned).Node;
                var policy = new TransitionEmissionLoweringPolicy((intent, _) =>
                {
                    var emission = EntityTransitionOperationFingerprints.DirectEmission(request, intent.Node);
                    return new(emission, new TransitionInteractionOrigin(request.Transition, intent.Node, subject, outcome),
                        new(operation.Activation.Value), causationId: null, authority, new(emission.Value), ordering: null,
                        new(InteractionDurabilityDemand.Durable, InteractionVisibilityDemand.AfterOriginCommit),
                        plan.Document.Metadata.Provenance);
                }, createRequestTarget);
                var validation = TransitionEmissionEnvelopeLowerer.TryLower(decision, contracts, policy, out var envelopes);
                return (validation, envelopes);
            }).ConfigureAwait(false);
        return execution;
    }

}
