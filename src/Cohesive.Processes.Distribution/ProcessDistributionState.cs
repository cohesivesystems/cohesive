using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Cohesive.Processes.Distribution;

/// <summary>Observable health advertised by a live worker incarnation.</summary>
public enum ProcessWorkerHealth
{
    /// <summary>No health was supplied; invalid for a registered worker.</summary>
    Unspecified = 0,

    /// <summary>The worker is eligible for new work.</summary>
    Healthy = 1,

    /// <summary>The worker remains observable but is not eligible for new work.</summary>
    Unhealthy = 2
}

/// <summary>Durable lifecycle state of one logical distributed work unit.</summary>
public enum ProcessWorkStatus
{
    /// <summary>No status was supplied; invalid for persisted work.</summary>
    Unspecified = 0,

    /// <summary>The work is durably admitted and may become claimable.</summary>
    Queued = 1,

    /// <summary>A live leased and fenced claim reserves the work.</summary>
    Claimed = 2,

    /// <summary>Effect ambiguity must be reconciled before redispatch or terminal settlement.</summary>
    ReconciliationRequired = 3,

    /// <summary>The work unit completed successfully.</summary>
    Succeeded = 4,

    /// <summary>The work unit reached an explicit terminal failure.</summary>
    Failed = 5,

    /// <summary>The work unit was cancelled through the distribution authority.</summary>
    Cancelled = 6,

    /// <summary>The work exhausted policy or was proven permanently oversized.</summary>
    Poisoned = 7
}

/// <summary>Terminal outcome reported by the current fenced worker claim.</summary>
public enum ProcessWorkCompletionOutcome
{
    /// <summary>No outcome was supplied; invalid for completion evidence.</summary>
    Unspecified = 0,

    /// <summary>The exact referenced work completed successfully.</summary>
    Succeeded = 1,

    /// <summary>The exact referenced work produced explicit terminal failure evidence.</summary>
    Failed = 2,

    /// <summary>The exact referenced work observed accepted cancellation.</summary>
    Cancelled = 3
}

/// <summary>Evidence about effects at a failed or interrupted work boundary.</summary>
public enum ProcessWorkEffectEvidence
{
    /// <summary>No effect evidence was supplied.</summary>
    None = 0,

    /// <summary>Execution did not begin an externally visible effect.</summary>
    NotStarted = 1,

    /// <summary>The effect is known to have committed.</summary>
    Applied = 2,

    /// <summary>The effect may have committed and must not be guessed.</summary>
    Ambiguous = 3
}

/// <summary>Disposition requested when a live claim cannot complete successfully.</summary>
public enum ProcessWorkReleaseDisposition
{
    /// <summary>No disposition was supplied; invalid for release evidence.</summary>
    Unspecified = 0,

    /// <summary>Return the work to the runnable queue, subject to attempt policy.</summary>
    Retry = 1,

    /// <summary>Require reconciliation before another physical attempt.</summary>
    Reconcile = 2,

    /// <summary>Settle the logical work as an explicit terminal failure.</summary>
    TerminalFailure = 3
}

/// <summary>Decision made from explicit reconciliation evidence.</summary>
public enum ProcessWorkReconciliationOutcome
{
    /// <summary>No outcome was supplied; invalid for reconciliation.</summary>
    Unspecified = 0,

    /// <summary>No authoritative completion occurred; the work may be redispatched.</summary>
    Redispatch = 1,

    /// <summary>Reconciliation proved successful authoritative completion.</summary>
    Succeeded = 2,

    /// <summary>Reconciliation proved terminal failure.</summary>
    Failed = 3,

    /// <summary>Reconciliation proved accepted cancellation.</summary>
    Cancelled = 4
}

/// <summary>Canonical work-submission intent admitted to a logical pool.</summary>
public sealed record ProcessWorkSubmission
{
    /// <summary>Creates one durable work-submission intent.</summary>
    /// <param name="schemaVersion">Exact portable distribution schema version.</param>
    /// <param name="id">Stable logical work identity retained across recovery.</param>
    /// <param name="idempotencyKey">Stable semantic intent identity.</param>
    /// <param name="reference">Exact canonical Process work reference.</param>
    /// <param name="requirements">Portable placement and execution requirements.</param>
    /// <param name="submittedAtUtc">UTC admission observation.</param>
    /// <exception cref="ArgumentException">
    /// An identity is default, <paramref name="submittedAtUtc"/> is not UTC, or a deadline predates submission.
    /// </exception>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="reference"/> or <paramref name="requirements"/> is <see langword="null"/>.
    /// </exception>
    [JsonConstructor]
    public ProcessWorkSubmission(
        Cohesive.Execution.ExecutionIrSchemaVersion schemaVersion,
        ProcessWorkId id,
        ProcessWorkIdempotencyKey idempotencyKey,
        ProcessWorkReference reference,
        ProcessWorkRequirements requirements,
        DateTimeOffset submittedAtUtc)
    {
        if (schemaVersion != ProcessDistributionWireNames.CurrentSchemaVersion)
            throw new ArgumentException("Unsupported Process distribution schema version.", nameof(schemaVersion));
        ProcessDistributionRequirements.Require(id.Value, nameof(id));
        ProcessDistributionRequirements.Require(idempotencyKey.Value, nameof(idempotencyKey));
        ProcessDistributionRequirements.RequireUtc(submittedAtUtc, nameof(submittedAtUtc));
        Reference = reference ?? throw new ArgumentNullException(nameof(reference));
        Requirements = requirements ?? throw new ArgumentNullException(nameof(requirements));
        if (requirements.DeadlineUtc < submittedAtUtc)
            throw new ArgumentException("A work deadline cannot predate submission.", nameof(requirements));

        SchemaVersion = schemaVersion;
        Id = id;
        IdempotencyKey = idempotencyKey;
        SubmittedAtUtc = submittedAtUtc;
        IntentFingerprint = ProcessDistributionFingerprinter.Intent(this);
    }

