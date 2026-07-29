using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Cohesive.Execution;

/// <summary>Canonical JSON property and variant names for durable-operation evidence.</summary>
public static class DurableOperationWireNames
{
    /// <summary>Attempt-observation discriminator property.</summary>
    public const string AttemptObservationDiscriminator = "attemptResultKind";

    /// <summary>Typed outcome observation variant.</summary>
    public const string OutcomeObservation = "outcome";

    /// <summary>Failure observation variant.</summary>
    public const string FailureObservation = "failure";

    /// <summary>Reconciliation-observation discriminator property.</summary>
    public const string ReconciliationObservationDiscriminator = "reconciliationKind";

    /// <summary>Confirmed reconciliation outcome variant.</summary>
    public const string ReconciledOutcome = "outcome";

    /// <summary>Confirmed-not-executed reconciliation variant.</summary>
    public const string ConfirmedNotExecuted = "notExecuted";

    /// <summary>Unresolved reconciliation variant.</summary>
    public const string Unresolved = "unresolved";
}

/// <summary>Lifecycle stage of one physical durable-operation attempt.</summary>
public enum DurableOperationAttemptStage
{
    /// <summary>No stage was declared; invalid in durable state.</summary>
    Unspecified = 0,

    /// <summary>Ownership was durably claimed but external dispatch has not begun.</summary>
    Claimed = 1,

    /// <summary>The dispatch boundary was durably recorded and the external outcome may be unknown.</summary>
    Dispatched = 2,

    /// <summary>The attempt ended with explicit failure evidence.</summary>
    Failed = 3,

    /// <summary>The attempt produced the one durable Request acknowledgement.</summary>
    Acknowledged = 4,

    /// <summary>A prior failure was explicitly resolved to the one durable Request acknowledgement.</summary>
    Resolved = 5
}

/// <summary>Required next semantic action after an unsuccessful or ambiguous attempt.</summary>
public enum DurableOperationRecoveryRequirement
{
    /// <summary>No recovery action is pending.</summary>
    None = 0,

    /// <summary>Another bounded physical attempt may be claimed.</summary>
    Retry = 1,

    /// <summary>The exact declared reconciliation path must run before retry or completion.</summary>
    Reconcile = 2,

    /// <summary>A declared typed terminal failure must be supplied.</summary>
    TerminalOutcome = 3,

    /// <summary>The exact declared escalation path must run.</summary>
    Escalate = 4
}

/// <summary>Derived lifecycle status of one logical durable Request operation.</summary>
public enum DurableOperationStatus
{
    /// <summary>The Request is durable and eligible for its first claim.</summary>
    Pending = 0,

    /// <summary>An owner holds a lease but has not crossed the dispatch boundary.</summary>
    Claimed = 1,

    /// <summary>The current attempt crossed the dispatch boundary.</summary>
    Dispatched = 2,

    /// <summary>The operation is eligible for another bounded attempt.</summary>
    RetryEligible = 3,

    /// <summary>The declared reconciliation path is required.</summary>
    ReconciliationRequired = 4,

    /// <summary>A declared typed terminal outcome is required.</summary>
    TerminalOutcomeRequired = 5,

    /// <summary>The declared escalation path is required.</summary>
    EscalationRequired = 6,

    /// <summary>One typed terminal outcome has been durably acknowledged.</summary>
    Acknowledged = 7,

    /// <summary>The acknowledged result received its durable target disposition.</summary>
    Dispositioned = 8
}

/// <summary>Boundary at which a physical attempt failed.</summary>
public enum DurableOperationFailurePhase
{
    /// <summary>No phase was declared; invalid failure evidence.</summary>
    Unspecified = 0,

    /// <summary>The failure occurred before any external call.</summary>
    PreCall = 1,

    /// <summary>The failure occurred while an external call may have been in flight.</summary>
    InCall = 2,

    /// <summary>The call returned but a target commit was not confirmed.</summary>
    PostCallPreCommit = 3,

    /// <summary>The target may have committed but no durable acknowledgement exists.</summary>
    PostCommitPreAcknowledgement = 4
}

/// <summary>Evidence about whether the physical external consequence occurred.</summary>
public enum DurableOperationEffectEvidence
{
    /// <summary>No evidence was declared; invalid failure evidence.</summary>
    Unspecified = 0,

    /// <summary>The external operation definitely did not execute.</summary>
    NotExecuted = 1,

    /// <summary>The external operation executed but definitely did not commit a consequence.</summary>
    NotCommitted = 2,

    /// <summary>The external consequence may or may not have committed.</summary>
    Ambiguous = 3
}

/// <summary>Whether explicit failure evidence permits retry absent ambiguity.</summary>
public enum DurableOperationFailureDisposition
{
    /// <summary>No disposition was declared; invalid failure evidence.</summary>
    Unspecified = 0,

    /// <summary>The failure may follow the bounded retry policy.</summary>
    Retryable = 1,

    /// <summary>The failure requires terminal resolution rather than another blind attempt.</summary>
    Terminal = 2
}

/// <summary>Portable failure evidence returned by an impure durable-operation adapter.</summary>
public sealed record DurableOperationFailure
{
    /// <summary>Creates explicit failure-phase and external-effect evidence.</summary>
    /// <param name="phase">Boundary at which the attempt failed.</param>
    /// <param name="effectEvidence">Evidence about external execution or commit.</param>
    /// <param name="disposition">Explicit retryability absent ambiguity.</param>
    /// <param name="code">Stable adapter-independent failure classification.</param>
    /// <param name="detail">Optional materially known portable detail.</param>
    /// <exception cref="ArgumentNullException"><paramref name="code"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="code"/> is empty or white-space, <paramref name="detail"/> is unknown or failed, or the
    /// phase and external-effect evidence are contradictory.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="phase"/>, <paramref name="effectEvidence"/>, or <paramref name="disposition"/> is
    /// unspecified or unsupported.
    /// </exception>
    [JsonConstructor]
    public DurableOperationFailure(
        DurableOperationFailurePhase phase,
        DurableOperationEffectEvidence effectEvidence,
        DurableOperationFailureDisposition disposition,
        string code,
        PortableValue? detail = null)
    {
        RequireDefined(phase, nameof(phase));
        RequireDefined(effectEvidence, nameof(effectEvidence));
        RequireDefined(disposition, nameof(disposition));
        if (phase == DurableOperationFailurePhase.PreCall
            && effectEvidence != DurableOperationEffectEvidence.NotExecuted)
        {
            throw new ArgumentException(
                "A pre-call failure must prove that the external operation did not execute.",
                nameof(effectEvidence));
        }
        if (phase == DurableOperationFailurePhase.PostCallPreCommit
            && effectEvidence == DurableOperationEffectEvidence.NotExecuted)
        {
            throw new ArgumentException(
                "A post-call failure cannot claim that the external operation never executed.",
                nameof(effectEvidence));
        }
        if (phase == DurableOperationFailurePhase.PostCommitPreAcknowledgement
            && effectEvidence != DurableOperationEffectEvidence.Ambiguous)
        {
            throw new ArgumentException(
                "A post-commit failure without acknowledgement has an ambiguous external consequence.",
                nameof(effectEvidence));
        }

        Phase = phase;
        EffectEvidence = effectEvidence;
        Disposition = disposition;
        Code = Guard.RequireNotNullOrWhiteSpace(code);
        Detail = detail is null
            ? null
            : InteractionValueRequirements.RequireMaterialized(detail, nameof(detail), "Failure detail");
    }

    /// <summary>Boundary at which the attempt failed.</summary>
    public DurableOperationFailurePhase Phase { get; }

    /// <summary>Evidence about external execution or commit.</summary>
    public DurableOperationEffectEvidence EffectEvidence { get; }

    /// <summary>Explicit retryability absent ambiguity.</summary>
    public DurableOperationFailureDisposition Disposition { get; }

    /// <summary>Stable adapter-independent failure classification.</summary>
    public string Code { get; }

    /// <summary>Optional materially known portable failure detail.</summary>
    public PortableValue? Detail { get; }

