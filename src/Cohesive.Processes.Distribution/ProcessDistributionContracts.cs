using System.Collections.Immutable;

namespace Cohesive.Processes.Distribution;

/// <summary>Physical guarantees and limits advertised by a distribution-ledger implementation.</summary>
public sealed record ProcessDistributionStoreCapabilities
{
    /// <summary>Creates distribution-store capability evidence.</summary>
    /// <param name="isDurable">Whether admitted ledger state survives runtime-process loss.</param>
    /// <param name="supportsAtomicClaim">Whether one work item can be reserved by at most one live claim.</param>
    /// <param name="supportsCompareAndSwap">Whether mutations reject stale physical revisions.</param>
    /// <param name="supportsWorkerLeases">Whether worker-incarnation liveness is persisted and expires.</param>
    /// <param name="supportsClaimRenewal">Whether work ownership can be renewed without changing its fence.</param>
    /// <param name="supportsMonotonicFencing">Whether reclaimed work receives a strictly greater fence.</param>
    /// <param name="supportsRunnableDiscovery">Whether consumers can find eligible work without direct addressing.</param>
    /// <param name="supportsCapacityReservations">Whether live claims atomically count against capacity.</param>
    /// <param name="supportsPoisonWork">Whether terminal poison evidence is durable.</param>
    /// <param name="supportsAtomicProcessCommit">
    /// Whether canonical Process state and newly runnable work can share one atomic provider boundary.
    /// </param>
    /// <param name="maximumAuthorityStateBytes">Optional maximum serialized state size of one atomic authority.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="maximumAuthorityStateBytes"/> is present and not positive.
    /// </exception>
    public ProcessDistributionStoreCapabilities(
        bool isDurable,
        bool supportsAtomicClaim,
        bool supportsCompareAndSwap,
        bool supportsWorkerLeases,
        bool supportsClaimRenewal,
        bool supportsMonotonicFencing,
        bool supportsRunnableDiscovery,
        bool supportsCapacityReservations,
        bool supportsPoisonWork,
        bool supportsAtomicProcessCommit,
        long? maximumAuthorityStateBytes = null)
    {
        if (maximumAuthorityStateBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumAuthorityStateBytes),
                maximumAuthorityStateBytes,
                "A present authority-state limit must be positive.");
        }

        IsDurable = isDurable;
        SupportsAtomicClaim = supportsAtomicClaim;
        SupportsCompareAndSwap = supportsCompareAndSwap;
        SupportsWorkerLeases = supportsWorkerLeases;
        SupportsClaimRenewal = supportsClaimRenewal;
        SupportsMonotonicFencing = supportsMonotonicFencing;
        SupportsRunnableDiscovery = supportsRunnableDiscovery;
        SupportsCapacityReservations = supportsCapacityReservations;
        SupportsPoisonWork = supportsPoisonWork;
        SupportsAtomicProcessCommit = supportsAtomicProcessCommit;
        MaximumAuthorityStateBytes = maximumAuthorityStateBytes;
    }

    /// <summary>Whether admitted ledger state survives runtime-process loss.</summary>
    public bool IsDurable { get; }

    /// <summary>Whether one work item can be reserved by at most one live claim.</summary>
    public bool SupportsAtomicClaim { get; }

    /// <summary>Whether mutations reject stale physical revisions.</summary>
    public bool SupportsCompareAndSwap { get; }

    /// <summary>Whether worker-incarnation liveness is persisted and expires.</summary>
    public bool SupportsWorkerLeases { get; }

    /// <summary>Whether work ownership can be renewed without changing its fence.</summary>
    public bool SupportsClaimRenewal { get; }

    /// <summary>Whether reclaimed work receives a strictly greater fence.</summary>
    public bool SupportsMonotonicFencing { get; }

    /// <summary>Whether consumers can find eligible work without direct addressing.</summary>
    public bool SupportsRunnableDiscovery { get; }

    /// <summary>Whether live claims atomically count against pool, domain, and worker capacity.</summary>
    public bool SupportsCapacityReservations { get; }

    /// <summary>Whether terminal poison evidence is durable.</summary>
    public bool SupportsPoisonWork { get; }

    /// <summary>Whether canonical Process state and newly runnable work can share one atomic provider boundary.</summary>
    public bool SupportsAtomicProcessCommit { get; }

    /// <summary>Optional maximum serialized state size of one atomic distribution authority.</summary>
    public long? MaximumAuthorityStateBytes { get; }
}

/// <summary>Observable disposition of one distribution-ledger operation.</summary>
public enum ProcessDistributionDisposition
{
    /// <summary>No disposition was supplied; invalid in a result.</summary>
    Unspecified = 0,

    /// <summary>The requested mutation committed.</summary>
    Applied = 1,

    /// <summary>Exact prior evidence was deterministically reused.</summary>
    Replayed = 2,

