using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Execution;
using Cohesive.Storage.Processes;
using Cohesive.Transitions.Execution;
using Cohesive.Transitions.IR;

namespace Cohesive.Storage;

/// <summary>Stable diagnostics for atomic Process-invoked Transition operation commits.</summary>
public static class EntityTransitionOperationDiagnosticCodes
{
    /// <summary>The entity repository cannot atomically commit entity state and a Transition operation receipt.</summary>
    public const string CapabilityInsufficient = "storage.entityTransition.operation.capability.insufficient";

    /// <summary>An operation occurrence identity was reused for different canonical content.</summary>
    public const string IdentityConflict = "storage.entityTransition.operation.identity.conflict";

    /// <summary>The entity concurrency fence no longer identifies the authoritative aggregate state.</summary>
    public const string ConcurrencyConflict = "storage.entityTransition.operation.concurrency.conflict";

    /// <summary>The authoritative subject presence contradicts the Transition's declared subject condition.</summary>
    public const string SubjectStateConflict = "storage.entityTransition.operation.subject.stateConflict";
}

/// <summary>Native atomic Transition operation behavior supported by an entity repository.</summary>
/// <param name="SupportsAtomicStateAndReceipt">
/// Whether entity state and one Process Transition operation receipt share one indivisible commit boundary.
/// </param>
public sealed record EntityTransitionOperationCapabilities(bool SupportsAtomicStateAndReceipt)
{
    /// <summary>Capability evidence for a repository with no atomic Transition operation protocol.</summary>
    public static EntityTransitionOperationCapabilities Unsupported { get; } = new(
        SupportsAtomicStateAndReceipt: false);

    /// <summary>Capability evidence for a repository that atomically commits entity state and operation receipts.</summary>
    public static EntityTransitionOperationCapabilities AtomicStateAndReceipt { get; } = new(
        SupportsAtomicStateAndReceipt: true);
}

/// <summary>Physical publication authority for canonical envelopes retained by an entity-side receipt.</summary>
public enum EntityTransitionEmissionPublicationAuthority
{
    /// <summary>No publication authority was declared; invalid in a retained receipt.</summary>
    Unspecified = 0,

    /// <summary>
    /// The entity receipt is durable handoff evidence; the invoking Process outbox is the sole publication authority.
    /// </summary>
    ProcessOutbox = 1
}

/// <summary>Authoritative subject-state condition required by one Transition operation commit.</summary>
public enum EntityTransitionSubjectCondition
{
    /// <summary>The subject must exist and match the supplied optimistic-concurrency token.</summary>
    MustExist = 0,

    /// <summary>The subject must be absent when initial entity state and the operation receipt commit.</summary>
    MustBeAbsent = 1
}

/// <summary>Exact replay lookup identity for one Process-invoked Transition operation.</summary>
public sealed record EntityTransitionOperationRequest
{
    /// <summary>Creates an exact Transition operation request identity.</summary>
    /// <param name="operation">Attempt-, activation-, token-, node-, and occurrence-scoped Process operation.</param>
    /// <param name="transition">Exact invoked Transition definition.</param>
    /// <param name="subject">Authoritative aggregate subject.</param>
    /// <param name="input">Typed materialized Transition input.</param>
    /// <exception cref="ArgumentNullException">Any reference argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="input"/> is unknown or failed.</exception>
    public EntityTransitionOperationRequest(
        ProcessOperationOccurrence operation,
        ExecutionDefinitionReference transition,
        InteractionEntityReference subject,
        PortableValue input)
    {
        Operation = operation ?? throw new ArgumentNullException(nameof(operation));
        Transition = transition ?? throw new ArgumentNullException(nameof(transition));
        Subject = subject ?? throw new ArgumentNullException(nameof(subject));
        Input = RequireMaterialized(input, nameof(input), "Transition input");
        Fingerprint = EntityTransitionOperationFingerprints.Request(this);
    }

    /// <summary>Exact Process operation occurrence.</summary>
    public ProcessOperationOccurrence Operation { get; }