    static void RequireDefined<T>(T value, string parameterName) where T : struct, Enum
    {
        if (!Enum.IsDefined(value) || Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture) == 0)
            throw new ArgumentOutOfRangeException(parameterName, value, "Durable operation failure evidence must be explicit.");
    }
}

/// <summary>Closed observation returned by one physical adapter invocation.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = DurableOperationWireNames.AttemptObservationDiscriminator)]
[JsonDerivedType(typeof(DurableOperationOutcomeObservation), DurableOperationWireNames.OutcomeObservation)]
[JsonDerivedType(typeof(DurableOperationFailureObservation), DurableOperationWireNames.FailureObservation)]
public abstract record DurableOperationAttemptObservation
{
    private protected DurableOperationAttemptObservation()
    {
    }

    internal abstract void EnsureDeclaredVariant();
}

/// <summary>Typed terminal outcome observed from a physical adapter invocation.</summary>
public sealed record DurableOperationOutcomeObservation : DurableOperationAttemptObservation
{
    internal override void EnsureDeclaredVariant() { }

    /// <summary>Creates a typed terminal outcome observation.</summary>
    /// <param name="outcome">Typed terminal Request outcome.</param>
    /// <param name="adapterEvidence">Optional materially known portable target receipt or evidence.</param>
    /// <exception cref="ArgumentNullException"><paramref name="outcome"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="adapterEvidence"/> is unknown or failed.</exception>
    [JsonConstructor]
    public DurableOperationOutcomeObservation(
        RequestTerminalOutcome outcome,
        PortableValue? adapterEvidence = null)
    {
        Outcome = Guard.RequireNotNull(outcome);
        AdapterEvidence = adapterEvidence is null
            ? null
            : InteractionValueRequirements.RequireMaterialized(
                adapterEvidence,
                nameof(adapterEvidence),
                "Adapter acknowledgement evidence");
    }

    /// <summary>Typed terminal Request outcome.</summary>
    public RequestTerminalOutcome Outcome { get; }

    /// <summary>Optional materially known portable target receipt or evidence.</summary>
    public PortableValue? AdapterEvidence { get; }
}

/// <summary>Explicit failure observation from a physical adapter invocation.</summary>
public sealed record DurableOperationFailureObservation : DurableOperationAttemptObservation
{
    internal override void EnsureDeclaredVariant() { }

    /// <summary>Creates a physical-attempt failure observation.</summary>
    /// <param name="failure">Portable phase, effect, and retry evidence.</param>
    /// <exception cref="ArgumentNullException"><paramref name="failure"/> is <see langword="null"/>.</exception>
    [JsonConstructor]
    public DurableOperationFailureObservation(DurableOperationFailure failure) =>
        Failure = Guard.RequireNotNull(failure);

    /// <summary>Portable phase, effect, and retry evidence.</summary>
    public DurableOperationFailure Failure { get; }
}

/// <summary>Closed evidence returned by an explicit reconciliation interaction.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = DurableOperationWireNames.ReconciliationObservationDiscriminator)]
[JsonDerivedType(typeof(DurableOperationReconciledOutcome), DurableOperationWireNames.ReconciledOutcome)]
[JsonDerivedType(typeof(DurableOperationConfirmedNotExecuted), DurableOperationWireNames.ConfirmedNotExecuted)]
[JsonDerivedType(typeof(DurableOperationUnresolved), DurableOperationWireNames.Unresolved)]
public abstract record DurableOperationReconciliationObservation
{
    private protected DurableOperationReconciliationObservation()
    {
    }

    internal abstract void EnsureDeclaredVariant();
}

/// <summary>Reconciliation confirmed one typed terminal Request outcome.</summary>
public sealed record DurableOperationReconciledOutcome : DurableOperationReconciliationObservation
{
    internal override void EnsureDeclaredVariant() { }

    /// <summary>Creates a reconciled terminal outcome.</summary>
    /// <param name="outcome">Confirmed typed terminal Request outcome.</param>
    /// <param name="adapterEvidence">Optional materially known portable reconciliation evidence.</param>
    /// <exception cref="ArgumentNullException"><paramref name="outcome"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="adapterEvidence"/> is unknown or failed.</exception>
    [JsonConstructor]
    public DurableOperationReconciledOutcome(
        RequestTerminalOutcome outcome,
        PortableValue? adapterEvidence = null)
    {
        Outcome = Guard.RequireNotNull(outcome);
        AdapterEvidence = adapterEvidence is null
            ? null
            : InteractionValueRequirements.RequireMaterialized(
                adapterEvidence,
                nameof(adapterEvidence),
                "Reconciliation evidence");
    }

    /// <summary>Confirmed typed terminal Request outcome.</summary>
    public RequestTerminalOutcome Outcome { get; }

    /// <summary>Optional materially known portable reconciliation evidence.</summary>
    public PortableValue? AdapterEvidence { get; }
}

/// <summary>Reconciliation proved that the external consequence never occurred.</summary>
public sealed record DurableOperationConfirmedNotExecuted : DurableOperationReconciliationObservation
{
    internal override void EnsureDeclaredVariant() { }

    /// <summary>Creates proof that the external consequence did not execute.</summary>
    [JsonConstructor]
    public DurableOperationConfirmedNotExecuted()
    {
    }
}

/// <summary>Reconciliation could not determine the external outcome.</summary>
public sealed record DurableOperationUnresolved : DurableOperationReconciliationObservation
{
    internal override void EnsureDeclaredVariant() { }

    /// <summary>Creates unresolved reconciliation evidence.</summary>
    /// <param name="detail">Optional materially known portable evidence.</param>
    /// <exception cref="ArgumentException"><paramref name="detail"/> is unknown or failed.</exception>
    [JsonConstructor]
    public DurableOperationUnresolved(PortableValue? detail = null) =>
        Detail = detail is null
            ? null
            : InteractionValueRequirements.RequireMaterialized(detail, nameof(detail), "Unresolved reconciliation detail");

    /// <summary>Optional materially known portable reconciliation evidence.</summary>
    public PortableValue? Detail { get; }
}

/// <summary>One entry in append-only durable evidence from fenced reconciliation.</summary>
public sealed record DurableOperationReconciliationEvidence
{
    /// <summary>Creates correlated reconciliation evidence.</summary>
    /// <param name="attemptId">Failed physical attempt being reconciled.</param>
    /// <param name="fence">Ownership fence of the ambiguous attempt.</param>
    /// <param name="observedAtUtc">Explicit UTC persistence observation.</param>
    /// <param name="observation">Confirmed outcome, proof of no execution, or unresolved evidence.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="attemptId"/> or <paramref name="fence"/> is default, or
    /// <paramref name="observedAtUtc"/> is not UTC.
    /// </exception>
    /// <exception cref="ArgumentNullException"><paramref name="observation"/> is <see langword="null"/>.</exception>
    [JsonConstructor]
    public DurableOperationReconciliationEvidence(
        OperationAttemptId attemptId,
        OperationFence fence,
        DateTimeOffset observedAtUtc,
        DurableOperationReconciliationObservation observation)
    {
        if (string.IsNullOrWhiteSpace(attemptId.Value))
            throw new ArgumentException("Reconciliation evidence requires an attempt identity.", nameof(attemptId));
        if (fence.Value <= 0)
            throw new ArgumentException("Reconciliation evidence requires a positive operation fence.", nameof(fence));
        DurableOperationClaim.RequireUtc(observedAtUtc, nameof(observedAtUtc));

        AttemptId = attemptId;
        Fence = fence;
        ObservedAtUtc = observedAtUtc;
        Observation = Guard.RequireNotNull(observation);
    }

    /// <summary>Failed physical attempt being reconciled.</summary>
    public OperationAttemptId AttemptId { get; }

    /// <summary>Ownership fence of the ambiguous attempt.</summary>
    public OperationFence Fence { get; }

    /// <summary>Explicit UTC persistence observation.</summary>
    public DateTimeOffset ObservedAtUtc { get; }

    /// <summary>Confirmed outcome, proof of no execution, or unresolved evidence.</summary>
    public DurableOperationReconciliationObservation Observation { get; }
}