    /// <summary>Exact portable distribution schema version.</summary>
    public Cohesive.Execution.ExecutionIrSchemaVersion SchemaVersion { get; }

    /// <summary>Stable logical work identity retained across recovery.</summary>
    public ProcessWorkId Id { get; }

    /// <summary>Stable semantic intent identity.</summary>
    public ProcessWorkIdempotencyKey IdempotencyKey { get; }

    /// <summary>Exact canonical Process work reference.</summary>
    public ProcessWorkReference Reference { get; }

    /// <summary>Portable placement and execution requirements.</summary>
    public ProcessWorkRequirements Requirements { get; }

    /// <summary>UTC admission observation.</summary>
    public DateTimeOffset SubmittedAtUtc { get; }

    /// <summary>Deterministic fingerprint of idempotent semantic intent.</summary>
    [JsonIgnore]
    public string IntentFingerprint { get; }

    /// <summary>Compares semantic submission intent while excluding occurrence identity and admission time.</summary>
    /// <param name="candidate">Candidate submission carrying the same idempotency key.</param>
    /// <returns><see langword="true"/> when semantic intent is identical; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="candidate"/> is <see langword="null"/>.</exception>
    public bool HasSameIdempotentIntent(ProcessWorkSubmission candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return IdempotencyKey == candidate.IdempotencyKey
            && string.Equals(IntentFingerprint, candidate.IntentFingerprint, StringComparison.Ordinal);
    }
}

/// <summary>Live leased registration evidence for one concrete worker incarnation.</summary>
public sealed record ProcessWorkerRegistration
{
    /// <summary>Creates worker registration evidence.</summary>
    /// <param name="offer">Exact immutable offer for this incarnation.</param>
    /// <param name="health">Current worker health.</param>
    /// <param name="draining">Whether new claims are disabled.</param>
    /// <param name="registeredAtUtc">UTC initial registration time.</param>
    /// <param name="renewedAtUtc">UTC latest renewal time.</param>
    /// <param name="expiresAtUtc">Exclusive UTC registration expiry.</param>
    /// <exception cref="ArgumentNullException"><paramref name="offer"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="health"/> is unsupported.</exception>
    /// <exception cref="ArgumentException">A time is not UTC or registration chronology is invalid.</exception>
    [JsonConstructor]
    public ProcessWorkerRegistration(
        ProcessWorkerOffer offer,
        ProcessWorkerHealth health,
        bool draining,
        DateTimeOffset registeredAtUtc,
        DateTimeOffset renewedAtUtc,
        DateTimeOffset expiresAtUtc)
    {
        if (!Enum.IsDefined(health) || health == ProcessWorkerHealth.Unspecified)
            throw new ArgumentOutOfRangeException(nameof(health), health, "Worker health must be explicit.");
        ProcessDistributionRequirements.RequireUtc(registeredAtUtc, nameof(registeredAtUtc));
        ProcessDistributionRequirements.RequireUtc(renewedAtUtc, nameof(renewedAtUtc));
        ProcessDistributionRequirements.RequireUtc(expiresAtUtc, nameof(expiresAtUtc));
        if (renewedAtUtc < registeredAtUtc || expiresAtUtc <= renewedAtUtc)
            throw new ArgumentException("Worker registration chronology is invalid.", nameof(expiresAtUtc));

        Offer = offer ?? throw new ArgumentNullException(nameof(offer));
        Health = health;
        Draining = draining;
        RegisteredAtUtc = registeredAtUtc;
        RenewedAtUtc = renewedAtUtc;
        ExpiresAtUtc = expiresAtUtc;
    }

    /// <summary>Exact immutable offer for this incarnation.</summary>
    public ProcessWorkerOffer Offer { get; }

