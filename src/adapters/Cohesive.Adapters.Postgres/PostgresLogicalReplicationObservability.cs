using Cohesive.Storage.Materialization;

namespace Cohesive.Adapters.Postgres;

/// <summary>Stable recovery classification for a PostgreSQL logical-replication source failure.</summary>
public enum PostgresLogicalReplicationFailureKind
{
    /// <summary>A transient connection or server failure may succeed after reconnecting from the same position.</summary>
    Transient = 0,

    /// <summary>The requested WAL position is no longer retained or is ahead of an admissible provider boundary.</summary>
    PositionUnavailable = 1,

    /// <summary>The configured logical replication slot is absent, invalidated, lost, or owned incompatibly.</summary>
    SlotUnavailable = 2,

    /// <summary>The physical slot was recreated without matching the position's operator-owned generation.</summary>
    SlotGenerationMismatch = 3,

    /// <summary>The runtime publication differs from the exact bound publication and table coverage.</summary>
    PublicationMismatch = 4,

    /// <summary>The runtime table replica identity differs from the expected binding.</summary>
    ReplicaIdentityMismatch = 5,

    /// <summary>A row mutation lacks the exact identity or before/after image required for canonical delivery.</summary>
    ChangeEvidenceUnavailable = 6,

    /// <summary>One indivisible provider transaction exceeds a hard local spool-admission limit.</summary>
    TransactionLimitExceeded = 7,

    /// <summary>Provider slot progress did not confirm an explicitly requested settlement in time.</summary>
    SettlementUnconfirmed = 8,

    /// <summary>The pgoutput stream violated its expected transaction, relation, tuple, or ordering contract.</summary>
    ProtocolViolation = 9,

    /// <summary>The operation cannot succeed without changing configuration, authorization, or semantic input.</summary>
    Terminal = 10
}

/// <summary>Provider-neutral health classification for a dedicated PostgreSQL logical replication slot.</summary>
public enum PostgresLogicalReplicationHealthState
{
    /// <summary>The slot is available and retained WAL is below configured danger thresholds.</summary>
    Healthy = 0,

    /// <summary>The slot is available but has produced no observable activity within the configured interval.</summary>
    Inactive = 1,

    /// <summary>Retained WAL bytes or unsettled time crossed a configured danger threshold.</summary>
    RetentionDanger = 2,

    /// <summary>The slot is absent, invalidated, or has irrecoverably lost required WAL.</summary>
    SlotLost = 3,

    /// <summary>The adapter could not obtain authoritative slot-health evidence.</summary>
    Unavailable = 4
}

/// <summary>Kind of bounded PostgreSQL logical-replication source operation observed by the adapter.</summary>
public enum PostgresLogicalReplicationOperationKind
{
    /// <summary>Captures the current durable WAL boundary.</summary>
    CaptureCurrentPosition = 0,

    /// <summary>Reads complete committed transactions after one durable boundary.</summary>
    ChangeRead = 1,

    /// <summary>Advances and confirms the dedicated provider slot after application checkpointing.</summary>
    SourceSettlement = 2,

    /// <summary>Creates a consistent-point and exported-snapshot bootstrap handoff.</summary>
    SnapshotHandoff = 3,

    /// <summary>Inspects slot lag, retention, activity, and availability.</summary>
    HealthInspection = 4
}

/// <summary>Operational disposition of one PostgreSQL logical-replication source operation.</summary>
public enum PostgresLogicalReplicationOperationDisposition
{
    /// <summary>The requested operation completed at its current provider boundary.</summary>
    Complete = 0,

    /// <summary>One or more complete transactions were returned and more provider input remains.</summary>
    Partial = 1,

    /// <summary>The source advanced through filtered provider input without emitting a canonical change.</summary>
    Progressed = 2,

    /// <summary>The change source reached its currently visible WAL boundary.</summary>
    CaughtUp = 3,

    /// <summary>A transient provider failure will be retried from the same durable source position.</summary>
    Retrying = 4,

    /// <summary>The provider acknowledged a newly requested source settlement.</summary>
    Acknowledged = 5,

    /// <summary>The provider had already advanced through the exact requested settlement position.</summary>
    Replayed = 6,

    /// <summary>The operation failed without returning a successful semantic result.</summary>
    Failed = 7,

    /// <summary>The caller canceled the operation.</summary>
    Canceled = 8
}

