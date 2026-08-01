using System.Collections.Immutable;
using System.Globalization;

namespace Cohesive.Adapters.Postgres;

internal enum PostgresLogicalReplicationPositionKind
{
    WalCut = 0,
    TransactionEnd = 1
}

internal readonly record struct PostgresLogicalReplicationWalPosition(ulong Value)
    : IComparable<PostgresLogicalReplicationWalPosition>
{
    public int CompareTo(PostgresLogicalReplicationWalPosition other) => Value.CompareTo(other.Value);

    public static bool operator <(
        PostgresLogicalReplicationWalPosition left,
        PostgresLogicalReplicationWalPosition right) => left.Value < right.Value;

    public static bool operator <=(
        PostgresLogicalReplicationWalPosition left,
        PostgresLogicalReplicationWalPosition right) => left.Value <= right.Value;

    public static bool operator >(
        PostgresLogicalReplicationWalPosition left,
        PostgresLogicalReplicationWalPosition right) => left.Value > right.Value;

    public static bool operator >=(
        PostgresLogicalReplicationWalPosition left,
        PostgresLogicalReplicationWalPosition right) => left.Value >= right.Value;

    public override string ToString() => string.Concat(
        (Value >> 32).ToString("X", CultureInfo.InvariantCulture),
        "/",
        (Value & uint.MaxValue).ToString("X", CultureInfo.InvariantCulture));

    internal static bool TryParse(
        string? value,
        out PostgresLogicalReplicationWalPosition position)
    {
        position = default;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var separator = value.AsSpan().IndexOf('/');
        if (separator <= 0
            || separator == value.Length - 1
            || value.AsSpan(separator + 1).IndexOf('/') >= 0
            || !uint.TryParse(
                value.AsSpan(0, separator),
                NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture,
                out var upper)
            || !uint.TryParse(
                value.AsSpan(separator + 1),
                NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture,
                out var lower))
        {
            return false;
        }

        position = new(((ulong)upper << 32) | lower);
        return string.Equals(position.ToString(), value, StringComparison.Ordinal);
    }

    internal long DistanceFrom(PostgresLogicalReplicationWalPosition earlier)
    {
        if (this < earlier)
            throw new ArgumentOutOfRangeException(nameof(earlier), "A WAL distance cannot move backward.");
        var distance = Value - earlier.Value;
        return distance > long.MaxValue ? long.MaxValue : checked((long)distance);
    }
}

internal enum PostgresLogicalReplicationWalState
{
    Reserved = 0,
    Extended = 1,
    Unreserved = 2,
    Lost = 3,
    Unknown = 4
}

internal sealed record PostgresLogicalReplicationColumn(
    string Name,
    uint DataTypeId,
    int TypeModifier,
    bool IsReplicaIdentity,
    uint? DomainBaseDataTypeId = null)
{
    internal uint EffectiveDataTypeId => DomainBaseDataTypeId ?? DataTypeId;
}

internal sealed record PostgresLogicalReplicationDeployment(
    string SystemIdentifier,
    uint Timeline,
    string DatabaseName,
    string PublicationName,
    bool PublishesInserts,
    bool PublishesUpdates,
    bool PublishesDeletes,
    bool PublishesTruncates,
    bool PublishesViaPartitionRoot,
    bool IncludesTable,
    bool HasRowFilter,
    bool IncludesAllTableColumns,
    string SchemaName,
    string TableName,
    PostgresLogicalReplicationReplicaIdentityBinding ReplicaIdentity,
    ImmutableArray<PostgresLogicalReplicationColumn> Columns,
    string SlotName,
    string OutputPlugin,
    bool IsLogicalSlot,
    bool IsTemporarySlot,
    bool IsTwoPhaseSlot,
    bool IsActive,
    PostgresLogicalReplicationWalPosition RestartPosition,
    PostgresLogicalReplicationWalPosition ConfirmedFlushPosition,
    PostgresLogicalReplicationWalPosition CurrentWalPosition,
    PostgresLogicalReplicationWalState WalState,
    long? SafeWalBytes,
    DateTimeOffset? InactiveSinceUtc,
    string? InvalidationReason);

internal enum PostgresLogicalReplicationCellKind
{
    Value = 0,
    Null = 1,
    UnchangedToast = 2
}