/// <summary>One leased and fenced ownership claim for a physical operation attempt.</summary>
public sealed record DurableOperationClaim
{
    /// <summary>Creates new leased ownership evidence whose latest observation is its acquisition.</summary>
    /// <param name="attemptId">Stable identity of this physical attempt.</param>
    /// <param name="claimant">Stable operational claimant identity.</param>
    /// <param name="fence">Monotonic ownership fence.</param>
    /// <param name="claimedAtUtc">Explicit UTC claim observation.</param>
    /// <param name="expiresAtUtc">Exclusive UTC lease expiry.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="attemptId"/> is default; <paramref name="claimant"/> is empty or white-space; a timestamp
    /// is not UTC; <paramref name="fence"/> is default; or expiry is not later than acquisition.
    /// </exception>
    /// <exception cref="ArgumentNullException"><paramref name="claimant"/> is <see langword="null"/>.</exception>
    public DurableOperationClaim(
        OperationAttemptId attemptId,
        string claimant,
        OperationFence fence,
        DateTimeOffset claimedAtUtc,
        DateTimeOffset expiresAtUtc)
        : this(attemptId, claimant, fence, claimedAtUtc, expiresAtUtc, claimedAtUtc)
    {
    }

    /// <summary>Creates leased ownership evidence.</summary>
    /// <param name="attemptId">Stable identity of this physical attempt.</param>
    /// <param name="claimant">Stable operational claimant identity.</param>
    /// <param name="fence">Monotonic ownership fence.</param>
    /// <param name="claimedAtUtc">Explicit UTC claim observation.</param>
    /// <param name="expiresAtUtc">Exclusive UTC lease expiry.</param>
    /// <param name="renewedAtUtc">Latest explicit UTC claim or renewal observation.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="attemptId"/> is default; <paramref name="claimant"/> is empty or white-space; a timestamp
    /// is not UTC; <paramref name="fence"/> is default; expiry is not later than acquisition; or renewal does not
    /// occur during the claim lifetime.
    /// </exception>
    /// <exception cref="ArgumentNullException"><paramref name="claimant"/> is <see langword="null"/>.</exception>
    [JsonConstructor]
    public DurableOperationClaim(
        OperationAttemptId attemptId,
        string claimant,
        OperationFence fence,
        DateTimeOffset claimedAtUtc,
        DateTimeOffset expiresAtUtc,
        DateTimeOffset renewedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(attemptId.Value))
            throw new ArgumentException("A durable claim requires an attempt identity.", nameof(attemptId));
        if (fence.Value <= 0)
            throw new ArgumentException("A durable claim requires a positive operation fence.", nameof(fence));
        RequireUtc(claimedAtUtc, nameof(claimedAtUtc));
        RequireUtc(expiresAtUtc, nameof(expiresAtUtc));
        RequireUtc(renewedAtUtc, nameof(renewedAtUtc));
        if (expiresAtUtc <= claimedAtUtc)
            throw new ArgumentException("A durable claim must expire after it is acquired.", nameof(expiresAtUtc));
        if (renewedAtUtc < claimedAtUtc || renewedAtUtc >= expiresAtUtc)
        {
            throw new ArgumentException(
                "The latest renewal must occur during the claim lifetime.",
                nameof(renewedAtUtc));
        }

        AttemptId = attemptId;
        Claimant = Guard.RequireNotNullOrWhiteSpace(claimant);
        Fence = fence;
        ClaimedAtUtc = claimedAtUtc;
        RenewedAtUtc = renewedAtUtc;
        ExpiresAtUtc = expiresAtUtc;
    }

    /// <summary>Stable identity of this physical attempt.</summary>
    public OperationAttemptId AttemptId { get; }

    /// <summary>Stable operational claimant identity.</summary>
    public string Claimant { get; }

    /// <summary>Monotonic ownership fence.</summary>
    public OperationFence Fence { get; }

    /// <summary>Explicit UTC claim observation.</summary>
    public DateTimeOffset ClaimedAtUtc { get; }

    /// <summary>Latest explicit UTC claim or renewal observation.</summary>
    public DateTimeOffset RenewedAtUtc { get; }

    /// <summary>Exclusive UTC lease expiry.</summary>
    public DateTimeOffset ExpiresAtUtc { get; }

    /// <summary>Returns whether the claim is live at an explicit UTC observation.</summary>
    /// <param name="observedAtUtc">UTC observation to compare with the exclusive expiry.</param>
    /// <returns><see langword="true"/> from acquisition until exclusive expiry; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="observedAtUtc"/> is not UTC.</exception>
    public bool IsLiveAt(DateTimeOffset observedAtUtc)
    {
        RequireUtc(observedAtUtc, nameof(observedAtUtc));
        return observedAtUtc >= ClaimedAtUtc && observedAtUtc < ExpiresAtUtc;
    }

    internal static void RequireUtc(DateTimeOffset value, string parameterName) =>
        ExecutionObservationRequirements.RequireUtc(value, parameterName);
}

/// <summary>Immutable current snapshot of one claimed physical-operation attempt.</summary>
public sealed record DurableOperationAttempt
{
    /// <summary>Creates one validated attempt-history entry.</summary>
    /// <param name="ordinal">One-based claim-attempt history ordinal.</param>
    /// <param name="claim">Leased and fenced ownership evidence.</param>
    /// <param name="stage">Current attempt stage.</param>
    /// <param name="dispatchedAtUtc">UTC dispatch-boundary observation when crossed.</param>
    /// <param name="completedAtUtc">
    /// UTC physical-attempt completion observation; a later recovery resolution retains this original value.
    /// </param>
    /// <param name="failure">Failure evidence for a failed attempt.</param>
    /// <exception cref="ArgumentNullException"><paramref name="claim"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="ordinal"/> is not positive or <paramref name="stage"/> is unspecified or unsupported.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Timestamps are not UTC or the supplied timestamps and failure contradict <paramref name="stage"/>.
    /// </exception>
    [JsonConstructor]
    public DurableOperationAttempt(
        int ordinal,
        DurableOperationClaim claim,
        DurableOperationAttemptStage stage,
        DateTimeOffset? dispatchedAtUtc = null,
        DateTimeOffset? completedAtUtc = null,
        DurableOperationFailure? failure = null)
    {
        claim = Guard.RequireNotNull(claim);
        if (ordinal <= 0)
            throw new ArgumentOutOfRangeException(nameof(ordinal), ordinal, "An attempt ordinal must be positive.");
        if (!Enum.IsDefined(stage) || stage == DurableOperationAttemptStage.Unspecified)
            throw new ArgumentOutOfRangeException(nameof(stage), stage, "An attempt stage must be explicit.");
        if (dispatchedAtUtc is { } dispatch)
            DurableOperationClaim.RequireUtc(dispatch, nameof(dispatchedAtUtc));
        if (completedAtUtc is { } completion)
            DurableOperationClaim.RequireUtc(completion, nameof(completedAtUtc));
        if (dispatchedAtUtc is { } dispatchBoundary
            && (dispatchBoundary < claim.ClaimedAtUtc || dispatchBoundary >= claim.ExpiresAtUtc))
        {
            throw new ArgumentException(
                "Dispatch must occur while the attempt claim is live.",
                nameof(dispatchedAtUtc));
        }
        if (completedAtUtc is { } completionBoundary
            && completionBoundary < (dispatchedAtUtc ?? claim.ClaimedAtUtc))
        {
            throw new ArgumentException(
                "Attempt completion cannot precede claim or dispatch evidence.",
                nameof(completedAtUtc));
        }
        if (completedAtUtc is { } completionAfterRenewal
            && completionAfterRenewal < claim.RenewedAtUtc)
        {
            throw new ArgumentException(
                "Attempt completion cannot precede its latest claim renewal evidence.",
                nameof(completedAtUtc));
        }
        if (stage == DurableOperationAttemptStage.Acknowledged
            && completedAtUtc is { } acknowledgementBoundary
            && acknowledgementBoundary >= claim.ExpiresAtUtc)
        {
            throw new ArgumentException(
                "Direct acknowledgement must complete while the ownership claim is live.",
                nameof(completedAtUtc));
        }

        switch (stage)
        {
            case DurableOperationAttemptStage.Claimed when dispatchedAtUtc is not null || completedAtUtc is not null || failure is not null:
                throw new ArgumentException("A claimed attempt cannot contain dispatch, completion, or failure evidence.", nameof(stage));
            case DurableOperationAttemptStage.Dispatched when dispatchedAtUtc is null || completedAtUtc is not null || failure is not null:
                throw new ArgumentException("A dispatched attempt requires dispatch evidence and cannot already be complete.", nameof(stage));
            case DurableOperationAttemptStage.Failed when completedAtUtc is null || failure is null:
                throw new ArgumentException("A failed attempt requires completion and failure evidence.", nameof(stage));
            case DurableOperationAttemptStage.Acknowledged when dispatchedAtUtc is null || completedAtUtc is null || failure is not null:
                throw new ArgumentException("An acknowledged attempt requires dispatch and completion evidence without failure.", nameof(stage));
            case DurableOperationAttemptStage.Resolved when completedAtUtc is null || failure is null:
                throw new ArgumentException("A resolved attempt retains its prior completion and failure evidence.", nameof(stage));
        }
        if (stage is DurableOperationAttemptStage.Failed or DurableOperationAttemptStage.Resolved
            && dispatchedAtUtc is null
            && failure?.Phase != DurableOperationFailurePhase.PreCall)
        {
            throw new ArgumentException(
                "Failure after the pre-call phase requires a persisted dispatch boundary.",
                nameof(failure));
        }
        if (stage == DurableOperationAttemptStage.Resolved && dispatchedAtUtc is null)
        {
            throw new ArgumentException(
                "A resolved attempt requires its persisted dispatch boundary.",
                nameof(dispatchedAtUtc));
        }

        Ordinal = ordinal;
        Claim = claim;
        Stage = stage;
        DispatchedAtUtc = dispatchedAtUtc;
        CompletedAtUtc = completedAtUtc;
        Failure = failure;
    }

