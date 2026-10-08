using System.Security.Cryptography;
using System.Text.Json;
using Cohesive.Adapters.Sql;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Storage;
using Cohesive.Storage.Processes;
using Npgsql;

namespace Cohesive.Adapters.Postgres;

/// <summary>Explicit native receipt storage and trusted partition for atomic transition operations.</summary>
public sealed class PostgresTransitionReceiptOptions
{
    /// <summary>Declares a receipt table; construction never creates schema or opens a connection.</summary>
    /// <param name="schema">Existing native schema.</param>
    /// <param name="table">Receipt table name, separate from entity tables.</param>
    /// <param name="partitionKey">Trusted physical partition, not request-derived authorization.</param>
    /// <param name="maximumReceiptBytes">Maximum canonical receipt size on write and read.</param>
    /// <exception cref="ArgumentException">An identifier or partition is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Byte bound is not positive.</exception>
    public PostgresTransitionReceiptOptions(string schema, string table, string partitionKey, int maximumReceiptBytes = 1_048_576)
    {
        _ = PostgresSqlDialect.Identifier(schema);
        _ = PostgresSqlDialect.Identifier(table);
        ArgumentException.ThrowIfNullOrWhiteSpace(partitionKey);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumReceiptBytes);
        var qualified = new SqlQualifiedTable(schema, table);
        Table = qualified.ToSql(PostgresSqlDialect.Instance);
        var insert = new SqlInsertBuilder(qualified);
        foreach (var column in Columns)
            insert.Value(column.Name, SqlExpression.RuntimeParameter(column.Name));
        InsertSql = insert.BuildTemplate(PostgresSqlDialect.Instance).Text;
        PartitionKey = partitionKey;
        MaximumReceiptBytes = maximumReceiptBytes;
        Capabilities = new(true, partitionKey);
    }
    static readonly (string Name, string Type, bool Required)[] Columns =
    [
        ("entity_type", "text", true), ("partition_key", "text", true), ("operation_id", "text", true),
        ("subject_id", "text", true), ("creation_subject", "text", false), ("content", "bytea", true),
        ("content_hash", "text", true), ("format_version", "integer", true)
    ];
    internal string InsertSql { get; }
    internal EntityTransitionOperationCapabilities Capabilities { get; }
    internal string Table { get; }
    /// <summary>Trusted physical partition for receipt reads and transition writes.</summary>
    public string PartitionKey { get; }
    /// <summary>Canonical evidence byte bound; oversized evidence fails before commit.</summary>
    public int MaximumReceiptBytes { get; }
    /// <summary>Native DDL for explicit migration; execute once before admitting process traffic.</summary>
    public string SchemaSql => $"""
        CREATE TABLE IF NOT EXISTS {Table} (
          entity_type text NOT NULL, partition_key text NOT NULL, operation_id text NOT NULL,
          subject_id text NOT NULL, creation_subject text NULL, content bytea NOT NULL,
          content_hash text NOT NULL, format_version integer NOT NULL,
          PRIMARY KEY (entity_type, partition_key, operation_id),
          UNIQUE (entity_type, partition_key, creation_subject)
        );
        """;

    /// <summary>Checks receipt columns and both immediate uniqueness fences before admitting traffic.</summary>
    /// <param name="dataSource">Native database authority; no schema changes are made.</param>
    /// <param name="cancellationToken">Cancellation of metadata reads.</param>
    /// <returns>Completion after schema validation.</returns>
    /// <exception cref="InvalidOperationException">Schema needs an explicit migration.</exception>
    /// <exception cref="NpgsqlException">The metadata query fails.</exception>
    public async Task ValidateSchemaAsync(NpgsqlDataSource dataSource, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        await using var columns = dataSource.CreateCommand("""
            SELECT attname, format_type(atttypid, atttypmod), attnotnull
            FROM pg_attribute WHERE attrelid=to_regclass($1) AND attnum>0 AND NOT attisdropped
            """);
        columns.Parameters.AddWithValue(Table);
        await using (var reader = await columns.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            var actual = new Dictionary<string, (string, bool)>(StringComparer.Ordinal);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                actual.Add(reader.GetString(0), (reader.GetString(1), reader.GetBoolean(2)));
            if (actual.Count != Columns.Length || Columns.Any(column => !actual.TryGetValue(column.Name, out var found)
                || found != (column.Type, column.Required)))
                throw new InvalidOperationException("Receipt columns require an explicit schema migration.");
        }
        await using var keys = dataSource.CreateCommand("""
            SELECT c.contype::text, array_agg(a.attname::text ORDER BY k.ordinality)
            FROM pg_constraint c CROSS JOIN LATERAL unnest(c.conkey) WITH ORDINALITY k(attnum, ordinality)
            JOIN pg_attribute a ON a.attrelid=c.conrelid AND a.attnum=k.attnum
            WHERE c.conrelid=to_regclass($1) AND c.contype IN ('p','u') AND NOT c.condeferrable
                AND EXISTS (SELECT 1 FROM pg_index i WHERE i.indexrelid=c.conindid AND NOT i.indnullsnotdistinct)
            GROUP BY c.oid, c.contype
            """);
        keys.Parameters.AddWithValue(Table);
        await using var keyReader = await keys.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        bool primary = false, creation = false;
        while (await keyReader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var names = keyReader.GetFieldValue<string[]>(1);
            primary |= keyReader.GetString(0) == "p" && names.Length == 3 && names.ToHashSet(StringComparer.Ordinal).SetEquals(["entity_type", "partition_key", "operation_id"]);
            creation |= keyReader.GetString(0) == "u" && names.Length == 3 && names.ToHashSet(StringComparer.Ordinal).SetEquals(["entity_type", "partition_key", "creation_subject"]);
        }
        if (!primary || !creation) throw new InvalidOperationException("Receipt uniqueness fences require an explicit schema migration.");
    }

}