    /// <summary>The requested pool, worker, or work record was not found.</summary>
    NotFound = 3,

    /// <summary>A stable identity was reused for different canonical content.</summary>
    IdentityConflict = 4,

    /// <summary>The current lifecycle state does not admit the operation.</summary>
    InvalidState = 5,

    /// <summary>The worker is absent, expired, unhealthy, draining, or outside the requested pool.</summary>
    WorkerUnavailable = 6,

    /// <summary>No currently eligible work can be claimed.</summary>
    NoEligibleWork = 7,

    /// <summary>The supplied claim fence has been superseded.</summary>
    StaleFence = 8,

    /// <summary>The supplied worker or work lease expired before the mutation boundary.</summary>
    LeaseExpired = 9,

    /// <summary>A hard capacity or compatibility boundary prevents admission.</summary>
    Incompatible = 10
}

/// <summary>Result of a pool, worker, work, completion, release, or reconciliation mutation.</summary>
/// <param name="Disposition">Observable ledger disposition.</param>
/// <param name="Work">Current work snapshot when one is addressed.</param>
/// <param name="Worker">Current worker snapshot when one is addressed.</param>
public sealed record ProcessDistributionMutationResult(
    ProcessDistributionDisposition Disposition,
    ProcessWorkRecord? Work = null,
    ProcessWorkerRegistration? Worker = null);

/// <summary>Result of one competing-consumer claim request.</summary>
/// <param name="Disposition">Applied claim or reason no claim was created.</param>
/// <param name="Claim">Exact leased and fenced claim when applied.</param>
public sealed record ProcessWorkClaimResult(
    ProcessDistributionDisposition Disposition,
    ProcessWorkClaim? Claim = null);

/// <summary>Safe aggregate pool-health and queue-capacity projection.</summary>
public sealed record ProcessWorkerPoolSnapshot
{
    /// <summary>Creates a safe pool snapshot.</summary>
    /// <param name="pool">Exact logical pool definition.</param>
    /// <param name="queued">Number of durably queued work units.</param>
    /// <param name="claimed">Number of currently claimed work units.</param>
    /// <param name="reconciliationRequired">Number of work units awaiting reconciliation.</param>
    /// <param name="terminal">Number of terminal work units.</param>
    /// <param name="healthyWorkers">Number of live healthy non-draining workers.</param>
    /// <param name="drainingWorkers">Number of live draining workers.</param>
    /// <param name="expiredWorkers">Number of expired worker incarnations retained for evidence.</param>
    /// <param name="reservedCapacity">Aggregate resource capacity reserved by current claims.</param>
    /// <param name="observedAtUtc">UTC projection observation.</param>
    /// <exception cref="ArgumentNullException"><paramref name="pool"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A count is negative.</exception>
    /// <exception cref="ArgumentException">Capacity is malformed or the observation is not UTC.</exception>
    public ProcessWorkerPoolSnapshot(
        ProcessWorkerPoolDefinition pool,
        int queued,
        int claimed,
        int reconciliationRequired,
        int terminal,
        int healthyWorkers,
        int drainingWorkers,
        int expiredWorkers,
        ImmutableArray<ProcessResourceQuantity> reservedCapacity,
        DateTimeOffset observedAtUtc)
    {
        if (queued < 0 || claimed < 0 || reconciliationRequired < 0 || terminal < 0
            || healthyWorkers < 0 || drainingWorkers < 0 || expiredWorkers < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(queued), "Pool snapshot counts cannot be negative.");
        }
        ProcessDistributionRequirements.RequireUtc(observedAtUtc, nameof(observedAtUtc));

        Pool = pool ?? throw new ArgumentNullException(nameof(pool));
        Queued = queued;
        Claimed = claimed;
        ReconciliationRequired = reconciliationRequired;
        Terminal = terminal;
        HealthyWorkers = healthyWorkers;
        DrainingWorkers = drainingWorkers;
        ExpiredWorkers = expiredWorkers;
        ReservedCapacity = ProcessDistributionRequirements.NormalizeCapacity(reservedCapacity, nameof(reservedCapacity));
        ObservedAtUtc = observedAtUtc;
    }

    /// <summary>Exact logical pool definition.</summary>
    public ProcessWorkerPoolDefinition Pool { get; }

    /// <summary>Number of durably queued work units.</summary>
    public int Queued { get; }

    /// <summary>Number of currently claimed work units.</summary>
    public int Claimed { get; }

    /// <summary>Number of work units awaiting reconciliation.</summary>
    public int ReconciliationRequired { get; }

    /// <summary>Number of terminal work units.</summary>
    public int Terminal { get; }

    /// <summary>Number of live healthy non-draining workers.</summary>
    public int HealthyWorkers { get; }

    /// <summary>Number of live draining workers.</summary>
    public int DrainingWorkers { get; }

    /// <summary>Number of expired worker incarnations retained for evidence.</summary>
    public int ExpiredWorkers { get; }

    /// <summary>Aggregate resource capacity reserved by current claims.</summary>
    public ImmutableArray<ProcessResourceQuantity> ReservedCapacity { get; }

    /// <summary>UTC projection observation.</summary>
    public DateTimeOffset ObservedAtUtc { get; }
}