    /// <summary>Exact invoked Transition definition.</summary>
    public ExecutionDefinitionReference Transition { get; }

    /// <summary>Authoritative aggregate subject.</summary>
    public InteractionEntityReference Subject { get; }

    /// <summary>Typed materialized Transition input.</summary>
    public PortableValue Input { get; }

    /// <summary>Canonical fingerprint of the complete replay lookup identity.</summary>
    public ProcessCommitFingerprint Fingerprint { get; }

    static PortableValue RequireMaterialized(PortableValue value, string parameterName, string description)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (value.State is PortableValueState.Unknown or PortableValueState.Failed)
        {
            throw new ArgumentException($"{description} must be materialized.", parameterName);
        }
        return value;
    }
}

/// <summary>Complete candidate entity-state and Transition-result intent for one atomic commit.</summary>
public sealed record EntityTransitionOperationCommit
{
    /// <summary>Creates a validated atomic Transition operation commit intent.</summary>
    /// <param name="request">Exact replay lookup identity.</param>
    /// <param name="write">Candidate entity state and the concurrency fence required by the subject condition.</param>
    /// <param name="decisionKind">Committable terminal Transition decision category.</param>
    /// <param name="result">Typed outcome and canonical handoff envelopes.</param>
    /// <param name="guaranteeDemands">Semantic commit guarantees derived by the Transition interpreter.</param>
    /// <param name="evidence">Ordered execution provenance for the Transition decision.</param>
    /// <param name="subjectCondition">Required authoritative subject state at the atomic boundary.</param>
    /// <exception cref="ArgumentNullException">Any reference argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// The write contradicts the subject condition or does not address <paramref name="request"/>'s subject, the
    /// decision is not committable, the result is not successful, the evidence does not identify the exact
    /// Transition and activation, or canonical envelope provenance does not identify the exact Process operation
    /// and Transition.
    /// </exception>
    public EntityTransitionOperationCommit(
        EntityTransitionOperationRequest request,
        EntityWriteRequest write,
        TransitionDecisionKind decisionKind,
        ProcessOperationResult result,
        TransitionGuaranteeDemands guaranteeDemands,
        TransitionExecutionEvidence evidence,
        EntityTransitionSubjectCondition subjectCondition = EntityTransitionSubjectCondition.MustExist)
    {
        Request = request ?? throw new ArgumentNullException(nameof(request));
        Write = write ?? throw new ArgumentNullException(nameof(write));
        Result = result ?? throw new ArgumentNullException(nameof(result));
        GuaranteeDemands = guaranteeDemands ?? throw new ArgumentNullException(nameof(guaranteeDemands));
        Evidence = evidence ?? throw new ArgumentNullException(nameof(evidence));

        if (!Enum.IsDefined(subjectCondition))
        {
            throw new ArgumentOutOfRangeException(
                nameof(subjectCondition),
                subjectCondition,
                "Unsupported entity Transition subject condition.");
        }
        if (subjectCondition == EntityTransitionSubjectCondition.MustExist
            && write.ExpectedConcurrencyToken is null)
        {
            throw new ArgumentException(
                "An existing-subject Process Transition commit requires an entity concurrency fence.",
                nameof(write));
        }
        if (subjectCondition == EntityTransitionSubjectCondition.MustBeAbsent
            && write.ExpectedConcurrencyToken is not null)
        {
            throw new ArgumentException(
                "An absent-subject Process Transition commit cannot carry an existing-entity concurrency fence.",
                nameof(write));
        }
        if (!string.Equals(
                request.Subject.EntityType.Value,
                write.Entity.ShapeId.Value,
                StringComparison.Ordinal)
            || !string.Equals(request.Subject.EntityId.Value, write.Entity.Id, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The candidate entity state must address the exact Transition operation subject.",
                nameof(write));
        }
        if (decisionKind is not (TransitionDecisionKind.Applied
            or TransitionDecisionKind.NoChange
            or TransitionDecisionKind.AdmissionRejected
            or TransitionDecisionKind.DomainRejected))
        {
            throw new ArgumentException(
                $"Transition decision '{decisionKind}' cannot be retained as a successful entity operation.",
                nameof(decisionKind));
        }
        if (subjectCondition == EntityTransitionSubjectCondition.MustBeAbsent
            && decisionKind != TransitionDecisionKind.Applied)
        {
            throw new ArgumentException(
                "An absent-subject entity commit requires an Applied Transition decision.",
                nameof(decisionKind));
        }
        if (subjectCondition == EntityTransitionSubjectCondition.MustBeAbsent
            && (!guaranteeDemands.CommitRequired || evidence.InitialObservation is null))
        {
            throw new ArgumentException(
                "An absent-subject entity commit requires canonical initial-observation evidence and a commit demand.",
                nameof(evidence));
        }
        if (!result.IsSuccessful)
        {
            throw new ArgumentException(
                "An entity Transition operation commit requires a successful typed result.",
                nameof(result));
        }
        if (!result.Emissions.IsEmpty && !guaranteeDemands.AtomicPatchAndEmissions)
        {
            throw new ArgumentException(
                "A Transition result with canonical envelopes requires an atomic patch-and-emissions guarantee.",
                nameof(guaranteeDemands));
        }
        if (evidence.Definition != request.Transition
            || evidence.Activation != request.Operation.Activation)
        {
            throw new ArgumentException(
                "Transition execution evidence must identify the exact invoked definition and Process activation.",
                nameof(evidence));
        }
        var outcomeTrace = evidence.Trace.LastOrDefault(static trace =>
            trace.Kind == TransitionTraceEventKind.OutcomeReturned);
        if (outcomeTrace is null
            || !string.Equals(outcomeTrace.Detail, decisionKind.ToString(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Transition execution evidence must retain the exact terminal decision category.",
                nameof(evidence));
        }

        ValidateEmissions(request, result, evidence, outcomeTrace.Node);
        SubjectCondition = subjectCondition;
        DecisionKind = decisionKind;
        PublicationAuthority = EntityTransitionEmissionPublicationAuthority.ProcessOutbox;
        Fingerprint = EntityTransitionOperationFingerprints.Commit(this);
    }

    /// <summary>Exact replay lookup identity.</summary>
    public EntityTransitionOperationRequest Request { get; }

    /// <summary>Candidate entity state and the concurrency fence required by <see cref="SubjectCondition"/>.</summary>
    public EntityWriteRequest Write { get; }

    /// <summary>Required authoritative subject state at the atomic boundary.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public EntityTransitionSubjectCondition SubjectCondition { get; }

    /// <summary>Committable terminal Transition decision category.</summary>
    public TransitionDecisionKind DecisionKind { get; }

    /// <summary>Typed outcome and canonical envelope handoff evidence.</summary>
    public ProcessOperationResult Result { get; }

    /// <summary>Semantic commit guarantees derived by the Transition interpreter.</summary>
    public TransitionGuaranteeDemands GuaranteeDemands { get; }

    /// <summary>Ordered execution provenance for the Transition decision.</summary>
    public TransitionExecutionEvidence Evidence { get; }

    /// <summary>Sole physical publication authority for retained canonical envelopes.</summary>
    public EntityTransitionEmissionPublicationAuthority PublicationAuthority { get; }

    /// <summary>Canonical fingerprint of the complete atomic commit intent.</summary>
    public ProcessCommitFingerprint Fingerprint { get; }

    static void ValidateEmissions(
        EntityTransitionOperationRequest request,
        ProcessOperationResult result,
        TransitionExecutionEvidence evidence,
        ExecutionNodeId outcome)
    {
        if (result.Emissions.Length != evidence.EmittedIntents.Length)
        {
            throw new ArgumentException(
                "A Transition operation result must retain every emitted intent as one canonical envelope.",
                nameof(result));
        }
        HashSet<EmissionId> identities = [];
        foreach (var emission in result.Emissions)
        {
            if (!identities.Add(emission.Context.EmissionId))
            {
                throw new ArgumentException(
                    "A Transition operation result cannot contain duplicate logical emission identities.",
                    nameof(result));
            }
            if (emission.Context.Origin is not ProcessInteractionOrigin origin
                || origin.Continuation != request.Operation.Continuation
                || origin.Activation != request.Operation.Activation
                || origin.Token != request.Operation.Token
                || origin.Node != request.Operation.Node
                || origin.Entity != request.Subject
                || origin.Transition != request.Transition
                || origin.Outcome != outcome
                || origin.TransitionNode is not { } transitionNode
                || !evidence.EmittedIntents.Contains(transitionNode))
            {
                throw new ArgumentException(
                    "Every retained envelope must identify the exact Process operation, Transition emission, subject, and outcome.",
                    nameof(result));
            }
        }
    }
}

/// <summary>Durable entity-side receipt for one exact Process-invoked Transition operation.</summary>
public sealed record EntityTransitionOperationReceipt
{
    /// <summary>Creates immutable atomic commit evidence.</summary>
    /// <param name="commit">Exact committed content.</param>
    /// <param name="entity">Persisted entity snapshot produced by the commit.</param>
    /// <param name="committedAtUtc">UTC physical commit observation.</param>
    /// <exception cref="ArgumentNullException"><paramref name="commit"/> or <paramref name="entity"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="entity"/> is not the exact committed candidate state, lacks materialized partition or
    /// concurrency evidence, or
    /// <paramref name="committedAtUtc"/> is not UTC.
    /// </exception>
    public EntityTransitionOperationReceipt(
        EntityTransitionOperationCommit commit,
        EntitySnapshot entity,
        DateTimeOffset committedAtUtc)
    {
        Commit = commit ?? throw new ArgumentNullException(nameof(commit));
        Entity = entity ?? throw new ArgumentNullException(nameof(entity));
        if (!entity.Entity.HasSameContent(commit.Write.Entity))
        {
            throw new ArgumentException(
                "A Transition operation receipt must retain the exact committed candidate entity state.",
                nameof(entity));
        }
        if (string.IsNullOrWhiteSpace(entity.PartitionKey)
            || string.IsNullOrWhiteSpace(entity.ConcurrencyToken.Value))
        {
            throw new ArgumentException(
                "A Transition operation receipt requires materialized partition and concurrency evidence.",
                nameof(entity));
        }
        if (committedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("A Transition operation receipt requires a UTC commit time.", nameof(committedAtUtc));
        }
        CommittedAtUtc = committedAtUtc;
    }