/// <summary>Provider-neutral lag, retained-WAL, and activity evidence for one exact logical-replication scope.</summary>
public sealed record PostgresLogicalReplicationHealthObservation
{
    /// <summary>Creates one attributable slot-health observation.</summary>
    /// <param name="state">Provider-neutral slot health classification.</param>
    /// <param name="scope">Exact logical-replication materialization source scope.</param>
    /// <param name="observedAtUtc">UTC time at which provider state was observed.</param>
    /// <param name="estimatedPendingWalBytes">Optional non-negative WAL bytes between settled and current positions.</param>
    /// <param name="retainedWalBytes">Optional non-negative WAL bytes retained on behalf of the slot.</param>
    /// <param name="remainingSafeWalBytes">
    /// Optional non-negative provider estimate before the slot risks losing required WAL.
    /// </param>
    /// <param name="estimatedLag">Optional non-negative wall-clock lag behind the latest observed commit.</param>
    /// <param name="inactivity">Optional non-negative duration since the last observed slot or stream activity.</param>
    /// <param name="evidenceReference">Non-sensitive attributable adapter evidence.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="scope"/> or <paramref name="evidenceReference"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="observedAtUtc"/> is not UTC or evidence is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="state"/> is unsupported, or a byte or duration measurement is negative.
    /// </exception>
    public PostgresLogicalReplicationHealthObservation(
        PostgresLogicalReplicationHealthState state,
        MaterializationSourceScope scope,
        DateTimeOffset observedAtUtc,
        long? estimatedPendingWalBytes,
        long? retainedWalBytes,
        long? remainingSafeWalBytes,
        TimeSpan? estimatedLag,
        TimeSpan? inactivity,
        string evidenceReference)
    {
        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(
                nameof(state),
                state,
                "Unsupported PostgreSQL logical-replication health state.");
        }
        if (observedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "A PostgreSQL logical-replication health observation time must be UTC.",
                nameof(observedAtUtc));
        }

        RequireNonNegative(estimatedPendingWalBytes, nameof(estimatedPendingWalBytes));
        RequireNonNegative(retainedWalBytes, nameof(retainedWalBytes));
        RequireNonNegative(remainingSafeWalBytes, nameof(remainingSafeWalBytes));
        RequireNonNegative(estimatedLag, nameof(estimatedLag));
        RequireNonNegative(inactivity, nameof(inactivity));

        State = state;
        Scope = Guard.RequireNotNull(scope);
        ObservedAtUtc = observedAtUtc;
        EstimatedPendingWalBytes = estimatedPendingWalBytes;
        RetainedWalBytes = retainedWalBytes;
        RemainingSafeWalBytes = remainingSafeWalBytes;
        EstimatedLag = estimatedLag;
        Inactivity = inactivity;
        EvidenceReference = Guard.RequireNotNullOrWhiteSpace(evidenceReference);
    }

    /// <summary>Provider-neutral slot health classification.</summary>
    public PostgresLogicalReplicationHealthState State { get; }

    /// <summary>Exact logical-replication materialization source scope.</summary>
    public MaterializationSourceScope Scope { get; }

    /// <summary>UTC time at which provider state was observed.</summary>
    public DateTimeOffset ObservedAtUtc { get; }

    /// <summary>Estimated WAL bytes between settled and current positions, when available.</summary>
    public long? EstimatedPendingWalBytes { get; }

    /// <summary>WAL bytes retained on behalf of the slot, when available.</summary>
    public long? RetainedWalBytes { get; }

    /// <summary>Provider-estimated bytes before required WAL is at risk, when available.</summary>
    public long? RemainingSafeWalBytes { get; }

    /// <summary>Estimated wall-clock lag behind the latest observed commit, when available.</summary>
    public TimeSpan? EstimatedLag { get; }

    /// <summary>Duration since the last observed slot or stream activity, when available.</summary>
    public TimeSpan? Inactivity { get; }

    /// <summary>Non-sensitive attributable adapter evidence.</summary>
    public string EvidenceReference { get; }

    static void RequireNonNegative(long? value, string parameterName)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                "A PostgreSQL logical-replication byte measurement cannot be negative.");
        }
    }

    static void RequireNonNegative(TimeSpan? value, string parameterName)
    {
        if (value < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                "A PostgreSQL logical-replication duration measurement cannot be negative.");
        }
    }
}

