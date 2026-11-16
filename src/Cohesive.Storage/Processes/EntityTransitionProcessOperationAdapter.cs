using Cohesive.Model.Serialization;
using Cohesive.Execution;
using Cohesive.Processes.Execution;
using Cohesive.Processes.IR;
using Cohesive.Transitions.Compilation;
using Cohesive.Transitions.Execution;
using Cohesive.Transitions.IR;
using Cohesive.Transitions.Model;

namespace Cohesive.Storage.Processes;

/// <summary>Stable diagnostics produced by the Process-to-entity Transition operation adapter.</summary>
public static class ProcessTransitionOperationAdapterDiagnosticCodes
{
    /// <summary>No exact Transition plan and entity repository binding was available.</summary>
    public const string BindingUnavailable = "storage.processes.transitionAdapter.binding.unavailable";

    /// <summary>The resolved binding did not contain the exact invoked Transition plan.</summary>
    public const string PlanInexact = "storage.processes.transitionAdapter.plan.inexact";

    /// <summary>The portable Process subject could not be resolved to the binding's entity authority.</summary>
    public const string SubjectInvalid = "storage.processes.transitionAdapter.subject.invalid";

    /// <summary>No authoritative entity state exists for the resolved subject.</summary>
    public const string SubjectMissing = "storage.processes.transitionAdapter.subject.missing";

    /// <summary>The current subject no longer matches the captured opaque concurrency token.</summary>
    public const string SubjectChanged = "storage.processes.transitionAdapter.subject.changed";

    /// <summary>The declared captured-token field is missing or is not a non-empty string.</summary>
    public const string CapturedConcurrencyTokenInvalid = "storage.processes.transitionAdapter.concurrencyToken.invalid";

    /// <summary>An authoritative entity already exists for a Transition that requires subject absence.</summary>
    public const string SubjectPresent = "storage.processes.transitionAdapter.subject.present";

    /// <summary>The Transition did not produce a committable typed decision.</summary>
    public const string DecisionNotCommittable = "storage.processes.transitionAdapter.decision.notCommittable";
}

/// <summary>Asynchronously realizes one exact Process-invoked Transition operation.</summary>
/// <remarks>
/// The adapter returns evidence to the Process interpreter; it does not own a Process ledger or publish envelopes.
/// Entity mutation and the replayable handoff receipt remain atomic under the selected entity repository.
/// </remarks>
public interface IProcessTransitionOperationAdapter
{
    /// <summary>Executes or replays one exact entity-side Transition operation.</summary>
    /// <param name="context">Operation context, time, tracing, identity, and cancellation.</param>
    /// <param name="invocation">Exact Process Transition occurrence and typed values.</param>
    /// <returns>The typed Transition outcome and canonical handoff envelopes, or structured failure evidence.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="context"/> or <paramref name="invocation"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="OperationCanceledException">The operation is cancelled before a physical boundary.</exception>
    ValueTask<ProcessOperationResult> ExecuteAsync(
        OperationContext context,
        ProcessTransitionInvocation invocation);
}

/// <summary>Exact semantic and physical binding for one Process-invoked Transition.</summary>
public sealed class ProcessTransitionOperationBinding
{
    readonly Func<ProcessTransitionInvocation, InteractionEntityReference>? resolveSubject;
    readonly Func<ProcessTransitionInvocation, TransitionEmissionIntent, int, InteractionTarget?>?
        createRequestTarget;