    /// <summary>Current worker health.</summary>
    public ProcessWorkerHealth Health { get; }

    /// <summary>Whether new claims are disabled.</summary>
    public bool Draining { get; }

    /// <summary>UTC initial registration time.</summary>
    public DateTimeOffset RegisteredAtUtc { get; }

    /// <summary>UTC latest renewal time.</summary>
    public DateTimeOffset RenewedAtUtc { get; }

    /// <summary>Exclusive UTC registration expiry.</summary>
    public DateTimeOffset ExpiresAtUtc { get; }

    /// <summary>Determines whether the worker is live at an explicit UTC observation.</summary>
    /// <param name="observedAtUtc">UTC time to test.</param>
    /// <returns><see langword="true"/> while the lease is live; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="observedAtUtc"/> is not UTC.</exception>
    public bool IsLive(DateTimeOffset observedAtUtc)
    {
        ProcessDistributionRequirements.RequireUtc(observedAtUtc, nameof(observedAtUtc));
        return observedAtUtc >= RegisteredAtUtc && observedAtUtc < ExpiresAtUtc;
    }
}

/// <summary>Durable leased and fenced assignment of one logical work unit.</summary>
public sealed record ProcessWorkClaim
{
    /// <summary>Creates one exact work claim.</summary>
    /// <param name="submission">Exact logical work submission.</param>
    /// <param name="request">Stable claim-request identity used for exact provider retries.</param>
    /// <param name="attempt">Positive physical attempt ordinal.</param>
    /// <param name="dispatch">Stable physical delivery identity.</param>
    /// <param name="worker">Owning worker incarnation.</param>
    /// <param name="fence">Monotonic ownership fence.</param>
    /// <param name="claimedAtUtc">UTC initial claim time.</param>
    /// <param name="renewedAtUtc">UTC latest renewal time.</param>
    /// <param name="expiresAtUtc">Exclusive UTC claim expiry.</param>
    /// <exception cref="ArgumentNullException"><paramref name="submission"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="attempt"/> is not positive.</exception>
    /// <exception cref="ArgumentException">An identity is default, a time is not UTC, or chronology is invalid.</exception>
    [JsonConstructor]
    public ProcessWorkClaim(
        ProcessWorkSubmission submission,
        ProcessWorkClaimRequestId request,
        int attempt,
        ProcessWorkDispatchId dispatch,
        ProcessWorkerIncarnationId worker,
        ProcessWorkFence fence,
        DateTimeOffset claimedAtUtc,
        DateTimeOffset renewedAtUtc,
        DateTimeOffset expiresAtUtc)
    {
        if (attempt <= 0)
            throw new ArgumentOutOfRangeException(nameof(attempt), attempt, "A work attempt must be positive.");
        ProcessDistributionRequirements.Require(request.Value, nameof(request));
        ProcessDistributionRequirements.Require(dispatch.Value, nameof(dispatch));
        ProcessDistributionRequirements.Require(worker.Value, nameof(worker));
        ProcessDistributionRequirements.Require(fence.Value, nameof(fence));
        ProcessDistributionRequirements.RequireUtc(claimedAtUtc, nameof(claimedAtUtc));
        ProcessDistributionRequirements.RequireUtc(renewedAtUtc, nameof(renewedAtUtc));
        ProcessDistributionRequirements.RequireUtc(expiresAtUtc, nameof(expiresAtUtc));
        if (renewedAtUtc < claimedAtUtc || expiresAtUtc <= renewedAtUtc)
            throw new ArgumentException("Work-claim chronology is invalid.", nameof(expiresAtUtc));

        Submission = submission ?? throw new ArgumentNullException(nameof(submission));
        Request = request;
        Attempt = attempt;
        Dispatch = dispatch;
        Worker = worker;
        Fence = fence;
        ClaimedAtUtc = claimedAtUtc;
        RenewedAtUtc = renewedAtUtc;
        ExpiresAtUtc = expiresAtUtc;
    }

    /// <summary>Exact logical work submission.</summary>
    public ProcessWorkSubmission Submission { get; }

    /// <summary>Stable claim-request identity used for exact provider retries.</summary>
    public ProcessWorkClaimRequestId Request { get; }

    /// <summary>Positive physical attempt ordinal.</summary>
    public int Attempt { get; }

    /// <summary>Stable physical delivery identity.</summary>
    public ProcessWorkDispatchId Dispatch { get; }

    /// <summary>Owning worker incarnation.</summary>
    public ProcessWorkerIncarnationId Worker { get; }

    /// <summary>Monotonic ownership fence.</summary>
    public ProcessWorkFence Fence { get; }

    /// <summary>UTC initial claim time.</summary>
    public DateTimeOffset ClaimedAtUtc { get; }

    /// <summary>UTC latest renewal time.</summary>
    public DateTimeOffset RenewedAtUtc { get; }