    /// <summary>Exact committed candidate, result, guarantees, provenance, and publication authority.</summary>
    public EntityTransitionOperationCommit Commit { get; }

    /// <summary>Persisted entity snapshot produced atomically with this receipt.</summary>
    public EntitySnapshot Entity { get; }

    /// <summary>UTC physical commit observation.</summary>
    public DateTimeOffset CommittedAtUtc { get; }

    /// <summary>Exact replay lookup identity.</summary>
    public EntityTransitionOperationRequest Request => Commit.Request;

    /// <summary>Typed Transition result and canonical handoff envelopes.</summary>
    public ProcessOperationResult Result => Commit.Result;

    /// <summary>Sole physical publication authority for the retained canonical envelopes.</summary>
    public EntityTransitionEmissionPublicationAuthority PublicationAuthority => Commit.PublicationAuthority;

}

/// <summary>Observable result of one entity-side Transition operation lookup or commit.</summary>
public enum EntityTransitionOperationDisposition
{
    /// <summary>No receipt exists for the exact operation occurrence.</summary>
    NotFound = 0,

    /// <summary>Entity state and the operation receipt committed atomically.</summary>
    Committed = 1,

    /// <summary>An exact retained receipt satisfied the request without another mutation.</summary>
    Replayed = 2,

    /// <summary>The repository cannot provide the required atomic commit boundary.</summary>
    CapabilityInsufficient = 3,

