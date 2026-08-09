using System.Collections.Immutable;
using System.Globalization;

namespace Cohesive.Processes.Distribution;

/// <summary>Deterministic in-memory reference interpreter for the portable distribution ledger.</summary>
/// <remarks>
/// This implementation is a concurrency-safe semantic oracle, not a production durability claim. All decisions
/// are made under one copy-on-write boundary so tests can prove eligibility, fairness, capacity, expiry, fencing,
/// draining, poison, cancellation, and reconciliation invariants without provider timing behavior.
/// </remarks>
public sealed class InMemoryProcessDistributionStore : IProcessDistributionStore
{
    const string OversizedReason = "processes.distribution.work.oversized";
    const string AttemptsExhaustedReason = "processes.distribution.work.attemptsExhausted";
    const string ClaimExpiredReason = "processes.distribution.work.claimExpired";
    const string DeadlineExpiredReason = "processes.distribution.work.deadlineExpired";

    readonly Lock gate = new();
    readonly Dictionary<ProcessWorkerPoolId, PoolState> pools = [];
    readonly Dictionary<ProcessWorkId, ProcessWorkRecord> work = [];
    readonly Dictionary<ProcessWorkIdempotencyKey, ProcessWorkId> idempotencyIndex = [];
    readonly Dictionary<ProcessWorkerIncarnationId, ProcessWorkerRegistration> workers = [];

    /// <summary>Creates an empty in-memory distribution reference interpreter.</summary>
    public InMemoryProcessDistributionStore()
    {
    }