    /// <summary>Creates one exact Transition-operation binding.</summary>
    /// <param name="plan">Exact compiled Transition plan.</param>
    /// <param name="repository">Authoritative repository for subjects selected by this binding.</param>
    /// <param name="interactionContracts">Exact catalog used to lower and validate Transition emissions.</param>
    /// <param name="resolveSubject">
    /// Optional custom portable-subject resolver. The default requires a concrete string entity identity and uses
    /// the repository's semantic entity type.
    /// </param>
    /// <param name="createRequestTarget">
    /// Optional explicit response-target policy for Request emission intents. Domain Events do not use it.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="plan"/>, <paramref name="repository"/>, or <paramref name="interactionContracts"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <param name="expectedConcurrencyTokenField">Optional required string input field containing the
    /// captured opaque storage token. Supported only for existing-subject transitions. Exact receipt replay
    /// precedes the current-state check; a fresh invocation fails when the subject changed.</param>
    /// <param name="partitionKey">Optional trusted subject-read partition. Inherits fixed receipt placement and rejects an explicit mismatch.</param>
    /// <exception cref="InvalidOperationException">The plan omits or changes the repository entity machine binding.</exception>
    public ProcessTransitionOperationBinding(
        CompiledTransitionPlan plan,
        IEntityRepository repository,
        InteractionContractCatalog interactionContracts,
        Func<ProcessTransitionInvocation, InteractionEntityReference>? resolveSubject = null,
        Func<ProcessTransitionInvocation, TransitionEmissionIntent, int, InteractionTarget?>?
            createRequestTarget = null,
        string? expectedConcurrencyTokenField = null,
        string? partitionKey = null)
    {
        Plan = plan ?? throw new ArgumentNullException(nameof(plan));
        Repository = repository ?? throw new ArgumentNullException(nameof(repository));
        InteractionContracts = interactionContracts ?? throw new ArgumentNullException(nameof(interactionContracts));
        this.resolveSubject = resolveSubject;
        this.createRequestTarget = createRequestTarget;
        if (expectedConcurrencyTokenField is not null)
        {
            var field = (plan.Definition.Input.Type as ObjectTypeRef)?.Fields
                .SingleOrDefault(field => field.Name == expectedConcurrencyTokenField);
            if (plan.Definition.SubjectCreation is not null || field is null
                || field.Type is not ScalarTypeRef { Kind: ScalarTypeKind.String }
                || field.Cardinality != FieldCardinality.Single || field.Presence != FieldPresence.Required
                || field.Nullability != FieldNullability.NonNullable)
                throw new ArgumentException("A captured concurrency token requires an existing subject and a required non-null string input field.", nameof(expectedConcurrencyTokenField));
        }
        SubjectReadOptions = EntityTransitionExecutor.ReadOptions(plan, repository, partitionKey);
        PartitionKey = SubjectReadOptions.PartitionKey;
        ExpectedConcurrencyTokenField = expectedConcurrencyTokenField;
    }

    /// <summary>Trusted subject-read partition; no partition is inferred from client input.</summary>
    public string? PartitionKey { get; }
    internal EntityReadOptions SubjectReadOptions { get; }

    /// <summary>Exact compiled Transition plan.</summary>
    public CompiledTransitionPlan Plan { get; }

    /// <summary>Authoritative entity repository.</summary>
    public IEntityRepository Repository { get; }

    /// <summary>Exact interaction catalog for canonical emission lowering.</summary>
    public InteractionContractCatalog InteractionContracts { get; }

    /// <summary>Optional input field retaining the opaque token captured before preparation. The token
    /// participates in exact operation identity through the retained Transition input.</summary>
    public string? ExpectedConcurrencyTokenField { get; }

    /// <summary>Attests the native receipt contract after checking this repository's declared capability.</summary>
    /// <returns>Exact domain input/output linking evidence plus the separate host receipt contract.</returns>
    /// <exception cref="ProcessTransitionBindingException">The repository cannot atomically commit state and receipt,
    /// or its entity shape does not match the Transition observation.</exception>
    /// <remarks>No repository read or write occurs. This checks provider-declared capability; provider
    /// conformance must independently qualify that claim.</remarks>
    public ProcessDefinitionLink CreateProcessDefinitionLink()
    {
        var validation = ValidateDefinitionLink();
        if (!validation.IsValid) throw new ProcessTransitionBindingException(validation);
        return new(Plan.DefinitionReference, ProcessDefinitionLinkKind.Transition, Plan.Definition.Input,
            Plan.Definition.Outcome, receiptContract: EntityTransitionReceiptReferences.ValueContract);
    }