    /// <summary>The optimistic-concurrency fence is stale.</summary>
    ConcurrencyConflict = 4,

    /// <summary>The operation occurrence identity is retained with different canonical content.</summary>
    IdentityConflict = 5,

    /// <summary>The authoritative subject presence contradicts the Transition's declared subject condition.</summary>
    SubjectStateConflict = 6
}

/// <summary>Closed receipt or structured rejection from an entity Transition operation.</summary>
public sealed record EntityTransitionOperationResult
{
    EntityTransitionOperationResult(
        EntityTransitionOperationDisposition disposition,
        EntityTransitionOperationReceipt? receipt,
        ImmutableArray<DocumentValidationDiagnostic> diagnostics)
    {
        Disposition = disposition;
        Receipt = receipt;
        Diagnostics = diagnostics.IsDefault ? [] : diagnostics;
    }

    /// <summary>Observable lookup or commit disposition.</summary>
    public EntityTransitionOperationDisposition Disposition { get; }

    /// <summary>Committed or replayed receipt, otherwise <see langword="null"/>.</summary>
    public EntityTransitionOperationReceipt? Receipt { get; }

    /// <summary>Structured capability, concurrency, or identity diagnostics.</summary>
    public ImmutableArray<DocumentValidationDiagnostic> Diagnostics { get; }

