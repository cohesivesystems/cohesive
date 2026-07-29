using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Cohesive.Model.Serialization;

namespace Cohesive.Execution;

/// <summary>Evidence that repeated physical execution preserves one logical external consequence.</summary>
public enum DurableOperationIdempotencyEvidence
{
    /// <summary>No evidence was declared; invalid for a durable Request binding.</summary>
    Unspecified = 0,

    /// <summary>No repeat-execution evidence is available.</summary>
    None = 1,

    /// <summary>The target operation is naturally idempotent for the canonical Request payload.</summary>
    NaturallyIdempotent = 2,

    /// <summary>The target durably deduplicates the stable interaction idempotency key.</summary>
    TargetDeduplication = 3
}

/// <summary>Stable logical-deduplication key derived without inventing a second operation identity.</summary>
/// <remarks>
/// <see cref="EmissionId"/> remains the logical operation identity. This key scopes target deduplication so an
/// identical idempotency value in another authority or Request contract cannot collide accidentally.
/// </remarks>
public sealed record DurableOperationDeduplicationKey
{
    /// <summary>Creates a scoped logical-deduplication key.</summary>
    /// <param name="authorityScope">Authority and tenant boundary of the Request.</param>
    /// <param name="requestContract">Exact canonical Request contract.</param>
    /// <param name="idempotencyKey">Stable Request idempotency basis.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="authorityScope"/> or <paramref name="requestContract"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="idempotencyKey"/> is a default value.</exception>
    [JsonConstructor]
    public DurableOperationDeduplicationKey(
        InteractionAuthorityScope authorityScope,
        RequestContractReference requestContract,
        InteractionIdempotencyKey idempotencyKey)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey.Value))
            throw new ArgumentException("A durable operation requires a stable idempotency key.", nameof(idempotencyKey));

        AuthorityScope = Guard.RequireNotNull(authorityScope);
        RequestContract = Guard.RequireNotNull(requestContract);
        IdempotencyKey = idempotencyKey;
    }

    /// <summary>Authority and tenant boundary of the Request.</summary>
    public InteractionAuthorityScope AuthorityScope { get; }

    /// <summary>Exact canonical Request contract.</summary>
    public RequestContractReference RequestContract { get; }

    /// <summary>Stable Request idempotency basis.</summary>
    public InteractionIdempotencyKey IdempotencyKey { get; }
}

/// <summary>Exact Reply contract selected for one terminal Request outcome.</summary>
public sealed record DurableReplyBinding
{
    /// <summary>Creates an exact terminal-outcome Reply binding.</summary>
    /// <param name="outcome">Stable terminal outcome declared by the Request.</param>
    /// <param name="reply">Exact Reply contract that discharges that outcome.</param>
    /// <exception cref="ArgumentException"><paramref name="outcome"/> is a default value.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="reply"/> is <see langword="null"/>.</exception>
    [JsonConstructor]
    public DurableReplyBinding(RequestTerminalOutcomeId outcome, ReplyContractReference reply)
    {
        if (string.IsNullOrWhiteSpace(outcome.Value))
            throw new ArgumentException("A durable Reply binding requires an outcome identity.", nameof(outcome));

        Outcome = outcome;
        Reply = Guard.RequireNotNull(reply);
    }

    /// <summary>Stable terminal outcome declared by the Request.</summary>
    public RequestTerminalOutcomeId Outcome { get; }

    /// <summary>Exact Reply contract that discharges the outcome.</summary>
    public ReplyContractReference Reply { get; }
}

/// <summary>Exact definition node that realizes reconciliation or escalation semantics.</summary>
/// <remarks>
/// The target is definition-level semantic data. A Process compiler may bind it to a future Process node and a
/// Transition compiler may bind it to a continuation node without introducing runtime delegates here.
/// </remarks>
public sealed record DurableOperationResolutionTarget
{
    /// <summary>Creates an exact recovery-path target.</summary>
    /// <param name="definition">Exact canonical definition containing the target node.</param>
    /// <param name="node">Stable recovery-path node identity.</param>
    /// <exception cref="ArgumentNullException"><paramref name="definition"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="node"/> is a default value.</exception>
    [JsonConstructor]
    public DurableOperationResolutionTarget(ExecutionDefinitionReference definition, ExecutionNodeId node)
    {
        if (string.IsNullOrWhiteSpace(node.Value))
            throw new ArgumentException("A durable operation resolution target requires a node identity.", nameof(node));

        Definition = Guard.RequireNotNull(definition);
        Node = node;
    }