    /// <summary>Exclusive UTC claim expiry.</summary>
    public DateTimeOffset ExpiresAtUtc { get; }

    /// <summary>Determines whether ownership is live at an explicit UTC observation.</summary>
    /// <param name="observedAtUtc">UTC time to test.</param>
    /// <returns><see langword="true"/> while the claim is live; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="observedAtUtc"/> is not UTC.</exception>
    public bool IsLive(DateTimeOffset observedAtUtc)
    {
        ProcessDistributionRequirements.RequireUtc(observedAtUtc, nameof(observedAtUtc));
        return observedAtUtc >= ClaimedAtUtc && observedAtUtc < ExpiresAtUtc;
    }
}

/// <summary>Terminal evidence submitted by the exact current work claim.</summary>
public sealed record ProcessWorkCompletion
{
    /// <summary>Creates terminal work evidence.</summary>
    /// <param name="claim">Exact claim identity and fence.</param>
    /// <param name="outcome">Explicit terminal outcome.</param>
    /// <param name="effectEvidence">Known effect evidence at completion.</param>
    /// <param name="observedAtUtc">UTC completion observation.</param>
    /// <param name="resultReference">Optional canonical result or artifact reference.</param>
    /// <param name="failureCode">Optional stable terminal failure code.</param>
    /// <exception cref="ArgumentNullException"><paramref name="claim"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An enum value is unsupported.</exception>
    /// <exception cref="ArgumentException">
    /// The observation is not UTC or result and failure evidence conflicts with <paramref name="outcome"/>.
    /// </exception>
    [JsonConstructor]
    public ProcessWorkCompletion(
        ProcessWorkClaim claim,
        ProcessWorkCompletionOutcome outcome,
        ProcessWorkEffectEvidence effectEvidence,
        DateTimeOffset observedAtUtc,
        string? resultReference = null,
        string? failureCode = null)
    {
        if (!Enum.IsDefined(outcome) || outcome == ProcessWorkCompletionOutcome.Unspecified)
            throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "A terminal work outcome is required.");
        if (!Enum.IsDefined(effectEvidence))
            throw new ArgumentOutOfRangeException(nameof(effectEvidence), effectEvidence, "Unsupported effect evidence.");
        ProcessDistributionRequirements.RequireUtc(observedAtUtc, nameof(observedAtUtc));
        if (outcome == ProcessWorkCompletionOutcome.Succeeded && !string.IsNullOrWhiteSpace(failureCode))
            throw new ArgumentException("Successful work cannot carry a failure code.", nameof(failureCode));
        if (outcome != ProcessWorkCompletionOutcome.Succeeded && string.IsNullOrWhiteSpace(failureCode))
            throw new ArgumentException("Failed or cancelled work requires a stable reason code.", nameof(failureCode));
        if (effectEvidence == ProcessWorkEffectEvidence.Ambiguous)
            throw new ArgumentException("Ambiguous effects require reconciliation rather than terminal completion.", nameof(effectEvidence));
        if (outcome == ProcessWorkCompletionOutcome.Succeeded && effectEvidence != ProcessWorkEffectEvidence.Applied)
            throw new ArgumentException("Successful work requires applied-effect evidence.", nameof(effectEvidence));

        Claim = claim ?? throw new ArgumentNullException(nameof(claim));
        Outcome = outcome;
        EffectEvidence = effectEvidence;
        ObservedAtUtc = observedAtUtc;
        ResultReference = resultReference.TrimmedEmptyOrWhiteSpaceAs();
        FailureCode = failureCode.TrimmedEmptyOrWhiteSpaceAs();
        Fingerprint = ProcessDistributionFingerprinter.Completion(this);
    }

    /// <summary>Exact claim identity and fence.</summary>
    public ProcessWorkClaim Claim { get; }

    /// <summary>Explicit terminal outcome.</summary>
    public ProcessWorkCompletionOutcome Outcome { get; }

    /// <summary>Known effect evidence at completion.</summary>
    public ProcessWorkEffectEvidence EffectEvidence { get; }

    /// <summary>UTC completion observation.</summary>
    public DateTimeOffset ObservedAtUtc { get; }

    /// <summary>Optional canonical result or artifact reference.</summary>
    public string? ResultReference { get; }

    /// <summary>Optional stable terminal failure or cancellation code.</summary>
    public string? FailureCode { get; }

    /// <summary>Deterministic fingerprint used to replay an ambiguous completion commit.</summary>
    [JsonIgnore]
    public string Fingerprint { get; }
}