    /// <summary>Creates a missing-receipt result.</summary>
    /// <returns>A result that permits first-time deterministic Transition evaluation.</returns>
    public static EntityTransitionOperationResult NotFound() =>
        new(EntityTransitionOperationDisposition.NotFound, receipt: null, []);

    /// <summary>Creates a successful atomic commit result.</summary>
    /// <param name="receipt">Newly committed receipt.</param>
    /// <returns>A successful commit result.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="receipt"/> is <see langword="null"/>.</exception>
    public static EntityTransitionOperationResult Committed(EntityTransitionOperationReceipt receipt) =>
        new(
            EntityTransitionOperationDisposition.Committed,
            receipt ?? throw new ArgumentNullException(nameof(receipt)),
            []);

    /// <summary>Creates an exact replay result.</summary>
    /// <param name="receipt">Previously committed receipt.</param>
    /// <returns>A successful replay result.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="receipt"/> is <see langword="null"/>.</exception>
    public static EntityTransitionOperationResult Replayed(EntityTransitionOperationReceipt receipt) =>
        new(
            EntityTransitionOperationDisposition.Replayed,
            receipt ?? throw new ArgumentNullException(nameof(receipt)),
            []);

    /// <summary>Creates a structured rejection result.</summary>
    /// <param name="disposition">Capability, concurrency, or identity conflict disposition.</param>
    /// <param name="diagnostic">Error diagnostic explaining the rejection.</param>
    /// <returns>A rejected result with no receipt.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="disposition"/> is not a rejection.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="diagnostic"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="diagnostic"/> is not an error.</exception>
    public static EntityTransitionOperationResult Rejected(
        EntityTransitionOperationDisposition disposition,
        DocumentValidationDiagnostic diagnostic)
    {
        if (disposition is not (EntityTransitionOperationDisposition.CapabilityInsufficient
            or EntityTransitionOperationDisposition.ConcurrencyConflict
            or EntityTransitionOperationDisposition.IdentityConflict
            or EntityTransitionOperationDisposition.SubjectStateConflict))
        {
            throw new ArgumentOutOfRangeException(nameof(disposition), disposition, "Disposition is not a rejection.");
        }
        ArgumentNullException.ThrowIfNull(diagnostic);
        if (diagnostic.Severity != DiagnosticSeverity.Error)
        {
            throw new ArgumentException("A rejected operation requires an error diagnostic.", nameof(diagnostic));
        }
        return new(disposition, receipt: null, [diagnostic]);
    }
}

/// <summary>Entity repository capable of one atomic state-and-Transition-receipt commit.</summary>
public interface IEntityTransitionOperationRepository : IEntityRepository
{
    /// <summary>Gets the repository's atomic entity-state and Transition-receipt capability evidence.</summary>
    new EntityTransitionOperationCapabilities TransitionOperationCapabilities { get; }

