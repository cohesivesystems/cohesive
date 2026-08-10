using System.Collections.Immutable;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Execution;

/// <summary>Stable diagnostics emitted while binding canonical Requests to durable operation execution.</summary>
public static class DurableOperationDiagnosticCodes
{
    /// <summary>The Request envelope or exact Request contract is invalid.</summary>
    public const string RequestInvalid = "execution.operation.request.invalid";

    /// <summary>The durable binding addresses a different exact Request contract.</summary>
    public const string RequestBindingMismatch = "execution.operation.binding.requestMismatch";

    /// <summary>A terminal Request outcome has no exact Reply binding or has more than one.</summary>
    public const string ReplyBindingIncomplete = "execution.operation.binding.reply.incomplete";

    /// <summary>An exact Reply binding is unknown or does not discharge its mapped Request outcome.</summary>
    public const string ReplyBindingInvalid = "execution.operation.binding.reply.invalid";

    /// <summary>The physical retry budget contradicts authored Request retry semantics.</summary>
    public const string RetryBudgetInvalid = "execution.operation.binding.retryBudget.invalid";

    /// <summary>Repeated physical execution lacks required idempotency or reconciliation evidence.</summary>
    public const string RetryEvidenceInsufficient = "execution.operation.binding.retryEvidence.insufficient";

    /// <summary>The concrete timeout trigger contradicts authored Request timeout semantics.</summary>
    public const string TimeoutBindingInvalid = "execution.operation.binding.timeout.invalid";

    /// <summary>A terminal-failure resolution lacks one exact declared failure outcome.</summary>
    public const string TerminalFailureBindingInvalid = "execution.operation.binding.terminalFailure.invalid";

    /// <summary>A reconciliation policy lacks its exact semantic path, or an undeclared path was supplied.</summary>
    public const string ReconciliationBindingInvalid = "execution.operation.binding.reconciliation.invalid";

    /// <summary>An escalation policy lacks its exact semantic path, or an undeclared path was supplied.</summary>
    public const string EscalationBindingInvalid = "execution.operation.binding.escalation.invalid";
}

/// <summary>Stable adapter-independent failure classifications produced by the reference protocol.</summary>
public static class DurableOperationFailureCodes
{
    /// <summary>A claim expired before the durable dispatch boundary.</summary>
    public const string ClaimExpiredBeforeDispatch = "operation.claim.expiredBeforeDispatch";

    /// <summary>A claim expired after dispatch, leaving the external consequence ambiguous.</summary>
    public const string ClaimExpiredAfterDispatch = "operation.claim.expiredAfterDispatch";

    /// <summary>A persisted dispatch was replayed without evidence that redispatch is safe.</summary>
    public const string UnsafeDispatchReplay = "operation.dispatch.replayUnsafe";

    /// <summary>A semantic timeout closed an in-flight operation with an ambiguous external consequence.</summary>
    public const string TimedOutInFlight = "operation.timeout.inFlight";

    /// <summary>A semantic timeout closed a claim before dispatch.</summary>
    public const string TimedOutBeforeDispatch = "operation.timeout.beforeDispatch";

    /// <summary>A semantic cancellation closed an in-flight operation with an ambiguous external consequence.</summary>
    public const string CancelledInFlight = "operation.cancellation.inFlight";

    /// <summary>A semantic cancellation closed a claim before dispatch.</summary>
    public const string CancelledBeforeDispatch = "operation.cancellation.beforeDispatch";
}

/// <summary>
/// Signals that an impure durable-operation boundary was rejected because the canonical Request deadline elapsed.
/// </summary>
/// <remarks>
/// The exception identifies the exact pre-I/O deadline guard so orchestration runtimes can translate it into
/// structured recovery without mistaking an arbitrary adapter <see cref="InvalidOperationException"/> for timeout
/// evidence. It never proves that a timeout outcome was durably admitted.
/// </remarks>
public sealed class DurableOperationDeadlineElapsedException : InvalidOperationException
{
    internal DurableOperationDeadlineElapsedException(string message)
        : base(message)
    {
    }
}

/// <summary>Whether an impure adapter can inspect a failed unresolved attempt without blindly executing again.</summary>
public enum DurableOperationReconciliationCapability
{
    /// <summary>No capability was declared; invalid adapter evidence.</summary>
    Unspecified = 0,

    /// <summary>The adapter cannot reconcile a failed unresolved attempt.</summary>
    Unsupported = 1,

    /// <summary>The adapter can reconcile by stable logical Request and target evidence.</summary>
    Supported = 2
}

/// <summary>Capabilities declared by one impure durable-operation adapter.</summary>
public sealed record DurableOperationAdapterCapabilities
{
    /// <summary>Creates adapter capability evidence.</summary>
    /// <param name="idempotencyEvidence">Repeat-execution evidence supplied by the adapter target.</param>
    /// <param name="reconciliation">Whether failed unresolved attempts can be reconciled.</param>
    /// <param name="supportedRequests">Non-empty exact Request contracts interpreted by the adapter.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="supportedRequests"/> is default or empty, contains null, or contains a duplicate.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="idempotencyEvidence"/> or <paramref name="reconciliation"/> is unspecified or outside the
    /// known contract values.
    /// </exception>
    public DurableOperationAdapterCapabilities(
        DurableOperationIdempotencyEvidence idempotencyEvidence,
        DurableOperationReconciliationCapability reconciliation,
        ImmutableArray<RequestContractReference> supportedRequests)
    {
        if (!Enum.IsDefined(idempotencyEvidence)
            || idempotencyEvidence == DurableOperationIdempotencyEvidence.Unspecified)
        {
            throw new ArgumentOutOfRangeException(
                nameof(idempotencyEvidence),
                idempotencyEvidence,
                "Adapter idempotency evidence must be explicit.");
        }
        if (!Enum.IsDefined(reconciliation)
            || reconciliation == DurableOperationReconciliationCapability.Unspecified)
        {
            throw new ArgumentOutOfRangeException(
                nameof(reconciliation),
                reconciliation,
                "Adapter reconciliation capability must be explicit.");
        }
        if (supportedRequests.IsDefaultOrEmpty)
        {
            throw new ArgumentException("A durable operation adapter must declare exact Request support.", nameof(supportedRequests));
        }

        var observed = new HashSet<RequestContractReference>();
        foreach (var request in supportedRequests)
        {
            if (request is null)
            {
                throw new ArgumentException("Adapter Request support cannot contain null entries.", nameof(supportedRequests));
            }

            if (!observed.Add(request))
            {
                throw new ArgumentException("Adapter Request support cannot contain duplicates.", nameof(supportedRequests));
            }
        }

        IdempotencyEvidence = idempotencyEvidence;
        Reconciliation = reconciliation;
        SupportedRequests = CanonicalDocumentCollections.SortIfNeeded(
            supportedRequests,
            static (left, right) => CompareRequestContracts(left, right));
    }

    /// <summary>Repeat-execution evidence supplied by the adapter target.</summary>
    public DurableOperationIdempotencyEvidence IdempotencyEvidence { get; }

    /// <summary>Whether failed unresolved attempts can be reconciled.</summary>
    public DurableOperationReconciliationCapability Reconciliation { get; }

    /// <summary>Exact Request contracts interpreted by this adapter.</summary>
    public ImmutableArray<RequestContractReference> SupportedRequests { get; }

    /// <summary>Returns whether the adapter declares support for one exact Request contract.</summary>
    /// <param name="request">Exact Request contract to inspect.</param>
    /// <returns><see langword="true"/> when the exact revision and fingerprint are supported.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    public bool Supports(RequestContractReference request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return CanonicalDocumentCollections.BinarySearchIndex(
            SupportedRequests,
            request,
            static (candidate, requested) => CompareRequestContracts(candidate, requested)) >= 0;
    }

    /// <summary>Compares capability evidence by value and exact supported Request set.</summary>
    /// <param name="other">Capability evidence to compare.</param>
    /// <returns><see langword="true"/> when capabilities and supported contracts are equal.</returns>
    public bool Equals(DurableOperationAdapterCapabilities? other) =>
        ReferenceEquals(this, other)
        || other is not null
        && IdempotencyEvidence == other.IdempotencyEvidence
        && Reconciliation == other.Reconciliation
        && SupportedRequests.SequenceEqual(other.SupportedRequests);

    /// <summary>Returns a structural hash code for capability evidence.</summary>
    /// <returns>A hash code aligned with <see cref="Equals(DurableOperationAdapterCapabilities?)"/>.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(IdempotencyEvidence);
        hash.Add(Reconciliation);
        foreach (var request in SupportedRequests)
        {
            hash.Add(request);
        }

        return hash.ToHashCode();
    }

    static int CompareRequestContracts(RequestContractReference left, RequestContractReference right)
        => ExecutionDefinitionReference.CompareCanonical(left.Definition, right.Definition);
}

/// <summary>Immutable canonical invocation supplied to an impure adapter.</summary>
/// <remarks>
/// The invocation deliberately exposes no aggregate state, entity repository, Transition callback, Process
/// runtime service, or provider transaction. Adapters return evidence; owning interpreters admit consequences.
/// </remarks>
public sealed record DurableOperationInvocation
{
    /// <summary>Creates one fenced physical invocation.</summary>
    /// <param name="request">Canonical logical Request, unchanged across retry and replay.</param>
    /// <param name="binding">Portable bounded execution refinement.</param>
    /// <param name="attemptId">Stable physical attempt identity.</param>
    /// <param name="attemptOrdinal">One-based claim-attempt history ordinal.</param>
    /// <param name="fence">Current ownership fence.</param>
    /// <param name="deduplicationKey">Scoped target-deduplication key.</param>
    /// <param name="deadlineUtc">Optional semantic Request deadline.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="request"/>, <paramref name="binding"/>, or <paramref name="deduplicationKey"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The attempt identity or fence is default; the binding addresses another Request; the deduplication key does
    /// not derive from that Request; or <paramref name="deadlineUtc"/> is not UTC.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="attemptOrdinal"/> is not positive.</exception>
    [System.Text.Json.Serialization.JsonConstructor]
    public DurableOperationInvocation(
        RequestEnvelope request,
        DurableRequestBinding binding,
        OperationAttemptId attemptId,
        int attemptOrdinal,
        OperationFence fence,
        DurableOperationDeduplicationKey deduplicationKey,
        DateTimeOffset? deadlineUtc)
    {
        request = Guard.RequireNotNull(request);
        binding = Guard.RequireNotNull(binding);
        deduplicationKey = Guard.RequireNotNull(deduplicationKey);
        if (string.IsNullOrWhiteSpace(attemptId.Value))
        {
            throw new ArgumentException("A durable invocation requires an attempt identity.", nameof(attemptId));
        }

        if (fence.Value <= 0)
        {
            throw new ArgumentException("A durable invocation requires a positive operation fence.", nameof(fence));
        }

        if (attemptOrdinal <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(attemptOrdinal), attemptOrdinal, "An attempt ordinal must be positive.");
        }

        if (deadlineUtc is { } deadline)
        {
            DurableOperationClaim.RequireUtc(deadline, nameof(deadlineUtc));
        }

        if (request.Contract != binding.Request)
        {
            throw new ArgumentException("Invocation and binding must reference the same exact Request contract.", nameof(binding));
        }

        var expectedDeduplicationKey = new DurableOperationDeduplicationKey(
            request.Context.AuthorityScope,
            request.Contract,
            request.Context.IdempotencyKey);
        if (deduplicationKey != expectedDeduplicationKey)
        {
            throw new ArgumentException(
                "Invocation deduplication evidence must be derived from the exact canonical Request.",
                nameof(deduplicationKey));
        }

        Request = request;
        Binding = binding;
        AttemptId = attemptId;
        AttemptOrdinal = attemptOrdinal;
        Fence = fence;
        DeduplicationKey = deduplicationKey;
        DeadlineUtc = deadlineUtc;
    }

    /// <summary>Canonical logical Request, unchanged across retry and replay.</summary>
    public RequestEnvelope Request { get; }

    /// <summary>Portable bounded execution refinement.</summary>
    public DurableRequestBinding Binding { get; }

    /// <summary>Stable physical attempt identity.</summary>
    public OperationAttemptId AttemptId { get; }

    /// <summary>One-based claim-attempt history ordinal.</summary>
    public int AttemptOrdinal { get; }

    /// <summary>Current ownership fence.</summary>
    public OperationFence Fence { get; }

    /// <summary>Scoped target-deduplication key.</summary>
    public DurableOperationDeduplicationKey DeduplicationKey { get; }

    /// <summary>Optional semantic Request deadline.</summary>
    public DateTimeOffset? DeadlineUtc { get; }
}

