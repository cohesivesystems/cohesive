namespace Cohesive.Adapters.Postgres;

/// <summary>Explicit operating policy for PostgreSQL logical-replication materialization reads and settlement.</summary>
/// <remarks>
/// <see cref="MaximumTransactionChanges"/> and <see cref="MaximumTransactionBytes"/> are hard local admission
/// limits for both the raw provider transaction and its final canonical projection. A key-changing update can project
/// to two deliveries, and canonical JSON can exceed raw pgoutput bytes. The limits are advertised as transaction-safety
/// capability bounds, not change-page limits. A transaction exceeding either limit fails before application progress
/// or slot settlement can advance. Request item and byte bounds remain soft targets for a transaction-aligned source.
/// </remarks>
public sealed record PostgresLogicalReplicationSourcePolicy
{
    /// <summary>Conventional maximum mutations retained for one indivisible transaction.</summary>
    public const int DefaultMaximumTransactionChanges = 100_000;

    /// <summary>Conventional maximum provider bytes retained for one indivisible transaction.</summary>
    public const long DefaultMaximumTransactionBytes = 64L * 1024 * 1024;

    /// <summary>Conventional maximum committed transactions combined into one change read.</summary>
    public const int DefaultMaximumTransactionsPerRead = 64;

    /// <summary>Conventional transient reconnect attempts within one source operation.</summary>
    public const int DefaultMaximumReconnectAttempts = 3;

    /// <summary>Conventional maximum encoded source-position characters.</summary>
    public const int DefaultMaximumPositionCharacters = 16 * 1024;

