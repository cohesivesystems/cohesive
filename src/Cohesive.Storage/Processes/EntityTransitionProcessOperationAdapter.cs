using Cohesive.Execution;
using Cohesive.Processes.Execution;
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

    /// <summary>The initialized subject violates the authoritative entity definition.</summary>
    public const string SubjectInitializationInvalid = "storage.processes.transitionAdapter.subject.initializationInvalid";

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
    public ProcessTransitionOperationBinding(
        CompiledTransitionPlan plan,
        IEntityRepository repository,
        InteractionContractCatalog interactionContracts,
        Func<ProcessTransitionInvocation, InteractionEntityReference>? resolveSubject = null,
        Func<ProcessTransitionInvocation, TransitionEmissionIntent, int, InteractionTarget?>?
            createRequestTarget = null,
        string? expectedConcurrencyTokenField = null)
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
        ExpectedConcurrencyTokenField = expectedConcurrencyTokenField;
    }

    /// <summary>Exact compiled Transition plan.</summary>
    public CompiledTransitionPlan Plan { get; }

    /// <summary>Authoritative entity repository.</summary>
    public IEntityRepository Repository { get; }

    /// <summary>Exact interaction catalog for canonical emission lowering.</summary>
    public InteractionContractCatalog InteractionContracts { get; }

    /// <summary>Optional input field retaining the opaque token captured before preparation. The token
    /// participates in exact operation identity through the retained Transition input.</summary>
    public string? ExpectedConcurrencyTokenField { get; }

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

        var lookup = await binding.Repository.TryGetTransitionOperation(context, request).ConfigureAwait(false);
        if (lookup.Disposition != EntityTransitionOperationDisposition.NotFound)
        {
            return Result(lookup);
        }

        EntitySnapshot? snapshot;
        try
        {
            snapshot = await binding.Repository.TryGet(context, subject.EntityId.Value,
                expectedConcurrencyToken is null ? EntityReadOptions.Full
                    : new EntityReadOptions(expectedConcurrencyToken: expectedConcurrencyToken)).ConfigureAwait(false);
        }
        catch (ObservationConcurrencyConflictException) when (expectedConcurrencyToken is not null)
        {
            return await ChangedSubjectAsync(binding, context, request).ConfigureAwait(false);
        }
        // Preserve the binding's guarantee even when a provider does not enforce read preconditions.
        if (snapshot is not null && expectedConcurrencyToken is { } expected && snapshot.ConcurrencyToken != expected)
            return await ChangedSubjectAsync(binding, context, request).ConfigureAwait(false);

        static async ValueTask<ProcessOperationResult> ChangedSubjectAsync(
            ProcessTransitionOperationBinding binding, OperationContext context, EntityTransitionOperationRequest request)
        {
            var raced = await binding.Repository.TryGetTransitionOperation(context, request).ConfigureAwait(false);
            return raced.Disposition != EntityTransitionOperationDisposition.NotFound ? Result(raced)
                : Failure(ProcessTransitionOperationAdapterDiagnosticCodes.SubjectChanged,
                    "The authoritative subject changed after its concurrency token was captured.", "/invocation/input");
        }
        var createsSubject = binding.Plan.Definition.SubjectCreation is not null;
        if (snapshot is null && !createsSubject)
        {
            // A matching entity commit may have raced the first lookup and a later delete. Prefer its durable
            // handoff evidence over reporting the now-absent subject.
            lookup = await binding.Repository.TryGetTransitionOperation(context, request).ConfigureAwait(false);
            if (lookup.Disposition != EntityTransitionOperationDisposition.NotFound)
            {
                return Result(lookup);
            }
            return Failure(
                ProcessTransitionOperationAdapterDiagnosticCodes.SubjectMissing,
                $"No authoritative entity exists for subject '{subject.EntityId.Value}'.",
                "/invocation/subject/entityId");
        }
        if (snapshot is not null && createsSubject)
        {
            // Prefer exact handoff evidence if the same operation won a race after the first lookup.
            lookup = await binding.Repository.TryGetTransitionOperation(context, request).ConfigureAwait(false);
            if (lookup.Disposition != EntityTransitionOperationDisposition.NotFound)
            {
                return Result(lookup);
            }

            // A replacement Process attempt has a fresh exact occurrence, but unique-subject creation is naturally
            // idempotent when the retained authority, Transition, subject, and input are identical. Replaying the
            // original receipt also preserves its canonical envelopes and target-deduplication identities.
            lookup = await binding.Repository.TryGetCreationTransitionOperation(context, request).ConfigureAwait(false);
            if (lookup.Disposition != EntityTransitionOperationDisposition.NotFound)
            {
                return Result(lookup);
            }
            return Failure(
                ProcessTransitionOperationAdapterDiagnosticCodes.SubjectPresent,
                $"Authoritative entity '{subject.EntityId.Value}' already exists for a creation Transition.",
                "/invocation/subject/entityId");
        }

        var decision = createsSubject
            ? TransitionReferenceInterpreter.DecideCreation(
                binding.Plan,
                invocation.Activation,
                invocation.Input)
            : TransitionReferenceInterpreter.DecideFullState(
                binding.Plan,
                invocation.Activation,
                invocation.Input,
                PortableValue.Concrete(
                    binding.Plan.Definition.Observation,
                    ObservationValue.FromObject(snapshot!.Entity.Observation.Fields)));
        if (decision.Kind is not (TransitionDecisionKind.Applied
            or TransitionDecisionKind.NoChange
            or TransitionDecisionKind.AdmissionRejected
            or TransitionDecisionKind.DomainRejected)
            || decision.Outcome is null)
        {
            return decision.Diagnostics.IsEmpty
                ? Failure(
                    ProcessTransitionOperationAdapterDiagnosticCodes.DecisionNotCommittable,
                    $"Transition '{invocation.Definition.DefinitionId.Value}' produced non-committable decision '{decision.Kind}'.",
                    "/decision/kind")
                : ProcessOperationResult.Failed(decision.Diagnostics[0]);
        }

        var lowering = ProcessTransitionEmissionEnvelopeLowerer.TryLower(
            invocation,
            subject,
            decision,
            binding.InteractionContracts,
            (intent, index) => binding.CreateRequestTarget(invocation, intent, index),
            out var envelopes);
        if (!lowering.IsValid)
        {
            return ProcessOperationResult.Failed(lowering.Diagnostics[0]);
        }

        var result = ProcessOperationResult.Completed(decision.Outcome, envelopes);
        if (createsSubject && decision.Kind != TransitionDecisionKind.Applied)
        {
            // A rejected creation is pure: no subject exists to mutate, and the enclosing Process commit retains
            // the deterministic result and any Process-owned envelope handoff.
            return result;
        }

        ObservationValue baseState;
        if (createsSubject)
        {
            var initial = decision.Evidence.InitialObservation;
            if (initial is null
                || initial.State != PortableValueState.Concrete
                || initial.Value is not { } initialValue
                || initialValue.Fields is null)
            {
                return Failure(
                    ProcessTransitionOperationAdapterDiagnosticCodes.DecisionNotCommittable,
                    "A successful creation Transition did not retain a concrete complete initial observation.",
                    "/decision/evidence/initialObservation");
            }
            baseState = initialValue;
        }
        else
        {
            baseState = ObservationValue.FromObject(snapshot!.Entity.Observation.Fields);
        }

        var projected = TransitionStateProjector.Apply(
            baseState,
            decision);
        var candidateVersion = createsSubject
            ? 0
            : decision.Kind == TransitionDecisionKind.Applied
                ? checked(snapshot!.Entity.Version + 1)
                : snapshot!.Entity.Version;
        EntityState candidateState;
        try
        {
            candidateState = binding.Repository.EntityDefinition.CreateState(
                subject.EntityId.Value,
                projected.Fields!,
                candidateVersion);
            if (createsSubject)
                binding.Repository.EntityDefinition.ValidateState(candidateState);
        }
        catch (SemanticRuleViolationException exception)
        {
            return Failure(
                createsSubject
                    ? ProcessTransitionOperationAdapterDiagnosticCodes.SubjectInitializationInvalid
                    : ProcessTransitionOperationAdapterDiagnosticCodes.DecisionNotCommittable,
                exception.Message,
                createsSubject
                    ? "/decision/evidence/initialObservation"
                    : "/decision/candidateObservation");
        }
        var candidate = candidateState.Snapshot;

        var commit = new EntityTransitionOperationCommit(
            request,
            new(candidate, createsSubject ? null : snapshot!.ConcurrencyToken),
            decision.Kind,
            result,
            decision.GuaranteeDemands,
            decision.Evidence,
            createsSubject
                ? EntityTransitionSubjectCondition.MustBeAbsent
                : EntityTransitionSubjectCondition.MustExist);
        var committed = await binding.Repository.CommitTransitionOperation(context, commit).ConfigureAwait(false);
        return Result(committed);
    }

    static ProcessOperationResult Result(EntityTransitionOperationResult operation) => operation.Receipt is { } receipt
        ? receipt.Result
        : ProcessOperationResult.Failed(operation.Diagnostics.FirstOrDefault()
            ?? new(
                ProcessTransitionOperationAdapterDiagnosticCodes.DecisionNotCommittable,
                DiagnosticSeverity.Error,
                $"Entity Transition operation ended with '{operation.Disposition}' without receipt or diagnostic evidence.",
                "/entityOperation"));

    static ProcessOperationResult Failure(string code, string message, string location) =>
        ProcessOperationResult.Failed(new(code, DiagnosticSeverity.Error, message, location));
}