    /// <summary>One-based claim-attempt history ordinal.</summary>
    public int Ordinal { get; }

    /// <summary>Leased and fenced ownership evidence.</summary>
    public DurableOperationClaim Claim { get; }

    /// <summary>Current attempt stage.</summary>
    public DurableOperationAttemptStage Stage { get; }

    /// <summary>UTC dispatch-boundary observation when crossed.</summary>
    public DateTimeOffset? DispatchedAtUtc { get; }

    /// <summary>UTC physical-attempt completion observation retained across later recovery resolution.</summary>
    public DateTimeOffset? CompletedAtUtc { get; }

    /// <summary>Failure evidence for a failed attempt.</summary>
    public DurableOperationFailure? Failure { get; }
}

/// <summary>Durable evidence that one physical attempt produced the logical Request outcome.</summary>
public sealed record DurableOperationAcknowledgement
{
    /// <summary>Creates one durable logical acknowledgement.</summary>
    /// <param name="requestId">Logical Request emission discharged by the outcome.</param>
    /// <param name="attemptId">
    /// Physical attempt that supplied or reconciled the outcome; null for an endogenous timeout or cancellation.
    /// </param>
    /// <param name="replyContract">Exact Reply contract selected for the outcome.</param>
    /// <param name="outcome">Typed terminal Request outcome.</param>
    /// <param name="acknowledgedAtUtc">UTC acknowledgement persistence observation.</param>
    /// <param name="adapterEvidence">Optional materially known portable target receipt or evidence.</param>
    /// <param name="recoveryIdentity">
    /// Exact reconciliation or escalation identity that produced the outcome, when applicable.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="replyContract"/> or <paramref name="outcome"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="requestId"/> or a present <paramref name="attemptId"/> is default;
    /// <paramref name="acknowledgedAtUtc"/> is not UTC; <paramref name="adapterEvidence"/> is unknown or failed;
    /// a missing attempt does not carry a timeout or cancellation; or <paramref name="recoveryIdentity"/> does not
    /// address the same Request and attempt.
    /// </exception>
    [JsonConstructor]
    public DurableOperationAcknowledgement(
        EmissionId requestId,
        OperationAttemptId? attemptId,
        ReplyContractReference replyContract,
        RequestTerminalOutcome outcome,
        DateTimeOffset acknowledgedAtUtc,
        PortableValue? adapterEvidence = null,
        DurableOperationRecoveryIdentity? recoveryIdentity = null)
    {
        replyContract = Guard.RequireNotNull(replyContract);
        outcome = Guard.RequireNotNull(outcome);
        if (string.IsNullOrWhiteSpace(requestId.Value))
            throw new ArgumentException("An acknowledgement requires its logical Request identity.", nameof(requestId));
        if (attemptId is { } physicalAttempt && string.IsNullOrWhiteSpace(physicalAttempt.Value))
            throw new ArgumentException("An acknowledgement requires its physical attempt identity.", nameof(attemptId));
        var endogenous = outcome is RequestTimeoutOutcome or RequestCancellationOutcome;
        if ((attemptId is null) != endogenous)
        {
            throw new ArgumentException(
                "Timeout and cancellation acknowledge endogenously; every other outcome requires a physical attempt.",
                nameof(attemptId));
        }
        if (recoveryIdentity is { } recovery
            && (recovery.OperationId != requestId || attemptId != recovery.SourceAttemptId))
        {
            throw new ArgumentException(
                "A recovery acknowledgement identity must address the same Request and physical attempt.",
                nameof(recoveryIdentity));
        }
        DurableOperationClaim.RequireUtc(acknowledgedAtUtc, nameof(acknowledgedAtUtc));

        RequestId = requestId;
        AttemptId = attemptId;
        ReplyContract = replyContract;
        Outcome = outcome;
        AcknowledgedAtUtc = acknowledgedAtUtc;
        AdapterEvidence = adapterEvidence is null
            ? null
            : InteractionValueRequirements.RequireMaterialized(
                adapterEvidence,
                nameof(adapterEvidence),
                "Adapter acknowledgement evidence");
        RecoveryIdentity = recoveryIdentity;
    }

    /// <summary>Logical Request emission discharged by the outcome.</summary>
    public EmissionId RequestId { get; }

    /// <summary>Physical attempt that supplied or reconciled the outcome; null for endogenous termination.</summary>
    public OperationAttemptId? AttemptId { get; }

    /// <summary>Exact Reply contract selected for the outcome.</summary>
    public ReplyContractReference ReplyContract { get; }

    /// <summary>Typed terminal Request outcome.</summary>
    public RequestTerminalOutcome Outcome { get; }

    /// <summary>UTC acknowledgement persistence observation.</summary>
    public DateTimeOffset AcknowledgedAtUtc { get; }

    /// <summary>Optional materially known portable target receipt or evidence.</summary>
    public PortableValue? AdapterEvidence { get; }

    /// <summary>Exact reconciliation or escalation identity that produced the outcome, when applicable.</summary>
    public DurableOperationRecoveryIdentity? RecoveryIdentity { get; }

}

/// <summary>Observed relationship between a Reply and its durable continuation target.</summary>
public enum DurableOperationResultArrival
{
    /// <summary>No arrival relationship was declared; invalid target evidence.</summary>
    Unspecified = 0,

    /// <summary>The exact target is open and eligible to consume the result.</summary>
    Eligible = 1,

    /// <summary>The target already reached a terminal disposition.</summary>
    Late = 2,

    /// <summary>The target is open but its exact continuation identity is incompatible.</summary>
    Stale = 3,

    /// <summary>The same logical result was already dispositioned.</summary>
    Duplicate = 4
}

/// <summary>Durable result disposition at a Process token or Transition continuation.</summary>
public enum DurableOperationAdmissionDisposition
{
    /// <summary>No disposition was declared; invalid admission evidence.</summary>
    Unspecified = 0,

    /// <summary>The exact open target may advance once.</summary>
    Accepted = 1,

    /// <summary>The result was rejected without advancing the target.</summary>
    Rejected = 2,

    /// <summary>The result was retained as evidence without advancing the target.</summary>
    Observed = 3,