    /// <summary>Exact canonical definition containing the target node.</summary>
    public ExecutionDefinitionReference Definition { get; }

    /// <summary>Stable recovery-path node identity.</summary>
    public ExecutionNodeId Node { get; }
}

/// <summary>Stable composite identity of one logical recovery obligation.</summary>
public sealed record DurableOperationRecoveryIdentity
{
    /// <summary>Creates a recovery identity from persisted operation and source-attempt evidence.</summary>
    /// <param name="operationId">Canonical Request emission and logical operation identity.</param>
    /// <param name="sourceAttemptId">Ambiguous or unresolved physical attempt.</param>
    /// <param name="sourceFence">Ownership fence of the source attempt.</param>
    /// <param name="requirement">Reconciliation or escalation requirement.</param>
    /// <exception cref="ArgumentException">
    /// An identity or fence is default, or <paramref name="requirement"/> is not reconciliation or escalation.
    /// </exception>
    [JsonConstructor]
    public DurableOperationRecoveryIdentity(
        EmissionId operationId,
        OperationAttemptId sourceAttemptId,
        OperationFence sourceFence,
        DurableOperationRecoveryRequirement requirement)
    {
        if (string.IsNullOrWhiteSpace(operationId.Value))
            throw new ArgumentException("Recovery requires a logical operation identity.", nameof(operationId));
        if (string.IsNullOrWhiteSpace(sourceAttemptId.Value))
            throw new ArgumentException("Recovery requires a source attempt identity.", nameof(sourceAttemptId));
        if (sourceFence.Value <= 0)
            throw new ArgumentException("Recovery requires a positive source fence.", nameof(sourceFence));
        if (requirement is not (DurableOperationRecoveryRequirement.Reconcile
            or DurableOperationRecoveryRequirement.Escalate))
        {
            throw new ArgumentException(
                "A recovery identity represents only reconciliation or escalation.",
                nameof(requirement));
        }

        OperationId = operationId;
        SourceAttemptId = sourceAttemptId;
        SourceFence = sourceFence;
        Requirement = requirement;
    }

    /// <summary>Canonical Request emission and logical operation identity.</summary>
    public EmissionId OperationId { get; }

    /// <summary>Ambiguous or unresolved physical attempt.</summary>
    public OperationAttemptId SourceAttemptId { get; }

    /// <summary>Ownership fence of the source attempt.</summary>
    public OperationFence SourceFence { get; }

    /// <summary>Reconciliation or escalation requirement.</summary>
    public DurableOperationRecoveryRequirement Requirement { get; }
}

/// <summary>Closed portable intent for an owning interpreter to execute one declared recovery path.</summary>
public sealed record DurableOperationRecoveryIntent
{
    /// <summary>Creates a closed recovery intent.</summary>
    /// <param name="identity">Stable logical recovery identity.</param>
    /// <param name="request">Canonical Request whose obligation is being recovered.</param>
    /// <param name="deduplicationKey">Scoped logical target-deduplication key.</param>
    /// <param name="target">Exact definition node realizing the authored recovery path.</param>
    /// <exception cref="ArgumentNullException">Any argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// The identity or deduplication key does not belong to <paramref name="request"/>.
    /// </exception>
    [JsonConstructor]
    public DurableOperationRecoveryIntent(
        DurableOperationRecoveryIdentity identity,
        RequestEnvelope request,
        DurableOperationDeduplicationKey deduplicationKey,
        DurableOperationResolutionTarget target)
    {
        Identity = Guard.RequireNotNull(identity);
        Request = Guard.RequireNotNull(request);
        DeduplicationKey = Guard.RequireNotNull(deduplicationKey);
        Target = Guard.RequireNotNull(target);
        if (identity.OperationId != request.Context.EmissionId)
            throw new ArgumentException("Recovery identity belongs to another logical Request.", nameof(identity));
        var expectedKey = new DurableOperationDeduplicationKey(
            request.Context.AuthorityScope,
            request.Contract,
            request.Context.IdempotencyKey);
        if (deduplicationKey != expectedKey)
            throw new ArgumentException("Recovery deduplication evidence belongs to another Request.", nameof(deduplicationKey));
    }