/// <summary>Portable durable ledger and competing-consumer port for Process work distribution.</summary>
/// <remarks>
/// Implementations may use a queue, database ledger, actor/grain placement, pod/job creation, or another strategy,
/// but must preserve the declared capabilities. The interface owns placement and claims only; it cannot advance a
/// canonical Process continuation.
/// </remarks>
public interface IProcessDistributionStore
{
    /// <summary>Physical guarantees and limits of the store.</summary>
    ProcessDistributionStoreCapabilities Capabilities { get; }

    /// <summary>Creates or exactly replays one logical worker-pool definition.</summary>
    /// <param name="context">Explicit cancellation, clock, identity, and tracing context.</param>
    /// <param name="pool">Exact effective pool policy and provenance.</param>
    /// <returns>Applied, replayed, or identity-conflict evidence.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException">The operation is cancelled before its atomic boundary.</exception>
    Task<ProcessDistributionMutationResult> EnsurePoolAsync(
        OperationContext context,
        ProcessWorkerPoolDefinition pool);

    /// <summary>Durably admits or exactly replays one canonical work intent.</summary>
    /// <param name="context">Explicit cancellation, clock, identity, and tracing context.</param>
    /// <param name="submission">Exact canonical work submission.</param>
    /// <returns>Applied, replayed, missing-pool, incompatible, or identity-conflict evidence.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException">The operation is cancelled before its atomic boundary.</exception>
    Task<ProcessDistributionMutationResult> SubmitAsync(
        OperationContext context,
        ProcessWorkSubmission submission);

    /// <summary>Registers or exactly replays one immutable worker-incarnation offer.</summary>
    /// <param name="context">Explicit cancellation, clock, identity, and tracing context.</param>
    /// <param name="offer">Exact worker offer.</param>
    /// <param name="observedAtUtc">UTC registration observation.</param>
    /// <returns>Applied, replayed, incompatible, or identity-conflict evidence.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="observedAtUtc"/> is not UTC.</exception>
    /// <exception cref="OperationCanceledException">The operation is cancelled before its atomic boundary.</exception>
    Task<ProcessDistributionMutationResult> RegisterWorkerAsync(
        OperationContext context,
        ProcessWorkerOffer offer,
        DateTimeOffset observedAtUtc);

    /// <summary>Renews one exact live worker incarnation and advertises current health.</summary>
    /// <param name="context">Explicit cancellation, clock, identity, and tracing context.</param>
    /// <param name="worker">Exact worker incarnation.</param>
    /// <param name="health">Current health evidence.</param>
    /// <param name="observedAtUtc">UTC renewal observation.</param>
    /// <returns>Applied, replayed, missing-worker, or expired-worker evidence.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An identity is default or time is not UTC.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="health"/> is unsupported.</exception>
    /// <exception cref="OperationCanceledException">The operation is cancelled before its atomic boundary.</exception>
    Task<ProcessDistributionMutationResult> RenewWorkerAsync(
        OperationContext context,
        ProcessWorkerIncarnationId worker,
        ProcessWorkerHealth health,
        DateTimeOffset observedAtUtc);

    /// <summary>Enables or disables draining for one live worker incarnation.</summary>
    /// <param name="context">Explicit cancellation, clock, identity, and tracing context.</param>
    /// <param name="worker">Exact worker incarnation.</param>
    /// <param name="draining">Whether new claims must stop.</param>
    /// <param name="observedAtUtc">UTC state-change observation.</param>
    /// <returns>Applied, replayed, missing-worker, or expired-worker evidence.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An identity is default or time is not UTC.</exception>
    /// <exception cref="OperationCanceledException">The operation is cancelled before its atomic boundary.</exception>
    Task<ProcessDistributionMutationResult> SetWorkerDrainingAsync(
        OperationContext context,
        ProcessWorkerIncarnationId worker,
        bool draining,
        DateTimeOffset observedAtUtc);

    /// <summary>Atomically discovers and claims one eligible work unit for a live worker.</summary>
    /// <param name="context">Explicit cancellation, clock, identity, and tracing context.</param>
    /// <param name="pool">Pool from which to claim.</param>
    /// <param name="worker">Exact worker incarnation.</param>
    /// <param name="request">Stable request identity retained across exact provider retries.</param>
    /// <param name="observedAtUtc">UTC claim observation.</param>
    /// <returns>An applied or exactly replayed live claim, or an observable reason no claim was created.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An identity is default or time is not UTC.</exception>
    /// <exception cref="OperationCanceledException">The operation is cancelled before its atomic boundary.</exception>
    Task<ProcessWorkClaimResult> ClaimAsync(
        OperationContext context,
        ProcessWorkerPoolId pool,
        ProcessWorkerIncarnationId worker,
        ProcessWorkClaimRequestId request,
        DateTimeOffset observedAtUtc);