/// <summary>Immutable request to reconcile one failed unresolved physical attempt.</summary>
public sealed record DurableOperationReconciliationRequest
{
    /// <summary>Creates an explicit reconciliation request.</summary>
    /// <param name="request">Canonical logical Request.</param>
    /// <param name="binding">Portable bounded execution refinement.</param>
    /// <param name="attempt">Failed attempt being reconciled.</param>
    /// <param name="deduplicationKey">Scoped target-deduplication key.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// The binding addresses another Request; the attempt is not failed; the deduplication key
    /// does not derive from that Request; or the binding has no exact reconciliation target.
    /// </exception>
    internal DurableOperationReconciliationRequest(
        RequestEnvelope request,
        DurableRequestBinding binding,
        DurableOperationAttempt attempt,
        DurableOperationDeduplicationKey deduplicationKey)
    {
        Request = Guard.RequireNotNull(request);
        Binding = Guard.RequireNotNull(binding);
        Attempt = Guard.RequireNotNull(attempt);
        DeduplicationKey = Guard.RequireNotNull(deduplicationKey);
        if (request.Contract != binding.Request)
        {
            throw new ArgumentException("Reconciliation and binding must reference the same Request contract.", nameof(binding));
        }

        if (attempt.Stage != DurableOperationAttemptStage.Failed || attempt.Failure is null)
        {
            throw new ArgumentException("Reconciliation requires a failed attempt.", nameof(attempt));
        }

        var expectedDeduplicationKey = new DurableOperationDeduplicationKey(
            request.Context.AuthorityScope,
            request.Contract,
            request.Context.IdempotencyKey);
        if (deduplicationKey != expectedDeduplicationKey)
        {
            throw new ArgumentException(
                "Reconciliation deduplication evidence must be derived from the exact canonical Request.",
                nameof(deduplicationKey));
        }
        if (binding.ReconciliationTarget is null)
        {
            throw new ArgumentException("Reconciliation requires an exact declared semantic target.", nameof(binding));
        }
    }

    /// <summary>Canonical logical Request.</summary>
    public RequestEnvelope Request { get; }

    /// <summary>Portable bounded execution refinement.</summary>
    public DurableRequestBinding Binding { get; }

    /// <summary>Failed unresolved attempt being reconciled.</summary>
    public DurableOperationAttempt Attempt { get; }

    /// <summary>Scoped target-deduplication key.</summary>
    public DurableOperationDeduplicationKey DeduplicationKey { get; }

    /// <summary>Exact definition node that realizes reconciliation semantics.</summary>
    public DurableOperationResolutionTarget Target => Binding.ReconciliationTarget!;

    /// <summary>Stable logical identity of this reconciliation obligation.</summary>
    public DurableOperationRecoveryIdentity Identity =>
        new(Request.Context.EmissionId, Attempt.Claim.AttemptId, Attempt.Claim.Fence, DurableOperationRecoveryRequirement.Reconcile);

    /// <summary>Closed portable reconciliation intent interpreted by the owning runtime.</summary>
    public DurableOperationRecoveryIntent Intent =>
        new(Identity, Request, DeduplicationKey, Target);
}

/// <summary>Impure adapter boundary for one canonical durable Request family.</summary>
/// <remarks>
/// Implementations interpret Requests and return typed outcome or failure evidence. They MUST NOT mutate
/// authoritative aggregate state directly and MUST use the supplied stable logical identities across retries.
/// Expected external failures MUST be returned as classified evidence. A thrown exception establishes no safe
/// failure phase or external-effect evidence; the owning runtime must classify and persist that ambiguity.
/// </remarks>
public interface IDurableOperationAdapter
{
    /// <summary>Capabilities and target evidence supplied by this adapter.</summary>
    DurableOperationAdapterCapabilities Capabilities { get; }

    /// <summary>Executes one fenced physical attempt.</summary>
    /// <param name="context">Explicit cancellation, time, and correlation context.</param>
    /// <param name="invocation">Immutable canonical Request invocation.</param>
    /// <returns>Typed terminal outcome or explicit failure-phase evidence.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="context"/> is canceled.</exception>
    ValueTask<DurableOperationAttemptObservation> ExecuteAsync(
        OperationContext context,
        DurableOperationInvocation invocation);

    /// <summary>Reconciles a failed unresolved attempt without blindly executing it again.</summary>
    /// <param name="context">Explicit cancellation, time, and correlation context.</param>
    /// <param name="request">Immutable failed-attempt reconciliation request.</param>
    /// <returns>Confirmed outcome, proof of no execution, or unresolved evidence.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="context"/> is canceled.</exception>
    ValueTask<DurableOperationReconciliationObservation> ReconcileAsync(
        OperationContext context,
        DurableOperationReconciliationRequest request);
}

/// <summary>Resolves one exact durable execution binding for a canonical Request.</summary>
/// <remarks>
/// Implementations are deployment policy shared by durable Process runtimes. They do not own Request semantics;
/// the exact canonical interaction contract and returned <see cref="DurableRequestBinding"/> remain authoritative.
/// A resolver used by a replaying runtime must return the same binding for the same immutable Request evidence.
/// </remarks>
public interface IDurableRequestBindingResolver
{
    /// <summary>Attempts to resolve the binding used to initialize one durable Request operation.</summary>
    /// <param name="request">Exact canonical Request emitted by a Process activation.</param>
    /// <param name="binding">Receives the exact durable execution binding when available.</param>
    /// <returns><see langword="true"/> when an exact binding is available; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    bool TryResolve(RequestEnvelope request, out DurableRequestBinding? binding);
}

/// <summary>Binding resolver that deliberately supports no durable Request contract.</summary>
public sealed class EmptyDurableRequestBindingResolver : IDurableRequestBindingResolver
{
    /// <summary>Shared stateless empty resolver.</summary>
    public static EmptyDurableRequestBindingResolver Instance { get; } = new();

    EmptyDurableRequestBindingResolver()
    {
    }

    /// <inheritdoc />
    public bool TryResolve(RequestEnvelope request, out DurableRequestBinding? binding)
    {
        ArgumentNullException.ThrowIfNull(request);
        binding = null;
        return false;
    }
}

/// <summary>Resolves the impure adapter for one already-bound durable Request operation.</summary>
public interface IDurableOperationAdapterResolver
{
    /// <summary>Attempts to resolve the adapter for an exact canonical Request.</summary>
    /// <param name="request">Exact canonical logical Request retained by the durable operation.</param>
    /// <param name="adapter">Receives the impure adapter when available.</param>
    /// <returns><see langword="true"/> when an exact adapter is available; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    bool TryResolve(RequestEnvelope request, out IDurableOperationAdapter? adapter);
}

/// <summary>Adapter resolver that deliberately supports no durable Request contract.</summary>
public sealed class EmptyDurableOperationAdapterResolver : IDurableOperationAdapterResolver
{
    /// <summary>Shared stateless empty resolver.</summary>
    public static EmptyDurableOperationAdapterResolver Instance { get; } = new();

    EmptyDurableOperationAdapterResolver()
    {
    }

    /// <inheritdoc />
    public bool TryResolve(RequestEnvelope request, out IDurableOperationAdapter? adapter)
    {
        ArgumentNullException.ThrowIfNull(request);
        adapter = null;
        return false;
    }
}

/// <summary>Classifies an adapter exception as explicit durable failure evidence.</summary>
public interface IDurableOperationExceptionClassifier
{
    /// <summary>Classifies an exception thrown after a durable dispatch marker.</summary>
    /// <param name="exception">Adapter exception carrying provider-specific execution evidence.</param>
    /// <returns>Explicit phase, effect, retry, code, and optional portable detail to retain.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="exception"/> is <see langword="null"/>.</exception>
    DurableOperationFailure Classify(Exception exception);
}

/// <summary>Conservative classifier that treats a thrown adapter exception as effect-ambiguous.</summary>
public sealed class ConservativeDurableOperationExceptionClassifier : IDurableOperationExceptionClassifier
{
    /// <summary>Stable failure code used when an adapter throws without more precise evidence.</summary>
    public const string AmbiguousAdapterException = "operation.adapter.exception.ambiguous";

    /// <summary>Shared stateless conservative classifier.</summary>
    public static ConservativeDurableOperationExceptionClassifier Instance { get; } = new();

    ConservativeDurableOperationExceptionClassifier()
    {
    }

    /// <inheritdoc />
    public DurableOperationFailure Classify(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return new(
            DurableOperationFailurePhase.InCall,
            DurableOperationEffectEvidence.Ambiguous,
            DurableOperationFailureDisposition.Retryable,
            AmbiguousAdapterException);
    }
}

/// <summary>Per-item evidence returned from one physical batch adapter invocation.</summary>
public sealed record DurableOperationBatchItemObservation
{
    /// <summary>Creates exactly correlated batch-item evidence.</summary>
    /// <param name="operationId">Logical Request operation identity.</param>
    /// <param name="attemptId">Physical attempt identity dispatched for the item.</param>
    /// <param name="fence">Ownership fence under which the item was dispatched.</param>
    /// <param name="observation">Typed outcome or explicit failure-phase evidence.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="operationId"/>, <paramref name="attemptId"/>, or <paramref name="fence"/> is default.
    /// </exception>
    /// <exception cref="ArgumentNullException"><paramref name="observation"/> is <see langword="null"/>.</exception>
    public DurableOperationBatchItemObservation(
        EmissionId operationId,
        OperationAttemptId attemptId,
        OperationFence fence,
        DurableOperationAttemptObservation observation)
    {
        if (string.IsNullOrWhiteSpace(operationId.Value))
        {
            throw new ArgumentException("Batch evidence requires a logical operation identity.", nameof(operationId));
        }

        if (string.IsNullOrWhiteSpace(attemptId.Value))
        {
            throw new ArgumentException("Batch evidence requires a physical attempt identity.", nameof(attemptId));
        }

        if (fence.Value <= 0)
        {
            throw new ArgumentException("Batch evidence requires a positive operation fence.", nameof(fence));
        }

        OperationId = operationId;
        AttemptId = attemptId;
        Fence = fence;
        Observation = Guard.RequireNotNull(observation);
    }

    /// <summary>Logical Request operation identity.</summary>
    public EmissionId OperationId { get; }

    /// <summary>Physical attempt identity dispatched for the item.</summary>
    public OperationAttemptId AttemptId { get; }

    /// <summary>Ownership fence under which the item was dispatched.</summary>
    public OperationFence Fence { get; }

    /// <summary>Typed outcome or explicit failure-phase evidence.</summary>
    public DurableOperationAttemptObservation Observation { get; }
}

/// <summary>Optional impure adapter boundary for one true physical batch of independently durable Requests.</summary>
/// <remarks>
/// Every dispatched invocation must receive exactly one keyed observation. Expected external failures are
/// returned as <see cref="DurableOperationFailureObservation"/>; thrown exceptions carry no safe effect-phase
/// inference and must be classified explicitly by the owning runtime.
/// </remarks>
public interface IDurableOperationBatchAdapter
{
    /// <summary>Capabilities and exact Request contracts supplied by this adapter.</summary>
    DurableOperationAdapterCapabilities Capabilities { get; }

    /// <summary>Executes one physical batch and returns complete per-item evidence.</summary>
    /// <param name="context">Explicit cancellation, time, and correlation context.</param>
    /// <param name="invocations">Non-empty independently fenced item invocations.</param>
    /// <returns>Exactly one keyed observation for every supplied invocation.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="context"/> is canceled.</exception>
    ValueTask<ImmutableArray<DurableOperationBatchItemObservation>> ExecuteBatchAsync(
        OperationContext context,
        ImmutableArray<DurableOperationInvocation> invocations);
}

/// <summary>Outcome of a claim request against durable operation state.</summary>
public enum DurableOperationClaimDisposition
{
    /// <summary>A new physical attempt and fence were allocated.</summary>
    Claimed = 0,

    /// <summary>The identical live claim was replayed idempotently.</summary>
    Replayed = 1,

    /// <summary>Another live claimant currently owns the operation.</summary>
    Busy = 2,

    /// <summary>The operation already has a durable acknowledgement or disposition.</summary>
    Completed = 3,

    /// <summary>Reconciliation, terminal resolution, or escalation is required before another claim.</summary>
    RecoveryRequired = 4,

    /// <summary>The proposed attempt identity was already used by different attempt evidence.</summary>
    IdentityConflict = 5,

    /// <summary>The semantic Request deadline elapsed and its typed timeout must be resolved.</summary>
    DeadlineElapsed = 6
}