/// <summary>Non-terminal or terminal release evidence from the exact current work claim.</summary>
public sealed record ProcessWorkRelease
{
    /// <summary>Creates work-release evidence.</summary>
    /// <param name="claim">Exact current claim identity and fence.</param>
    /// <param name="disposition">Retry, reconcile, or terminal-failure decision.</param>
    /// <param name="effectEvidence">Known effect evidence at release.</param>
    /// <param name="reasonCode">Stable attributable reason code.</param>
    /// <param name="observedAtUtc">UTC release observation.</param>
    /// <param name="notBeforeUtc">Optional UTC earliest redispatch time.</param>
    /// <exception cref="ArgumentNullException"><paramref name="claim"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An enum value is unsupported.</exception>
    /// <exception cref="ArgumentException">A reason is empty, a time is not UTC, or retry chronology is invalid.</exception>
    [JsonConstructor]
    public ProcessWorkRelease(
        ProcessWorkClaim claim,
        ProcessWorkReleaseDisposition disposition,
        ProcessWorkEffectEvidence effectEvidence,
        string reasonCode,
        DateTimeOffset observedAtUtc,
        DateTimeOffset? notBeforeUtc = null)
    {
        if (!Enum.IsDefined(disposition) || disposition == ProcessWorkReleaseDisposition.Unspecified)
            throw new ArgumentOutOfRangeException(nameof(disposition), disposition, "A release disposition is required.");
        if (!Enum.IsDefined(effectEvidence))
            throw new ArgumentOutOfRangeException(nameof(effectEvidence), effectEvidence, "Unsupported effect evidence.");
        ProcessDistributionRequirements.RequireUtc(observedAtUtc, nameof(observedAtUtc));
        if (notBeforeUtc is { } notBefore)
        {
            ProcessDistributionRequirements.RequireUtc(notBefore, nameof(notBeforeUtc));
            if (notBefore < observedAtUtc)
                throw new ArgumentException("Retry eligibility cannot predate release.", nameof(notBeforeUtc));
        }
        if (disposition != ProcessWorkReleaseDisposition.Retry && notBeforeUtc is not null)
            throw new ArgumentException("Only retry release may declare delayed eligibility.", nameof(notBeforeUtc));
        if (disposition == ProcessWorkReleaseDisposition.Reconcile
            && effectEvidence != ProcessWorkEffectEvidence.Ambiguous)
        {
            throw new ArgumentException(
                "Reconciliation release requires ambiguous-effect evidence.",
                nameof(effectEvidence));
        }
        if (disposition != ProcessWorkReleaseDisposition.Reconcile
            && effectEvidence == ProcessWorkEffectEvidence.Ambiguous)
        {
            throw new ArgumentException(
                "Ambiguous effects require reconciliation rather than retry or terminal failure.",
                nameof(disposition));
        }

        Claim = claim ?? throw new ArgumentNullException(nameof(claim));
        Disposition = disposition;
        EffectEvidence = effectEvidence;
        ReasonCode = Guard.RequireNotNullOrWhiteSpace(reasonCode);
        ObservedAtUtc = observedAtUtc;
        NotBeforeUtc = notBeforeUtc;
        Fingerprint = ProcessDistributionFingerprinter.Release(this);
    }

    /// <summary>Exact current claim identity and fence.</summary>
    public ProcessWorkClaim Claim { get; }

    /// <summary>Retry, reconcile, or terminal-failure decision.</summary>
    public ProcessWorkReleaseDisposition Disposition { get; }

    /// <summary>Known effect evidence at release.</summary>
    public ProcessWorkEffectEvidence EffectEvidence { get; }

    /// <summary>Stable attributable reason code.</summary>
    public string ReasonCode { get; }

    /// <summary>UTC release observation.</summary>
    public DateTimeOffset ObservedAtUtc { get; }

    /// <summary>Optional UTC earliest redispatch time.</summary>
    public DateTimeOffset? NotBeforeUtc { get; }

    /// <summary>Deterministic fingerprint used to replay an outcome-ambiguous release commit.</summary>
    [JsonIgnore]
    public string Fingerprint { get; }
}