    /// <summary>Stable logical recovery identity.</summary>
    public DurableOperationRecoveryIdentity Identity { get; }

    /// <summary>Canonical Request whose obligation is being recovered.</summary>
    public RequestEnvelope Request { get; }

    /// <summary>Scoped logical target-deduplication key.</summary>
    public DurableOperationDeduplicationKey DeduplicationKey { get; }

    /// <summary>Exact definition node realizing the authored recovery path.</summary>
    public DurableOperationResolutionTarget Target { get; }
}

/// <summary>Portable execution refinement for one exact canonical Request contract.</summary>
/// <remarks>
/// Authored response meaning remains in <see cref="RequestResponseObligation"/>. This binding supplies the
/// bounded attempt and lease policy plus exact Reply and recovery-path links needed by a durable interpretation.
/// It contains no handler, clock, repository, transaction, or provider object.
/// </remarks>
public sealed record DurableRequestBinding
{
    /// <summary>Creates a normalized durable Request binding.</summary>
    /// <param name="request">Exact Request contract interpreted by the binding.</param>
    /// <param name="replies">One exact Reply mapping for every terminal Request outcome.</param>
    /// <param name="maxAttempts">Maximum physical execution attempts, including the first attempt.</param>
    /// <param name="claimLease">Positive ownership-lease duration for each attempt.</param>
    /// <param name="timeoutAfter">Optional positive semantic timeout measured from operation creation.</param>
    /// <param name="idempotencyEvidence">Evidence supporting repeated physical execution.</param>
    /// <param name="terminalFailureOutcome">Exact typed failure used when policy resolves ambiguity as failure.</param>
    /// <param name="reconciliationTarget">Exact semantic path required by reconciliation policy.</param>
    /// <param name="escalationTarget">Exact semantic path required by escalation policy.</param>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="replies"/> is default or empty, contains a null binding, or duplicates an outcome;
    /// or an optional outcome identity is default.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="maxAttempts"/> or <paramref name="claimLease"/> is not positive;
    /// <paramref name="timeoutAfter"/> is present but not positive; or <paramref name="idempotencyEvidence"/> is
    /// unspecified or unsupported.
    /// </exception>
    [JsonConstructor]
    public DurableRequestBinding(
        RequestContractReference request,
        ImmutableArray<DurableReplyBinding> replies,
        int maxAttempts,
        TimeSpan claimLease,
        TimeSpan? timeoutAfter,
        DurableOperationIdempotencyEvidence idempotencyEvidence,
        RequestTerminalOutcomeId? terminalFailureOutcome = null,
        DurableOperationResolutionTarget? reconciliationTarget = null,
        DurableOperationResolutionTarget? escalationTarget = null)
    {
        if (replies.IsDefaultOrEmpty)
            throw new ArgumentException("A durable Request binding requires terminal Reply mappings.", nameof(replies));
        if (maxAttempts <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxAttempts), maxAttempts, "A durable Request requires a positive attempt budget.");
        if (claimLease <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(claimLease), claimLease, "A durable Request requires a positive claim lease.");
        if (timeoutAfter is { } timeout && timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeoutAfter), timeoutAfter, "An optional Request timeout must be positive.");
        if (!Enum.IsDefined(idempotencyEvidence)
            || idempotencyEvidence == DurableOperationIdempotencyEvidence.Unspecified)
        {
            throw new ArgumentOutOfRangeException(
                nameof(idempotencyEvidence),
                idempotencyEvidence,
                "Durable operation idempotency evidence must be explicit.");
        }
        if (terminalFailureOutcome is { } terminalFailure && string.IsNullOrWhiteSpace(terminalFailure.Value))
        {
            throw new ArgumentException(
                "An optional terminal-failure outcome cannot be default.",
                nameof(terminalFailureOutcome));
        }

        var observed = new HashSet<RequestTerminalOutcomeId>();
        foreach (var reply in replies)
        {
            if (reply is null)
                throw new ArgumentException("Durable Reply mappings cannot contain null entries.", nameof(replies));
            if (!observed.Add(reply.Outcome))
            {
                throw new ArgumentException(
                    $"Terminal outcome '{reply.Outcome.Value}' has more than one durable Reply mapping.",
                    nameof(replies));
            }
        }

        Request = Guard.RequireNotNull(request);
        Replies = CanonicalDocumentCollections.SortIfNeeded(
            replies,
            static (left, right) => StringComparer.Ordinal.Compare(left.Outcome.Value, right.Outcome.Value));
        MaxAttempts = maxAttempts;
        ClaimLease = claimLease;
        TimeoutAfter = timeoutAfter;
        IdempotencyEvidence = idempotencyEvidence;
        TerminalFailureOutcome = terminalFailureOutcome;
        ReconciliationTarget = reconciliationTarget;
        EscalationTarget = escalationTarget;
    }

    /// <summary>Exact Request contract interpreted by the binding.</summary>
    public RequestContractReference Request { get; }

    /// <summary>Exact terminal Reply mappings in outcome-identity order.</summary>
    public ImmutableArray<DurableReplyBinding> Replies { get; }

    /// <summary>Maximum physical execution attempts, including the first attempt.</summary>
    public int MaxAttempts { get; }

    /// <summary>Ownership-lease duration for each physical attempt.</summary>
    public TimeSpan ClaimLease { get; }

    /// <summary>Optional semantic timeout measured from operation creation.</summary>
    public TimeSpan? TimeoutAfter { get; }

    /// <summary>Evidence supporting repeated physical execution.</summary>
    public DurableOperationIdempotencyEvidence IdempotencyEvidence { get; }

    /// <summary>Typed terminal failure selected by a terminal-failure resolution policy.</summary>
    public RequestTerminalOutcomeId? TerminalFailureOutcome { get; }

    /// <summary>Exact semantic path required for reconciliation, when declared.</summary>
    public DurableOperationResolutionTarget? ReconciliationTarget { get; }

    /// <summary>Exact semantic path required for escalation, when declared.</summary>
    public DurableOperationResolutionTarget? EscalationTarget { get; }

    /// <summary>Finds the exact Reply contract for a terminal outcome.</summary>
    /// <param name="outcome">Stable terminal outcome identity.</param>
    /// <returns>The matching Reply binding, or <see langword="null"/> when absent.</returns>
    /// <exception cref="ArgumentException"><paramref name="outcome"/> is a default value.</exception>
    public DurableReplyBinding? FindReply(RequestTerminalOutcomeId outcome)
    {
        if (string.IsNullOrWhiteSpace(outcome.Value))
            throw new ArgumentException("A default outcome identity cannot be resolved.", nameof(outcome));

        var index = CanonicalDocumentCollections.BinarySearchIndex(
            Replies,
            outcome,
            static (candidate, requested) =>
                StringComparer.Ordinal.Compare(candidate.Outcome.Value, requested.Value));
        return index >= 0 ? Replies[index] : null;
    }

    /// <summary>Compares bindings by their complete normalized semantic value.</summary>
    /// <param name="other">Binding to compare.</param>
    /// <returns><see langword="true"/> when every scalar, mapping, and target is equal.</returns>
    public bool Equals(DurableRequestBinding? other) =>
        ReferenceEquals(this, other)
        || other is not null
        && Request == other.Request
        && MaxAttempts == other.MaxAttempts
        && ClaimLease == other.ClaimLease
        && TimeoutAfter == other.TimeoutAfter
        && IdempotencyEvidence == other.IdempotencyEvidence
        && TerminalFailureOutcome == other.TerminalFailureOutcome
        && ReconciliationTarget == other.ReconciliationTarget
        && EscalationTarget == other.EscalationTarget
        && Replies.SequenceEqual(other.Replies);

    /// <summary>Returns a structural hash code for the normalized binding.</summary>
    /// <returns>A hash code aligned with <see cref="Equals(DurableRequestBinding?)"/>.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Request);
        hash.Add(MaxAttempts);
        hash.Add(ClaimLease);
        hash.Add(TimeoutAfter);
        hash.Add(IdempotencyEvidence);
        hash.Add(TerminalFailureOutcome);
        hash.Add(ReconciliationTarget);
        hash.Add(EscalationTarget);
        foreach (var reply in Replies)
            hash.Add(reply);
        return hash.ToHashCode();
    }
}