    /// <summary>The target reused a previously durable disposition without advancing again.</summary>
    ReusedPriorDisposition = 4
}

/// <summary>Explicit target-state evidence supplied to result admission.</summary>
public sealed record DurableOperationTargetObservation
{
    /// <summary>Creates exact continuation-target evidence.</summary>
    /// <param name="target">Exact target observed by the owning Process or Transition interpreter.</param>
    /// <param name="arrival">Eligible, late, stale, or duplicate relationship.</param>
    /// <param name="priorDisposition">Prior durable disposition available for reuse, when applicable.</param>
    /// <exception cref="ArgumentNullException"><paramref name="target"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="arrival"/> or a present <paramref name="priorDisposition"/> is unspecified or unsupported.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// An eligible result supplies a prior disposition, or a prior disposition recursively names reuse.
    /// </exception>
    [JsonConstructor]
    public DurableOperationTargetObservation(
        InteractionTarget target,
        DurableOperationResultArrival arrival,
        DurableOperationAdmissionDisposition? priorDisposition = null)
    {
        if (!Enum.IsDefined(arrival) || arrival == DurableOperationResultArrival.Unspecified)
            throw new ArgumentOutOfRangeException(nameof(arrival), arrival, "Result arrival evidence must be explicit.");
        if (priorDisposition is { } prior
            && (!Enum.IsDefined(prior) || prior == DurableOperationAdmissionDisposition.Unspecified))
        {
            throw new ArgumentOutOfRangeException(
                nameof(priorDisposition),
                priorDisposition,
                "A prior admission disposition must be explicit.");
        }
        if (arrival == DurableOperationResultArrival.Eligible && priorDisposition is not null)
            throw new ArgumentException("An eligible target cannot already have a prior disposition.", nameof(priorDisposition));
        if (priorDisposition == DurableOperationAdmissionDisposition.ReusedPriorDisposition)
            throw new ArgumentException("A reused disposition cannot recursively reuse another reuse marker.", nameof(priorDisposition));

        Target = Guard.RequireNotNull(target);
        Arrival = arrival;
        PriorDisposition = priorDisposition;
    }

    /// <summary>Exact target observed by the owning Process or Transition interpreter.</summary>
    public InteractionTarget Target { get; }

    /// <summary>Eligible, late, stale, or duplicate relationship.</summary>
    public DurableOperationResultArrival Arrival { get; }

    /// <summary>Prior durable disposition available for reuse, when applicable.</summary>
    public DurableOperationAdmissionDisposition? PriorDisposition { get; }
}

/// <summary>Durable disposition of one acknowledged result at its exact semantic target.</summary>
public sealed record DurableOperationAdmission
{
    /// <summary>Creates durable target-disposition evidence.</summary>
    /// <param name="requestId">Logical Request emission whose result was dispositioned.</param>
    /// <param name="attemptId">Physical attempt acknowledged for the Request, or null for endogenous termination.</param>
    /// <param name="outcome">Stable terminal Request outcome.</param>
    /// <param name="target">Exact Process-token or Transition-continuation target.</param>
    /// <param name="arrival">Observed relationship to the target.</param>
    /// <param name="disposition">Durable target disposition.</param>
    /// <param name="priorDisposition">Prior disposition reused by policy, when applicable.</param>
    /// <exception cref="ArgumentNullException"><paramref name="target"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// An identity is default, or prior-disposition evidence contradicts <paramref name="disposition"/>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="arrival"/> or <paramref name="disposition"/> is unspecified or unsupported.
    /// </exception>
    [JsonConstructor]
    public DurableOperationAdmission(
        EmissionId requestId,
        OperationAttemptId? attemptId,
        RequestTerminalOutcomeId outcome,
        InteractionTarget target,
        DurableOperationResultArrival arrival,
        DurableOperationAdmissionDisposition disposition,
        DurableOperationAdmissionDisposition? priorDisposition = null)
    {
        if (string.IsNullOrWhiteSpace(requestId.Value))
            throw new ArgumentException("An admission requires its logical Request identity.", nameof(requestId));
        if (attemptId is { } physicalAttempt && string.IsNullOrWhiteSpace(physicalAttempt.Value))
            throw new ArgumentException("An admission requires its acknowledged attempt identity.", nameof(attemptId));
        if (string.IsNullOrWhiteSpace(outcome.Value))
            throw new ArgumentException("An admission requires its terminal outcome identity.", nameof(outcome));
        if (!Enum.IsDefined(arrival) || arrival == DurableOperationResultArrival.Unspecified)
            throw new ArgumentOutOfRangeException(nameof(arrival), arrival, "Admission arrival evidence must be explicit.");
        if (!Enum.IsDefined(disposition) || disposition == DurableOperationAdmissionDisposition.Unspecified)
            throw new ArgumentOutOfRangeException(nameof(disposition), disposition, "Admission disposition must be explicit.");
        if (priorDisposition is { } prior
            && (!Enum.IsDefined(prior)
                || prior is DurableOperationAdmissionDisposition.Unspecified
                    or DurableOperationAdmissionDisposition.ReusedPriorDisposition))
        {
            throw new ArgumentOutOfRangeException(
                nameof(priorDisposition),
                priorDisposition,
                "A prior admission disposition must be a concrete non-recursive disposition.");
        }
        if ((arrival == DurableOperationResultArrival.Eligible)
            != (disposition == DurableOperationAdmissionDisposition.Accepted))
        {
            throw new ArgumentException(
                "Only an eligible result may be accepted, and every eligible result must be accepted.",
                nameof(disposition));
        }
        if ((disposition == DurableOperationAdmissionDisposition.ReusedPriorDisposition) != (priorDisposition is not null))
        {
            throw new ArgumentException(
                "Only a reused disposition must retain its prior durable disposition.",
                nameof(priorDisposition));
        }

        RequestId = requestId;
        AttemptId = attemptId;
        Outcome = outcome;
        Target = Guard.RequireNotNull(target);
        Arrival = arrival;
        Disposition = disposition;
        PriorDisposition = priorDisposition;
    }

    /// <summary>Logical Request emission whose result was dispositioned.</summary>
    public EmissionId RequestId { get; }

    /// <summary>Physical attempt acknowledged for the Request, or null for endogenous termination.</summary>
    public OperationAttemptId? AttemptId { get; }

    /// <summary>Stable terminal Request outcome.</summary>
    public RequestTerminalOutcomeId Outcome { get; }

    /// <summary>Exact Process-token or Transition-continuation target.</summary>
    public InteractionTarget Target { get; }

    /// <summary>Observed relationship to the target.</summary>
    public DurableOperationResultArrival Arrival { get; }

    /// <summary>Durable target disposition.</summary>
    public DurableOperationAdmissionDisposition Disposition { get; }

    /// <summary>Prior disposition reused by policy, when applicable.</summary>
    public DurableOperationAdmissionDisposition? PriorDisposition { get; }

    /// <summary>Whether the owning interpreter may advance its target exactly once.</summary>
    public bool AdvancesTarget => Disposition == DurableOperationAdmissionDisposition.Accepted;
}

/// <summary>Portable immutable semantic snapshot of one logical durable Request operation.</summary>
/// <remarks>
/// Storage interpreters persist this state and atomically coordinate it with checkpoints, inboxes, and outboxes.
/// This type does not prescribe a physical schema, repository, transaction, lease provider, or delivery host.
/// </remarks>
public sealed record DurableOperationState
{
    /// <summary>Current portable durable-operation state schema.</summary>
    public static ExecutionIrSchemaVersion CurrentSchemaVersion { get; } = new("cohesive-durable-operation/v1");