/// <summary>Attributable decision that resolves effect-ambiguous work.</summary>
public sealed record ProcessWorkReconciliation
{
    /// <summary>Creates reconciliation evidence.</summary>
    /// <param name="work">Logical work being reconciled.</param>
    /// <param name="fence">Last ambiguous ownership fence being resolved.</param>
    /// <param name="outcome">Evidence-backed reconciliation outcome.</param>
    /// <param name="evidenceReference">Stable reference to durable reconciliation evidence.</param>
    /// <param name="observedAtUtc">UTC reconciliation observation.</param>
    /// <param name="resultReference">Optional canonical result or artifact reference.</param>
    /// <param name="failureCode">Optional stable failure or cancellation code.</param>
    /// <exception cref="ArgumentException">An identity is default, a time is not UTC, or evidence conflicts.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="outcome"/> is unsupported.</exception>
    [JsonConstructor]
    public ProcessWorkReconciliation(
        ProcessWorkId work,
        ProcessWorkFence fence,
        ProcessWorkReconciliationOutcome outcome,
        string evidenceReference,
        DateTimeOffset observedAtUtc,
        string? resultReference = null,
        string? failureCode = null)
    {
        ProcessDistributionRequirements.Require(work.Value, nameof(work));
        ProcessDistributionRequirements.Require(fence.Value, nameof(fence));
        if (!Enum.IsDefined(outcome) || outcome == ProcessWorkReconciliationOutcome.Unspecified)
            throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "A reconciliation outcome is required.");
        ProcessDistributionRequirements.RequireUtc(observedAtUtc, nameof(observedAtUtc));
        if (outcome == ProcessWorkReconciliationOutcome.Succeeded && !string.IsNullOrWhiteSpace(failureCode))
            throw new ArgumentException("Successful reconciliation cannot carry a failure code.", nameof(failureCode));
        if (outcome is ProcessWorkReconciliationOutcome.Failed or ProcessWorkReconciliationOutcome.Cancelled
            && string.IsNullOrWhiteSpace(failureCode))
        {
            throw new ArgumentException("Failed or cancelled reconciliation requires a reason code.", nameof(failureCode));
        }

        Work = work;
        Fence = fence;
        Outcome = outcome;
        EvidenceReference = Guard.RequireNotNullOrWhiteSpace(evidenceReference);
        ObservedAtUtc = observedAtUtc;
        ResultReference = resultReference.TrimmedEmptyOrWhiteSpaceAs();
        FailureCode = failureCode.TrimmedEmptyOrWhiteSpaceAs();
    }

    /// <summary>Logical work being reconciled.</summary>
    public ProcessWorkId Work { get; }

    /// <summary>Last ambiguous ownership fence being resolved.</summary>
    public ProcessWorkFence Fence { get; }

    /// <summary>Evidence-backed reconciliation outcome.</summary>
    public ProcessWorkReconciliationOutcome Outcome { get; }

    /// <summary>Stable reference to durable reconciliation evidence.</summary>
    public string EvidenceReference { get; }

    /// <summary>UTC reconciliation observation.</summary>
    public DateTimeOffset ObservedAtUtc { get; }

    /// <summary>Optional canonical result or artifact reference.</summary>
    public string? ResultReference { get; }

    /// <summary>Optional stable failure or cancellation code.</summary>
    public string? FailureCode { get; }
}