public sealed partial class PostgresEntityRepository
{
    readonly PostgresTransitionReceiptOptions? receipts;
    static readonly JsonSerializerOptions ReceiptJson = CreateReceiptJson();
    static JsonSerializerOptions CreateReceiptJson()
    {
        var options = EntityStorageJson.CreateOptions();
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
    /// <inheritdoc />
    public EntityTransitionOperationCapabilities TransitionOperationCapabilities => receipts is null
        ? EntityTransitionOperationCapabilities.Unsupported : receipts.Capabilities;

    /// <inheritdoc />
    public async Task<EntityTransitionOperationResult> TryGetTransitionOperation(OperationContext context, EntityTransitionOperationRequest request)
    {
        ValidateReceiptRequest(context, request);
        var retained = await ReadReceipt(context, null, null, OperationKey(request.Operation), creation: false).ConfigureAwait(false);
        return retained is null ? EntityTransitionOperationResult.NotFound() : retained.Replay(request);
    }
    /// <inheritdoc />
    public async Task<EntityTransitionOperationResult> TryGetCreationTransitionOperation(OperationContext context, EntityTransitionOperationRequest request)
    {
        ValidateReceiptRequest(context, request);
        var retained = await ReadReceipt(context, null, null, request.Subject.EntityId.Value, creation: true).ConfigureAwait(false);
        return retained is null ? EntityTransitionOperationResult.NotFound() : retained.ReplayCreation(request);
    }
    /// <inheritdoc />
    public async Task<EntityTransitionOperationResult> ResolveTransitionOperation(OperationContext context, EntityTransitionOperationReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        RequireReceipts(context);
        if (reference.Subject.EntityType.Value != EntityType) throw new ArgumentException("Receipt entity authority differs.", nameof(reference));
        var retained = await ReadReceipt(context, null, null, OperationKey(reference.Operation), creation: false).ConfigureAwait(false);
        return retained is null ? EntityTransitionOperationResult.NotFound() : retained.Replay(reference);
    }

    /// <summary>Commits state and immutable operation evidence in one native PostgreSQL transaction.</summary>
    /// <param name="context">Invocation cancellation and physical commit time.</param>
    /// <param name="commit">Canonical conditional write and process-owned result.</param>
    /// <returns>Committed/replayed receipt or structured identity, presence, or concurrency conflict.</returns>
    /// <remarks>No automatic retries. Cancellation or a connection failure during COMMIT is ambiguous;
    /// resolve the exact operation before deciding whether to retry. Receipt retention is explicit and must
    /// outlive supported retries. Does not deliver emissions or create an outbox.</remarks>
    /// <exception cref="ArgumentException">Entity, partition, or write evidence is invalid.</exception>
    /// <exception cref="NotSupportedException">Receipt support is not configured or encoding is unsupported.</exception>
    /// <exception cref="InvalidOperationException">Retained evidence is corrupt or exceeds its bound.</exception>
    /// <exception cref="NpgsqlException">Native transaction fails; no automatic retry occurs.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    public Task<EntityTransitionOperationResult> CommitTransitionOperation(OperationContext context, EntityTransitionOperationCommit commit)
    {
        ArgumentNullException.ThrowIfNull(commit);
        ValidateReceiptRequest(context, commit.Request);
        ValidateWrite(commit.Write);
        if (GetPartitionKey(commit.Write.Entity) != receipts!.PartitionKey)
            throw new ArgumentException("Transition candidate differs from the trusted receipt partition.", nameof(commit));
        return EntityTransitionCommitProtocol.CommitAsync(this, context, commit, TryCommitTransitionOperation);
    }

    async Task<EntityTransitionOperationReceipt?> TryCommitTransitionOperation(OperationContext context, EntityTransitionOperationCommit commit)
    {
        await using var connection = await runtime.DataSource.OpenConnectionAsync(context.CancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(context.CancellationToken).ConfigureAwait(false);
        var operation = OperationKey(commit.Request.Operation);
        // Serialize identical occurrences and subject creation; collisions only reduce concurrency.
        await Lock("operation/" + operation).ConfigureAwait(false);
        await Lock("subject/" + commit.Request.Subject.EntityId.Value).ConfigureAwait(false);
        var retained = await ReadReceipt(context, connection, transaction, operation, false).ConfigureAwait(false);
        if (retained is not null) return null;
        var creation = commit.SubjectCondition == EntityTransitionSubjectCondition.MustBeAbsent;
        if (creation && await ReadReceipt(context, connection, transaction, commit.Request.Subject.EntityId.Value, true).ConfigureAwait(false) is not null)
            return null;
        EntitySnapshot snapshot;
        try { snapshot = await UpsertCore(context, connection, transaction, commit.Write, createOnly: creation).ConfigureAwait(false); }
        catch (ObservationConcurrencyConflictException)
        {
            return null;
        }
        var receipt = new EntityTransitionOperationReceipt(commit, snapshot, context.UtcNow);
        var bytes = StrictDocumentJson.GetCanonicalBytes(receipt, ReceiptJson);
        if (bytes.Length > receipts!.MaximumReceiptBytes) throw new InvalidOperationException("Transition receipt exceeds the configured byte limit.");
        await using var insert = new NpgsqlCommand(receipts!.InsertSql, connection, transaction);
        Add(insert, EntityType, receipts.PartitionKey, operation, commit.Request.Subject.EntityId.Value,
            creation ? commit.Request.Subject.EntityId.Value : DBNull.Value, bytes, Hash(bytes), EntityStorageJson.FormatVersion);
        await insert.ExecuteNonQueryAsync(context.CancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(context.CancellationToken).ConfigureAwait(false);
        return receipt;

        async Task Lock(string key)
        {
            await using var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended($1,0))", connection, transaction);
            Add(command, receipts!.Table + "/" + EntityType + "/" + receipts.PartitionKey + "/" + key);
            await command.ExecuteNonQueryAsync(context.CancellationToken).ConfigureAwait(false);
        }
    }

    async Task<EntityTransitionOperationReceipt?> ReadReceipt(OperationContext context, NpgsqlConnection? connection,
        NpgsqlTransaction? transaction, string key, bool creation)
    {
        var options = receipts!;
        var text = $"SELECT content, content_hash, format_version, operation_id, subject_id, creation_subject FROM {options.Table} WHERE entity_type=$1 AND partition_key=$2 AND {(creation ? "creation_subject" : "operation_id")}=$3";
        await using var command = connection is null ? runtime.DataSource.CreateCommand(text) : new NpgsqlCommand(text, connection, transaction);
        Add(command, EntityType, options.PartitionKey, key);
        await using var reader = await command.ExecuteReaderAsync(System.Data.CommandBehavior.SequentialAccess, context.CancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(context.CancellationToken).ConfigureAwait(false)) return null;
        var length = reader.GetBytes(0, 0, null, 0, 0);
        if (length <= 0 || length > options.MaximumReceiptBytes) throw new InvalidOperationException("Invalid receipt size.");
        var bytes = new byte[checked((int)length)];
        if (reader.GetBytes(0, 0, bytes, 0, bytes.Length) != length || Hash(bytes) != reader.GetString(1))
            throw new InvalidOperationException("Receipt integrity check failed.");
        if (reader.GetInt32(2) != EntityStorageJson.FormatVersion) throw new NotSupportedException("Receipt encoding requires explicit migration.");
        var receipt = JsonSerializer.Deserialize<EntityTransitionOperationReceipt>(bytes, ReceiptJson)
            ?? throw new InvalidOperationException("Receipt is absent.");
        ValidateReceiptRequest(context, receipt.Request);
        EntityDefinition.ValidateObservation(receipt.Entity.Entity.Observation);
        if (receipt.Entity.PartitionKey != options.PartitionKey || OperationKey(receipt.Request.Operation) != reader.GetString(3)
            || receipt.Request.Subject.EntityId.Value != reader.GetString(4)
            || (receipt.Commit.SubjectCondition == EntityTransitionSubjectCondition.MustBeAbsent
                ? reader.IsDBNull(5) || reader.GetString(5) != receipt.Request.Subject.EntityId.Value : !reader.IsDBNull(5))
            || !bytes.AsSpan().SequenceEqual(StrictDocumentJson.GetCanonicalBytes(receipt, ReceiptJson)))
            throw new InvalidOperationException("Retained receipt contradicts its identity or canonical evidence.");
        return receipt;
    }
    void RequireReceipts(OperationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.ThrowIfCancellationRequested();
        if (receipts is null) throw new NotSupportedException("Configure transition receipt storage before admitting process operations.");
    }
    void ValidateReceiptRequest(OperationContext context, EntityTransitionOperationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireReceipts(context);
        if (request.Subject.EntityType.Value != EntityType) throw new ArgumentException("Receipt entity authority differs.", nameof(request));
    }
    static string OperationKey(ProcessOperationOccurrence operation) => Hash(StrictDocumentJson.GetCanonicalBytes(operation, ReceiptJson));
    static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    static void Add(NpgsqlCommand command, params object[] values)
    {
        foreach (var value in values) command.Parameters.Add(new NpgsqlParameter { Value = value });
    }
}