    /// <summary>Restores a reference interpreter from one complete provider-neutral ledger.</summary>
    /// <param name="ledger">Exact persisted pool, fairness, worker, and work state.</param>
    /// <exception cref="ArgumentNullException"><paramref name="ledger"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// Work references an absent pool, worker offers reference absent pools, or an idempotency key is duplicated.
    /// </exception>
    public InMemoryProcessDistributionStore(ProcessDistributionLedgerDocument ledger)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        foreach (var pool in ledger.Pools)
            pools.Add(pool.Definition.Id, new(pool));
        foreach (var registration in ledger.Workers)
        {
            if (registration.Offer.Pools.Any(pool => !pools.ContainsKey(pool)))
                throw new ArgumentException("A restored worker offer references an absent pool.", nameof(ledger));
            workers.Add(registration.Offer.Worker, registration);
        }
        foreach (var record in ledger.Work)
        {
            if (!pools.ContainsKey(record.Submission.Requirements.Pool))
                throw new ArgumentException("Restored work references an absent pool.", nameof(ledger));
            work.Add(record.Submission.Id, record);
            if (!idempotencyIndex.TryAdd(record.Submission.IdempotencyKey, record.Submission.Id))
                throw new ArgumentException("Restored work duplicates an idempotency key.", nameof(ledger));
        }
    }

    /// <inheritdoc />
    public ProcessDistributionStoreCapabilities Capabilities { get; } = new(
        isDurable: false,
        supportsAtomicClaim: true,
        supportsCompareAndSwap: true,
        supportsWorkerLeases: true,
        supportsClaimRenewal: true,
        supportsMonotonicFencing: true,
        supportsRunnableDiscovery: true,
        supportsCapacityReservations: true,
        supportsPoisonWork: true,
        supportsAtomicProcessCommit: false);

    /// <summary>Captures the complete provider-neutral ledger in canonical identity order.</summary>
    /// <returns>An immutable current-version ledger document.</returns>
    public ProcessDistributionLedgerDocument CaptureLedger()
    {
        lock (gate)
        {
            return new(
                ProcessDistributionWireNames.CurrentSchemaVersion,
                [.. pools.Values
                    .OrderBy(static item => item.Definition.Id.Value, StringComparer.Ordinal)
                    .Select(static item => item.Capture())],
                [.. workers.Values.OrderBy(static item => item.Offer.Worker.Value, StringComparer.Ordinal)],
                [.. work.Values.OrderBy(static item => item.Submission.Id.Value, StringComparer.Ordinal)]);
        }
    }

    /// <inheritdoc />
    public Task<ProcessDistributionMutationResult> EnsurePoolAsync(
        OperationContext context,
        ProcessWorkerPoolDefinition pool)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(pool);
        context.ThrowIfCancellationRequested();

        lock (gate)
        {
            if (pools.TryGetValue(pool.Id, out var existing))
            {
                return Task.FromResult(new ProcessDistributionMutationResult(
                    SamePool(existing.Definition, pool)
                        ? ProcessDistributionDisposition.Replayed
                        : ProcessDistributionDisposition.IdentityConflict));
            }

            pools.Add(pool.Id, new(pool));
            return Task.FromResult(new ProcessDistributionMutationResult(ProcessDistributionDisposition.Applied));
        }
    }

    /// <inheritdoc />
    public Task<ProcessDistributionMutationResult> SubmitAsync(
        OperationContext context,
        ProcessWorkSubmission submission)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(submission);
        context.ThrowIfCancellationRequested();

        lock (gate)
        {
            if (!pools.TryGetValue(submission.Requirements.Pool, out var pool))
                return Task.FromResult(new ProcessDistributionMutationResult(ProcessDistributionDisposition.NotFound));

            if (work.TryGetValue(submission.Id, out var sameIdentity))
            {
                return Task.FromResult(new ProcessDistributionMutationResult(
                    sameIdentity.Submission.HasSameIdempotentIntent(submission)
                        ? ProcessDistributionDisposition.Replayed
                        : ProcessDistributionDisposition.IdentityConflict,
                    sameIdentity));
            }

            if (idempotencyIndex.TryGetValue(submission.IdempotencyKey, out var priorId))
            {
                var prior = work[priorId];
                return Task.FromResult(new ProcessDistributionMutationResult(
                    prior.Submission.HasSameIdempotentIntent(submission)
                        ? ProcessDistributionDisposition.Replayed
                        : ProcessDistributionDisposition.IdentityConflict,
                    prior));
            }

            if (!RequirementsFitPoolPolicy(submission.Requirements, pool.Definition.Policy))
                return Task.FromResult(new ProcessDistributionMutationResult(ProcessDistributionDisposition.Incompatible));

            var oversized = ExceedsHardPoolCapacity(submission.Requirements, pool.Definition.Policy);
            var poison = oversized
                && pool.Definition.Policy.OversizedWorkBehavior == ProcessOversizedWorkBehavior.Poison;
            var record = new ProcessWorkRecord(
                submission,
                poison ? ProcessWorkStatus.Poisoned : ProcessWorkStatus.Queued,
                revision: 1,
                attemptCount: 0,
                highestFence: 0,
                availableAtUtc: submission.SubmittedAtUtc,
                claim: null,
                cancellationRequested: false,
                completion: null,
                reconciliation: null,
                reasonCode: poison ? OversizedReason : null,
                updatedAtUtc: submission.SubmittedAtUtc);
            work.Add(submission.Id, record);
            idempotencyIndex.Add(submission.IdempotencyKey, submission.Id);
            return Task.FromResult(new ProcessDistributionMutationResult(ProcessDistributionDisposition.Applied, record));
        }
    }

    /// <inheritdoc />
    public Task<ProcessDistributionMutationResult> RegisterWorkerAsync(
        OperationContext context,
        ProcessWorkerOffer offer,
        DateTimeOffset observedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(offer);
        ProcessDistributionRequirements.RequireUtc(observedAtUtc, nameof(observedAtUtc));
        context.ThrowIfCancellationRequested();

        lock (gate)
        {
            if (offer.Pools.Any(pool => !pools.ContainsKey(pool)))
                return Task.FromResult(new ProcessDistributionMutationResult(ProcessDistributionDisposition.Incompatible));

            if (workers.TryGetValue(offer.Worker, out var existing))
            {
                var disposition = SameOffer(existing.Offer, offer)
                    ? existing.IsLive(context.UtcNow)
                        ? ProcessDistributionDisposition.Replayed
                        : ProcessDistributionDisposition.LeaseExpired
                    : ProcessDistributionDisposition.IdentityConflict;
                return Task.FromResult(new ProcessDistributionMutationResult(disposition, Worker: existing));
            }

            var leaseDuration = WorkerLeaseDuration(offer);
            var registration = new ProcessWorkerRegistration(
                offer,
                ProcessWorkerHealth.Healthy,
                draining: false,
                registeredAtUtc: observedAtUtc,
                renewedAtUtc: observedAtUtc,
                expiresAtUtc: Add(observedAtUtc, leaseDuration));
            workers.Add(offer.Worker, registration);
            return Task.FromResult(new ProcessDistributionMutationResult(
                ProcessDistributionDisposition.Applied,
                Worker: registration));
        }
    }

    /// <inheritdoc />
    public Task<ProcessDistributionMutationResult> RenewWorkerAsync(
        OperationContext context,
        ProcessWorkerIncarnationId worker,
        ProcessWorkerHealth health,
        DateTimeOffset observedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(context);
        ProcessDistributionRequirements.Require(worker.Value, nameof(worker));
        ProcessDistributionRequirements.RequireUtc(observedAtUtc, nameof(observedAtUtc));
        if (!Enum.IsDefined(health) || health == ProcessWorkerHealth.Unspecified)
            throw new ArgumentOutOfRangeException(nameof(health), health, "Worker health must be explicit.");
        context.ThrowIfCancellationRequested();

        lock (gate)
        {
            if (!workers.TryGetValue(worker, out var existing))
                return Task.FromResult(new ProcessDistributionMutationResult(ProcessDistributionDisposition.NotFound));
            if (!existing.IsLive(context.UtcNow) || !existing.IsLive(observedAtUtc))
            {
                return Task.FromResult(new ProcessDistributionMutationResult(
                    ProcessDistributionDisposition.LeaseExpired,
                    Worker: existing));
            }
            if (observedAtUtc < existing.RenewedAtUtc)
                throw new ArgumentException("A worker renewal cannot predate retained lease evidence.", nameof(observedAtUtc));

            var requestedExpiry = Add(observedAtUtc, WorkerLeaseDuration(existing.Offer));
            if (existing.RenewedAtUtc == observedAtUtc
                && existing.ExpiresAtUtc >= requestedExpiry
                && existing.Health == health)
            {
                return Task.FromResult(new ProcessDistributionMutationResult(
                    ProcessDistributionDisposition.Replayed,
                    Worker: existing));
            }

            var renewed = new ProcessWorkerRegistration(
                existing.Offer,
                health,
                existing.Draining,
                existing.RegisteredAtUtc,
                observedAtUtc,
                requestedExpiry);
            workers[worker] = renewed;
            return Task.FromResult(new ProcessDistributionMutationResult(
                ProcessDistributionDisposition.Applied,
                Worker: renewed));
        }
    }

    /// <inheritdoc />
    public Task<ProcessDistributionMutationResult> SetWorkerDrainingAsync(
        OperationContext context,
        ProcessWorkerIncarnationId worker,
        bool draining,
        DateTimeOffset observedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(context);
        ProcessDistributionRequirements.Require(worker.Value, nameof(worker));
        ProcessDistributionRequirements.RequireUtc(observedAtUtc, nameof(observedAtUtc));
        context.ThrowIfCancellationRequested();

        lock (gate)
        {
            if (!workers.TryGetValue(worker, out var existing))
                return Task.FromResult(new ProcessDistributionMutationResult(ProcessDistributionDisposition.NotFound));
            if (!existing.IsLive(context.UtcNow) || !existing.IsLive(observedAtUtc))
            {
                return Task.FromResult(new ProcessDistributionMutationResult(
                    ProcessDistributionDisposition.LeaseExpired,
                    Worker: existing));
            }
            if (observedAtUtc < existing.RenewedAtUtc)
                throw new ArgumentException("A drain observation cannot predate retained lease evidence.", nameof(observedAtUtc));
            if (existing.Draining == draining)
            {
                return Task.FromResult(new ProcessDistributionMutationResult(
                    ProcessDistributionDisposition.Replayed,
                    Worker: existing));
            }

            var updated = new ProcessWorkerRegistration(
                existing.Offer,
                existing.Health,
                draining,
                existing.RegisteredAtUtc,
                observedAtUtc,
                existing.ExpiresAtUtc);
            workers[worker] = updated;
            return Task.FromResult(new ProcessDistributionMutationResult(
                ProcessDistributionDisposition.Applied,
                Worker: updated));
        }
    }

    /// <inheritdoc />
    public Task<ProcessWorkClaimResult> ClaimAsync(
        OperationContext context,
        ProcessWorkerPoolId pool,
        ProcessWorkerIncarnationId worker,
        ProcessWorkClaimRequestId request,
        DateTimeOffset observedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(context);
        ProcessDistributionRequirements.Require(pool.Value, nameof(pool));
        ProcessDistributionRequirements.Require(worker.Value, nameof(worker));
        ProcessDistributionRequirements.Require(request.Value, nameof(request));
        ProcessDistributionRequirements.RequireUtc(observedAtUtc, nameof(observedAtUtc));
        context.ThrowIfCancellationRequested();

        lock (gate)
        {
            var physicalNow = context.UtcNow;
            RecoverExpiredClaims(physicalNow);
            if (!pools.TryGetValue(pool, out var poolState))
                return Task.FromResult(new ProcessWorkClaimResult(ProcessDistributionDisposition.NotFound));
            ExpireDeadlines(pool, observedAtUtc);
            var requestedRecord = work.Values.FirstOrDefault(item => item.Claim?.Request == request);
            if (requestedRecord?.Claim is { } requestedClaim)
            {
                var disposition = requestedClaim.Worker == worker
                    && requestedClaim.Submission.Requirements.Pool == pool
                    ? requestedRecord.Status == ProcessWorkStatus.Claimed
                        && requestedClaim.IsLive(physicalNow)
                        && requestedClaim.IsLive(observedAtUtc)
                            ? ProcessDistributionDisposition.Replayed
                            : ProcessDistributionDisposition.LeaseExpired
                    : ProcessDistributionDisposition.IdentityConflict;
                return Task.FromResult(new ProcessWorkClaimResult(
                    disposition,
                    disposition == ProcessDistributionDisposition.Replayed ? requestedClaim : null));
            }
            if (!workers.TryGetValue(worker, out var registration)
                || !registration.IsLive(physicalNow)
                || !registration.IsLive(observedAtUtc)
                || registration.Health != ProcessWorkerHealth.Healthy
                || registration.Draining
                || !registration.Offer.Pools.Contains(pool))
            {
                return Task.FromResult(new ProcessWorkClaimResult(ProcessDistributionDisposition.WorkerUnavailable));
            }

            var poolClaims = LiveClaims(pool, physicalNow);
            if (poolClaims.Count >= poolState.Definition.Policy.MaximumConcurrentClaims)
                return Task.FromResult(new ProcessWorkClaimResult(ProcessDistributionDisposition.NoEligibleWork));
            var workerClaims = poolClaims.Where(item => item.Claim!.Worker == worker).ToArray();
            if (workerClaims.Length >= registration.Offer.MaximumConcurrentClaims)
                return Task.FromResult(new ProcessWorkClaimResult(ProcessDistributionDisposition.NoEligibleWork));

            var candidates = work.Values
                .Where(item => item.Submission.Requirements.Pool == pool
                    && item.Status == ProcessWorkStatus.Queued
                    && item.AvailableAtUtc <= observedAtUtc
                    && (item.Submission.Requirements.DeadlineUtc is null
                        || item.Submission.Requirements.DeadlineUtc >= observedAtUtc)
                    && WorkerEligible(registration.Offer, item.Submission)
                    && FitsCapacity(
                        poolState.Definition.Policy,
                        registration.Offer,
                        poolClaims,
                        workerClaims,
                        item.Submission.Requirements))
                .OrderByDescending(static item => item.Submission.Requirements.Priority)
                .ThenBy(item => poolState.FairnessOrdinal(item.Submission.Requirements.FairnessKey))
                .ThenBy(static item => item.Submission.SubmittedAtUtc)
                .ThenBy(static item => item.Submission.Id.Value, StringComparer.Ordinal)
                .ToArray();
            if (candidates.Length == 0)
                return Task.FromResult(new ProcessWorkClaimResult(ProcessDistributionDisposition.NoEligibleWork));

            var selected = candidates[0];
            var nextAttempt = checked(selected.AttemptCount + 1);
            var fence = new ProcessWorkFence(nextAttempt.ToString(CultureInfo.InvariantCulture));
            var claimExpiry = Add(observedAtUtc, poolState.Definition.Policy.ClaimLeaseDuration);
            if (claimExpiry > registration.ExpiresAtUtc)
                claimExpiry = registration.ExpiresAtUtc;
            var dispatch = new ProcessWorkDispatchId($"dispatch/{selected.Submission.Id.Value}/{nextAttempt.ToString(CultureInfo.InvariantCulture)}");
            var claim = new ProcessWorkClaim(
                selected.Submission,
                request,
                nextAttempt,
                dispatch,
                worker,
                fence,
                observedAtUtc,
                observedAtUtc,
                claimExpiry);
            var updated = Replace(
                selected,
                ProcessWorkStatus.Claimed,
                observedAtUtc,
                revision: checked(selected.Revision + 1),
                attemptCount: nextAttempt,
                highestFence: nextAttempt,
                availableAtUtc: selected.AvailableAtUtc,
                claim: claim,
                cancellationRequested: false,
                completion: null,
                reconciliation: selected.Reconciliation,
                reasonCode: null);
            work[selected.Submission.Id] = updated;
            poolState.RecordFairness(selected.Submission.Requirements.FairnessKey);
            return Task.FromResult(new ProcessWorkClaimResult(ProcessDistributionDisposition.Applied, claim));
        }
    }

    /// <inheritdoc />
    public Task<ProcessDistributionMutationResult> RenewClaimAsync(
        OperationContext context,
        ProcessWorkClaim claim,
        DateTimeOffset observedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(claim);
        ProcessDistributionRequirements.RequireUtc(observedAtUtc, nameof(observedAtUtc));
        context.ThrowIfCancellationRequested();

        lock (gate)
        {
            if (!work.TryGetValue(claim.Submission.Id, out var record))
                return Task.FromResult(new ProcessDistributionMutationResult(ProcessDistributionDisposition.NotFound));
            var validation = ValidateCurrentClaim(record, claim, context.UtcNow, observedAtUtc);
            if (validation != ProcessDistributionDisposition.Applied)
            {
                if (validation == ProcessDistributionDisposition.LeaseExpired)
                    record = ExpireClaim(record, context.UtcNow);
                return Task.FromResult(new ProcessDistributionMutationResult(validation, record));
            }
            var current = record.Claim!;
            if (!workers.TryGetValue(current.Worker, out var worker)
                || !worker.IsLive(context.UtcNow)
                || !worker.IsLive(observedAtUtc))
            {
                record = ExpireClaim(record, context.UtcNow);
                return Task.FromResult(new ProcessDistributionMutationResult(
                    ProcessDistributionDisposition.LeaseExpired,
                    record));
            }
            if (observedAtUtc < current.RenewedAtUtc)
                throw new ArgumentException("A claim renewal cannot predate retained lease evidence.", nameof(observedAtUtc));

            var policy = pools[record.Submission.Requirements.Pool].Definition.Policy;
            var expiry = Add(observedAtUtc, policy.ClaimLeaseDuration);
            if (expiry > worker.ExpiresAtUtc)
                expiry = worker.ExpiresAtUtc;
            if (current.RenewedAtUtc == observedAtUtc && current.ExpiresAtUtc >= expiry)
                return Task.FromResult(new ProcessDistributionMutationResult(ProcessDistributionDisposition.Replayed, record));

            var renewed = new ProcessWorkClaim(
                current.Submission,
                current.Request,
                current.Attempt,
                current.Dispatch,
                current.Worker,
                current.Fence,
                current.ClaimedAtUtc,
                observedAtUtc,
                expiry);
            var updated = Replace(
                record,
                record.Status,
                observedAtUtc,
                checked(record.Revision + 1),
                record.AttemptCount,
                record.HighestFence,
                record.AvailableAtUtc,
                renewed,
                record.CancellationRequested,
                record.Completion,
                record.Reconciliation,
                record.ReasonCode);
            work[record.Submission.Id] = updated;
            return Task.FromResult(new ProcessDistributionMutationResult(ProcessDistributionDisposition.Applied, updated));
        }
    }

    /// <inheritdoc />
    public Task<ProcessDistributionMutationResult> CompleteAsync(
        OperationContext context,
        ProcessWorkCompletion completion)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(completion);
        context.ThrowIfCancellationRequested();

        lock (gate)
        {
            var id = completion.Claim.Submission.Id;
            if (!work.TryGetValue(id, out var record))
                return Task.FromResult(new ProcessDistributionMutationResult(ProcessDistributionDisposition.NotFound));
            if (record.Completion is { } prior)
            {
                return Task.FromResult(new ProcessDistributionMutationResult(
                    string.Equals(prior.Fingerprint, completion.Fingerprint, StringComparison.Ordinal)
                        ? ProcessDistributionDisposition.Replayed
                        : ProcessDistributionDisposition.IdentityConflict,
                    record));
            }
            if (completion.EffectEvidence == ProcessWorkEffectEvidence.Ambiguous)
                return Task.FromResult(new ProcessDistributionMutationResult(ProcessDistributionDisposition.InvalidState, record));

            var validation = ValidateCurrentClaim(record, completion.Claim, context.UtcNow, completion.ObservedAtUtc);
            if (validation != ProcessDistributionDisposition.Applied)
            {
                if (validation == ProcessDistributionDisposition.LeaseExpired)
                    record = ExpireClaim(record, context.UtcNow);
                return Task.FromResult(new ProcessDistributionMutationResult(validation, record));
            }

            var status = completion.Outcome switch
            {
                ProcessWorkCompletionOutcome.Succeeded => ProcessWorkStatus.Succeeded,
                ProcessWorkCompletionOutcome.Failed => ProcessWorkStatus.Failed,
                ProcessWorkCompletionOutcome.Cancelled => ProcessWorkStatus.Cancelled,
                _ => throw new InvalidOperationException("Unsupported validated completion outcome.")
            };
            var updated = Replace(
                record,
                status,
                completion.ObservedAtUtc,
                checked(record.Revision + 1),
                record.AttemptCount,
                record.HighestFence,
                record.AvailableAtUtc,
                claim: null,
                cancellationRequested: false,
                completion,
                record.Reconciliation,
                completion.FailureCode);
            work[id] = updated;
            return Task.FromResult(new ProcessDistributionMutationResult(ProcessDistributionDisposition.Applied, updated));
        }
    }

    /// <inheritdoc />
    public Task<ProcessDistributionMutationResult> ReleaseAsync(
        OperationContext context,
        ProcessWorkRelease release)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(release);
        context.ThrowIfCancellationRequested();

        lock (gate)
        {
            var id = release.Claim.Submission.Id;
            if (!work.TryGetValue(id, out var record))
                return Task.FromResult(new ProcessDistributionMutationResult(ProcessDistributionDisposition.NotFound));
            if (record.LastRelease is { } prior
                && prior.Claim.Request == release.Claim.Request
                && prior.Claim.Dispatch == release.Claim.Dispatch)
            {
                return Task.FromResult(new ProcessDistributionMutationResult(
                    string.Equals(prior.Fingerprint, release.Fingerprint, StringComparison.Ordinal)
                        ? ProcessDistributionDisposition.Replayed
                        : ProcessDistributionDisposition.IdentityConflict,
                    record));
            }
            var validation = ValidateCurrentClaim(record, release.Claim, context.UtcNow, release.ObservedAtUtc);
            if (validation != ProcessDistributionDisposition.Applied)
            {
                if (validation == ProcessDistributionDisposition.LeaseExpired)
                    record = ExpireClaim(record, context.UtcNow);
                return Task.FromResult(new ProcessDistributionMutationResult(validation, record));
            }

            var policy = pools[record.Submission.Requirements.Pool].Definition.Policy;
            var exhausted = record.AttemptCount >= policy.MaximumAttempts;
            ProcessWorkStatus status;
            ProcessWorkClaim? retainedClaim;
            DateTimeOffset availableAt;
            string reason;
            switch (release.Disposition)
            {
                case ProcessWorkReleaseDisposition.Retry when exhausted:
                    status = ProcessWorkStatus.Poisoned;
                    retainedClaim = null;
                    availableAt = record.AvailableAtUtc;
                    reason = AttemptsExhaustedReason;
                    break;
                case ProcessWorkReleaseDisposition.Retry:
                    status = ProcessWorkStatus.Queued;
                    retainedClaim = null;
                    availableAt = release.NotBeforeUtc ?? release.ObservedAtUtc;
                    reason = release.ReasonCode;
                    break;
                case ProcessWorkReleaseDisposition.Reconcile:
                    status = ProcessWorkStatus.ReconciliationRequired;
                    retainedClaim = record.Claim;
                    availableAt = record.AvailableAtUtc;
                    reason = release.ReasonCode;
                    break;
                case ProcessWorkReleaseDisposition.TerminalFailure:
                    status = ProcessWorkStatus.Failed;
                    retainedClaim = null;
                    availableAt = record.AvailableAtUtc;
                    reason = release.ReasonCode;
                    break;
                default:
                    throw new InvalidOperationException("Unsupported validated release disposition.");
            }

            var updated = Replace(
                record,
                status,
                release.ObservedAtUtc,
                checked(record.Revision + 1),
                record.AttemptCount,
                record.HighestFence,
                availableAt,
                retainedClaim,
                cancellationRequested: false,
                completion: null,
                record.Reconciliation,
                reason,
                lastRelease: release);
            work[id] = updated;
            return Task.FromResult(new ProcessDistributionMutationResult(ProcessDistributionDisposition.Applied, updated));
        }
    }

    /// <inheritdoc />
    public Task<ProcessDistributionMutationResult> ReconcileAsync(
        OperationContext context,
        ProcessWorkReconciliation reconciliation)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(reconciliation);
        context.ThrowIfCancellationRequested();

        lock (gate)
        {
            if (!work.TryGetValue(reconciliation.Work, out var record))
                return Task.FromResult(new ProcessDistributionMutationResult(ProcessDistributionDisposition.NotFound));
            if (record.Reconciliation == reconciliation)
                return Task.FromResult(new ProcessDistributionMutationResult(ProcessDistributionDisposition.Replayed, record));
            if (record.Status != ProcessWorkStatus.ReconciliationRequired || record.Claim is null)
                return Task.FromResult(new ProcessDistributionMutationResult(ProcessDistributionDisposition.InvalidState, record));
            if (record.Claim.Fence != reconciliation.Fence)
                return Task.FromResult(new ProcessDistributionMutationResult(ProcessDistributionDisposition.StaleFence, record));
            if (reconciliation.ObservedAtUtc < record.UpdatedAtUtc)
                throw new ArgumentException("Reconciliation cannot predate retained ambiguous evidence.", nameof(reconciliation));

            var policy = pools[record.Submission.Requirements.Pool].Definition.Policy;
            var status = reconciliation.Outcome switch
            {
                ProcessWorkReconciliationOutcome.Redispatch when record.AttemptCount >= policy.MaximumAttempts
                    => ProcessWorkStatus.Poisoned,
                ProcessWorkReconciliationOutcome.Redispatch => ProcessWorkStatus.Queued,
                ProcessWorkReconciliationOutcome.Succeeded => ProcessWorkStatus.Succeeded,
                ProcessWorkReconciliationOutcome.Failed => ProcessWorkStatus.Failed,
                ProcessWorkReconciliationOutcome.Cancelled => ProcessWorkStatus.Cancelled,
                _ => throw new InvalidOperationException("Unsupported validated reconciliation outcome.")
            };
            var reason = reconciliation.Outcome switch
            {
                ProcessWorkReconciliationOutcome.Redispatch when status == ProcessWorkStatus.Poisoned
                    => AttemptsExhaustedReason,
                ProcessWorkReconciliationOutcome.Redispatch => null,
                _ => reconciliation.FailureCode
            };
            var updated = Replace(
                record,
                status,
                reconciliation.ObservedAtUtc,
                checked(record.Revision + 1),
                record.AttemptCount,
                record.HighestFence,
                status == ProcessWorkStatus.Queued ? reconciliation.ObservedAtUtc : record.AvailableAtUtc,
                claim: null,
                cancellationRequested: false,
                completion: null,
                reconciliation,
                reason);
            work[record.Submission.Id] = updated;
            return Task.FromResult(new ProcessDistributionMutationResult(ProcessDistributionDisposition.Applied, updated));
        }
    }

    /// <inheritdoc />
    public Task<ProcessDistributionMutationResult> RequestCancellationAsync(
        OperationContext context,
        ProcessWorkId workId,
        string reasonCode,
        DateTimeOffset observedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(context);
        ProcessDistributionRequirements.Require(workId.Value, nameof(workId));
        reasonCode = Guard.RequireNotNullOrWhiteSpace(reasonCode);
        ProcessDistributionRequirements.RequireUtc(observedAtUtc, nameof(observedAtUtc));
        context.ThrowIfCancellationRequested();

        lock (gate)
        {
            if (!work.TryGetValue(workId, out var record))
                return Task.FromResult(new ProcessDistributionMutationResult(ProcessDistributionDisposition.NotFound));
            if (observedAtUtc < record.UpdatedAtUtc)
                throw new ArgumentException("Cancellation cannot predate retained work evidence.", nameof(observedAtUtc));
            if (record.Status == ProcessWorkStatus.Cancelled)
            {
                return Task.FromResult(new ProcessDistributionMutationResult(
                    string.Equals(record.ReasonCode, reasonCode, StringComparison.Ordinal)
                        ? ProcessDistributionDisposition.Replayed
                        : ProcessDistributionDisposition.IdentityConflict,
                    record));
            }

            ProcessWorkRecord updated;
            switch (record.Status)
            {
                case ProcessWorkStatus.Queued:
                    updated = Replace(
                        record,
                        ProcessWorkStatus.Cancelled,
                        observedAtUtc,
                        checked(record.Revision + 1),
                        record.AttemptCount,
                        record.HighestFence,
                        record.AvailableAtUtc,
                        claim: null,
                        cancellationRequested: false,
                        completion: null,
                        record.Reconciliation,
                        reasonCode);
                    break;
                case ProcessWorkStatus.Claimed:
                    if (record.CancellationRequested && string.Equals(record.ReasonCode, reasonCode, StringComparison.Ordinal))
                        return Task.FromResult(new ProcessDistributionMutationResult(ProcessDistributionDisposition.Replayed, record));
                    updated = Replace(
                        record,
                        record.Status,
                        observedAtUtc,
                        checked(record.Revision + 1),
                        record.AttemptCount,
                        record.HighestFence,
                        record.AvailableAtUtc,
                        record.Claim,
                        cancellationRequested: true,
                        completion: null,
                        record.Reconciliation,
                        reasonCode);
                    break;
                default:
                    return Task.FromResult(new ProcessDistributionMutationResult(ProcessDistributionDisposition.InvalidState, record));
            }

            work[workId] = updated;
            return Task.FromResult(new ProcessDistributionMutationResult(ProcessDistributionDisposition.Applied, updated));
        }
    }

    /// <inheritdoc />
    public Task<ProcessWorkRecord?> InspectWorkAsync(OperationContext context, ProcessWorkId workId)
    {
        ArgumentNullException.ThrowIfNull(context);
        ProcessDistributionRequirements.Require(workId.Value, nameof(workId));
        context.ThrowIfCancellationRequested();
        lock (gate)
        {
            RecoverExpiredClaims(context.UtcNow);
            if (work.TryGetValue(workId, out var existing))
                ExpireDeadlines(existing.Submission.Requirements.Pool, context.UtcNow);
            return Task.FromResult(work.GetValueOrDefault(workId));
        }
    }

    /// <inheritdoc />
    public Task<ProcessWorkerPoolSnapshot?> InspectPoolAsync(
        OperationContext context,
        ProcessWorkerPoolId pool,
        DateTimeOffset observedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(context);
        ProcessDistributionRequirements.Require(pool.Value, nameof(pool));
        ProcessDistributionRequirements.RequireUtc(observedAtUtc, nameof(observedAtUtc));
        context.ThrowIfCancellationRequested();
        lock (gate)
        {
            RecoverExpiredClaims(context.UtcNow);
            if (!pools.TryGetValue(pool, out var state))
                return Task.FromResult<ProcessWorkerPoolSnapshot?>(null);
            ExpireDeadlines(pool, observedAtUtc);

            var poolWork = work.Values.Where(item => item.Submission.Requirements.Pool == pool).ToArray();
            var poolWorkers = workers.Values.Where(item => item.Offer.Pools.Contains(pool)).ToArray();
            var claims = poolWork.Where(static item => item.Status == ProcessWorkStatus.Claimed).ToArray();
            var reserved = SumCapacity(claims.SelectMany(static item => item.Submission.Requirements.Capacity));
            var snapshot = new ProcessWorkerPoolSnapshot(
                state.Definition,
                queued: poolWork.Count(static item => item.Status == ProcessWorkStatus.Queued),
                claimed: claims.Length,
                reconciliationRequired: poolWork.Count(static item => item.Status == ProcessWorkStatus.ReconciliationRequired),
                terminal: poolWork.Count(static item => item.Status is ProcessWorkStatus.Succeeded
                    or ProcessWorkStatus.Failed or ProcessWorkStatus.Cancelled or ProcessWorkStatus.Poisoned),
                healthyWorkers: poolWorkers.Count(item => item.IsLive(observedAtUtc)
                    && item.Health == ProcessWorkerHealth.Healthy && !item.Draining),
                drainingWorkers: poolWorkers.Count(item => item.IsLive(observedAtUtc) && item.Draining),
                expiredWorkers: poolWorkers.Count(item => !item.IsLive(observedAtUtc)),
                reservedCapacity: reserved,
                observedAtUtc);
            return Task.FromResult<ProcessWorkerPoolSnapshot?>(snapshot);
        }
    }

    void RecoverExpiredClaims(DateTimeOffset observedAtUtc)
    {
        foreach (var record in work.Values.Where(static item => item.Status == ProcessWorkStatus.Claimed).ToArray())
        {
            var claim = record.Claim!;
            var workerLive = workers.TryGetValue(claim.Worker, out var worker) && worker.IsLive(observedAtUtc);
            if (!claim.IsLive(observedAtUtc) || !workerLive)
                ExpireClaim(record, observedAtUtc);
        }
    }

    ProcessWorkRecord ExpireClaim(ProcessWorkRecord record, DateTimeOffset observedAtUtc)
    {
        if (record.Status != ProcessWorkStatus.Claimed || record.Claim is null)
            return record;

        var policy = pools[record.Submission.Requirements.Pool].Definition.Policy;
        var reconcile = record.Submission.Requirements.RecoveryMode
            == ProcessWorkRecoveryMode.ReconcileBeforeRedispatch;
        var exhausted = record.AttemptCount >= policy.MaximumAttempts;
        var status = reconcile
            ? ProcessWorkStatus.ReconciliationRequired
            : exhausted
                ? ProcessWorkStatus.Poisoned
                : ProcessWorkStatus.Queued;
        var updated = Replace(
            record,
            status,
            observedAtUtc,
            checked(record.Revision + 1),
            record.AttemptCount,
            record.HighestFence,
            status == ProcessWorkStatus.Queued ? observedAtUtc : record.AvailableAtUtc,
            status == ProcessWorkStatus.ReconciliationRequired ? record.Claim : null,
            cancellationRequested: false,
            completion: null,
            record.Reconciliation,
            exhausted && !reconcile ? AttemptsExhaustedReason : ClaimExpiredReason);
        work[record.Submission.Id] = updated;
        return updated;
    }

    void ExpireDeadlines(ProcessWorkerPoolId pool, DateTimeOffset observedAtUtc)
    {
        foreach (var record in work.Values.Where(item =>
                     item.Submission.Requirements.Pool == pool
                     && item.Status == ProcessWorkStatus.Queued
                     && item.Submission.Requirements.DeadlineUtc < observedAtUtc).ToArray())
        {
            work[record.Submission.Id] = Replace(
                record,
                ProcessWorkStatus.Failed,
                observedAtUtc,
                checked(record.Revision + 1),
                record.AttemptCount,
                record.HighestFence,
                record.AvailableAtUtc,
                claim: null,
                cancellationRequested: false,
                completion: null,
                record.Reconciliation,
                DeadlineExpiredReason);
        }
    }

    ProcessDistributionDisposition ValidateCurrentClaim(
        ProcessWorkRecord record,
        ProcessWorkClaim candidate,
        DateTimeOffset physicalNow,
        DateTimeOffset observedAtUtc)
    {
        if (record.HighestFence > candidate.Fence.Ordinal)
            return ProcessDistributionDisposition.StaleFence;
        if (record.Status != ProcessWorkStatus.Claimed || record.Claim is null)
            return ProcessDistributionDisposition.InvalidState;
        var current = record.Claim;
        if (current.Fence != candidate.Fence
            || current.Worker != candidate.Worker
            || current.Dispatch != candidate.Dispatch
            || current.Attempt != candidate.Attempt)
        {
            return ProcessDistributionDisposition.StaleFence;
        }
        if (!current.IsLive(physicalNow) || !current.IsLive(observedAtUtc))
            return ProcessDistributionDisposition.LeaseExpired;
        return ProcessDistributionDisposition.Applied;
    }

    List<ProcessWorkRecord> LiveClaims(ProcessWorkerPoolId pool, DateTimeOffset observedAtUtc) =>
        work.Values.Where(item => item.Submission.Requirements.Pool == pool
            && item.Status == ProcessWorkStatus.Claimed
            && item.Claim!.IsLive(observedAtUtc)).ToList();

    static bool WorkerEligible(ProcessWorkerOffer offer, ProcessWorkSubmission submission)
    {
        var requirements = submission.Requirements;
        return offer.SupportedProcessIrVersions.Contains(submission.Reference.ProcessIrVersion)
            && offer.SupportedWorkKinds.Contains(submission.Reference.Kind)
            && offer.SupportedEffectGuarantees.Contains(submission.Requirements.EffectGuarantee)
            && requirements.Capabilities.All(required => offer.Capabilities.Contains(required, StringComparer.Ordinal))
            && (requirements.Affinity is null
                || offer.Affinities.Contains(requirements.Affinity, StringComparer.Ordinal));
    }

    static bool FitsCapacity(
        ProcessWorkerPoolPolicy policy,
        ProcessWorkerOffer offer,
        IReadOnlyCollection<ProcessWorkRecord> poolClaims,
        IReadOnlyCollection<ProcessWorkRecord> workerClaims,
        ProcessWorkRequirements requirements)
    {
        if (requirements.CapacityDomain is { } domain)
        {
            var limit = policy.CapacityDomains.Single(item => string.Equals(item.Identity, domain, StringComparison.Ordinal));
            if (poolClaims.Count(item => string.Equals(
                    item.Submission.Requirements.CapacityDomain,
                    domain,
                    StringComparison.Ordinal)) >= limit.MaximumParallelism)
            {
                return false;
            }
        }

        return FitsResourceCapacity(
                policy.Capacity,
                poolClaims,
                requirements.Capacity,
                missingCapacityIsUnbounded: true)
            && FitsResourceCapacity(
                offer.Capacity,
                workerClaims,
                requirements.Capacity,
                missingCapacityIsUnbounded: false);
    }

    static bool FitsResourceCapacity(
        ImmutableArray<ProcessResourceQuantity> available,
        IEnumerable<ProcessWorkRecord> claims,
        ImmutableArray<ProcessResourceQuantity> required,
        bool missingCapacityIsUnbounded)
    {
        if (required.IsEmpty)
            return true;
        if (available.IsEmpty)
            return missingCapacityIsUnbounded;
        var byResource = available.ToDictionary(static item => item.Resource, StringComparer.Ordinal);
        var used = claims.SelectMany(static item => item.Submission.Requirements.Capacity)
            .GroupBy(static item => item.Resource, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.Sum(item => item.Units), StringComparer.Ordinal);
        foreach (var requirement in required)
        {
            if (!byResource.TryGetValue(requirement.Resource, out var capacity)
                || !string.Equals(capacity.Unit, requirement.Unit, StringComparison.Ordinal)
                || used.GetValueOrDefault(requirement.Resource) > capacity.Units - requirement.Units)
            {
                return false;
            }
        }
        return true;
    }

    static bool RequirementsFitPoolPolicy(
        ProcessWorkRequirements requirements,
        ProcessWorkerPoolPolicy policy)
    {
        if (requirements.CapacityDomain is { } domain
            && !policy.CapacityDomains.Any(item => string.Equals(item.Identity, domain, StringComparison.Ordinal)))
        {
            return false;
        }
        var poolCapacity = policy.Capacity.ToDictionary(static item => item.Resource, StringComparer.Ordinal);
        return requirements.Capacity.All(requirement =>
            !poolCapacity.TryGetValue(requirement.Resource, out var capacity)
            || string.Equals(capacity.Unit, requirement.Unit, StringComparison.Ordinal));
    }

    static bool ExceedsHardPoolCapacity(
        ProcessWorkRequirements requirements,
        ProcessWorkerPoolPolicy policy)
    {
        var capacity = policy.Capacity.ToDictionary(static item => item.Resource, StringComparer.Ordinal);
        return requirements.Capacity.Any(requirement =>
            capacity.TryGetValue(requirement.Resource, out var limit)
            && requirement.Units > limit.Units);
    }

    static ImmutableArray<ProcessResourceQuantity> SumCapacity(IEnumerable<ProcessResourceQuantity> capacity)
    {
        Dictionary<string, (long Units, string Unit)> totals = new(StringComparer.Ordinal);
        foreach (var item in capacity)
        {
            if (totals.TryGetValue(item.Resource, out var current))
                totals[item.Resource] = (checked(current.Units + item.Units), item.Unit);
            else
                totals.Add(item.Resource, (item.Units, item.Unit));
        }
        return [.. totals.OrderBy(static item => item.Key, StringComparer.Ordinal)
            .Select(static item => new ProcessResourceQuantity(item.Key, item.Value.Units, item.Value.Unit))];
    }

    static ProcessWorkRecord Replace(
        ProcessWorkRecord current,
        ProcessWorkStatus status,
        DateTimeOffset updatedAtUtc,
        long revision,
        int attemptCount,
        long highestFence,
        DateTimeOffset availableAtUtc,
        ProcessWorkClaim? claim,
        bool cancellationRequested,
        ProcessWorkCompletion? completion,
        ProcessWorkReconciliation? reconciliation,
        string? reasonCode,
        ProcessWorkRelease? lastRelease = null) => new(
        current.Submission,
        status,
        revision,
        attemptCount,
        highestFence,
        availableAtUtc,
        claim,
        cancellationRequested,
        completion,
        reconciliation,
        reasonCode,
        updatedAtUtc,
        lastRelease);

    TimeSpan WorkerLeaseDuration(ProcessWorkerOffer offer) =>
        offer.Pools.Select(pool => pools[pool].Definition.Policy.WorkerLeaseDuration).Min();

    static DateTimeOffset Add(DateTimeOffset value, TimeSpan duration)
    {
        try
        {
            return value.Add(duration);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new ArgumentOutOfRangeException(
                nameof(duration),
                duration,
                "Lease expiry exceeds the supported UTC range.");
        }
    }

    static bool SamePool(ProcessWorkerPoolDefinition left, ProcessWorkerPoolDefinition right)
    {
        var leftPolicy = left.Policy;
        var rightPolicy = right.Policy;
        return left.Id == right.Id
            && left.SchemaVersion == right.SchemaVersion
            && leftPolicy.MaximumConcurrentClaims == rightPolicy.MaximumConcurrentClaims
            && leftPolicy.MaximumAttempts == rightPolicy.MaximumAttempts
            && leftPolicy.WorkerLeaseDuration == rightPolicy.WorkerLeaseDuration
            && leftPolicy.ClaimLeaseDuration == rightPolicy.ClaimLeaseDuration
            && leftPolicy.OversizedWorkBehavior == rightPolicy.OversizedWorkBehavior
            && leftPolicy.Evidence == rightPolicy.Evidence
            && leftPolicy.Capacity.SequenceEqual(rightPolicy.Capacity)
            && leftPolicy.CapacityDomains.SequenceEqual(rightPolicy.CapacityDomains);
    }

    static bool SameOffer(ProcessWorkerOffer left, ProcessWorkerOffer right) =>
        left.Worker == right.Worker
        && left.SchemaVersion == right.SchemaVersion
        && left.MaximumConcurrentClaims == right.MaximumConcurrentClaims
        && left.Pools.SequenceEqual(right.Pools)
        && left.SupportedProcessIrVersions.SequenceEqual(right.SupportedProcessIrVersions)
        && left.SupportedWorkKinds.SequenceEqual(right.SupportedWorkKinds)
        && left.SupportedEffectGuarantees.SequenceEqual(right.SupportedEffectGuarantees)
        && left.Capabilities.SequenceEqual(right.Capabilities, StringComparer.Ordinal)
        && left.Capacity.SequenceEqual(right.Capacity)
        && left.Affinities.SequenceEqual(right.Affinities, StringComparer.Ordinal);

    sealed class PoolState
    {
        readonly Dictionary<string, long> fairness = new(StringComparer.Ordinal);
        long nextFairnessOrdinal;

        internal PoolState(ProcessWorkerPoolDefinition definition)
        {
            Definition = definition;
        }

        internal PoolState(ProcessDistributionPoolLedger ledger)
        {
            Definition = ledger.Definition;
            nextFairnessOrdinal = ledger.NextFairnessOrdinal;
            foreach (var position in ledger.Fairness)
                fairness.Add(position.Key, position.Ordinal);
        }

        internal ProcessWorkerPoolDefinition Definition { get; }

        internal long FairnessOrdinal(string? key) => fairness.GetValueOrDefault(key ?? string.Empty);

        internal void RecordFairness(string? key) =>
            fairness[key ?? string.Empty] = checked(++nextFairnessOrdinal);

        internal ProcessDistributionPoolLedger Capture() => new(
            Definition,
            nextFairnessOrdinal,
            [.. fairness.OrderBy(static item => item.Key, StringComparer.Ordinal)
                .Select(static item => new ProcessDistributionFairnessPosition(item.Key, item.Value))]);
    }
}