/// <summary>Immutable observable state of one logical distributed work unit.</summary>
public sealed record ProcessWorkRecord
{
    /// <summary>Creates an immutable work-state snapshot.</summary>
    /// <param name="submission">Exact admitted submission.</param>
    /// <param name="status">Current durable lifecycle state.</param>
    /// <param name="revision">Positive monotonic ledger revision.</param>
    /// <param name="attemptCount">Number of physical claims created so far.</param>
    /// <param name="highestFence">Highest fence created so far, or zero before the first claim.</param>
    /// <param name="availableAtUtc">UTC earliest claim eligibility.</param>
    /// <param name="claim">Current live or last ambiguous claim, when required by status.</param>
    /// <param name="cancellationRequested">Whether cancellation has been requested from a current worker.</param>
    /// <param name="completion">Terminal worker completion evidence, when present.</param>
    /// <param name="reconciliation">Latest explicit reconciliation evidence, when present.</param>
    /// <param name="reasonCode">Optional stable lifecycle reason code.</param>
    /// <param name="updatedAtUtc">UTC latest ledger update.</param>
    /// <param name="lastRelease">Latest release evidence retained for exact provider retry.</param>
    /// <exception cref="ArgumentNullException"><paramref name="submission"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Status, revision, attempts, or fence evidence is invalid.</exception>
    /// <exception cref="ArgumentException">Time, claim, completion, or lifecycle evidence is inconsistent.</exception>
    [JsonConstructor]
    public ProcessWorkRecord(
        ProcessWorkSubmission submission,
        ProcessWorkStatus status,
        long revision,
        int attemptCount,
        long highestFence,
        DateTimeOffset availableAtUtc,
        ProcessWorkClaim? claim,
        bool cancellationRequested,
        ProcessWorkCompletion? completion,
        ProcessWorkReconciliation? reconciliation,
        string? reasonCode,
        DateTimeOffset updatedAtUtc,
        ProcessWorkRelease? lastRelease = null)
    {
        if (!Enum.IsDefined(status) || status == ProcessWorkStatus.Unspecified)
            throw new ArgumentOutOfRangeException(nameof(status), status, "A work status is required.");
        if (revision <= 0)
            throw new ArgumentOutOfRangeException(nameof(revision), revision, "Work revision must be positive.");
        if (attemptCount < 0)
            throw new ArgumentOutOfRangeException(nameof(attemptCount), attemptCount, "Attempt count cannot be negative.");
        if (highestFence < 0 || highestFence != attemptCount)
            throw new ArgumentOutOfRangeException(nameof(highestFence), highestFence, "Fence and attempt ordinals must advance together.");
        ProcessDistributionRequirements.RequireUtc(availableAtUtc, nameof(availableAtUtc));
        ProcessDistributionRequirements.RequireUtc(updatedAtUtc, nameof(updatedAtUtc));
        ArgumentNullException.ThrowIfNull(submission);
        if (updatedAtUtc < submission.SubmittedAtUtc || availableAtUtc < submission.SubmittedAtUtc)
            throw new ArgumentException("Work-state chronology cannot predate submission.", nameof(updatedAtUtc));
        if (status is ProcessWorkStatus.Claimed or ProcessWorkStatus.ReconciliationRequired && claim is null)
            throw new ArgumentException("Claimed or ambiguous work requires retained claim evidence.", nameof(claim));
        if (status is not (ProcessWorkStatus.Claimed or ProcessWorkStatus.ReconciliationRequired) && claim is not null)
            throw new ArgumentException("Only claimed or ambiguous work may retain a claim.", nameof(claim));
        if (claim is not null
            && (claim.Submission.Id != submission.Id
                || claim.Attempt != attemptCount
                || claim.Fence.Ordinal != highestFence))
        {
            throw new ArgumentException("Retained claim evidence does not belong to the work revision.", nameof(claim));
        }
        if (completion is not null
            && (completion.Claim.Submission.Id != submission.Id
                || completion.Claim.Attempt != attemptCount
                || completion.Claim.Fence.Ordinal != highestFence))
        {
            throw new ArgumentException("Completion evidence does not belong to the work revision.", nameof(completion));
        }
        if (completion is not null && status is not (ProcessWorkStatus.Succeeded or ProcessWorkStatus.Failed or ProcessWorkStatus.Cancelled))
            throw new ArgumentException("Worker completion evidence requires a terminal status.", nameof(completion));
        if (reconciliation is not null
            && (reconciliation.Work != submission.Id
                || reconciliation.Fence.Ordinal > highestFence))
        {
            throw new ArgumentException("Reconciliation evidence does not belong to the retained work fence.", nameof(reconciliation));
        }
        if (lastRelease is not null
            && (lastRelease.Claim.Submission.Id != submission.Id
                || lastRelease.Claim.Fence.Ordinal > highestFence))
        {
            throw new ArgumentException("Release evidence does not belong to the retained work fence.", nameof(lastRelease));
        }
        if (cancellationRequested && status != ProcessWorkStatus.Claimed)
            throw new ArgumentException("Only currently claimed work may retain a pending cancellation request.", nameof(cancellationRequested));

        Submission = submission;
        Status = status;
        Revision = revision;
        AttemptCount = attemptCount;
        HighestFence = highestFence;
        AvailableAtUtc = availableAtUtc;
        Claim = claim;
        CancellationRequested = cancellationRequested;
        Completion = completion;
        Reconciliation = reconciliation;
        ReasonCode = reasonCode.TrimmedEmptyOrWhiteSpaceAs();
        UpdatedAtUtc = updatedAtUtc;
        LastRelease = lastRelease;
    }

    /// <summary>Exact admitted submission.</summary>
    public ProcessWorkSubmission Submission { get; }

    /// <summary>Current durable lifecycle state.</summary>
    public ProcessWorkStatus Status { get; }

    /// <summary>Positive monotonic ledger revision.</summary>
    [JsonConverter(typeof(Cohesive.Model.Serialization.StringEncodedInt64JsonConverter))]
    public long Revision { get; }

    /// <summary>Number of physical claims created so far.</summary>
    public int AttemptCount { get; }

    /// <summary>Highest fence created so far, or zero before the first claim.</summary>
    [JsonConverter(typeof(Cohesive.Model.Serialization.StringEncodedInt64JsonConverter))]
    public long HighestFence { get; }

    /// <summary>UTC earliest claim eligibility.</summary>
    public DateTimeOffset AvailableAtUtc { get; }

    /// <summary>Current live or last ambiguous claim, when required by status.</summary>
    public ProcessWorkClaim? Claim { get; }

    /// <summary>Whether cancellation has been requested from the current worker.</summary>
    public bool CancellationRequested { get; }

    /// <summary>Terminal worker completion evidence, when present.</summary>
    public ProcessWorkCompletion? Completion { get; }

    /// <summary>Latest explicit reconciliation evidence, when present.</summary>
    public ProcessWorkReconciliation? Reconciliation { get; }

    /// <summary>Optional stable lifecycle reason code.</summary>
    public string? ReasonCode { get; }

    /// <summary>UTC latest ledger update.</summary>
    public DateTimeOffset UpdatedAtUtc { get; }

    /// <summary>Latest release evidence retained long enough to reconcile an outcome-ambiguous provider call.</summary>
    public ProcessWorkRelease? LastRelease { get; }
}