    /// <summary>Creates one validated durable-operation state snapshot.</summary>
    /// <param name="schemaVersion">Exact durable-operation state schema.</param>
    /// <param name="request">Canonical logical Request.</param>
    /// <param name="binding">Portable bounded execution refinement.</param>
    /// <param name="createdAtUtc">Explicit UTC operation-creation observation.</param>
    /// <param name="attempts">Ordered attempt snapshots; new attempts append after prior attempts close.</param>
    /// <param name="reconciliations">Append-only fenced reconciliation evidence.</param>
    /// <param name="recoveryRequirement">Required next recovery action.</param>
    /// <param name="acknowledgement">One durable logical acknowledgement, when present.</param>
    /// <param name="admission">One durable target disposition, when present.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="request"/> or <paramref name="binding"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The schema is not the current exact version; creation time is not UTC; the binding addresses another Request
    /// contract; attempt or reconciliation history is malformed; or acknowledgement, admission, fence, and recovery
    /// evidence are inconsistent.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="recoveryRequirement"/> is unsupported.</exception>
    [JsonConstructor]
    public DurableOperationState(
        ExecutionIrSchemaVersion schemaVersion,
        RequestEnvelope request,
        DurableRequestBinding binding,
        DateTimeOffset createdAtUtc,
        ImmutableArray<DurableOperationAttempt> attempts = default,
        ImmutableArray<DurableOperationReconciliationEvidence> reconciliations = default,
        DurableOperationRecoveryRequirement recoveryRequirement = DurableOperationRecoveryRequirement.None,
        DurableOperationAcknowledgement? acknowledgement = null,
        DurableOperationAdmission? admission = null)
    {
        if (schemaVersion != CurrentSchemaVersion)
            throw new ArgumentException("Durable operation state requires the current exact schema version.", nameof(schemaVersion));
        DurableOperationClaim.RequireUtc(createdAtUtc, nameof(createdAtUtc));
        if (!Enum.IsDefined(recoveryRequirement))
        {
            throw new ArgumentOutOfRangeException(
                nameof(recoveryRequirement),
                recoveryRequirement,
                "Unsupported durable operation recovery requirement.");
        }

        Request = Guard.RequireNotNull(request);
        Binding = Guard.RequireNotNull(binding);
        if (request.Contract != binding.Request)
            throw new ArgumentException("Durable operation state and binding must reference the same exact Request contract.", nameof(binding));
        DateTimeOffset? deadlineUtc = null;
        if (binding.TimeoutAfter is { } timeout)
        {
            try
            {
                deadlineUtc = createdAtUtc.Add(timeout);
            }
            catch (ArgumentOutOfRangeException exception)
            {
                throw new ArgumentException(
                    "The semantic timeout deadline cannot be represented from operation creation time.",
                    nameof(binding),
                    exception);
            }
        }

        var normalizedAttempts = attempts.IsDefault ? [] : attempts;
        var attemptsById = new Dictionary<OperationAttemptId, (DurableOperationAttempt Attempt, int Index)>();
        var priorFence = 0L;
        var dispatchCount = 0;
        for (var index = 0; index < normalizedAttempts.Length; index++)
        {
            var attempt = normalizedAttempts[index]
                ?? throw new ArgumentException("Attempt history cannot contain null entries.", nameof(attempts));
            if (attempt.Ordinal != index + 1)
                throw new ArgumentException("Attempt history ordinals must be contiguous and one-based.", nameof(attempts));
            if (!attemptsById.TryAdd(attempt.Claim.AttemptId, (attempt, index)))
                throw new ArgumentException("Attempt history cannot reuse a physical attempt identity.", nameof(attempts));
            if (attempt.Claim.Fence.Value <= priorFence)
                throw new ArgumentException("Attempt fences must increase monotonically.", nameof(attempts));
            if (attempt.Claim.ClaimedAtUtc < createdAtUtc)
                throw new ArgumentException("Attempt history cannot precede operation creation.", nameof(attempts));
            if (deadlineUtc is { } deadline
                && (attempt.Claim.ClaimedAtUtc >= deadline
                    || attempt.Claim.RenewedAtUtc >= deadline
                    || attempt.DispatchedAtUtc >= deadline))
            {
                throw new ArgumentException(
                    "Claim, renewal, and dispatch evidence must precede the semantic deadline.",
                    nameof(attempts));
            }
            if (attempt.Claim.ExpiresAtUtc - attempt.Claim.RenewedAtUtc != binding.ClaimLease)
            {
                throw new ArgumentException(
                    "Attempt claim expiry must apply the binding's exact lease duration to its latest renewal.",
                    nameof(attempts));
            }
            if (index > 0
                && normalizedAttempts[index - 1].CompletedAtUtc is { } priorCompletion
                && attempt.Claim.ClaimedAtUtc < priorCompletion)
            {
                throw new ArgumentException("Attempt history timestamps must be monotonic.", nameof(attempts));
            }
            if (index < normalizedAttempts.Length - 1
                && attempt.Stage is DurableOperationAttemptStage.Claimed or DurableOperationAttemptStage.Dispatched)
            {
                throw new ArgumentException("Only the last attempt may remain active.", nameof(attempts));
            }
            if (index < normalizedAttempts.Length - 1
                && attempt.Stage is DurableOperationAttemptStage.Acknowledged or DurableOperationAttemptStage.Resolved)
            {
                throw new ArgumentException("Attempt history cannot continue after acknowledgement.", nameof(attempts));
            }
            if (attempt.DispatchedAtUtc is not null)
                dispatchCount++;
            priorFence = attempt.Claim.Fence.Value;
        }
        if (dispatchCount > binding.MaxAttempts)
            throw new ArgumentException("Attempt history exceeds the bounded physical-dispatch budget.", nameof(attempts));

        var normalizedReconciliations = reconciliations.IsDefault ? [] : reconciliations;
        var priorReconciliationTime = createdAtUtc;
        var latestReconciliationByAttempt = new Dictionary<OperationAttemptId, DateTimeOffset>();
        for (var reconciliationIndex = 0;
             reconciliationIndex < normalizedReconciliations.Length;
             reconciliationIndex++)
        {
            var reconciliation = normalizedReconciliations[reconciliationIndex];
            if (reconciliation is null)
                throw new ArgumentException("Reconciliation history cannot contain null entries.", nameof(reconciliations));
            if (reconciliation.Observation is DurableOperationReconciledOutcome
                && reconciliationIndex != normalizedReconciliations.Length - 1)
            {
                throw new ArgumentException(
                    "A terminal reconciliation outcome must be the final reconciliation evidence.",
                    nameof(reconciliations));
            }
            if (!attemptsById.TryGetValue(reconciliation.AttemptId, out var attemptEntry)
                || attemptEntry.Attempt is not { } attempt
                || attempt.Claim.Fence != reconciliation.Fence
                || attempt.Stage is not (DurableOperationAttemptStage.Failed or DurableOperationAttemptStage.Resolved)
                || attempt.Failure is null)
            {
                throw new ArgumentException(
                    "Reconciliation evidence must address an exact failed attempt and fence.",
                    nameof(reconciliations));
            }
            var reconciliationFloor = attempt.CompletedAtUtc
                                      ?? attempt.DispatchedAtUtc
                                      ?? attempt.Claim.ClaimedAtUtc;
            if (reconciliation.ObservedAtUtc < priorReconciliationTime
                || reconciliation.ObservedAtUtc < reconciliationFloor)
            {
                throw new ArgumentException("Reconciliation history timestamps must be monotonic.", nameof(reconciliations));
            }
            if (deadlineUtc is { } deadline && reconciliation.ObservedAtUtc >= deadline)
            {
                throw new ArgumentException(
                    "Reconciliation evidence must precede the semantic deadline.",
                    nameof(reconciliations));
            }
            priorReconciliationTime = reconciliation.ObservedAtUtc;
            latestReconciliationByAttempt[reconciliation.AttemptId] = reconciliation.ObservedAtUtc;
        }
        var latestPriorReconciliation = createdAtUtc;
        foreach (var attempt in normalizedAttempts)
        {
            if (attempt.Claim.ClaimedAtUtc < latestPriorReconciliation)
            {
                throw new ArgumentException(
                    "A later attempt cannot precede reconciliation evidence for a prior attempt.",
                    nameof(attempts));
            }
            if (latestReconciliationByAttempt.TryGetValue(attempt.Claim.AttemptId, out var reconciledAtUtc)
                && reconciledAtUtc > latestPriorReconciliation)
            {
                latestPriorReconciliation = reconciledAtUtc;
            }
        }
        var lastAttempt = normalizedAttempts.IsEmpty ? null : normalizedAttempts[^1];
        var hasActiveAttempt = lastAttempt?.Stage is DurableOperationAttemptStage.Claimed or DurableOperationAttemptStage.Dispatched;
        if (hasActiveAttempt && recoveryRequirement != DurableOperationRecoveryRequirement.None)
            throw new ArgumentException("An active attempt cannot simultaneously require recovery.", nameof(recoveryRequirement));
        if (recoveryRequirement != DurableOperationRecoveryRequirement.None
            && lastAttempt?.Stage != DurableOperationAttemptStage.Failed)
        {
            throw new ArgumentException("Recovery requires a failed latest attempt.", nameof(recoveryRequirement));
        }
        if (recoveryRequirement == DurableOperationRecoveryRequirement.Retry
            && dispatchCount >= binding.MaxAttempts)
        {
            throw new ArgumentException("Retry recovery requires remaining physical-dispatch budget.", nameof(recoveryRequirement));
        }
        if (recoveryRequirement == DurableOperationRecoveryRequirement.Reconcile
            && binding.ReconciliationTarget is null)
        {
            throw new ArgumentException("Reconciliation recovery requires its exact declared target.", nameof(recoveryRequirement));
        }
        if (recoveryRequirement == DurableOperationRecoveryRequirement.TerminalOutcome
            && binding.TerminalFailureOutcome is null)
        {
            throw new ArgumentException("Terminal recovery requires its exact declared failure outcome.", nameof(recoveryRequirement));
        }
        if (recoveryRequirement == DurableOperationRecoveryRequirement.Escalate
            && binding.EscalationTarget is null)
        {
            throw new ArgumentException("Escalation recovery requires its exact declared target.", nameof(recoveryRequirement));
        }
        if (lastAttempt?.Stage == DurableOperationAttemptStage.Failed
            && recoveryRequirement == DurableOperationRecoveryRequirement.None
            && acknowledgement is null)
        {
            throw new ArgumentException("A failed latest attempt requires an explicit recovery action.", nameof(recoveryRequirement));
        }
        if (acknowledgement is not null)
        {
            if (acknowledgement.RequestId != request.Context.EmissionId)
                throw new ArgumentException("Acknowledgement belongs to another logical Request.", nameof(acknowledgement));
            if (acknowledgement.AcknowledgedAtUtc < createdAtUtc)
                throw new ArgumentException("Acknowledgement cannot precede operation creation.", nameof(acknowledgement));
            if (acknowledgement.AcknowledgedAtUtc < priorReconciliationTime)
            {
                throw new ArgumentException(
                    "Acknowledgement cannot precede retained reconciliation evidence.",
                    nameof(acknowledgement));
            }
            if (acknowledgement.Outcome is RequestTimeoutOutcome)
            {
                if (deadlineUtc is null || acknowledgement.AcknowledgedAtUtc < deadlineUtc)
                {
                    throw new ArgumentException(
                        "A timeout acknowledgement requires and cannot precede its semantic deadline.",
                        nameof(acknowledgement));
                }
            }
            else if (deadlineUtc is { } deadline && acknowledgement.AcknowledgedAtUtc >= deadline)
            {
                throw new ArgumentException(
                    "Only the typed timeout may acknowledge at or after the semantic deadline.",
                    nameof(acknowledgement));
            }
            if (acknowledgement.AttemptId is { } acknowledgedAttemptId)
            {
                var acknowledgedAttempt = normalizedAttempts.FirstOrDefault(
                    attempt => attempt.Claim.AttemptId == acknowledgedAttemptId);
                if (acknowledgedAttempt is null
                    || acknowledgedAttempt.Stage is not (DurableOperationAttemptStage.Acknowledged or DurableOperationAttemptStage.Resolved))
                {
                    throw new ArgumentException(
                        "Acknowledgement must reference an acknowledged attempt in history.",
                        nameof(acknowledgement));
                }
                if (lastAttempt?.Claim.AttemptId != acknowledgedAttemptId)
                    throw new ArgumentException("Acknowledgement must close the final attempt in history.", nameof(acknowledgement));
                if (acknowledgedAttempt.CompletedAtUtc is { } completedAtUtc
                    && acknowledgement.AcknowledgedAtUtc < completedAtUtc)
                {
                    throw new ArgumentException(
                        "Acknowledgement cannot precede its physical attempt completion.",
                        nameof(acknowledgement));
                }
                if (acknowledgedAttempt.Stage == DurableOperationAttemptStage.Acknowledged
                    && acknowledgedAttempt.CompletedAtUtc != acknowledgement.AcknowledgedAtUtc)
                {
                    throw new ArgumentException(
                        "Direct acknowledgement time must match its final attempt evidence.",
                        nameof(acknowledgement));
                }
                if (acknowledgement.RecoveryIdentity is { } recoveryIdentity
                    && (acknowledgedAttempt.Stage != DurableOperationAttemptStage.Resolved
                        || recoveryIdentity.OperationId != request.Context.EmissionId
                        || recoveryIdentity.SourceAttemptId != acknowledgedAttemptId
                        || recoveryIdentity.SourceFence != acknowledgedAttempt.Claim.Fence))
                {
                    throw new ArgumentException(
                        "Recovery acknowledgement identity must exactly address its resolved Request attempt and fence.",
                        nameof(acknowledgement));
                }
                if (acknowledgedAttempt.Stage == DurableOperationAttemptStage.Resolved)
                {
                    var validResolution = acknowledgement.RecoveryIdentity switch
                    {
                        null => acknowledgement.Outcome is RequestFailureOutcome
                                && binding.TerminalFailureOutcome == acknowledgement.Outcome.Id,
                        { Requirement: DurableOperationRecoveryRequirement.Reconcile } =>
                            binding.ReconciliationTarget is not null,
                        { Requirement: DurableOperationRecoveryRequirement.Escalate } =>
                            binding.EscalationTarget is not null,
                        _ => false
                    };
                    if (!validResolution)
                    {
                        throw new ArgumentException(
                            "A resolved acknowledgement must retain its exact terminal, reconciliation, or escalation provenance.",
                            nameof(acknowledgement));
                    }
                }
            }
            else if (acknowledgement.Outcome is not (RequestTimeoutOutcome or RequestCancellationOutcome)
                     || hasActiveAttempt
                     || lastAttempt?.Stage is DurableOperationAttemptStage.Acknowledged
                         or DurableOperationAttemptStage.Resolved)
            {
                throw new ArgumentException(
                    "Only an endogenous timeout or cancellation may acknowledge without a physical attempt.",
                    nameof(acknowledgement));
            }
            else if (acknowledgement.RecoveryIdentity is not null)
            {
                throw new ArgumentException(
                    "An endogenous acknowledgement cannot carry a physical recovery identity.",
                    nameof(acknowledgement));
            }
            if (acknowledgement.AttemptId is null
                && lastAttempt?.CompletedAtUtc is { } finalCompletion
                && acknowledgement.AcknowledgedAtUtc < finalCompletion)
            {
                throw new ArgumentException(
                    "An endogenous acknowledgement cannot precede prior attempt completion.",
                    nameof(acknowledgement));
            }
            if (binding.FindReply(acknowledgement.Outcome.Id)?.Reply != acknowledgement.ReplyContract)
                throw new ArgumentException("Acknowledgement must use the exact bound Reply contract.", nameof(acknowledgement));
            if (recoveryRequirement != DurableOperationRecoveryRequirement.None)
                throw new ArgumentException("An acknowledged operation cannot require recovery.", nameof(recoveryRequirement));
        }
        else if (lastAttempt?.Stage is DurableOperationAttemptStage.Acknowledged or DurableOperationAttemptStage.Resolved)
        {
            throw new ArgumentException("An acknowledged attempt requires durable acknowledgement evidence.", nameof(acknowledgement));
        }
        foreach (var reconciliation in normalizedReconciliations)
        {
            if (reconciliation.Observation is DurableOperationReconciledOutcome resolved
                && (acknowledgement?.AttemptId != reconciliation.AttemptId
                    || acknowledgement.Outcome != resolved.Outcome
                    || acknowledgement.AdapterEvidence != resolved.AdapterEvidence
                    || acknowledgement.AcknowledgedAtUtc != reconciliation.ObservedAtUtc
                    || acknowledgement.RecoveryIdentity
                        != new DurableOperationRecoveryIdentity(
                            request.Context.EmissionId,
                            reconciliation.AttemptId,
                            reconciliation.Fence,
                            DurableOperationRecoveryRequirement.Reconcile)))
            {
                throw new ArgumentException(
                    "A reconciled terminal outcome must be the operation's exact durable acknowledgement.",
                    nameof(reconciliations));
            }
        }
        if (acknowledgement?.RecoveryIdentity is
            { Requirement: DurableOperationRecoveryRequirement.Reconcile } reconciliationIdentity
            && !normalizedReconciliations.Any(reconciliation =>
                reconciliation.AttemptId == reconciliationIdentity.SourceAttemptId
                && reconciliation.Fence == reconciliationIdentity.SourceFence
                && reconciliation.ObservedAtUtc == acknowledgement.AcknowledgedAtUtc
                && reconciliation.Observation is DurableOperationReconciledOutcome resolved
                && resolved.Outcome == acknowledgement.Outcome
                && resolved.AdapterEvidence == acknowledgement.AdapterEvidence))
        {
            throw new ArgumentException(
                "A reconciliation acknowledgement requires its exact fenced outcome evidence.",
                nameof(reconciliations));
        }
        if (admission is not null)
        {
            if (acknowledgement is null
                || admission.RequestId != acknowledgement.RequestId
                || admission.AttemptId != acknowledgement.AttemptId
                || admission.Outcome != acknowledgement.Outcome.Id
                || admission.Target != request.ResponseTarget)
            {
                throw new ArgumentException("Admission must disposition the current acknowledgement at the exact Request target.", nameof(admission));
            }
        }

        SchemaVersion = schemaVersion;
        CreatedAtUtc = createdAtUtc;
        Attempts = normalizedAttempts;
        Reconciliations = normalizedReconciliations;
        RecoveryRequirement = recoveryRequirement;
        Acknowledgement = acknowledgement;
        Admission = admission;
    }