    /// <summary>Conventional delay before a transient replication reconnect.</summary>
    public static TimeSpan DefaultReconnectDelay { get; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Conventional maximum wait without observable replication progress during one bounded read.</summary>
    public static TimeSpan DefaultReadInactivityTimeout { get; } = TimeSpan.FromSeconds(60);

    /// <summary>Conventional timeout for verifying that slot settlement became visible.</summary>
    public static TimeSpan DefaultSettlementConfirmationTimeout { get; } = TimeSpan.FromSeconds(30);

    /// <summary>Conventional polling interval while confirming slot settlement.</summary>
    public static TimeSpan DefaultSettlementConfirmationPollInterval { get; } = TimeSpan.FromMilliseconds(100);

    /// <summary>Conventional retained-WAL warning threshold.</summary>
    public const long DefaultRetentionDangerBytes = 8L * 1024 * 1024 * 1024;

    /// <summary>Conventional unsettled-duration warning threshold.</summary>
    public static TimeSpan DefaultRetentionDangerTime { get; } = TimeSpan.FromMinutes(15);

    /// <summary>Conventional bounded logical-replication policy.</summary>
    public static PostgresLogicalReplicationSourcePolicy Default { get; } = new();

    /// <summary>Creates explicit logical-replication operating policy.</summary>
    /// <param name="maximumTransactionChanges">
    /// Hard maximum raw provider mutations and final canonical deliveries for one indivisible committed transaction.
    /// </param>
    /// <param name="maximumTransactionBytes">
    /// Hard maximum raw provider spool bytes and final canonical JSON bytes for one committed transaction.
    /// </param>
    /// <param name="maximumTransactionsPerRead">
    /// Maximum complete committed transactions combined into one bounded change read.
    /// </param>
    /// <param name="maximumReconnectAttempts">
    /// Non-negative transient reconnect attempts performed within one source operation after its initial attempt.
    /// </param>
    /// <param name="reconnectDelay">Non-negative delay before each transient reconnect.</param>
    /// <param name="readInactivityTimeout">
    /// Positive maximum duration a bounded read may wait without observable replication progress.
    /// </param>
    /// <param name="settlementConfirmationTimeout">
    /// Positive maximum duration spent verifying that provider slot progress reached a requested settlement.
    /// </param>
    /// <param name="settlementConfirmationPollInterval">
    /// Positive interval between settlement-verification reads, no greater than the confirmation timeout.
    /// </param>
    /// <param name="retentionDangerBytes">
    /// Positive retained-WAL byte count at which health becomes retention-dangerous.
    /// </param>
    /// <param name="retentionDangerTime">
    /// Positive unsettled or inactive duration at which health becomes retention-dangerous.
    /// </param>
    /// <param name="maximumPositionCharacters">Positive maximum encoded authenticated source-position characters.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A transaction, read, retention, or position bound is not positive or exceeds its supported CLR allocation
    /// boundary; reconnect attempts or delay are negative; a confirmation duration is not positive; or the polling
    /// interval exceeds the confirmation timeout.
    /// </exception>
    public PostgresLogicalReplicationSourcePolicy(
        int maximumTransactionChanges = DefaultMaximumTransactionChanges,
        long maximumTransactionBytes = DefaultMaximumTransactionBytes,
        int maximumTransactionsPerRead = DefaultMaximumTransactionsPerRead,
        int maximumReconnectAttempts = DefaultMaximumReconnectAttempts,
        TimeSpan? reconnectDelay = null,
        TimeSpan? readInactivityTimeout = null,
        TimeSpan? settlementConfirmationTimeout = null,
        TimeSpan? settlementConfirmationPollInterval = null,
        long retentionDangerBytes = DefaultRetentionDangerBytes,
        TimeSpan? retentionDangerTime = null,
        int maximumPositionCharacters = DefaultMaximumPositionCharacters)
    {
        if (maximumTransactionChanges <= 0 || maximumTransactionChanges >= Array.MaxLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumTransactionChanges),
                maximumTransactionChanges,
                $"A logical-replication transaction must retain from 1 through {Array.MaxLength - 1} changes.");
        }
        if (maximumTransactionBytes <= 0 || maximumTransactionBytes > Array.MaxLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumTransactionBytes),
                maximumTransactionBytes,
                $"A logical-replication transaction byte spool must be from 1 through {Array.MaxLength} bytes.");
        }
        if (maximumTransactionsPerRead <= 0 || maximumTransactionsPerRead >= Array.MaxLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumTransactionsPerRead),
                maximumTransactionsPerRead,
                $"A logical-replication read must retain from 1 through {Array.MaxLength - 1} transactions.");
        }
        if (maximumReconnectAttempts < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumReconnectAttempts),
                maximumReconnectAttempts,
                "Logical-replication reconnect attempts cannot be negative.");
        }

        var effectiveReconnectDelay = reconnectDelay ?? DefaultReconnectDelay;
        if (effectiveReconnectDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(reconnectDelay),
                effectiveReconnectDelay,
                "A logical-replication reconnect delay cannot be negative.");
        }

        var effectiveReadInactivityTimeout = readInactivityTimeout ?? DefaultReadInactivityTimeout;
        if (effectiveReadInactivityTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(readInactivityTimeout),
                effectiveReadInactivityTimeout,
                "A logical-replication read inactivity timeout must be positive.");
        }

        var effectiveConfirmationTimeout =
            settlementConfirmationTimeout ?? DefaultSettlementConfirmationTimeout;
        if (effectiveConfirmationTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(settlementConfirmationTimeout),
                effectiveConfirmationTimeout,
                "A logical-replication settlement confirmation timeout must be positive.");
        }

        var effectiveConfirmationPollInterval =
            settlementConfirmationPollInterval ?? DefaultSettlementConfirmationPollInterval;
        if (effectiveConfirmationPollInterval <= TimeSpan.Zero
            || effectiveConfirmationPollInterval > effectiveConfirmationTimeout)
        {
            throw new ArgumentOutOfRangeException(
                nameof(settlementConfirmationPollInterval),
                effectiveConfirmationPollInterval,
                "A logical-replication settlement confirmation poll interval must be positive and no greater than its timeout.");
        }
        if (retentionDangerBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(retentionDangerBytes),
                retentionDangerBytes,
                "A logical-replication retained-WAL warning threshold must be positive.");
        }

        var effectiveRetentionDangerTime = retentionDangerTime ?? DefaultRetentionDangerTime;
        if (effectiveRetentionDangerTime <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(retentionDangerTime),
                effectiveRetentionDangerTime,
                "A logical-replication retention-duration warning threshold must be positive.");
        }
        if (maximumPositionCharacters <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumPositionCharacters),
                maximumPositionCharacters,
                "A logical-replication source-position character bound must be positive.");
        }

        MaximumTransactionChanges = maximumTransactionChanges;
        MaximumTransactionBytes = maximumTransactionBytes;
        MaximumTransactionsPerRead = maximumTransactionsPerRead;
        MaximumReconnectAttempts = maximumReconnectAttempts;
        ReconnectDelay = effectiveReconnectDelay;
        ReadInactivityTimeout = effectiveReadInactivityTimeout;
        SettlementConfirmationTimeout = effectiveConfirmationTimeout;
        SettlementConfirmationPollInterval = effectiveConfirmationPollInterval;
        RetentionDangerBytes = retentionDangerBytes;
        RetentionDangerTime = effectiveRetentionDangerTime;
        MaximumPositionCharacters = maximumPositionCharacters;
    }

    /// <summary>Hard maximum raw mutations and final canonical deliveries for one indivisible transaction.</summary>
    public int MaximumTransactionChanges { get; }

    /// <summary>Hard maximum raw provider spool bytes and final canonical JSON bytes for one transaction.</summary>
    public long MaximumTransactionBytes { get; }

    /// <summary>Maximum complete committed transactions combined into one change read.</summary>
    public int MaximumTransactionsPerRead { get; }

    /// <summary>Maximum transient reconnect attempts after the initial operation attempt.</summary>
    public int MaximumReconnectAttempts { get; }

    /// <summary>Delay before each transient reconnect.</summary>
    public TimeSpan ReconnectDelay { get; }

    /// <summary>Maximum wait without observable replication progress during one bounded read.</summary>
    public TimeSpan ReadInactivityTimeout { get; }

    /// <summary>Maximum duration spent confirming provider slot settlement.</summary>
    public TimeSpan SettlementConfirmationTimeout { get; }

    /// <summary>Interval between provider settlement-confirmation reads.</summary>
    public TimeSpan SettlementConfirmationPollInterval { get; }

    /// <summary>Retained-WAL byte count at which health becomes retention-dangerous.</summary>
    public long RetentionDangerBytes { get; }

    /// <summary>Unsettled or inactive duration at which health becomes retention-dangerous.</summary>
    public TimeSpan RetentionDangerTime { get; }

    /// <summary>Maximum encoded authenticated source-position characters.</summary>
    public int MaximumPositionCharacters { get; }
}