static class ProcessDistributionFingerprinter
{
    internal static string Intent(ProcessWorkSubmission submission)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, "cohesive.processes.distribution.intent/v1");
        Append(hash, submission.SchemaVersion.Value);
        Append(hash, submission.IdempotencyKey.Value);
        AppendReference(hash, submission.Reference);
        AppendRequirements(hash, submission.Requirements);
        return $"sha256-v1:{Convert.ToHexStringLower(hash.GetHashAndReset())}";
    }

    internal static string Completion(ProcessWorkCompletion completion)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, "cohesive.processes.distribution.completion/v1");
        Append(hash, completion.Claim.Submission.Id.Value);
        Append(hash, completion.Claim.Dispatch.Value);
        Append(hash, completion.Claim.Worker.Value);
        Append(hash, completion.Claim.Fence.Value);
        Append(hash, ((int)completion.Outcome).ToString(System.Globalization.CultureInfo.InvariantCulture));
        Append(hash, ((int)completion.EffectEvidence).ToString(System.Globalization.CultureInfo.InvariantCulture));
        Append(hash, completion.ObservedAtUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        Append(hash, completion.ResultReference ?? string.Empty);
        Append(hash, completion.FailureCode ?? string.Empty);
        return $"sha256-v1:{Convert.ToHexStringLower(hash.GetHashAndReset())}";
    }

    internal static string Release(ProcessWorkRelease release)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, "cohesive.processes.distribution.release/v1");
        Append(hash, release.Claim.Submission.Id.Value);
        Append(hash, release.Claim.Request.Value);
        Append(hash, release.Claim.Dispatch.Value);
        Append(hash, release.Claim.Worker.Value);
        Append(hash, release.Claim.Fence.Value);
        Append(hash, ((int)release.Disposition).ToString(System.Globalization.CultureInfo.InvariantCulture));
        Append(hash, ((int)release.EffectEvidence).ToString(System.Globalization.CultureInfo.InvariantCulture));
        Append(hash, release.ReasonCode);
        Append(hash, release.ObservedAtUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        Append(hash, release.NotBeforeUtc?.ToString("O", System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty);
        return $"sha256-v1:{Convert.ToHexStringLower(hash.GetHashAndReset())}";
    }

    static void AppendReference(IncrementalHash hash, ProcessWorkReference reference)
    {
        Append(hash, reference.Definition.DefinitionId.Value);
        Append(hash, reference.Definition.RevisionId.Value);
        Append(hash, reference.Definition.Fingerprint.Algorithm);
        Append(hash, reference.Definition.Fingerprint.Canonicalization);
        Append(hash, reference.Definition.Fingerprint.Value);
        Append(hash, reference.ProcessIrVersion.Value);
        Append(hash, reference.Continuation.ProcessInstanceId.Value);
        Append(hash, reference.Continuation.ProcessAttemptId.Value);
        Append(hash, ((int)reference.Kind).ToString(System.Globalization.CultureInfo.InvariantCulture));
        Append(hash, reference.SemanticPath.ToString());
        Append(hash, reference.Provenance.Producer.Producer);
        Append(hash, reference.Provenance.Producer.Version ?? string.Empty);
        Append(hash, reference.Provenance.Source.Reference);
        Append(hash, reference.Provenance.Source.SemanticPath?.ToString() ?? string.Empty);
        Append(hash, reference.Provenance.Source.Description ?? string.Empty);
        Append(hash, ((int)reference.Provenance.Origin).ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    static void AppendRequirements(IncrementalHash hash, ProcessWorkRequirements requirements)
    {
        Append(hash, requirements.Pool.Value);
        foreach (var capability in requirements.Capabilities)
            Append(hash, capability);
        Append(hash, "capacity");
        foreach (var capacity in requirements.Capacity)
        {
            Append(hash, capacity.Resource);
            Append(hash, capacity.Units.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Append(hash, capacity.Unit);
        }
        Append(hash, ((int)requirements.EffectGuarantee).ToString(System.Globalization.CultureInfo.InvariantCulture));
        Append(hash, ((int)requirements.RecoveryMode).ToString(System.Globalization.CultureInfo.InvariantCulture));
        Append(hash, requirements.CapacityDomain ?? string.Empty);
        Append(hash, requirements.FairnessKey ?? string.Empty);
        Append(hash, requirements.Affinity ?? string.Empty);
        Append(hash, requirements.Priority.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Append(hash, requirements.DeadlineUtc?.ToString("O", System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty);
        Append(hash, requirements.ExecutionTimeout?.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty);
    }

    static void Append(IncrementalHash hash, string value)
    {
        var byteCount = Encoding.UTF8.GetByteCount(value);
        Span<byte> length = stackalloc byte[sizeof(int)];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, byteCount);
        hash.AppendData(length);
        hash.AppendData(Encoding.UTF8.GetBytes(value));
    }
}