    /// <summary>Checks native receipt capability and observation authority without performing IO.</summary>
    /// <returns>Independent, coded diagnostics for each unsatisfied binding requirement.</returns>
    DocumentValidationResult ValidateDefinitionLink() => EntityTransitionExecutor.ValidateBinding(Plan, Repository);

    internal EntityConcurrencyToken? ResolveExpectedConcurrencyToken(ProcessTransitionInvocation invocation)
    {
        if (ExpectedConcurrencyTokenField is not { } field) return null;
        if (invocation.Input.State != PortableValueState.Concrete || invocation.Input.Value is not { } input
            || !input.TryGetProperty(field, out var value) || value.Kind != ObservationValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
            throw new InvalidOperationException("The captured concurrency token input must contain a non-empty string.");
        return new(value.GetRequiredString());
    }

    internal InteractionEntityReference ResolveSubject(ProcessTransitionInvocation invocation)
    {
        if (resolveSubject is not null)
        {
            return resolveSubject(invocation)
                ?? throw new InvalidOperationException("A Transition-operation subject resolver returned null.");
        }

        var id = invocation.Subject.State == PortableValueState.Concrete
            ? invocation.Subject.Value?.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new InvalidOperationException(
                "The conventional Process Transition subject must be a concrete non-empty string entity identity.");
        }
        return new(new(Repository.EntityType), new(id));
    }

    internal InteractionTarget? CreateRequestTarget(
        ProcessTransitionInvocation invocation,
        TransitionEmissionIntent intent,
        int index) => createRequestTarget?.Invoke(invocation, intent, index);
}

/// <summary>
/// General adapter from Process Transition occurrences to atomic entity state-and-receipt operations.
/// </summary>
public sealed class EntityTransitionProcessOperationAdapter : IProcessTransitionOperationAdapter
{
    readonly Func<ProcessTransitionInvocation, ProcessTransitionOperationBinding?> resolveBinding;
    readonly object subscriptionGate = new();
    FailureSubscription[] failureSubscriptions = [];

    /// <summary>Subscribes to private failures produced only by this adapter instance.</summary>
    /// <param name="observer">Synchronous, thread-safe operator sink; receives private native details, never public response data.</param>
    /// <returns>Idempotent subscription handle. Dispose to release the observer; a delivery already in flight may still invoke its callback.</returns>
    /// <exception cref="ArgumentNullException">Observer is null.</exception>
    /// <remarks>No history is retained. Concurrent operations may invoke observers concurrently. Each observer's
    /// recoverable failure is isolated from the operation and other observers. Route payloads only to protected sinks.</remarks>
    public IDisposable SubscribeTransitionFailures(Action<EntityTransitionFailureDiagnostic> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        var subscription = new FailureSubscription(this, observer);
        lock (subscriptionGate) Volatile.Write(ref failureSubscriptions, [.. failureSubscriptions, subscription]);
        return subscription;
    }

    void RecordFailure(OperationContext context, EntityTransitionOperationResult result)
    {
        var subscriptions = Volatile.Read(ref failureSubscriptions);
        if (subscriptions.Length == 0) return;
        var diagnostic = new EntityTransitionFailureDiagnostic(context.TraceContext, result);
        foreach (var subscription in subscriptions) subscription.Deliver(diagnostic);
    }

    sealed class FailureSubscription(EntityTransitionProcessOperationAdapter owner,
        Action<EntityTransitionFailureDiagnostic> observer) : IDisposable
    {
        Action<EntityTransitionFailureDiagnostic>? callback = observer;
        EntityTransitionProcessOperationAdapter? source = owner;
        public void Dispose()
        {
            Interlocked.Exchange(ref callback, null);
            var current = Interlocked.Exchange(ref source, null);
            if (current is null) return;
            lock (current.subscriptionGate)
                Volatile.Write(ref current.failureSubscriptions, [.. current.failureSubscriptions.Where(item => !ReferenceEquals(item, this))]);
        }
        internal void Deliver(EntityTransitionFailureDiagnostic diagnostic)
        {
            var target = Volatile.Read(ref callback);
            if (target is null) return;
            try { target(diagnostic); }
            catch (Exception exception) when (exception is not (OutOfMemoryException or AccessViolationException))
            {
                // Report only the count, never the private diagnostic or observer exception.
                ExecutionTelemetry.RecordDiagnosticSubscriberFailure();
            }
        }
    }