    /// <summary>Exact durable-operation state schema.</summary>
    public ExecutionIrSchemaVersion SchemaVersion { get; }

    /// <summary>Canonical logical Request.</summary>
    public RequestEnvelope Request { get; }

    /// <summary>Portable bounded execution refinement.</summary>
    public DurableRequestBinding Binding { get; }

    /// <summary>Explicit UTC operation-creation observation.</summary>
    public DateTimeOffset CreatedAtUtc { get; }

    /// <summary>Ordered attempt snapshots; new attempts append after prior attempts close.</summary>
    public ImmutableArray<DurableOperationAttempt> Attempts { get; }

    /// <summary>Append-only fenced reconciliation evidence.</summary>
    public ImmutableArray<DurableOperationReconciliationEvidence> Reconciliations { get; }

    /// <summary>Required next recovery action.</summary>
    public DurableOperationRecoveryRequirement RecoveryRequirement { get; }

    /// <summary>One durable logical acknowledgement, when present.</summary>
    public DurableOperationAcknowledgement? Acknowledgement { get; }

    /// <summary>One durable target disposition, when present.</summary>
    public DurableOperationAdmission? Admission { get; }

    /// <summary>Logical operation identity; exactly the canonical Request emission identity.</summary>
    [JsonIgnore]
    public EmissionId OperationId => Request.Context.EmissionId;

