using System.Text.Json;
using System.Text;
using Cohesive.Execution;
using Cohesive.Storage;
using Cohesive.Storage.Commits;
using Microsoft.Data.Sqlite;
using static Cohesive.Adapters.SQLite.SqliteStorageCommitSql;

namespace Cohesive.Adapters.SQLite;

/// <summary>Realizes conditional portable item writes and receipts in one owned SQLite transaction.</summary>
/// <remarks>
/// This adapter owns a dedicated schema; it does not enlist arbitrary repository tables. Construct once and
/// reuse; connections and transactions belong to individual calls. Apply Schema explicitly before use.
/// FULL synchronization is required for the durable receipt profile. Query guards require every relevant
/// writer to advance the guard and the query to follow its guard read on a fresh committed read boundary.
/// </remarks>
public sealed class SqliteStorageCommitExecutor : IStorageCommitExecutor
{
    readonly SqliteDatabase database;
    /// <summary>Configured per-row UTF-8 serialized byte bound, or null for no adapter row limit.</summary>
    public long? MaximumStoredPayloadBytes { get; }
    static readonly JsonSerializerOptions Json = StorageCommitJson.CreateOptions();

    /// <summary>Creates a commit executor without opening or migrating the database.</summary>
    /// <param name="database">Database authority; must use FULL durability.</param>
    /// <param name="maximumStoredPayloadBytes">Optional positive per-row UTF-8 payload limit. Native reads are bounded to twice this value to accommodate UTF-16 databases, then checked as UTF-8 before JSON decoding. Includes receipt rows.</param>
    /// <exception cref="ArgumentOutOfRangeException">The payload limit is not positive.</exception>
    /// <exception cref="ArgumentNullException">The database is null.</exception>
    /// <exception cref="ArgumentException">The database uses a weaker durability profile.</exception>
    public SqliteStorageCommitExecutor(SqliteDatabase database, long? maximumStoredPayloadBytes = null)
    {
        this.database = database ?? throw new ArgumentNullException(nameof(database));
        if (maximumStoredPayloadBytes is <= 0) throw new ArgumentOutOfRangeException(nameof(maximumStoredPayloadBytes));
        MaximumStoredPayloadBytes = maximumStoredPayloadBytes;
        if (database.Options.Durability != SqliteDurability.Full)
            throw new ArgumentException("Atomic durable receipts require SQLite FULL synchronization.", nameof(database));
    }

    /// <summary>Dedicated, versioned item/receipt schema; apply explicitly during installation.</summary>
    public static SqliteSchema Schema { get; } = SqliteStorageCommitSql.Schema;

    /// <inheritdoc />
    public StorageCommitCapabilities Capabilities { get; } = new(Target: null,
        SupportsMultiplePartitions: true, SupportsQueryGuards: true);