    /// <summary>Looks up an exact operation before deterministic Transition evaluation.</summary>
    /// <param name="context">Operation context and cancellation.</param>
    /// <param name="request">Exact replay lookup identity.</param>
    /// <returns>Missing, exact replay, or conflicting identity evidence.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="context"/> or <paramref name="request"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="OperationCanceledException">The operation is cancelled before the lookup.</exception>
    Task<EntityTransitionOperationResult> TryGetTransitionOperation(
        OperationContext context,
        EntityTransitionOperationRequest request);

    /// <summary>Atomically commits candidate entity state and one replayable operation receipt.</summary>
    /// <param name="context">Operation context, time, and cancellation.</param>
    /// <param name="commit">Complete deterministic atomic commit intent.</param>
    /// <returns>Committed, replayed, subject-state, stale-concurrency, or identity-conflict evidence.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="context"/> or <paramref name="commit"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="OperationCanceledException">The operation is cancelled before the atomic boundary.</exception>
    Task<EntityTransitionOperationResult> CommitTransitionOperation(
        OperationContext context,
        EntityTransitionOperationCommit commit);
}

/// <summary>Capability-aware replay and commit operations for Process-invoked entity Transitions.</summary>
public static class EntityTransitionOperationRepositoryExtensions
{
    /// <summary>Looks up an exact operation through a capability-checked entity repository.</summary>
    /// <param name="repository">Entity repository selected for the authoritative subject.</param>
    /// <param name="context">Operation context and cancellation.</param>
    /// <param name="request">Exact replay lookup identity.</param>
    /// <returns>Missing, replay, conflict, or capability evidence.</returns>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException">The operation is cancelled before the lookup.</exception>
    public static Task<EntityTransitionOperationResult> TryGetTransitionOperation(
        this IEntityRepository repository,
        OperationContext context,
        EntityTransitionOperationRequest request)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        context.ThrowIfCancellationRequested();
        return repository is IEntityTransitionOperationRepository atomic
               && repository.TransitionOperationCapabilities.SupportsAtomicStateAndReceipt
            ? atomic.TryGetTransitionOperation(context, request)
            : Task.FromResult(CapabilityFailure(repository));
    }