    /// <summary>Creates an adapter over an exact, caller-owned binding resolver.</summary>
    /// <param name="resolveBinding">
    /// Resolves the exact Transition plan, entity repository, interaction catalog, and optional subject/target policy.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="resolveBinding"/> is <see langword="null"/>.</exception>
    public EntityTransitionProcessOperationAdapter(
        Func<ProcessTransitionInvocation, ProcessTransitionOperationBinding?> resolveBinding) =>
        this.resolveBinding = resolveBinding ?? throw new ArgumentNullException(nameof(resolveBinding));

    /// <inheritdoc />
    public async ValueTask<ProcessOperationResult> ExecuteAsync(
        OperationContext context,
        ProcessTransitionInvocation invocation)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(invocation);
        context.ThrowIfCancellationRequested();

        var binding = resolveBinding(invocation);
        if (binding is null)
        {
            return Failure(
                ProcessTransitionOperationAdapterDiagnosticCodes.BindingUnavailable,
                $"No exact entity Transition binding is available for '{invocation.Definition.DefinitionId.Value}'.",
                "/invocation/definition");
        }
        if (binding.Plan.DefinitionReference != invocation.Definition)
        {
            return Failure(
                ProcessTransitionOperationAdapterDiagnosticCodes.PlanInexact,
                "The resolved Transition plan does not match the exact invoked definition, revision, and fingerprint.",
                "/binding/plan");
        }

        InteractionEntityReference subject;
        EntityConcurrencyToken? expectedConcurrencyToken;
        try
        {
            subject = binding.ResolveSubject(invocation);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return Failure(
                ProcessTransitionOperationAdapterDiagnosticCodes.SubjectInvalid,
                exception.Message,
                "/invocation/subject");
        }
        try
        {
            expectedConcurrencyToken = binding.ResolveExpectedConcurrencyToken(invocation);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return Failure(ProcessTransitionOperationAdapterDiagnosticCodes.CapturedConcurrencyTokenInvalid,
                exception.Message, "/invocation/input");
        }
        if (!string.Equals(subject.EntityType.Value, binding.Repository.EntityType, StringComparison.Ordinal))
        {
            return Failure(
                ProcessTransitionOperationAdapterDiagnosticCodes.SubjectInvalid,
                $"Resolved entity type '{subject.EntityType.Value}' does not match repository authority '{binding.Repository.EntityType}'.",
                "/invocation/subject/entityType");
        }

        var operation = new ProcessOperationOccurrence(
            invocation.Continuation,
            invocation.Activation,
            invocation.Token,
            invocation.Node,
            invocation.Occurrence);
        var request = new EntityTransitionOperationRequest(
            operation,
            invocation.Context.AuthorityScope,
            invocation.Definition,
            subject,
            invocation.Input);

        var execution = await EntityTransitionExecutor.ExecuteAsync(context, binding.Repository, binding.Plan,
            binding.SubjectReadOptions, request, expectedConcurrencyToken, decision =>
            {
                var validation = ProcessTransitionEmissionEnvelopeLowerer.TryLower(invocation, subject, decision,
                    binding.InteractionContracts, (intent, index) => binding.CreateRequestTarget(invocation, intent, index),
                    out var envelopes);
                return (validation, envelopes);
            }).ConfigureAwait(false);
        if (execution.Operation is { Receipt: null } operationResult) RecordFailure(context, operationResult);
        return execution.Result;
    }

    static ProcessOperationResult Failure(string code, string message, string location) =>
        ProcessOperationResult.Failed(new(code, DiagnosticSeverity.Error, message, location));
}