/// <summary>Validated result of claiming one logical durable Request.</summary>
public sealed record DurableOperationClaimResult
{
    internal DurableOperationClaimResult(
        DurableOperationState state,
        DurableOperationClaimDisposition disposition,
        DurableOperationClaim? claim)
    {
        State = Guard.RequireNotNull(state);
        if (!Enum.IsDefined(disposition))
        {
            throw new ArgumentOutOfRangeException(nameof(disposition), disposition, "Unsupported claim disposition.");
        }

        if ((disposition is DurableOperationClaimDisposition.Claimed or DurableOperationClaimDisposition.Replayed)
            != (claim is not null))
        {
            throw new ArgumentException("Only claimed or replayed results carry a live claim.", nameof(claim));
        }
        Disposition = disposition;
        Claim = claim;
    }

    /// <summary>Replacement durable operation state.</summary>
    public DurableOperationState State { get; }

    /// <summary>Observable claim disposition.</summary>
    public DurableOperationClaimDisposition Disposition { get; }

    /// <summary>Live claim when ownership was acquired or replayed; otherwise <see langword="null"/>.</summary>
    public DurableOperationClaim? Claim { get; }
}

/// <summary>Outcome of renewing a live durable-operation claim.</summary>
public enum DurableOperationRenewalDisposition
{
    /// <summary>The live claim expiry was extended under its existing fence.</summary>
    Renewed = 0,

    /// <summary>The requested expiry was already represented by durable state.</summary>
    Replayed = 1,

    /// <summary>The supplied attempt, claimant, or fence is stale.</summary>
    StaleFence = 2,

    /// <summary>The lease had already expired and recovery policy now applies.</summary>
    LeaseExpired = 3,

    /// <summary>The operation already has a durable acknowledgement or disposition.</summary>
    Completed = 4,

    /// <summary>The current attempt is no longer claimable or dispatchable.</summary>
    InvalidState = 5,

    /// <summary>The semantic Request deadline elapsed and the claim cannot be extended.</summary>
    DeadlineElapsed = 6
}

/// <summary>Validated result of renewing one live claim.</summary>
public sealed record DurableOperationRenewalResult
{
    internal DurableOperationRenewalResult(
        DurableOperationState state,
        DurableOperationRenewalDisposition disposition,
        DurableOperationClaim? claim)
    {
        State = Guard.RequireNotNull(state);
        if (!Enum.IsDefined(disposition))
        {
            throw new ArgumentOutOfRangeException(nameof(disposition), disposition, "Unsupported renewal disposition.");
        }

        if ((disposition is DurableOperationRenewalDisposition.Renewed or DurableOperationRenewalDisposition.Replayed)
            != (claim is not null))
        {
            throw new ArgumentException("Only renewed or replayed results carry a live claim.", nameof(claim));
        }
        Disposition = disposition;
        Claim = claim;
    }

    /// <summary>Replacement durable operation state.</summary>
    public DurableOperationState State { get; }

    /// <summary>Observable renewal disposition.</summary>
    public DurableOperationRenewalDisposition Disposition { get; }

    /// <summary>Renewed or replayed claim when available; otherwise <see langword="null"/>.</summary>
    public DurableOperationClaim? Claim { get; }
}

/// <summary>Outcome of crossing the durable dispatch boundary.</summary>
public enum DurableOperationDispatchDisposition
{
    /// <summary>Dispatch was durably marked and the invocation may be sent to the adapter.</summary>
    Dispatched = 0,

    /// <summary>The identical dispatch decision was replayed idempotently.</summary>
    Replayed = 1,

    /// <summary>The supplied attempt or fence is stale.</summary>
    StaleFence = 2,

    /// <summary>The lease expired and recovery policy now applies.</summary>
    LeaseExpired = 3,

    /// <summary>The operation already has a durable acknowledgement or disposition.</summary>
    Completed = 4,

    /// <summary>The supplied attempt is not currently claimable for dispatch.</summary>
    InvalidState = 5,

    /// <summary>Redispatch is unsafe and authored recovery policy must run first.</summary>
    RecoveryRequired = 6,

    /// <summary>The semantic Request deadline elapsed before dispatch.</summary>
    DeadlineElapsed = 7
}

/// <summary>Validated result of crossing the durable dispatch boundary.</summary>
public sealed record DurableOperationDispatchResult
{
    internal DurableOperationDispatchResult(
        DurableOperationState state,
        DurableOperationDispatchDisposition disposition,
        DurableOperationInvocation? invocation)
    {
        State = Guard.RequireNotNull(state);
        if (!Enum.IsDefined(disposition))
        {
            throw new ArgumentOutOfRangeException(nameof(disposition), disposition, "Unsupported dispatch disposition.");
        }

        if ((disposition is DurableOperationDispatchDisposition.Dispatched or DurableOperationDispatchDisposition.Replayed)
            != (invocation is not null))
        {
            throw new ArgumentException("Only dispatched or safely replayed results carry an invocation.", nameof(invocation));
        }
        Disposition = disposition;
        Invocation = invocation;
    }

    /// <summary>Replacement durable operation state.</summary>
    public DurableOperationState State { get; }

    /// <summary>Observable dispatch disposition.</summary>
    public DurableOperationDispatchDisposition Disposition { get; }

    /// <summary>Adapter invocation when dispatch may proceed; otherwise <see langword="null"/>.</summary>
    public DurableOperationInvocation? Invocation { get; }
}

/// <summary>Outcome of recording adapter or reconciliation evidence.</summary>
public enum DurableOperationObservationDisposition
{
    /// <summary>One typed terminal outcome was durably acknowledged.</summary>
    Acknowledged = 0,

    /// <summary>The same acknowledgement or failure evidence was replayed.</summary>
    Replayed = 1,

    /// <summary>Another bounded attempt may be claimed.</summary>
    RetryEligible = 2,

    /// <summary>Explicit reconciliation is required.</summary>
    ReconciliationRequired = 3,

    /// <summary>A declared typed terminal failure is required.</summary>
    TerminalOutcomeRequired = 4,

    /// <summary>Explicit escalation is required.</summary>
    EscalationRequired = 5,

    /// <summary>The supplied attempt or fence is stale.</summary>
    StaleFence = 6,

    /// <summary>The evidence conflicts with a prior durable acknowledgement.</summary>
    ConflictingOutcome = 7,

    /// <summary>The evidence violates the exact Request contract or current operation state.</summary>
    InvalidEvidence = 8,

    /// <summary>An adapter result arrived after another terminal outcome had already won.</summary>
    LateResult = 9,

    /// <summary>The semantic deadline elapsed, so its exact typed timeout must win before other evidence.</summary>
    DeadlineElapsed = 10
}

/// <summary>Validated result of recording adapter or recovery evidence.</summary>
public sealed record DurableOperationObservationResult
{
    internal DurableOperationObservationResult(
        DurableOperationState state,
        DurableOperationObservationDisposition disposition)
    {
        State = Guard.RequireNotNull(state);
        if (!Enum.IsDefined(disposition))
        {
            throw new ArgumentOutOfRangeException(nameof(disposition), disposition, "Unsupported observation disposition.");
        }

        Disposition = disposition;
    }

    /// <summary>Replacement durable operation state.</summary>
    public DurableOperationState State { get; }

    /// <summary>Observable evidence disposition.</summary>
    public DurableOperationObservationDisposition Disposition { get; }
}

/// <summary>Outcome of result admission at a Process token or Transition continuation.</summary>
public enum DurableOperationAdmissionResultKind
{
    /// <summary>A new durable target disposition was produced.</summary>
    Dispositioned = 0,

    /// <summary>The prior durable disposition was returned without advancing again.</summary>
    Duplicate = 1,

    /// <summary>No durable acknowledgement exists to admit.</summary>
    NotAcknowledged = 2,

    /// <summary>The observed target is not the exact Request response target.</summary>
    TargetMismatch = 3,

    /// <summary>Policy requires prior disposition evidence that was not supplied.</summary>
    PriorDispositionRequired = 4
}

/// <summary>Validated result of planning one durable target admission.</summary>
public sealed record DurableOperationAdmissionResult
{
    internal DurableOperationAdmissionResult(
        DurableOperationState state,
        DurableOperationAdmissionResultKind kind,
        DurableOperationAdmission? admission)
    {
        State = Guard.RequireNotNull(state);
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported admission result kind.");
        }

        if ((kind is DurableOperationAdmissionResultKind.Dispositioned or DurableOperationAdmissionResultKind.Duplicate)
            != (admission is not null))
        {
            throw new ArgumentException("Only dispositioned or duplicate results carry an admission.", nameof(admission));
        }
        Kind = kind;
        Admission = admission;
    }

    /// <summary>Replacement durable operation state.</summary>
    public DurableOperationState State { get; }

    /// <summary>Observable admission result.</summary>
    public DurableOperationAdmissionResultKind Kind { get; }

    /// <summary>Closed target-disposition intent when available; otherwise <see langword="null"/>.</summary>
    public DurableOperationAdmission? Admission { get; }
}

/// <summary>
/// Deterministic reference state machine for durable Request execution, acknowledgement, and result admission.
/// </summary>
/// <remarks>
/// Every method consumes explicit state and observations and returns replacement state. Callers choose and persist
/// the durable cuts. Physical compare-and-swap, inbox/outbox, checkpoint, and operation-ledger mechanisms are
/// supplied later by Storage interpretations rather than hidden in this executor.
/// </remarks>
public sealed class DurableOperationReferenceExecutor
{
    readonly InteractionContractCatalog contracts;

    /// <summary>Creates a reference executor over exact canonical interaction contracts.</summary>
    /// <param name="contracts">Exact Request and Reply contract catalog.</param>
    /// <exception cref="ArgumentNullException"><paramref name="contracts"/> is <see langword="null"/>.</exception>
    public DurableOperationReferenceExecutor(InteractionContractCatalog contracts) =>
        this.contracts = Guard.RequireNotNull(contracts);

    /// <summary>Creates the closed recovery intent derived from persisted operation state.</summary>
    /// <remarks>
    /// The intent is derived rather than redundantly stored: the exact binding target, requirement, source
    /// attempt, and fence in state are its single persisted source of truth.
    /// </remarks>
    /// <param name="state">Current durable operation state.</param>
    /// <returns>A reconciliation or escalation intent when required; otherwise <see langword="null"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="state"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">Required source-attempt or exact target evidence is absent.</exception>
    public static DurableOperationRecoveryIntent? GetRecoveryIntent(DurableOperationState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.RecoveryRequirement is not (DurableOperationRecoveryRequirement.Reconcile
            or DurableOperationRecoveryRequirement.Escalate))
        {
            return null;
        }

        var attempt = state.CurrentAttempt
            ?? throw new InvalidOperationException("A durable recovery intent requires its source attempt.");
        var target = state.RecoveryRequirement == DurableOperationRecoveryRequirement.Reconcile
            ? state.Binding.ReconciliationTarget
            : state.Binding.EscalationTarget;
        if (target is null)
        {
            throw new InvalidOperationException("A durable recovery intent requires its exact declared target.");
        }