/// <summary>Typed evidence for one bounded PostgreSQL logical-replication source operation.</summary>
public sealed record PostgresLogicalReplicationOperationObservation
{
    /// <summary>Creates one attributable logical-replication operation observation.</summary>
    /// <param name="operation">Observed source operation.</param>
    /// <param name="disposition">Operational result.</param>
    /// <param name="scope">Exact logical-replication materialization source scope.</param>
    /// <param name="startedAtUtc">UTC operation start.</param>
    /// <param name="completedAtUtc">UTC operation completion.</param>
    /// <param name="attempt">Positive one-based provider attempt number.</param>
    /// <param name="transactionCount">Non-negative complete provider transaction count.</param>
    /// <param name="changeCount">Non-negative canonical change-delivery count.</param>
    /// <param name="canonicalByteCount">Non-negative canonical change bytes.</param>
    /// <param name="evidenceReference">Non-sensitive attributable adapter evidence.</param>
    /// <param name="failureKind">Failure classification for failed or retrying operations.</param>
    /// <param name="retryAfter">Non-negative retry delay present only for a retrying operation.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="scope"/> or <paramref name="evidenceReference"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Times are not chronological UTC values, evidence is empty, or failure/retry evidence conflicts with the
    /// disposition.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// An enum is unsupported; attempt is not positive; a transaction, change, byte count, or retry delay is
    /// negative.
    /// </exception>
    public PostgresLogicalReplicationOperationObservation(
        PostgresLogicalReplicationOperationKind operation,
        PostgresLogicalReplicationOperationDisposition disposition,
        MaterializationSourceScope scope,
        DateTimeOffset startedAtUtc,
        DateTimeOffset completedAtUtc,
        int attempt,
        long transactionCount,
        long changeCount,
        long canonicalByteCount,
        string evidenceReference,
        PostgresLogicalReplicationFailureKind? failureKind = null,
        TimeSpan? retryAfter = null)
    {
        if (!Enum.IsDefined(operation))
        {
            throw new ArgumentOutOfRangeException(
                nameof(operation),
                operation,
                "Unsupported PostgreSQL logical-replication source operation.");
        }
        if (!Enum.IsDefined(disposition))
        {
            throw new ArgumentOutOfRangeException(
                nameof(disposition),
                disposition,
                "Unsupported PostgreSQL logical-replication operation disposition.");
        }
        if (failureKind.HasValue && !Enum.IsDefined(failureKind.Value))
        {
            throw new ArgumentOutOfRangeException(
                nameof(failureKind),
                failureKind,
                "Unsupported PostgreSQL logical-replication failure kind.");
        }
        if (startedAtUtc.Offset != TimeSpan.Zero
            || completedAtUtc.Offset != TimeSpan.Zero
            || completedAtUtc < startedAtUtc)
        {
            throw new ArgumentException(
                "PostgreSQL logical-replication operation times must be chronological UTC values.",
                nameof(startedAtUtc));
        }
        if (attempt <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(attempt),
                attempt,
                "A PostgreSQL logical-replication provider attempt must be positive.");
        }
        if (transactionCount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(transactionCount),
                transactionCount,
                "An observed transaction count cannot be negative.");
        }
        if (changeCount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(changeCount),
                changeCount,
                "An observed change count cannot be negative.");
        }
        if (canonicalByteCount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(canonicalByteCount),
                canonicalByteCount,
                "An observed canonical byte count cannot be negative.");
        }
        if (retryAfter < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(retryAfter),
                retryAfter,
                "A PostgreSQL logical-replication retry delay cannot be negative.");
        }

        var failedOrRetrying = disposition is PostgresLogicalReplicationOperationDisposition.Failed
            or PostgresLogicalReplicationOperationDisposition.Retrying;
        if (failedOrRetrying != failureKind.HasValue)
        {
            throw new ArgumentException(
                "Failed and retrying logical-replication observations require a failure kind; successful and canceled observations must omit it.",
                nameof(failureKind));
        }
        if ((disposition == PostgresLogicalReplicationOperationDisposition.Retrying) != retryAfter.HasValue)
        {
            throw new ArgumentException(
                "Only a retrying logical-replication observation carries a retry delay.",
                nameof(retryAfter));
        }

        Operation = operation;
        Disposition = disposition;
        Scope = Guard.RequireNotNull(scope);
        StartedAtUtc = startedAtUtc;
        CompletedAtUtc = completedAtUtc;
        Attempt = attempt;
        TransactionCount = transactionCount;
        ChangeCount = changeCount;
        CanonicalByteCount = canonicalByteCount;
        EvidenceReference = Guard.RequireNotNullOrWhiteSpace(evidenceReference);
        FailureKind = failureKind;
        RetryAfter = retryAfter;
    }

    /// <summary>Observed source operation.</summary>
    public PostgresLogicalReplicationOperationKind Operation { get; }

    /// <summary>Operational result.</summary>
    public PostgresLogicalReplicationOperationDisposition Disposition { get; }

    /// <summary>Exact logical-replication materialization source scope.</summary>
    public MaterializationSourceScope Scope { get; }

    /// <summary>UTC operation start.</summary>
    public DateTimeOffset StartedAtUtc { get; }

    /// <summary>UTC operation completion.</summary>
    public DateTimeOffset CompletedAtUtc { get; }

    /// <summary>Positive one-based provider attempt number.</summary>
    public int Attempt { get; }

    /// <summary>Complete provider transactions represented by the observation.</summary>
    public long TransactionCount { get; }

    /// <summary>Canonical change deliveries represented by the observation.</summary>
    public long ChangeCount { get; }

    /// <summary>Canonical change bytes represented by the observation.</summary>
    public long CanonicalByteCount { get; }

    /// <summary>Non-sensitive attributable adapter evidence.</summary>
    public string EvidenceReference { get; }

    /// <summary>Failure classification for a failed or retrying operation.</summary>
    public PostgresLogicalReplicationFailureKind? FailureKind { get; }

    /// <summary>Delay before the next retry attempt, when the operation is retrying.</summary>
    public TimeSpan? RetryAfter { get; }
}