internal sealed record PostgresLogicalReplicationCell(
    string ColumnName,
    PostgresLogicalReplicationCellKind Kind,
    object? Value,
    int EncodedBytes);

internal sealed record PostgresLogicalReplicationRow(
    ImmutableArray<PostgresLogicalReplicationCell> Cells);

internal enum PostgresLogicalReplicationMutationKind
{
    Insert = 0,
    Update = 1,
    Delete = 2
}

internal sealed record PostgresLogicalReplicationMutation(
    int Ordinal,
    PostgresLogicalReplicationMutationKind Kind,
    PostgresLogicalReplicationReplicaIdentityKind ReplicaIdentity,
    PostgresLogicalReplicationRow? OldRow,
    PostgresLogicalReplicationRow? NewRow);

internal sealed record PostgresLogicalReplicationTransaction(
    uint TransactionId,
    PostgresLogicalReplicationWalPosition FinalPosition,
    PostgresLogicalReplicationWalPosition CommitPosition,
    PostgresLogicalReplicationWalPosition EndPosition,
    DateTimeOffset CommittedAtUtc,
    ImmutableArray<PostgresLogicalReplicationMutation> Mutations,
    long RetainedBytes);

internal sealed record PostgresLogicalReplicationReadBatch(
    ImmutableArray<PostgresLogicalReplicationTransaction> Transactions,
    PostgresLogicalReplicationWalPosition ScannedThrough,
    bool ReachedUpperBoundary);

internal enum PostgresLogicalReplicationFeedbackDisposition
{
    Confirmed = 0,
    AlreadyConfirmed = 1
}

internal sealed record PostgresLogicalReplicationFeedback(
    PostgresLogicalReplicationFeedbackDisposition Disposition,
    PostgresLogicalReplicationWalPosition PriorConfirmedPosition,
    PostgresLogicalReplicationWalPosition ConfirmedPosition);

internal sealed class PostgresLogicalReplicationProtocolException : Exception
{
    internal PostgresLogicalReplicationProtocolException(
        PostgresLogicalReplicationFailureKind failureKind,
        bool isTransient,
        string evidenceReference,
        Exception? innerException = null)
        : base("The PostgreSQL logical-replication provider operation failed.", innerException)
    {
        if (!Enum.IsDefined(failureKind))
        {
            throw new ArgumentOutOfRangeException(
                nameof(failureKind),
                failureKind,
                "Unsupported PostgreSQL logical-replication protocol failure kind.");
        }

        FailureKind = failureKind;
        IsTransient = isTransient;
        EvidenceReference = Guard.RequireNotNullOrWhiteSpace(evidenceReference);
    }

    internal PostgresLogicalReplicationFailureKind FailureKind { get; }

    internal bool IsTransient { get; }

    internal string EvidenceReference { get; }
}

internal interface IPostgresLogicalReplicationSnapshotImport : IAsyncDisposable
{
    PostgresNpgsqlCommandExecutor ExecuteCommand { get; }
}

internal interface IPostgresLogicalReplicationSnapshotExport : IAsyncDisposable
{
    string SnapshotName { get; }

    PostgresLogicalReplicationWalPosition ConsistentPosition { get; }

    ValueTask<IPostgresLogicalReplicationSnapshotImport> ImportAsync(
        CancellationToken cancellationToken);
}

internal interface IPostgresLogicalReplicationProtocol
{
    ValueTask<PostgresLogicalReplicationDeployment> InspectAsync(
        CancellationToken cancellationToken);

    ValueTask<PostgresLogicalReplicationReadBatch> ReadAsync(
        PostgresLogicalReplicationWalPosition afterPosition,
        PostgresLogicalReplicationWalPosition upperBoundary,
        int maximumTransactions,
        int preferredMaximumMutations,
        long preferredMaximumBytes,
        int maximumTransactionMutations,
        long maximumTransactionBytes,
        TimeSpan inactivityTimeout,
        CancellationToken cancellationToken);

    ValueTask<PostgresLogicalReplicationFeedback> SettleAsync(
        PostgresLogicalReplicationWalPosition position,
        TimeSpan confirmationTimeout,
        TimeSpan confirmationPollInterval,
        CancellationToken cancellationToken);

    ValueTask<IPostgresLogicalReplicationSnapshotExport> CreateSnapshotExportAsync(
        CancellationToken cancellationToken);
}