    /// <summary>Commits an operation through a capability-checked entity repository.</summary>
    /// <param name="repository">Entity repository selected for the authoritative subject.</param>
    /// <param name="context">Operation context, time, and cancellation.</param>
    /// <param name="commit">Complete deterministic atomic commit intent.</param>
    /// <returns>Committed, replayed, conflict, or capability evidence.</returns>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException">The operation is cancelled before the atomic boundary.</exception>
    public static Task<EntityTransitionOperationResult> CommitTransitionOperation(
        this IEntityRepository repository,
        OperationContext context,
        EntityTransitionOperationCommit commit)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(commit);
        context.ThrowIfCancellationRequested();
        return repository is IEntityTransitionOperationRepository atomic
               && repository.TransitionOperationCapabilities.SupportsAtomicStateAndReceipt
            ? atomic.CommitTransitionOperation(context, commit)
            : Task.FromResult(CapabilityFailure(repository));
    }

    /// <summary>
    /// Replays an exact receipt or lazily evaluates and commits one first-time deterministic Transition operation.
    /// </summary>
    /// <param name="repository">Entity repository selected for the authoritative subject.</param>
    /// <param name="context">Operation context, time, and cancellation.</param>
    /// <param name="request">Exact replay lookup identity known before Transition evaluation.</param>
    /// <param name="prepareCommit">
    /// Pure, non-committing Transition evaluation and lowering callback invoked only when no exact receipt exists.
    /// </param>
    /// <returns>Replay, commit, conflict, or capability evidence.</returns>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="prepareCommit"/> returns <see langword="null"/> or a commit for another request.
    /// </exception>
    /// <exception cref="OperationCanceledException">The operation is cancelled before lookup or commit.</exception>
    public static async Task<EntityTransitionOperationResult> ExecuteTransitionOperation(
        this IEntityRepository repository,
        OperationContext context,
        EntityTransitionOperationRequest request,
        Func<EntityTransitionOperationCommit> prepareCommit)
    {
        ArgumentNullException.ThrowIfNull(prepareCommit);
        var lookup = await repository.TryGetTransitionOperation(context, request).ConfigureAwait(false);
        if (lookup.Disposition != EntityTransitionOperationDisposition.NotFound)
        {
            return lookup;
        }

        var commit = prepareCommit()
            ?? throw new InvalidOperationException("Transition operation preparation returned null.");
        if (commit.Request.Fingerprint != request.Fingerprint)
        {
            throw new InvalidOperationException(
                "Transition operation preparation returned a commit for another replay request.");
        }
        return await repository.CommitTransitionOperation(context, commit).ConfigureAwait(false);
    }

    internal static EntityTransitionOperationResult IdentityConflict(string message, string location) =>
        EntityTransitionOperationResult.Rejected(
            EntityTransitionOperationDisposition.IdentityConflict,
            Error(EntityTransitionOperationDiagnosticCodes.IdentityConflict, message, location));

    internal static EntityTransitionOperationResult ConcurrencyConflict(string message) =>
        EntityTransitionOperationResult.Rejected(
            EntityTransitionOperationDisposition.ConcurrencyConflict,
            Error(EntityTransitionOperationDiagnosticCodes.ConcurrencyConflict, message, "/write/expectedConcurrencyToken"));

    internal static EntityTransitionOperationResult SubjectStateConflict(string message) =>
        EntityTransitionOperationResult.Rejected(
            EntityTransitionOperationDisposition.SubjectStateConflict,
            Error(EntityTransitionOperationDiagnosticCodes.SubjectStateConflict, message, "/write/subjectCondition"));

    static EntityTransitionOperationResult CapabilityFailure(IEntityRepository repository) =>
        EntityTransitionOperationResult.Rejected(
            EntityTransitionOperationDisposition.CapabilityInsufficient,
            Error(
                EntityTransitionOperationDiagnosticCodes.CapabilityInsufficient,
                $"Entity repository '{repository.EntityType}' cannot atomically commit entity state and a Process Transition operation receipt.",
                "/repository/transitionOperationCapabilities"));

    static DocumentValidationDiagnostic Error(string code, string message, string location) =>
        new(code, DiagnosticSeverity.Error, message, location);
}

static class EntityTransitionOperationFingerprints
{
    internal static ProcessCommitFingerprint Request(EntityTransitionOperationRequest request) =>
        ProcessStorageContentFingerprints.Value(new RequestContent(
            request.Operation,
            request.Transition,
            request.Subject,
            request.Input));

    internal static ProcessCommitFingerprint Commit(EntityTransitionOperationCommit commit) =>
        ProcessStorageContentFingerprints.Value(new CommitContent(
            commit.Request,
            commit.Write,
            commit.SubjectCondition == EntityTransitionSubjectCondition.MustExist
                ? null
                : commit.SubjectCondition,
            commit.DecisionKind,
            commit.Result,
            commit.GuaranteeDemands,
            commit.Evidence,
            commit.PublicationAuthority));

    sealed record RequestContent(
        ProcessOperationOccurrence Operation,
        ExecutionDefinitionReference Transition,
        InteractionEntityReference Subject,
        PortableValue Input);

    sealed record CommitContent(
        EntityTransitionOperationRequest Request,
        EntityWriteRequest Write,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        EntityTransitionSubjectCondition? SubjectCondition,
        TransitionDecisionKind DecisionKind,
        ProcessOperationResult Result,
        TransitionGuaranteeDemands GuaranteeDemands,
        TransitionExecutionEvidence Evidence,
        EntityTransitionEmissionPublicationAuthority PublicationAuthority);
}