        var identity = new DurableOperationRecoveryIdentity(
            state.OperationId,
            attempt.Claim.AttemptId,
            attempt.Claim.Fence,
            state.RecoveryRequirement);
        return new(identity, state.Request, state.DeduplicationKey, target);
    }

    /// <summary>Validates and creates initial durable state for one canonical Request.</summary>
    /// <param name="request">Canonical durable Request envelope.</param>
    /// <param name="binding">Portable bounded execution refinement.</param>
    /// <param name="createdAtUtc">Explicit UTC operation-creation observation.</param>
    /// <param name="state">Receives initial state only when every exact link and policy is valid.</param>
    /// <returns>Structured deterministic binding diagnostics.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="request"/> or <paramref name="binding"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="createdAtUtc"/> is not UTC.</exception>
    public DocumentValidationResult TryCreate(
        RequestEnvelope request,
        DurableRequestBinding binding,
        DateTimeOffset createdAtUtc,
        out DurableOperationState? state)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(binding);
        DurableOperationClaim.RequireUtc(createdAtUtc, nameof(createdAtUtc));

        List<DocumentValidationDiagnostic> diagnostics = [];
        if (binding.TimeoutAfter is { } timeout)
        {
            try
            {
                _ = createdAtUtc.Add(timeout);
            }
            catch (ArgumentOutOfRangeException)
            {
                diagnostics.Add(Error(
                    DurableOperationDiagnosticCodes.TimeoutBindingInvalid,
                    "The semantic timeout deadline cannot be represented from operation creation time.",
                    "/binding/timeoutAfter"));
            }
        }
        var envelopeValidation = InteractionEnvelopeValidator.Validate(request, contracts, contracts.ShapeGraph);
        diagnostics.AddRange(envelopeValidation.Diagnostics);
        if (request.Context.Delivery.Durability != InteractionDurabilityDemand.Durable)
        {
            diagnostics.Add(Error(
                DurableOperationDiagnosticCodes.RequestInvalid,
                "A durable operation requires a Request with durable delivery semantics.",
                "/request/context/delivery/durability"));
        }
        if (request.Contract != binding.Request)
        {
            diagnostics.Add(Error(
                DurableOperationDiagnosticCodes.RequestBindingMismatch,
                "The durable binding references a different exact Request contract.",
                "/binding/request"));
        }

        RequestContractDefinition? requestDefinition = null;
        if (!contracts.TryResolve(request.Contract, out var resolved)
            || resolved is not RequestContractDefinition typedRequest)
        {
            diagnostics.Add(Error(
                DurableOperationDiagnosticCodes.RequestInvalid,
                "The exact Request contract cannot be resolved as a canonical Request definition.",
                "/request/contract"));
        }
        else
        {
            requestDefinition = typedRequest;
            ValidateBinding(binding, typedRequest.Response, diagnostics);
        }

        diagnostics.Sort(DocumentValidationDiagnosticComparer.Ordinal);
        var validation = DocumentValidationResult.FromDiagnostics(diagnostics);
        state = validation.IsValid && requestDefinition is not null
            ? new(
                DurableOperationState.CurrentSchemaVersion,
                request,
                binding,
                createdAtUtc)
            : null;
        return validation;
    }

    /// <summary>Claims or idempotently replays ownership of one physical attempt.</summary>
    /// <param name="state">Current durable operation state.</param>
    /// <param name="attemptId">Stable identity proposed for this physical attempt.</param>
    /// <param name="claimant">Stable operational claimant identity.</param>
    /// <param name="observedAtUtc">Explicit UTC claim observation.</param>
    /// <returns>Replacement state, observable disposition, and live claim when available.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="state"/> or <paramref name="claimant"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="attemptId"/> is default, <paramref name="claimant"/> is empty, or
    /// <paramref name="observedAtUtc"/> is not UTC or precedes persisted operation evidence.
    /// </exception>
    /// <exception cref="OverflowException">The next ownership fence cannot be represented.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The resulting lease expiry cannot be represented.</exception>
    /// <exception cref="InvalidOperationException">
    /// Policy evaluation is required but the executor catalog cannot resolve the state's exact Request contract.
    /// </exception>
    public DurableOperationClaimResult Claim(
        DurableOperationState state,
        OperationAttemptId attemptId,
        string claimant,
        DateTimeOffset observedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (string.IsNullOrWhiteSpace(attemptId.Value))
        {
            throw new ArgumentException("A durable claim requires an attempt identity.", nameof(attemptId));
        }

        claimant = Guard.RequireNotNullOrWhiteSpace(claimant);
        DurableOperationClaim.RequireUtc(observedAtUtc, nameof(observedAtUtc));
        RequireOperationObservation(state, observedAtUtc, nameof(observedAtUtc));

        if (state.Acknowledgement is not null || state.Admission is not null)
        {
            return new(state, DurableOperationClaimDisposition.Completed, claim: null);
        }

        if (HasElapsedDeadline(state, observedAtUtc))
        {
            return new(state, DurableOperationClaimDisposition.DeadlineElapsed, claim: null);
        }

        var current = state.CurrentAttempt;
        if (current?.CompletedAtUtc is { } priorCompletion && observedAtUtc < priorCompletion)
        {
            throw new ArgumentException("A new claim cannot precede prior attempt completion.", nameof(observedAtUtc));
        }

        if (current?.Stage is DurableOperationAttemptStage.Claimed or DurableOperationAttemptStage.Dispatched)
        {
            if (current.Claim.IsLiveAt(observedAtUtc))
            {
                if (current.Claim.AttemptId == attemptId
                    && string.Equals(current.Claim.Claimant, claimant, StringComparison.Ordinal))
                {
                    return new(state, DurableOperationClaimDisposition.Replayed, current.Claim);
                }

                return new(state, DurableOperationClaimDisposition.Busy, claim: null);
            }

            state = ExpireCurrentAttempt(state, observedAtUtc);
        }

        if (state.Attempts.Any(attempt => attempt.Claim.AttemptId == attemptId))
        {
            return new(state, DurableOperationClaimDisposition.IdentityConflict, claim: null);
        }

        if (state.RecoveryRequirement is not (DurableOperationRecoveryRequirement.None or DurableOperationRecoveryRequirement.Retry))
        {
            return new(state, DurableOperationClaimDisposition.RecoveryRequired, claim: null);
        }

        if (DispatchAttemptCount(state) >= state.Binding.MaxAttempts)
        {
            state = WithRecovery(state, ResolveUnresolvedRequirement(Response(state)));
            return new(state, DurableOperationClaimDisposition.RecoveryRequired, claim: null);
        }

        var nextFence = checked((state.CurrentAttempt?.Claim.Fence.Value ?? 0) + 1);
        var expiresAtUtc = observedAtUtc.Add(state.Binding.ClaimLease);
        var claim = new DurableOperationClaim(
            attemptId,
            claimant,
            new(nextFence),
            observedAtUtc,
            expiresAtUtc);
        var attempt = new DurableOperationAttempt(
            state.Attempts.Length + 1,
            claim,
            DurableOperationAttemptStage.Claimed);
        var replacement = Replace(
            state,
            attempts: [.. state.Attempts, attempt],
            recoveryRequirement: DurableOperationRecoveryRequirement.None);
        return new(replacement, DurableOperationClaimDisposition.Claimed, claim);
    }

    /// <summary>Renews one live claimed or dispatched attempt without changing its ownership fence.</summary>
    /// <param name="state">Current durable operation state.</param>
    /// <param name="attemptId">Live physical attempt identity.</param>
    /// <param name="fence">Current ownership fence.</param>
    /// <param name="claimant">Stable operational claimant identity.</param>
    /// <param name="observedAtUtc">Explicit UTC renewal observation.</param>
    /// <returns>Replacement state, observable disposition, and renewed claim when available.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="state"/> or <paramref name="claimant"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="claimant"/> is empty or <paramref name="observedAtUtc"/> is not UTC or precedes persisted operation evidence.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">The resulting lease expiry cannot be represented.</exception>
    /// <exception cref="InvalidOperationException">
    /// Expiry policy must be evaluated but the executor catalog cannot resolve the state's exact Request contract.
    /// </exception>
    public DurableOperationRenewalResult RenewClaim(
        DurableOperationState state,
        OperationAttemptId attemptId,
        OperationFence fence,
        string claimant,
        DateTimeOffset observedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(state);
        claimant = Guard.RequireNotNullOrWhiteSpace(claimant);
        DurableOperationClaim.RequireUtc(observedAtUtc, nameof(observedAtUtc));
        RequireOperationObservation(state, observedAtUtc, nameof(observedAtUtc));
        if (state.Acknowledgement is not null || state.Admission is not null)
        {
            return new(state, DurableOperationRenewalDisposition.Completed, claim: null);
        }

        var current = state.CurrentAttempt;
        if (!Matches(current, attemptId, fence)
            || !string.Equals(current!.Claim.Claimant, claimant, StringComparison.Ordinal))
        {
            return new(state, DurableOperationRenewalDisposition.StaleFence, claim: null);
        }
        RequireAttemptObservation(current, observedAtUtc, nameof(observedAtUtc));
        if (current.Stage is not (DurableOperationAttemptStage.Claimed or DurableOperationAttemptStage.Dispatched))
        {
            return new(state, DurableOperationRenewalDisposition.InvalidState, claim: null);
        }

        if (HasElapsedDeadline(state, observedAtUtc))
        {
            return new(state, DurableOperationRenewalDisposition.DeadlineElapsed, claim: null);
        }

        if (!current.Claim.IsLiveAt(observedAtUtc))
        {
            var expired = ExpireCurrentAttempt(state, observedAtUtc);
            return new(expired, DurableOperationRenewalDisposition.LeaseExpired, claim: null);
        }

        var expiresAtUtc = observedAtUtc.Add(state.Binding.ClaimLease);
        if (expiresAtUtc <= current.Claim.ExpiresAtUtc)
        {
            return new(state, DurableOperationRenewalDisposition.Replayed, current.Claim);
        }

        var claim = new DurableOperationClaim(
            current.Claim.AttemptId,
            current.Claim.Claimant,
            current.Claim.Fence,
            current.Claim.ClaimedAtUtc,
            expiresAtUtc,
            renewedAtUtc: observedAtUtc);
        var renewed = new DurableOperationAttempt(
            current.Ordinal,
            claim,
            current.Stage,
            current.DispatchedAtUtc,
            current.CompletedAtUtc,
            current.Failure);
        var replacement = ReplaceLastAttempt(state, renewed);
        return new(replacement, DurableOperationRenewalDisposition.Renewed, claim);
    }

    /// <summary>Durably marks external dispatch and produces the immutable adapter invocation.</summary>
    /// <param name="state">Current durable operation state.</param>
    /// <param name="attemptId">Claimed physical attempt identity.</param>
    /// <param name="fence">Current ownership fence.</param>
    /// <param name="observedAtUtc">Explicit UTC dispatch observation.</param>
    /// <returns>Replacement state, observable disposition, and invocation when dispatch may proceed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="state"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="observedAtUtc"/> is not UTC or precedes persisted operation evidence.</exception>
    /// <exception cref="InvalidOperationException">
    /// Recovery policy must be evaluated but the executor catalog cannot resolve the state's exact Request contract.
    /// </exception>
    public DurableOperationDispatchResult BeginDispatch(
        DurableOperationState state,
        OperationAttemptId attemptId,
        OperationFence fence,
        DateTimeOffset observedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(state);
        DurableOperationClaim.RequireUtc(observedAtUtc, nameof(observedAtUtc));
        RequireOperationObservation(state, observedAtUtc, nameof(observedAtUtc));
        if (state.Acknowledgement is not null || state.Admission is not null)
        {
            return new(state, DurableOperationDispatchDisposition.Completed, invocation: null);
        }

        var current = state.CurrentAttempt;
        if (!Matches(current, attemptId, fence))
        {
            return new(state, DurableOperationDispatchDisposition.StaleFence, invocation: null);
        }

        RequireAttemptObservation(current!, observedAtUtc, nameof(observedAtUtc));
        if (HasElapsedDeadline(state, observedAtUtc))
        {
            return new(state, DurableOperationDispatchDisposition.DeadlineElapsed, invocation: null);
        }

        if (!current!.Claim.IsLiveAt(observedAtUtc))
        {
            var expired = ExpireCurrentAttempt(state, observedAtUtc);
            return new(expired, DurableOperationDispatchDisposition.LeaseExpired, invocation: null);
        }
        if (current.Stage == DurableOperationAttemptStage.Dispatched)
        {
            if (state.Binding.IdempotencyEvidence == DurableOperationIdempotencyEvidence.None)
            {
                var failure = new DurableOperationFailure(
                    DurableOperationFailurePhase.InCall,
                    DurableOperationEffectEvidence.Ambiguous,
                    DurableOperationFailureDisposition.Retryable,
                    DurableOperationFailureCodes.UnsafeDispatchReplay);
                var recovery = RecordFailureState(state, current, failure, observedAtUtc);
                return new(
                    recovery,
                    DurableOperationDispatchDisposition.RecoveryRequired,
                    invocation: null);
            }

            return new(
                state,
                DurableOperationDispatchDisposition.Replayed,
                CreateInvocation(state, current));
        }
        if (current.Stage != DurableOperationAttemptStage.Claimed)
        {
            return new(state, DurableOperationDispatchDisposition.InvalidState, invocation: null);
        }

        var dispatched = new DurableOperationAttempt(
            current.Ordinal,
            current.Claim,
            DurableOperationAttemptStage.Dispatched,
            dispatchedAtUtc: observedAtUtc);
        var replacement = ReplaceLastAttempt(state, dispatched);
        return new(
            replacement,
            DurableOperationDispatchDisposition.Dispatched,
            CreateInvocation(replacement, dispatched));
    }

    /// <summary>Invokes an impure adapter without changing durable state.</summary>
    /// <remarks>Delegated adapter exceptions propagate and do not mutate state or imply any failure evidence.</remarks>
    /// <param name="context">Explicit cancellation, time, and correlation context.</param>
    /// <param name="invocation">Previously persisted dispatch invocation.</param>
    /// <param name="adapter">Impure adapter interpretation.</param>
    /// <returns>Typed outcome or explicit failure evidence to persist separately.</returns>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    /// <exception cref="DurableOperationDeadlineElapsedException">The semantic Request deadline elapsed.</exception>
    /// <exception cref="InvalidOperationException">
    /// Adapter capabilities do not satisfy the durable binding or the adapter returned a null observation.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="context"/> is canceled.</exception>
    public static async ValueTask<DurableOperationAttemptObservation> ExecuteAsync(
        OperationContext context,
        DurableOperationInvocation invocation,
        IDurableOperationAdapter adapter)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(adapter);
        context.ThrowIfCancellationRequested();
        if (invocation.DeadlineUtc is { } deadline && context.UtcNow >= deadline)
        {
            throw new DurableOperationDeadlineElapsedException(
                "The semantic Request deadline elapsed; persist the exact typed timeout instead of dispatching.");
        }
        ValidateAdapterCapabilities(invocation.Binding, adapter.Capabilities);
        return await adapter.ExecuteAsync(context, invocation).ConfigureAwait(false)
            ?? throw new InvalidOperationException("A durable operation adapter returned a null attempt observation.");
    }

    /// <summary>Invokes one true physical batch while preserving independent item identities and evidence.</summary>
    /// <param name="context">Explicit cancellation, time, and correlation context.</param>
    /// <param name="invocations">Non-empty independently dispatched item invocations.</param>
    /// <param name="adapter">Impure physical-batch adapter interpretation.</param>
    /// <returns>Complete per-item evidence normalized to invocation order.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="context"/> or <paramref name="adapter"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="invocations"/> is default or empty, contains null, repeats an exact item, or contains more
    /// than one attempt for the same logical operation.
    /// </exception>
    /// <exception cref="DurableOperationDeadlineElapsedException">
    /// The semantic Request deadline elapsed for an invocation.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Adapter capabilities do not satisfy an invocation, or adapter evidence is incomplete, duplicated, or
    /// addresses an item that was not dispatched.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="context"/> is canceled.</exception>
    public static async ValueTask<ImmutableArray<DurableOperationBatchItemObservation>> ExecuteBatchAsync(
        OperationContext context,
        ImmutableArray<DurableOperationInvocation> invocations,
        IDurableOperationBatchAdapter adapter)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(adapter);
        if (invocations.IsDefaultOrEmpty)
        {
            throw new ArgumentException("A durable physical batch cannot be default or empty.", nameof(invocations));
        }

        context.ThrowIfCancellationRequested();

        var expected = new Dictionary<(EmissionId, OperationAttemptId, OperationFence), int>(invocations.Length);
        var operationIds = new HashSet<EmissionId>();
        for (var index = 0; index < invocations.Length; index++)
        {
            var invocation = invocations[index]
                ?? throw new ArgumentException("A durable physical batch cannot contain null invocations.", nameof(invocations));
            if (invocation.DeadlineUtc is { } deadline && context.UtcNow >= deadline)
            {
                throw new DurableOperationDeadlineElapsedException(
                    $"The semantic Request deadline elapsed for batch item '{invocation.Request.Context.EmissionId.Value}'.");
            }
            ValidateAdapterCapabilities(invocation.Binding, adapter.Capabilities);
            if (!operationIds.Add(invocation.Request.Context.EmissionId))
            {
                throw new ArgumentException(
                    "A durable physical batch cannot contain more than one attempt for a logical operation.",
                    nameof(invocations));
            }
            var key = (invocation.Request.Context.EmissionId, invocation.AttemptId, invocation.Fence);
            if (!expected.TryAdd(key, index))
            {
                throw new ArgumentException("A durable physical batch cannot repeat an operation item.", nameof(invocations));
            }
        }

        var observed = await adapter.ExecuteBatchAsync(context, invocations).ConfigureAwait(false);
        if (observed.IsDefault || observed.Length != invocations.Length)
        {
            throw new InvalidOperationException(
                "A durable physical batch must return exactly one observation for every dispatched item.");
        }

        var byItem = new Dictionary<(EmissionId, OperationAttemptId, OperationFence), DurableOperationBatchItemObservation>(
            observed.Length);
        foreach (var item in observed)
        {
            if (item is null)
            {
                throw new InvalidOperationException("A durable physical batch returned a null item observation.");
            }

            var key = (item.OperationId, item.AttemptId, item.Fence);
            if (!expected.ContainsKey(key))
            {
                throw new InvalidOperationException("A durable physical batch returned evidence for an undispatched item.");
            }

            if (!byItem.TryAdd(key, item))
            {
                throw new InvalidOperationException("A durable physical batch returned duplicate evidence for one item.");
            }
        }

        var normalized = ImmutableArray.CreateBuilder<DurableOperationBatchItemObservation>(invocations.Length);
        foreach (var invocation in invocations)
        {
            var key = (invocation.Request.Context.EmissionId, invocation.AttemptId, invocation.Fence);
            if (!byItem.TryGetValue(key, out var item))
            {
                throw new InvalidOperationException("A durable physical batch omitted evidence for a dispatched item.");
            }

            normalized.Add(item);
        }

        return normalized.MoveToImmutable();
    }

    /// <summary>Records typed adapter outcome or explicit failure evidence under the current fence.</summary>
    /// <param name="state">Current durable operation state.</param>
    /// <param name="attemptId">Physical attempt that produced the evidence.</param>
    /// <param name="fence">Ownership fence under which the adapter ran.</param>
    /// <param name="observation">Typed outcome or explicit failure evidence.</param>
    /// <param name="observedAtUtc">Explicit UTC persistence observation.</param>
    /// <returns>Replacement state and observable acknowledgement or recovery disposition.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="state"/> or <paramref name="observation"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="observedAtUtc"/> is not UTC or precedes persisted operation evidence.</exception>
    /// <exception cref="InvalidOperationException">
    /// Outcome or recovery policy must be evaluated but the executor catalog cannot resolve the state's exact
    /// Request contract.
    /// </exception>
    public DurableOperationObservationResult RecordObservation(
        DurableOperationState state,
        OperationAttemptId attemptId,
        OperationFence fence,
        DurableOperationAttemptObservation observation,
        DateTimeOffset observedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(observation);
        DurableOperationClaim.RequireUtc(observedAtUtc, nameof(observedAtUtc));
        RequireOperationObservation(state, observedAtUtc, nameof(observedAtUtc));

        if (state.Acknowledgement is { } existing)
        {
            if (existing.AttemptId == attemptId && !Matches(state.CurrentAttempt, attemptId, fence))
            {
                return new(state, DurableOperationObservationDisposition.StaleFence);
            }

            if (observation is DurableOperationOutcomeObservation duplicate)
            {
                if (existing.AttemptId == attemptId
                    && state.CurrentAttempt?.Stage == DurableOperationAttemptStage.Resolved)
                {
                    return new(state, DurableOperationObservationDisposition.LateResult);
                }
                if (existing.AttemptId == attemptId && existing.Outcome == duplicate.Outcome)
                {
                    return existing.AdapterEvidence == duplicate.AdapterEvidence
                           && existing.ReplyOrigin == duplicate.ReplyOrigin
                        ? new(state, DurableOperationObservationDisposition.Replayed)
                        : new(state, DurableOperationObservationDisposition.ConflictingOutcome);
                }

                if (existing.AttemptId != attemptId)
                {
                    return new(
                        state,
                        existing.AttemptId is null
                            ? DurableOperationObservationDisposition.LateResult
                            : DurableOperationObservationDisposition.StaleFence);
                }
            }

            return new(state, DurableOperationObservationDisposition.ConflictingOutcome);
        }
        if (HasElapsedDeadline(state, observedAtUtc))
        {
            return new(state, DurableOperationObservationDisposition.DeadlineElapsed);
        }

        var current = state.CurrentAttempt;
        if (!Matches(current, attemptId, fence))
        {
            return new(state, DurableOperationObservationDisposition.StaleFence);
        }

        RequireAttemptObservation(current!, observedAtUtc, nameof(observedAtUtc));
        if (current!.Stage == DurableOperationAttemptStage.Failed)
        {
            return observation is DurableOperationFailureObservation repeated
                   && current.Failure == repeated.Failure
                ? new(state, DurableOperationObservationDisposition.Replayed)
                : new(state, DurableOperationObservationDisposition.StaleFence);
        }
        if (current.Stage != DurableOperationAttemptStage.Dispatched)
        {
            return new(state, DurableOperationObservationDisposition.InvalidEvidence);
        }

        if (!current!.Claim.IsLiveAt(observedAtUtc))
        {
            var expired = ExpireCurrentAttempt(state, observedAtUtc);
            return new(expired, ToObservationDisposition(expired.RecoveryRequirement));
        }

        return observation switch
        {
            DurableOperationOutcomeObservation outcome => Acknowledge(
                state,
                current,
                outcome.Outcome,
                outcome.AdapterEvidence,
                outcome.ReplyOrigin,
                observedAtUtc,
                DurableOperationAttemptStage.Acknowledged),
            DurableOperationFailureObservation failure => RecordFailure(
                state,
                current,
                failure.Failure,
                observedAtUtc),
            _ => throw new ArgumentOutOfRangeException(
                nameof(observation),
                observation.GetType(),
                "Unsupported durable operation observation type.")
        };
    }

    /// <summary>Invokes the adapter reconciliation boundary without changing durable state.</summary>
    /// <remarks>Delegated adapter exceptions propagate and do not mutate state or imply reconciliation evidence.</remarks>
    /// <param name="context">Explicit cancellation, time, and correlation context.</param>
    /// <param name="state">State requiring reconciliation.</param>
    /// <param name="adapter">Impure adapter interpretation.</param>
    /// <returns>Confirmed outcome, proof of no execution, or unresolved evidence.</returns>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    /// <exception cref="DurableOperationDeadlineElapsedException">The semantic Request deadline elapsed.</exception>
    /// <exception cref="InvalidOperationException">
    /// The state does not require reconciliation, adapter capabilities do not satisfy the binding, or the adapter
    /// returned a null observation.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="context"/> is canceled.</exception>
    public static async ValueTask<DurableOperationReconciliationObservation> ReconcileAsync(
        OperationContext context,
        DurableOperationState state,
        IDurableOperationAdapter adapter)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(adapter);
        context.ThrowIfCancellationRequested();
        if (state.RecoveryRequirement != DurableOperationRecoveryRequirement.Reconcile
            || state.CurrentAttempt is not { } attempt)
        {
            throw new InvalidOperationException("The durable operation does not currently require reconciliation.");
        }
        if (HasElapsedDeadline(state, context.UtcNow))
        {
            throw new DurableOperationDeadlineElapsedException(
                "The semantic Request deadline elapsed; persist the exact typed timeout instead of reconciling.");
        }

        ValidateReconciliationAdapterCapabilities(state.Binding, adapter.Capabilities);
        var request = new DurableOperationReconciliationRequest(
            state.Request,
            state.Binding,
            attempt,
            state.DeduplicationKey);
        return await adapter.ReconcileAsync(context, request).ConfigureAwait(false)
            ?? throw new InvalidOperationException("A durable operation adapter returned a null reconciliation observation.");
    }

    /// <summary>Records explicit reconciliation evidence.</summary>
    /// <param name="state">State requiring reconciliation.</param>
    /// <param name="attemptId">Exact failed physical attempt being reconciled.</param>
    /// <param name="fence">Ownership fence of the failed attempt.</param>
    /// <param name="observation">Confirmed outcome, proof of no execution, or unresolved evidence.</param>
    /// <param name="observedAtUtc">Explicit UTC persistence observation.</param>
    /// <returns>Replacement state and observable acknowledgement or recovery disposition.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="state"/> or <paramref name="observation"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="observedAtUtc"/> is not UTC or precedes persisted operation evidence.</exception>
    /// <exception cref="InvalidOperationException">
    /// Outcome or recovery policy must be evaluated but the executor catalog cannot resolve the state's exact
    /// Request contract.
    /// </exception>
    public DurableOperationObservationResult RecordReconciliation(
        DurableOperationState state,
        OperationAttemptId attemptId,
        OperationFence fence,
        DurableOperationReconciliationObservation observation,
        DateTimeOffset observedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(observation);
        DurableOperationClaim.RequireUtc(observedAtUtc, nameof(observedAtUtc));
        RequireOperationObservation(state, observedAtUtc, nameof(observedAtUtc));
        if (state.Acknowledgement is { } acknowledgement)
        {
            var priorEvidence = LastReconciliation(state, attemptId, fence);
            if (priorEvidence?.Observation == observation)
            {
                return new(state, DurableOperationObservationDisposition.Replayed);
            }

            if (acknowledgement.AttemptId is null)
            {
                return new(state, DurableOperationObservationDisposition.LateResult);
            }

            if (acknowledgement.AttemptId != attemptId || !Matches(state.CurrentAttempt, attemptId, fence))
            {
                return new(state, DurableOperationObservationDisposition.StaleFence);
            }

            return acknowledgement.RecoveryIdentity?.Requirement == DurableOperationRecoveryRequirement.Reconcile
                ? new(state, DurableOperationObservationDisposition.ConflictingOutcome)
                : new(state, DurableOperationObservationDisposition.LateResult);
        }
        if (HasElapsedDeadline(state, observedAtUtc))
        {
            return new(state, DurableOperationObservationDisposition.DeadlineElapsed);
        }

        var prior = LastReconciliation(state, attemptId, fence);
        if (prior?.Observation == observation)
        {
            return new(state, DurableOperationObservationDisposition.Replayed);
        }

        var attempt = state.CurrentAttempt;
        if (!Matches(attempt, attemptId, fence))
        {
            return new(state, DurableOperationObservationDisposition.StaleFence);
        }

        RequireAttemptObservation(attempt!, observedAtUtc, nameof(observedAtUtc));
        if (state.RecoveryRequirement != DurableOperationRecoveryRequirement.Reconcile
            || attempt!.Stage != DurableOperationAttemptStage.Failed)
        {
            return new(state, DurableOperationObservationDisposition.InvalidEvidence);
        }

        var evidence = new DurableOperationReconciliationEvidence(
            attemptId,
            fence,
            observedAtUtc,
            observation);
        var reconciliations = (ImmutableArray<DurableOperationReconciliationEvidence>)[.. state.Reconciliations, evidence];

        return observation switch
        {
            DurableOperationReconciledOutcome outcome => Acknowledge(
                state,
                attempt!,
                outcome.Outcome,
                outcome.AdapterEvidence,
                outcome.ReplyOrigin,
                observedAtUtc,
                DurableOperationAttemptStage.Resolved,
                reconciliations,
                new DurableOperationRecoveryIdentity(
                    state.OperationId,
                    attemptId,
                    fence,
                    DurableOperationRecoveryRequirement.Reconcile)),
            DurableOperationConfirmedNotExecuted => ResolveConfirmedNotExecuted(
                Replace(state, reconciliations: reconciliations)),
            DurableOperationUnresolved => ResolveUnresolved(
                Replace(state, reconciliations: reconciliations)),
            _ => throw new ArgumentOutOfRangeException(
                nameof(observation),
                observation.GetType(),
                "Unsupported reconciliation observation type.")
        };
    }

    /// <summary>Resolves an elapsed semantic deadline as its exact declared typed timeout.</summary>
    /// <param name="state">Current durable operation state.</param>
    /// <param name="outcome">Exact declared typed timeout value.</param>
    /// <param name="observedAtUtc">Explicit UTC timeout observation at or after the deadline.</param>
    /// <returns>Replacement state and acknowledgement, replay, conflict, deadline, or invalid-evidence disposition.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="state"/> or <paramref name="outcome"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="observedAtUtc"/> is not UTC or precedes persisted operation evidence.</exception>
    /// <exception cref="InvalidOperationException">
    /// Timeout policy must be evaluated but the executor catalog cannot resolve the state's exact Request contract.
    /// </exception>
    public DurableOperationObservationResult ResolveTimeout(
        DurableOperationState state,
        RequestTimeoutOutcome outcome,
        DateTimeOffset observedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(outcome);
        DurableOperationClaim.RequireUtc(observedAtUtc, nameof(observedAtUtc));
        RequireOperationObservation(state, observedAtUtc, nameof(observedAtUtc));
        if (state.Acknowledgement is { } acknowledgement)
        {
            return acknowledgement.Outcome == outcome
                ? new(state, DurableOperationObservationDisposition.Replayed)
                : new(state, DurableOperationObservationDisposition.ConflictingOutcome);
        }
        if (state.Binding.TimeoutAfter is null
            || Response(state).Timeout != RequestOptionalTerminalSemantics.TerminalOutcome
            || !HasElapsedDeadline(state, observedAtUtc))
        {
            return new(state, DurableOperationObservationDisposition.InvalidEvidence);
        }

        return AcknowledgeEndogenous(
            state,
            outcome,
            observedAtUtc,
            DurableOperationFailureCodes.TimedOutBeforeDispatch,
            DurableOperationFailureCodes.TimedOutInFlight);
    }

    /// <summary>Resolves an explicit semantic cancellation as its exact declared typed cancellation outcome.</summary>
    /// <remarks>
    /// Host cancellation of <see cref="ExecuteAsync"/> is operational interruption only. It never calls this
    /// transition or fabricates a semantic cancellation outcome.
    /// </remarks>
    /// <param name="state">Current durable operation state.</param>
    /// <param name="outcome">Exact declared typed cancellation value.</param>
    /// <param name="observedAtUtc">Explicit UTC cancellation observation.</param>
    /// <returns>Replacement state and acknowledgement, replay, conflict, or invalid-evidence disposition.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="state"/> or <paramref name="outcome"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="observedAtUtc"/> is not UTC or precedes persisted operation evidence.</exception>
    /// <exception cref="InvalidOperationException">
    /// Cancellation policy must be evaluated but the executor catalog cannot resolve the state's exact Request contract.
    /// </exception>
    public DurableOperationObservationResult ResolveCancellation(
        DurableOperationState state,
        RequestCancellationOutcome outcome,
        DateTimeOffset observedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(outcome);
        DurableOperationClaim.RequireUtc(observedAtUtc, nameof(observedAtUtc));
        RequireOperationObservation(state, observedAtUtc, nameof(observedAtUtc));
        if (state.Acknowledgement is { } acknowledgement)
        {
            return acknowledgement.Outcome == outcome
                ? new(state, DurableOperationObservationDisposition.Replayed)
                : new(state, DurableOperationObservationDisposition.ConflictingOutcome);
        }
        if (HasElapsedDeadline(state, observedAtUtc))
        {
            return new(state, DurableOperationObservationDisposition.DeadlineElapsed);
        }

        if (Response(state).Cancellation != RequestOptionalTerminalSemantics.TerminalOutcome)
        {
            return new(state, DurableOperationObservationDisposition.InvalidEvidence);
        }

        return AcknowledgeEndogenous(
            state,
            outcome,
            observedAtUtc,
            DurableOperationFailureCodes.CancelledBeforeDispatch,
            DurableOperationFailureCodes.CancelledInFlight);
    }

    /// <summary>Records the typed terminal outcome returned by the exact declared escalation path.</summary>
    /// <param name="state">State requiring escalation.</param>
    /// <param name="identity">Stable recovery identity supplied with the escalation intent.</param>
    /// <param name="outcome">Exact declared terminal Request outcome.</param>
    /// <param name="evidence">Optional materially known portable escalation evidence.</param>
    /// <param name="replyOrigin">
    /// Exact semantic origin that produced the recovered Reply. Child Process Requests require a Process origin
    /// matching their pinned child target; ordinary Requests require <see langword="null"/>.
    /// </param>
    /// <param name="observedAtUtc">Explicit UTC persistence observation.</param>
    /// <returns>
    /// Replacement state and acknowledgement, replay, conflict, stale, deadline, or invalid-evidence disposition.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="state"/>, <paramref name="identity"/>, or <paramref name="outcome"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="evidence"/> is unknown or failed, or <paramref name="observedAtUtc"/> is not UTC or precedes persisted operation evidence.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Outcome policy must be evaluated but the executor catalog cannot resolve the state's exact Request contract.
    /// </exception>
    public DurableOperationObservationResult ResolveEscalation(
        DurableOperationState state,
        DurableOperationRecoveryIdentity identity,
        RequestTerminalOutcome outcome,
        PortableValue? evidence,
        InteractionOrigin? replyOrigin,
        DateTimeOffset observedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(outcome);
        DurableOperationClaim.RequireUtc(observedAtUtc, nameof(observedAtUtc));
        RequireOperationObservation(state, observedAtUtc, nameof(observedAtUtc));
        if (evidence is not null)
        {
            _ = InteractionValueRequirements.RequireMaterialized(evidence, nameof(evidence), "Escalation evidence");
        }

        if (state.Acknowledgement is { } acknowledgement)
        {
            return acknowledgement.RecoveryIdentity == identity
                   && acknowledgement.Outcome == outcome
                   && acknowledgement.AdapterEvidence == evidence
                   && acknowledgement.ReplyOrigin == replyOrigin
                ? new(state, DurableOperationObservationDisposition.Replayed)
                : identity.OperationId != state.OperationId
                  || identity.Requirement != DurableOperationRecoveryRequirement.Escalate
                  || !Matches(state.CurrentAttempt, identity.SourceAttemptId, identity.SourceFence)
                    ? new(state, DurableOperationObservationDisposition.StaleFence)
                    : new(state, DurableOperationObservationDisposition.ConflictingOutcome);
        }
        if (HasElapsedDeadline(state, observedAtUtc))
        {
            return new(state, DurableOperationObservationDisposition.DeadlineElapsed);
        }

        var expected = GetRecoveryIntent(state);
        if (expected?.Identity != identity)
        {
            return new(state, DurableOperationObservationDisposition.StaleFence);
        }

        if (identity.Requirement != DurableOperationRecoveryRequirement.Escalate
            || state.CurrentAttempt is not { Stage: DurableOperationAttemptStage.Failed } attempt)
        {
            return new(state, DurableOperationObservationDisposition.InvalidEvidence);
        }

        return Acknowledge(
            state,
            attempt,
            outcome,
            evidence,
            replyOrigin,
            observedAtUtc,
            DurableOperationAttemptStage.Resolved,
            recoveryIdentity: identity);
    }

    /// <summary>Supplies the exact declared typed terminal failure required by policy.</summary>
    /// <param name="state">State requiring a terminal outcome.</param>
    /// <param name="outcome">Declared typed failure outcome.</param>
    /// <param name="replyOrigin">
    /// Exact semantic origin that produced the recovered Reply. Child Process Requests require a Process origin
    /// matching their pinned child target; ordinary Requests require <see langword="null"/>.
    /// </param>
    /// <param name="observedAtUtc">Explicit UTC acknowledgement observation.</param>
    /// <returns>Replacement state and acknowledgement, replay, conflict, deadline, or invalid-evidence disposition.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="state"/> or <paramref name="outcome"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="observedAtUtc"/> is not UTC or precedes persisted operation evidence.</exception>
    /// <exception cref="InvalidOperationException">
    /// Outcome policy must be evaluated but the executor catalog cannot resolve the state's exact Request contract.
    /// </exception>
    public DurableOperationObservationResult ResolveTerminalOutcome(
        DurableOperationState state,
        RequestFailureOutcome outcome,
        InteractionOrigin? replyOrigin,
        DateTimeOffset observedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(outcome);
        DurableOperationClaim.RequireUtc(observedAtUtc, nameof(observedAtUtc));
        RequireOperationObservation(state, observedAtUtc, nameof(observedAtUtc));
        if (state.Acknowledgement is { } acknowledgement)
        {
            if (acknowledgement.Outcome != outcome
                || acknowledgement.ReplyOrigin != replyOrigin)
            {
                return new(state, DurableOperationObservationDisposition.ConflictingOutcome);
            }

            return state.CurrentAttempt?.Stage == DurableOperationAttemptStage.Resolved
                   && acknowledgement.RecoveryIdentity is null
                ? new(state, DurableOperationObservationDisposition.Replayed)
                : new(state, DurableOperationObservationDisposition.LateResult);
        }
        if (HasElapsedDeadline(state, observedAtUtc))
        {
            return new(state, DurableOperationObservationDisposition.DeadlineElapsed);
        }

        if (state.RecoveryRequirement != DurableOperationRecoveryRequirement.TerminalOutcome
            || state.CurrentAttempt is not { Stage: DurableOperationAttemptStage.Failed } attempt
            || state.Binding.TerminalFailureOutcome != outcome.Id)
        {
            return new(state, DurableOperationObservationDisposition.InvalidEvidence);
        }

        return Acknowledge(
            state,
            attempt,
            outcome,
            adapterEvidence: null,
            replyOrigin,
            observedAtUtc,
            DurableOperationAttemptStage.Resolved);
    }

    /// <summary>Plans the durable disposition of the acknowledged result at its exact target.</summary>
    /// <param name="state">Acknowledged durable operation state.</param>
    /// <param name="target">Target liveness and prior-disposition evidence from its owning interpreter.</param>
    /// <returns>Replacement state and a closed intent that never mutates target authority directly.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="state"/> or <paramref name="target"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Admission policy must be evaluated but the executor catalog cannot resolve the state's exact Request contract.
    /// </exception>
    public DurableOperationAdmissionResult AdmitResult(
        DurableOperationState state,
        DurableOperationTargetObservation target)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(target);
        if (target.Target != state.Request.ResponseTarget)
        {
            return new(state, DurableOperationAdmissionResultKind.TargetMismatch, admission: null);
        }

        if (state.Admission is { } existing)
        {
            return new(state, DurableOperationAdmissionResultKind.Duplicate, existing);
        }

        if (state.Acknowledgement is not { } acknowledgement)
        {
            return new(state, DurableOperationAdmissionResultKind.NotAcknowledged, admission: null);
        }

        var response = Response(state);
        var policy = target.Arrival switch
        {
            DurableOperationResultArrival.Eligible => (RequestResultDisposition?)null,
            DurableOperationResultArrival.Late => response.LateResult,
            DurableOperationResultArrival.Stale => response.StaleResult,
            DurableOperationResultArrival.Duplicate => response.DuplicateResult,
            _ => throw new ArgumentOutOfRangeException(
                nameof(target),
                target.Arrival,
                "Unsupported target arrival relationship.")
        };
        var disposition = policy switch
        {
            null => DurableOperationAdmissionDisposition.Accepted,
            RequestResultDisposition.Reject => DurableOperationAdmissionDisposition.Rejected,
            RequestResultDisposition.Observe => DurableOperationAdmissionDisposition.Observed,
            RequestResultDisposition.ReusePriorDisposition => DurableOperationAdmissionDisposition.ReusedPriorDisposition,
            _ => throw new ArgumentOutOfRangeException(
                nameof(target),
                policy,
                "Unsupported Request result disposition.")
        };
        if (disposition == DurableOperationAdmissionDisposition.ReusedPriorDisposition
            && target.PriorDisposition is null)
        {
            return new(state, DurableOperationAdmissionResultKind.PriorDispositionRequired, admission: null);
        }

        var admission = new DurableOperationAdmission(
            state.OperationId,
            acknowledgement.AttemptId,
            acknowledgement.Outcome.Id,
            target.Target,
            target.Arrival,
            disposition,
            disposition == DurableOperationAdmissionDisposition.ReusedPriorDisposition
                ? target.PriorDisposition
                : null);
        var replacement = Replace(state, admission: admission);
        return new(replacement, DurableOperationAdmissionResultKind.Dispositioned, admission);
    }

    void ValidateBinding(
        DurableRequestBinding binding,
        RequestResponseObligation response,
        ICollection<DocumentValidationDiagnostic> diagnostics)
    {
        if (binding.Replies.Length != response.TerminalOutcomes.Length
            || response.TerminalOutcomes.Any(outcome => binding.FindReply(outcome.Id) is null))
        {
            diagnostics.Add(Error(
                DurableOperationDiagnosticCodes.ReplyBindingIncomplete,
                "Every terminal Request outcome requires exactly one exact Reply mapping.",
                "/binding/replies"));
        }

        for (var index = 0; index < binding.Replies.Length; index++)
        {
            var reply = binding.Replies[index];
            if (response.Find(reply.Outcome) is null
                || !contracts.TryResolve(reply.Reply, out var definition)
                || definition is not ReplyContractDefinition replyDefinition
                || replyDefinition.Request != binding.Request
                || replyDefinition.Outcome != reply.Outcome)
            {
                diagnostics.Add(Error(
                    DurableOperationDiagnosticCodes.ReplyBindingInvalid,
                    $"Reply mapping '{reply.Outcome.Value}' does not exactly discharge its bound Request outcome.",
                    $"/binding/replies/{index}"));
            }
        }

        if (response.Retry == RequestRetrySemantics.Never && binding.MaxAttempts != 1)
        {
            diagnostics.Add(Error(
                DurableOperationDiagnosticCodes.RetryBudgetInvalid,
                "A Request that forbids retry must have an attempt budget of exactly one.",
                "/binding/maxAttempts"));
        }
        if (response.Retry == RequestRetrySemantics.StableIdentity
            && binding.MaxAttempts > 1
            && binding.IdempotencyEvidence == DurableOperationIdempotencyEvidence.None)
        {
            diagnostics.Add(Error(
                DurableOperationDiagnosticCodes.RetryEvidenceInsufficient,
                "Stable-identity retry requires natural idempotency or target deduplication evidence.",
                "/binding/idempotencyEvidence"));
        }

        var supportsTimeout = response.Timeout == RequestOptionalTerminalSemantics.TerminalOutcome;
        if (supportsTimeout != (binding.TimeoutAfter is not null))
        {
            diagnostics.Add(Error(
                DurableOperationDiagnosticCodes.TimeoutBindingInvalid,
                "The concrete timeout trigger must be present exactly when the Request declares a timeout outcome.",
                "/binding/timeoutAfter"));
        }

        var terminalFailureRequired = response.AmbiguousOutcome == RequestResolutionSemantics.TerminalFailure
                                      || response.UnresolvedOutcome == RequestResolutionSemantics.TerminalFailure;
        var terminalFailure = binding.TerminalFailureOutcome is { } failureId
            ? response.Find(failureId)
            : null;
        var terminalFailureBindingValid = terminalFailureRequired
            ? terminalFailure is RequestFailureDefinition
            : binding.TerminalFailureOutcome is null;
        if (!terminalFailureBindingValid)
        {
            diagnostics.Add(Error(
                DurableOperationDiagnosticCodes.TerminalFailureBindingInvalid,
                "Terminal-failure policy requires exactly one declared typed failure outcome binding.",
                "/binding/terminalFailureOutcome"));
        }

        var reconciliationRequired = (response.Retry == RequestRetrySemantics.ReconcileBeforeRetry
                                      && binding.MaxAttempts > 1)
                                     || response.AmbiguousOutcome == RequestResolutionSemantics.Reconcile
                                     || response.UnresolvedOutcome == RequestResolutionSemantics.Reconcile;
        if (reconciliationRequired != (binding.ReconciliationTarget is not null))
        {
            diagnostics.Add(Error(
                DurableOperationDiagnosticCodes.ReconciliationBindingInvalid,
                "Reconciliation policy and its exact semantic target must be declared together.",
                "/binding/reconciliationTarget"));
        }

        var escalationRequired = response.AmbiguousOutcome == RequestResolutionSemantics.Escalate
                                 || response.UnresolvedOutcome == RequestResolutionSemantics.Escalate;
        if (escalationRequired != (binding.EscalationTarget is not null))
        {
            diagnostics.Add(Error(
                DurableOperationDiagnosticCodes.EscalationBindingInvalid,
                "Escalation policy and its exact semantic target must be declared together.",
                "/binding/escalationTarget"));
        }
    }

    DurableOperationState ExpireCurrentAttempt(DurableOperationState state, DateTimeOffset observedAtUtc)
    {
        var attempt = state.CurrentAttempt
            ?? throw new InvalidOperationException("Cannot expire an operation without an attempt.");
        var failure = attempt.Stage switch
        {
            DurableOperationAttemptStage.Claimed => new DurableOperationFailure(
                DurableOperationFailurePhase.PreCall,
                DurableOperationEffectEvidence.NotExecuted,
                DurableOperationFailureDisposition.Retryable,
                DurableOperationFailureCodes.ClaimExpiredBeforeDispatch),
            DurableOperationAttemptStage.Dispatched => new DurableOperationFailure(
                DurableOperationFailurePhase.InCall,
                DurableOperationEffectEvidence.Ambiguous,
                DurableOperationFailureDisposition.Retryable,
                DurableOperationFailureCodes.ClaimExpiredAfterDispatch),
            _ => throw new InvalidOperationException("Only active attempts can expire.")
        };
        return RecordFailureState(state, attempt, failure, observedAtUtc);
    }

    DurableOperationObservationResult RecordFailure(
        DurableOperationState state,
        DurableOperationAttempt attempt,
        DurableOperationFailure failure,
        DateTimeOffset observedAtUtc)
    {
        var replacement = RecordFailureState(state, attempt, failure, observedAtUtc);
        return new(replacement, ToObservationDisposition(replacement.RecoveryRequirement));
    }

    DurableOperationState RecordFailureState(
        DurableOperationState state,
        DurableOperationAttempt attempt,
        DurableOperationFailure failure,
        DateTimeOffset observedAtUtc)
    {
        var failed = new DurableOperationAttempt(
            attempt.Ordinal,
            attempt.Claim,
            DurableOperationAttemptStage.Failed,
            attempt.DispatchedAtUtc,
            observedAtUtc,
            failure);
        var response = Response(state);
        var recovery = attempt.DispatchedAtUtc is null
                       && failure.EffectEvidence == DurableOperationEffectEvidence.NotExecuted
            ? DurableOperationRecoveryRequirement.Retry
            : failure.EffectEvidence == DurableOperationEffectEvidence.Ambiguous
            ? ResolveAmbiguousRequirement(state, response)
            : failure.Disposition == DurableOperationFailureDisposition.Retryable
              && response.Retry != RequestRetrySemantics.Never
              && DispatchAttemptCount(state) < state.Binding.MaxAttempts
                ? DurableOperationRecoveryRequirement.Retry
                : ResolveUnresolvedRequirement(response);
        return ReplaceLastAttempt(state, failed, recovery);
    }

    DurableOperationRecoveryRequirement ResolveAmbiguousRequirement(
        DurableOperationState state,
        RequestResponseObligation response)
    {
        if (response.Retry == RequestRetrySemantics.StableIdentity
            && state.Binding.IdempotencyEvidence is DurableOperationIdempotencyEvidence.NaturallyIdempotent
                or DurableOperationIdempotencyEvidence.TargetDeduplication
            && DispatchAttemptCount(state) < state.Binding.MaxAttempts)
        {
            return DurableOperationRecoveryRequirement.Retry;
        }
        if (response.Retry == RequestRetrySemantics.ReconcileBeforeRetry
            && DispatchAttemptCount(state) < state.Binding.MaxAttempts)
        {
            return DurableOperationRecoveryRequirement.Reconcile;
        }

        return response.AmbiguousOutcome switch
        {
            RequestResolutionSemantics.TerminalFailure => DurableOperationRecoveryRequirement.TerminalOutcome,
            RequestResolutionSemantics.Reconcile => DurableOperationRecoveryRequirement.Reconcile,
            RequestResolutionSemantics.Escalate => DurableOperationRecoveryRequirement.Escalate,
            _ => throw new ArgumentOutOfRangeException(
                nameof(response),
                response.AmbiguousOutcome,
                "Unsupported ambiguous-outcome policy.")
        };
    }

    static DurableOperationRecoveryRequirement ResolveUnresolvedRequirement(RequestResponseObligation response) =>
        response.UnresolvedOutcome switch
        {
            RequestResolutionSemantics.TerminalFailure => DurableOperationRecoveryRequirement.TerminalOutcome,
            RequestResolutionSemantics.Reconcile => DurableOperationRecoveryRequirement.Reconcile,
            RequestResolutionSemantics.Escalate => DurableOperationRecoveryRequirement.Escalate,
            _ => throw new ArgumentOutOfRangeException(
                nameof(response),
                response.UnresolvedOutcome,
                "Unsupported unresolved-outcome policy.")
        };

    DurableOperationObservationResult Acknowledge(
        DurableOperationState state,
        DurableOperationAttempt attempt,
        RequestTerminalOutcome outcome,
        PortableValue? adapterEvidence,
        InteractionOrigin? replyOrigin,
        DateTimeOffset observedAtUtc,
        DurableOperationAttemptStage resolvedStage,
        ImmutableArray<DurableOperationReconciliationEvidence>? reconciliations = null,
        DurableOperationRecoveryIdentity? recoveryIdentity = null)
    {
        RequireAttemptObservation(attempt, observedAtUtc, nameof(observedAtUtc));
        if (outcome is RequestTimeoutOutcome or RequestCancellationOutcome)
        {
            return new(state, DurableOperationObservationDisposition.InvalidEvidence);
        }

        if (!ReplyOriginMatchesRequest(state.Request, replyOrigin))
        {
            return new(state, DurableOperationObservationDisposition.InvalidEvidence);
        }

        if (!TryValidateOutcome(state, outcome, out var replyBinding))
        {
            return new(state, DurableOperationObservationDisposition.InvalidEvidence);
        }

        var resolved = new DurableOperationAttempt(
            attempt.Ordinal,
            attempt.Claim,
            resolvedStage,
            attempt.DispatchedAtUtc,
            resolvedStage == DurableOperationAttemptStage.Resolved
                ? attempt.CompletedAtUtc
                : observedAtUtc,
            resolvedStage == DurableOperationAttemptStage.Resolved ? attempt.Failure : null);
        var acknowledgement = new DurableOperationAcknowledgement(
            state.OperationId,
            attempt.Claim.AttemptId,
            replyBinding!.Reply,
            outcome,
            observedAtUtc,
            adapterEvidence,
            recoveryIdentity,
            replyOrigin);
        var replacement = Replace(
            state,
            attempts: state.Attempts.SetItem(state.Attempts.Length - 1, resolved),
            reconciliations: reconciliations,
            recoveryRequirement: DurableOperationRecoveryRequirement.None,
            acknowledgement: acknowledgement);
        return new(replacement, DurableOperationObservationDisposition.Acknowledged);
    }

    DurableOperationObservationResult AcknowledgeEndogenous(
        DurableOperationState state,
        RequestTerminalOutcome outcome,
        DateTimeOffset observedAtUtc,
        string beforeDispatchFailureCode,
        string inFlightFailureCode)
    {
        if (state.Request.ChildTarget is not null)
        {
            return new(state, DurableOperationObservationDisposition.InvalidEvidence);
        }

        if (!TryValidateOutcome(state, outcome, out var replyBinding))
        {
            return new(state, DurableOperationObservationDisposition.InvalidEvidence);
        }

        var attempts = state.Attempts;
        if (state.CurrentAttempt is { } current)
        {
            RequireAttemptObservation(current, observedAtUtc, nameof(observedAtUtc));
            if (current.Stage is DurableOperationAttemptStage.Claimed or DurableOperationAttemptStage.Dispatched)
            {
                var dispatched = current.Stage == DurableOperationAttemptStage.Dispatched;
                var failure = new DurableOperationFailure(
                    dispatched
                        ? DurableOperationFailurePhase.InCall
                        : DurableOperationFailurePhase.PreCall,
                    dispatched
                        ? DurableOperationEffectEvidence.Ambiguous
                        : DurableOperationEffectEvidence.NotExecuted,
                    DurableOperationFailureDisposition.Terminal,
                    dispatched ? inFlightFailureCode : beforeDispatchFailureCode);
                var failed = new DurableOperationAttempt(
                    current.Ordinal,
                    current.Claim,
                    DurableOperationAttemptStage.Failed,
                    current.DispatchedAtUtc,
                    observedAtUtc,
                    failure);
                attempts = attempts.SetItem(attempts.Length - 1, failed);
            }
        }

        var acknowledgement = new DurableOperationAcknowledgement(
            state.OperationId,
            attemptId: null,
            replyBinding!.Reply,
            outcome,
            observedAtUtc);
        var replacement = Replace(
            state,
            attempts: attempts,
            recoveryRequirement: DurableOperationRecoveryRequirement.None,
            acknowledgement: acknowledgement);
        return new(replacement, DurableOperationObservationDisposition.Acknowledged);
    }

    static bool ReplyOriginMatchesRequest(RequestEnvelope request, InteractionOrigin? replyOrigin) =>
        request.ChildTarget is { } childTarget
            ? replyOrigin is ProcessInteractionOrigin childOrigin
              && childOrigin.Definition == childTarget.Definition
              && childOrigin.Continuation == childTarget.Continuation
            : replyOrigin is null;

    bool TryValidateOutcome(
        DurableOperationState state,
        RequestTerminalOutcome outcome,
        out DurableReplyBinding? replyBinding)
    {
        var expected = Response(state).Find(outcome.Id);
        replyBinding = state.Binding.FindReply(outcome.Id);
        if (expected is null || replyBinding is null || outcome.Value.Contract != expected.Schema.Contract)
        {
            return false;
        }

        return (expected, outcome) switch
        {
            (RequestResultDefinition, RequestResultOutcome) => true,
            (RequestFailureDefinition, RequestFailureOutcome) => true,
            (RequestTimeoutDefinition, RequestTimeoutOutcome) => true,
            (RequestCancellationDefinition, RequestCancellationOutcome) => true,
            _ => false
        };
    }

    DurableOperationObservationResult ResolveConfirmedNotExecuted(DurableOperationState state)
    {
        var recovery = DispatchAttemptCount(state) < state.Binding.MaxAttempts
            && Response(state).Retry != RequestRetrySemantics.Never
                ? DurableOperationRecoveryRequirement.Retry
                : ResolveUnresolvedRequirement(Response(state));
        var replacement = WithRecovery(state, recovery);
        return new(replacement, ToObservationDisposition(recovery));
    }

    DurableOperationObservationResult ResolveUnresolved(DurableOperationState state)
    {
        var recovery = ResolveUnresolvedRequirement(Response(state));
        var replacement = WithRecovery(state, recovery);
        return new(replacement, ToObservationDisposition(recovery));
    }

    RequestResponseObligation Response(DurableOperationState state)
    {
        if (contracts.TryResolve(state.Request.Contract, out var definition)
            && definition is RequestContractDefinition request)
        {
            return request.Response;
        }

        throw new InvalidOperationException("Durable operation state references an unavailable Request contract.");
    }

    static DurableOperationInvocation CreateInvocation(
        DurableOperationState state,
        DurableOperationAttempt attempt)
    {
        DateTimeOffset? deadline = null;
        if (state.Binding.TimeoutAfter is { } timeout)
        {
            deadline = state.CreatedAtUtc.Add(timeout);
        }

        return new(
            state.Request,
            state.Binding,
            attempt.Claim.AttemptId,
            attempt.Ordinal,
            attempt.Claim.Fence,
            state.DeduplicationKey,
            deadline);
    }

    static bool Matches(
        DurableOperationAttempt? attempt,
        OperationAttemptId attemptId,
        OperationFence fence) =>
        attempt is not null
        && attempt.Claim.AttemptId == attemptId
        && attempt.Claim.Fence == fence;

    static DurableOperationReconciliationEvidence? LastReconciliation(
        DurableOperationState state,
        OperationAttemptId attemptId,
        OperationFence fence)
    {
        for (var index = state.Reconciliations.Length - 1; index >= 0; index--)
        {
            var evidence = state.Reconciliations[index];
            if (evidence.AttemptId == attemptId && evidence.Fence == fence)
            {
                return evidence;
            }
        }

        return null;
    }

    static int DispatchAttemptCount(DurableOperationState state)
    {
        var count = 0;
        foreach (var attempt in state.Attempts)
        {
            if (attempt.DispatchedAtUtc is not null)
            {
                count++;
            }
        }

        return count;
    }

    static bool HasElapsedDeadline(DurableOperationState state, DateTimeOffset observedAtUtc) =>
        state.Binding.TimeoutAfter is { } timeout
        && observedAtUtc >= state.CreatedAtUtc.Add(timeout);

    static void RequireOperationObservation(
        DurableOperationState state,
        DateTimeOffset observedAtUtc,
        string parameterName)
    {
        var latestEvidenceAtUtc = state.CreatedAtUtc;
        foreach (var attempt in state.Attempts)
        {
            latestEvidenceAtUtc = Later(latestEvidenceAtUtc, attempt.Claim.RenewedAtUtc);
            if (attempt.DispatchedAtUtc is { } dispatchedAtUtc)
            {
                latestEvidenceAtUtc = Later(latestEvidenceAtUtc, dispatchedAtUtc);
            }

            if (attempt.CompletedAtUtc is { } completedAtUtc)
            {
                latestEvidenceAtUtc = Later(latestEvidenceAtUtc, completedAtUtc);
            }
        }
        foreach (var reconciliation in state.Reconciliations)
        {
            latestEvidenceAtUtc = Later(latestEvidenceAtUtc, reconciliation.ObservedAtUtc);
        }

        if (state.Acknowledgement is { } acknowledgement)
        {
            latestEvidenceAtUtc = Later(latestEvidenceAtUtc, acknowledgement.AcknowledgedAtUtc);
        }

        if (observedAtUtc < latestEvidenceAtUtc)
        {
            throw new ArgumentException("An operation observation cannot precede persisted operation evidence.", parameterName);
        }
    }

    static DateTimeOffset Later(DateTimeOffset left, DateTimeOffset right) => left >= right ? left : right;

    static void RequireAttemptObservation(
        DurableOperationAttempt attempt,
        DateTimeOffset observedAtUtc,
        string parameterName)
    {
        var lowerBound = attempt.CompletedAtUtc
                         ?? attempt.DispatchedAtUtc
                         ?? attempt.Claim.ClaimedAtUtc;
        if (observedAtUtc < lowerBound)
        {
            throw new ArgumentException("An attempt observation cannot precede durable attempt evidence.", parameterName);
        }
    }

    /// <summary>Validates that adapter capabilities satisfy one exact durable Request dispatch binding.</summary>
    /// <param name="binding">Portable execution refinement whose exact Request and idempotency evidence are required.</param>
    /// <param name="capabilities">Capabilities declared by the selected impure adapter.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="binding"/> or <paramref name="capabilities"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The adapter does not support the exact Request contract or required idempotency evidence.
    /// </exception>
    public static void ValidateAdapterCapabilities(
        DurableRequestBinding binding,
        DurableOperationAdapterCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ValidateAdapterCapabilitiesCore(binding, capabilities, requiresReconciliation: false);
    }

    /// <summary>Validates that adapter capabilities satisfy one exact durable Request reconciliation binding.</summary>
    /// <param name="binding">Portable execution refinement whose reconciliation path must be interpreted.</param>
    /// <param name="capabilities">Capabilities declared by the selected impure adapter.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="binding"/> or <paramref name="capabilities"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The adapter does not support the exact Request contract, required idempotency evidence, or reconciliation.
    /// </exception>
    public static void ValidateReconciliationAdapterCapabilities(
        DurableRequestBinding binding,
        DurableOperationAdapterCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ValidateAdapterCapabilitiesCore(binding, capabilities, requiresReconciliation: true);
    }

    static void ValidateAdapterCapabilitiesCore(
        DurableRequestBinding binding,
        DurableOperationAdapterCapabilities capabilities,
        bool requiresReconciliation)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        if (!capabilities.Supports(binding.Request))
        {
            throw new InvalidOperationException("The durable operation adapter does not support the exact Request contract.");
        }

        if (binding.IdempotencyEvidence != DurableOperationIdempotencyEvidence.None
            && capabilities.IdempotencyEvidence != binding.IdempotencyEvidence)
        {
            throw new InvalidOperationException(
                "The durable operation adapter does not supply the idempotency evidence required by the binding.");
        }
        if (requiresReconciliation
            && capabilities.Reconciliation != DurableOperationReconciliationCapability.Supported)
        {
            throw new InvalidOperationException("The durable operation adapter does not support required reconciliation.");
        }
    }

    static DurableOperationObservationDisposition ToObservationDisposition(
        DurableOperationRecoveryRequirement requirement) => requirement switch
        {
            DurableOperationRecoveryRequirement.Retry => DurableOperationObservationDisposition.RetryEligible,
            DurableOperationRecoveryRequirement.Reconcile => DurableOperationObservationDisposition.ReconciliationRequired,
            DurableOperationRecoveryRequirement.TerminalOutcome => DurableOperationObservationDisposition.TerminalOutcomeRequired,
            DurableOperationRecoveryRequirement.Escalate => DurableOperationObservationDisposition.EscalationRequired,
            _ => DurableOperationObservationDisposition.InvalidEvidence
        };

    static DurableOperationState ReplaceLastAttempt(
        DurableOperationState state,
        DurableOperationAttempt attempt,
        DurableOperationRecoveryRequirement recovery = DurableOperationRecoveryRequirement.None) =>
        Replace(
            state,
            attempts: state.Attempts.SetItem(state.Attempts.Length - 1, attempt),
            recoveryRequirement: recovery);

    static DurableOperationState WithRecovery(
        DurableOperationState state,
        DurableOperationRecoveryRequirement recovery) =>
        Replace(state, recoveryRequirement: recovery);

    static DurableOperationState Replace(
        DurableOperationState state,
        ImmutableArray<DurableOperationAttempt>? attempts = null,
        ImmutableArray<DurableOperationReconciliationEvidence>? reconciliations = null,
        DurableOperationRecoveryRequirement? recoveryRequirement = null,
        DurableOperationAcknowledgement? acknowledgement = null,
        DurableOperationAdmission? admission = null) =>
        new(
            state.SchemaVersion,
            state.Request,
            state.Binding,
            state.CreatedAtUtc,
            attempts ?? state.Attempts,
            reconciliations ?? state.Reconciliations,
            recoveryRequirement ?? state.RecoveryRequirement,
            acknowledgement ?? state.Acknowledgement,
            admission ?? state.Admission);

    static DocumentValidationDiagnostic Error(string code, string message, string location) =>
        new(
            code,
            DiagnosticSeverity.Error,
            message,
            location,
            Evidence: new(stage: "durableOperationBinding"));
}