/// <summary>Explicit sink for typed PostgreSQL logical-replication operation and health observations.</summary>
public interface IPostgresLogicalReplicationObserver
{
    /// <summary>Observes one completed, failed, retrying, or canceled source operation.</summary>
    /// <param name="observation">Typed operation evidence.</param>
    /// <remarks>Implementations SHOULD return promptly, MUST be thread-safe, and MUST NOT throw.</remarks>
    void Observe(PostgresLogicalReplicationOperationObservation observation);

    /// <summary>Observes one provider-neutral slot-health sample.</summary>
    /// <param name="observation">Typed lag, retention, activity, and availability evidence.</param>
    /// <remarks>Implementations SHOULD return promptly, MUST be thread-safe, and MUST NOT throw.</remarks>
    void Observe(PostgresLogicalReplicationHealthObservation observation);
}

/// <summary>Sanitized typed exception from a failed PostgreSQL logical-replication source operation.</summary>
public sealed class PostgresLogicalReplicationException : Exception
{
    /// <summary>Creates a sanitized logical-replication source failure.</summary>
    /// <param name="message">Non-sensitive human-facing failure summary.</param>
    /// <param name="failureKind">Stable retry and recovery classification.</param>
    /// <param name="observation">Failed operation evidence.</param>
    /// <param name="health">Optional contemporaneous provider-neutral slot-health evidence.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="message"/> or <paramref name="observation"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="message"/> is empty, <paramref name="observation"/> is not a failed operation, or
    /// <paramref name="health"/> belongs to another source scope.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="failureKind"/> is unsupported.</exception>
    public PostgresLogicalReplicationException(
        string message,
        PostgresLogicalReplicationFailureKind failureKind,
        PostgresLogicalReplicationOperationObservation observation,
        PostgresLogicalReplicationHealthObservation? health = null)
        : base(Guard.RequireNotNullOrWhiteSpace(message))
    {
        if (!Enum.IsDefined(failureKind))
        {
            throw new ArgumentOutOfRangeException(
                nameof(failureKind),
                failureKind,
                "Unsupported PostgreSQL logical-replication failure kind.");
        }
        Observation = Guard.RequireNotNull(observation);
        if (observation.Disposition != PostgresLogicalReplicationOperationDisposition.Failed)
        {
            throw new ArgumentException(
                "A PostgreSQL logical-replication exception requires failed operation evidence.",
                nameof(observation));
        }
        if (observation.FailureKind != failureKind)
        {
            throw new ArgumentException(
                "A PostgreSQL logical-replication exception and its operation evidence must have the same failure kind.",
                nameof(observation));
        }
        if (health is not null && health.Scope != observation.Scope)
        {
            throw new ArgumentException(
                "A PostgreSQL logical-replication exception health observation must belong to its operation scope.",
                nameof(health));
        }

        FailureKind = failureKind;
        Health = health;
    }

    /// <summary>Stable retry and recovery classification.</summary>
    public PostgresLogicalReplicationFailureKind FailureKind { get; }

    /// <summary>Typed non-sensitive failed-operation evidence.</summary>
    public PostgresLogicalReplicationOperationObservation Observation { get; }

    /// <summary>Optional contemporaneous provider-neutral slot-health evidence.</summary>
    public PostgresLogicalReplicationHealthObservation? Health { get; }
}