    /// <summary>Renews one exact current work claim without changing its fence.</summary>
    /// <param name="context">Explicit cancellation, clock, identity, and tracing context.</param>
    /// <param name="claim">Exact current claim.</param>
    /// <param name="observedAtUtc">UTC renewal observation.</param>
    /// <returns>Applied, replayed, stale-fence, expired-lease, or missing-work evidence.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="observedAtUtc"/> is not UTC.</exception>
    /// <exception cref="OperationCanceledException">The operation is cancelled before its atomic boundary.</exception>
    Task<ProcessDistributionMutationResult> RenewClaimAsync(
        OperationContext context,
        ProcessWorkClaim claim,
        DateTimeOffset observedAtUtc);

    /// <summary>Commits terminal evidence under the exact current live claim and fence.</summary>
    /// <param name="context">Explicit cancellation, clock, identity, and tracing context.</param>
    /// <param name="completion">Exact terminal completion intent.</param>
    /// <returns>Applied, replayed, conflict, stale-fence, expired-lease, or missing-work evidence.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException">The operation is cancelled before its atomic boundary.</exception>
    Task<ProcessDistributionMutationResult> CompleteAsync(
        OperationContext context,
        ProcessWorkCompletion completion);

    /// <summary>Releases a live claim for retry, reconciliation, or terminal failure.</summary>
    /// <param name="context">Explicit cancellation, clock, identity, and tracing context.</param>
    /// <param name="release">Exact release intent.</param>
    /// <returns>Applied, stale-fence, expired-lease, invalid-state, or missing-work evidence.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException">The operation is cancelled before its atomic boundary.</exception>
    Task<ProcessDistributionMutationResult> ReleaseAsync(
        OperationContext context,
        ProcessWorkRelease release);

    /// <summary>Resolves one reconciliation-required work item from explicit durable evidence.</summary>
    /// <param name="context">Explicit cancellation, clock, identity, and tracing context.</param>
    /// <param name="reconciliation">Exact reconciliation decision and evidence reference.</param>
    /// <returns>Applied, replayed, stale-fence, invalid-state, or missing-work evidence.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="OperationCanceledException">The operation is cancelled before its atomic boundary.</exception>
    Task<ProcessDistributionMutationResult> ReconcileAsync(
        OperationContext context,
        ProcessWorkReconciliation reconciliation);

    /// <summary>Requests cancellation of queued or currently claimed work.</summary>
    /// <param name="context">Explicit cancellation, clock, identity, and tracing context.</param>
    /// <param name="work">Logical work identity.</param>
    /// <param name="reasonCode">Stable attributable cancellation reason.</param>
    /// <param name="observedAtUtc">UTC cancellation observation.</param>
    /// <returns>Applied, replayed, invalid-state, or missing-work evidence.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An identity or reason is default, or time is not UTC.</exception>
    /// <exception cref="OperationCanceledException">The operation is cancelled before its atomic boundary.</exception>
    Task<ProcessDistributionMutationResult> RequestCancellationAsync(
        OperationContext context,
        ProcessWorkId work,
        string reasonCode,
        DateTimeOffset observedAtUtc);

    /// <summary>Loads the current work-ledger snapshot.</summary>
    /// <param name="context">Explicit cancellation, clock, identity, and tracing context.</param>
    /// <param name="work">Logical work identity.</param>
    /// <returns>The current immutable snapshot, or <see langword="null"/> when absent.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="work"/> is default.</exception>
    /// <exception cref="OperationCanceledException">The operation is cancelled before reading.</exception>
    Task<ProcessWorkRecord?> InspectWorkAsync(OperationContext context, ProcessWorkId work);

    /// <summary>Projects safe queue, worker, and capacity health for one pool.</summary>
    /// <param name="context">Explicit cancellation, clock, identity, and tracing context.</param>
    /// <param name="pool">Logical worker-pool identity.</param>
    /// <param name="observedAtUtc">UTC projection observation.</param>
    /// <returns>A safe pool snapshot, or <see langword="null"/> when the pool is absent.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An identity is default or time is not UTC.</exception>
    /// <exception cref="OperationCanceledException">The operation is cancelled before reading.</exception>
    Task<ProcessWorkerPoolSnapshot?> InspectPoolAsync(
        OperationContext context,
        ProcessWorkerPoolId pool,
        DateTimeOffset observedAtUtc);
}