    /// <summary>Scoped logical target-deduplication key.</summary>
    [JsonIgnore]
    public DurableOperationDeduplicationKey DeduplicationKey =>
        new(Request.Context.AuthorityScope, Request.Contract, Request.Context.IdempotencyKey);

    /// <summary>Latest physical attempt, or <see langword="null"/> before the first claim.</summary>
    [JsonIgnore]
    public DurableOperationAttempt? CurrentAttempt => Attempts.IsEmpty ? null : Attempts[^1];

    /// <summary>Derived lifecycle status.</summary>
    [JsonIgnore]
    public DurableOperationStatus Status => Admission is not null
        ? DurableOperationStatus.Dispositioned
        : Acknowledgement is not null
            ? DurableOperationStatus.Acknowledged
            : RecoveryRequirement switch
            {
                DurableOperationRecoveryRequirement.Retry => DurableOperationStatus.RetryEligible,
                DurableOperationRecoveryRequirement.Reconcile => DurableOperationStatus.ReconciliationRequired,
                DurableOperationRecoveryRequirement.TerminalOutcome => DurableOperationStatus.TerminalOutcomeRequired,
                DurableOperationRecoveryRequirement.Escalate => DurableOperationStatus.EscalationRequired,
                _ => CurrentAttempt?.Stage switch
                {
                    DurableOperationAttemptStage.Claimed => DurableOperationStatus.Claimed,
                    DurableOperationAttemptStage.Dispatched => DurableOperationStatus.Dispatched,
                    _ => DurableOperationStatus.Pending
                }
            };

    /// <summary>Creates the canonical Reply from this state's exact Request and acknowledgement.</summary>
    /// <param name="replyId">Stable logical Reply emission identity.</param>
    /// <param name="origin">Closed Transition or Process origin producing the Reply.</param>
    /// <param name="idempotencyKey">Stable logical Reply deduplication basis.</param>
    /// <param name="ordering">Optional explicit Reply ordering key.</param>
    /// <param name="provenance">Reply producer and semantic source attribution.</param>
    /// <returns>
    /// A current-schema Reply preserving the exact Request correlation, authority, delivery, and causal identity.
    /// </returns>
    /// <exception cref="InvalidOperationException">This operation has no durable acknowledgement.</exception>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="origin"/> or <paramref name="provenance"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="replyId"/> or <paramref name="idempotencyKey"/> is a default value.
    /// </exception>
    public ReplyEnvelope CreateReply(
        EmissionId replyId,
        InteractionOrigin origin,
        InteractionIdempotencyKey idempotencyKey,
        InteractionOrdering? ordering,
        ExecutionProvenance provenance)
    {
        var acknowledgement = Acknowledgement
            ?? throw new InvalidOperationException("A canonical Reply requires a durable acknowledgement.");
        var context = new InteractionEnvelopeContext(
            replyId,
            Guard.RequireNotNull(origin),
            Request.Context.CorrelationId,
            Request.Context.EmissionId,
            Request.Context.AuthorityScope,
            idempotencyKey,
            ordering,
            Request.Context.Delivery,
            Guard.RequireNotNull(provenance));
        return new(
            InteractionEnvelope.CurrentSchemaVersion,
            context,
            acknowledgement.ReplyContract,
            Request.Context.EmissionId,
            acknowledgement.Outcome);
    }

    /// <summary>Compares state snapshots by complete semantic value and ordered attempt history.</summary>
    /// <param name="other">State to compare.</param>
    /// <returns><see langword="true"/> when every semantic value is equal.</returns>
    public bool Equals(DurableOperationState? other) =>
        ReferenceEquals(this, other)
        || other is not null
        && SchemaVersion == other.SchemaVersion
        && Request == other.Request
        && Binding == other.Binding
        && CreatedAtUtc == other.CreatedAtUtc
        && RecoveryRequirement == other.RecoveryRequirement
        && Acknowledgement == other.Acknowledgement
        && Admission == other.Admission
        && Attempts.SequenceEqual(other.Attempts)
        && Reconciliations.SequenceEqual(other.Reconciliations);

    /// <summary>Returns a structural hash code for the complete semantic state.</summary>
    /// <returns>A hash code aligned with <see cref="Equals(DurableOperationState?)"/>.</returns>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(SchemaVersion);
        hash.Add(Request);
        hash.Add(Binding);
        hash.Add(CreatedAtUtc);
        hash.Add(RecoveryRequirement);
        hash.Add(Acknowledgement);
        hash.Add(Admission);
        foreach (var attempt in Attempts)
            hash.Add(attempt);
        foreach (var reconciliation in Reconciliations)
            hash.Add(reconciliation);
        return hash.ToHashCode();
    }
}