    /// <inheritdoc />
    public StorageCommitResult? Validate(StorageCommitIntent intent)
    {
        if (Capabilities.Validate(intent) is { } unsupported) return unsupported;
        if (MaximumStoredPayloadBytes is not { } maximum) return null;
        foreach (var write in intent.Writes)
            if (Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(write.Value, Json)) > maximum) return TooLarge();
        return Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(new StorageCommitReceipt(intent.Reference, intent.Result), Json)) > maximum
            ? TooLarge() : null;
    }

    static StorageCommitResult TooLarge() => StorageCommitResult.Rejected(StorageCommitDisposition.Unsupported,
        "storage.commit.row-payload-limit", "A serialized item or receipt exceeds the configured per-row byte limit.");

    /// <inheritdoc />
    public ValueTask<StorageCommitResult> CommitAsync(OperationContext context, StorageCommitIntent intent)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (Validate(intent) is { } unsupported) return ValueTask.FromResult(unsupported);
        using var connection = database.OpenConnection(context.CancellationToken);
        using var transaction = connection.BeginTransaction(deferred: false);
        using var commands = new SqliteCommandScope(database, connection, transaction);
        var existing = ReadReceipt(commands, intent.ReceiptAddress, context.CancellationToken);
        if (existing is not null) return ValueTask.FromResult(existing.Reconcile(intent.Reference));
        for (var index = 0; index < intent.Writes.Length; index++)
        {
            var write = intent.Writes[index];
            var payload = JsonSerializer.Serialize(write.Value, Json);
            var affected = Write(commands, write.Address, SqliteStorageCommitSql.ItemKind,
                payload, intent.Fingerprint, write.ExpectedToken, context.CancellationToken);
            if (affected != 1)
                return ValueTask.FromResult(StorageCommitResult.PreconditionFailed($"/writes/{index}"));
        }
        var receipt = new StorageCommitReceipt(intent.Reference, intent.Result);
        if (Write(commands, intent.ReceiptAddress, SqliteStorageCommitSql.ReceiptKind,
            JsonSerializer.Serialize(receipt, Json), intent.Fingerprint, expected: null, context.CancellationToken) != 1)
            throw new InvalidOperationException("The operation receipt changed inside an exclusive writer transaction.");
        context.CancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
        return ValueTask.FromResult(StorageCommitResult.Success(receipt, StorageCommitDisposition.Committed));
    }

    /// <inheritdoc />
    public ValueTask<StorageCommitResult> ReconcileAsync(OperationContext context, StorageCommitReference commit)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(commit);
        using var connection = database.OpenConnection(context.CancellationToken);
        using var transaction = connection.BeginTransaction(deferred: true);
        using var commands = new SqliteCommandScope(database, connection, transaction);
        return ValueTask.FromResult(ReadReceipt(commands, commit.Address, context.CancellationToken)?.Reconcile(commit)
            ?? StorageCommitResult.Unknown());
    }

    /// <summary>Reads a committed item and its opaque version for a later conditional intent.</summary>
    /// <param name="context">Cancellation and operation context.</param>
    /// <param name="address">Logical item identity.</param>
    /// <returns>The committed portable item, or null when absent.</returns>
    /// <exception cref="InvalidDataException">A persisted item exceeds the configured per-row byte limit.</exception>
    /// <exception cref="SqliteException">Reading or acquiring the configured database fails.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed before reading.</exception>
    public ValueTask<StorageCommitItem?> ReadAsync(OperationContext context, StorageCommitAddress address)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(address);
        using var connection = database.OpenConnection(context.CancellationToken);
        using var transaction = connection.BeginTransaction(deferred: true);
        using var commands = new SqliteCommandScope(database, connection, transaction);
        using var reader = Read(commands, address, SqliteStorageCommitSql.ItemKind, context.CancellationToken);
        return ValueTask.FromResult(reader.Read()
            ? new StorageCommitItem(JsonSerializer.Deserialize<PortableValue>(ReadPayload(reader), Json)!, new(reader.GetString(1)))
            : null);
    }

    StorageCommitReceipt? ReadReceipt(SqliteCommandScope commands, StorageCommitAddress address, CancellationToken cancellation)
    {
        using var reader = Read(commands, address, SqliteStorageCommitSql.ReceiptKind, cancellation);
        return reader.Read() ? JsonSerializer.Deserialize<StorageCommitReceipt>(ReadPayload(reader), Json)
            ?? throw new JsonException("A durable receipt cannot be null.") : null;
    }

    string ReadPayload(SqliteDataReader reader)
    {
        // SQLite may store text in UTF-8 or UTF-16. UTF-16 needs at most twice the UTF-8 byte count.
        // Bound native transfer first, then enforce the exact UTF-8 budget before JSON decoding.
        if (MaximumStoredPayloadBytes is { } maximum && reader.GetInt64(2) > (maximum > long.MaxValue / 2 ? long.MaxValue : maximum * 2))
            throw new InvalidDataException("Stored commit payload exceeds the configured per-row byte limit.");
        var payload = reader.GetString(0);
        if (MaximumStoredPayloadBytes is { } budget && Encoding.UTF8.GetByteCount(payload) > budget)
            throw new InvalidDataException("Stored commit payload exceeds the configured per-row byte limit.");
        return payload;
    }

    static SqliteDataReader Read(SqliteCommandScope commands, StorageCommitAddress address, string kind, CancellationToken cancellation) =>
        commands.ExecuteReader(SqliteStorageCommitSql.Read, cancellation,
            (Target, address.Target), (Partition, address.Partition), (Id, address.Id), (Kind, kind));

    static int Write(SqliteCommandScope commands, StorageCommitAddress address, string kind, string payload,
        string token, EntityConcurrencyToken? expected, CancellationToken cancellation) => expected is { } version
        ? commands.ExecuteNonQuery(SqliteStorageCommitSql.Replace, cancellation,
            (Target, address.Target), (Partition, address.Partition), (Id, address.Id), (Kind, kind),
            (Payload, payload), (Token, token), (Expected, version.Value))
        : commands.ExecuteNonQuery(SqliteStorageCommitSql.Create, cancellation,
            (Target, address.Target), (Partition, address.Partition), (Id, address.Id), (Kind, kind),
            (Payload, payload), (Token, token));
}
