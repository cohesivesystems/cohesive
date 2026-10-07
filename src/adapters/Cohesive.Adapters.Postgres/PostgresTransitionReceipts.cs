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
        Table = $"\"{schema.Replace("\"", "\"\"")}\".\"{table.Replace("\"", "\"\"")}\"";
        PartitionKey = partitionKey;
        MaximumReceiptBytes = maximumReceiptBytes;
    }
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
        ? EntityTransitionOperationCapabilities.Unsupported : EntityTransitionOperationCapabilities.AtomicStateAndReceipt;

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
    public async Task<EntityTransitionOperationResult> CommitTransitionOperation(OperationContext context, EntityTransitionOperationCommit commit)
    {
        ArgumentNullException.ThrowIfNull(commit);
        ValidateReceiptRequest(context, commit.Request);
        ValidateWrite(commit.Write);
        if (GetPartitionKey(commit.Write.Entity) != receipts!.PartitionKey)
            throw new ArgumentException("Transition candidate differs from the trusted receipt partition.", nameof(commit));
        await using var connection = await runtime.DataSource.OpenConnectionAsync(context.CancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(context.CancellationToken).ConfigureAwait(false);
        var operation = OperationKey(commit.Request.Operation);
        // Serialize identical occurrences and subject creation; collisions only reduce concurrency.
        await Lock("operation/" + operation).ConfigureAwait(false);
        await Lock("subject/" + commit.Request.Subject.EntityId.Value).ConfigureAwait(false);
        var retained = await ReadReceipt(context, connection, transaction, operation, false).ConfigureAwait(false);
        if (retained is not null) return retained.Replay(commit);
        var creation = commit.SubjectCondition == EntityTransitionSubjectCondition.MustBeAbsent;
        if (creation)
        {
            retained = await ReadReceipt(context, connection, transaction, commit.Request.Subject.EntityId.Value, true).ConfigureAwait(false);
            if (retained is not null)
            {
                var replay = retained.ReplayCreation(commit.Request);
                if (replay.Receipt is not null && (retained.Entity.Entity != commit.Write.Entity
                    || retained.Commit.DecisionKind != commit.DecisionKind || retained.Result.Value != commit.Result.Value))
                    return Reject(EntityTransitionOperationDisposition.IdentityConflict, EntityTransitionOperationDiagnosticCodes.IdentityConflict,
                        "Creation intent differs from retained evidence.");
                return replay;
            }
        }
        EntitySnapshot snapshot;
        try { snapshot = await UpsertCore(context, connection, transaction, commit.Write, createOnly: creation).ConfigureAwait(false); }
        catch (ObservationConcurrencyConflictException)
        {
            return creation
                ? Reject(EntityTransitionOperationDisposition.SubjectStateConflict, EntityTransitionOperationDiagnosticCodes.SubjectStateConflict, "Creation subject already exists.")
                : Reject(EntityTransitionOperationDisposition.ConcurrencyConflict, EntityTransitionOperationDiagnosticCodes.ConcurrencyConflict, "Subject no longer matches its concurrency fence.");
        }
        var receipt = new EntityTransitionOperationReceipt(commit, snapshot, context.UtcNow);
        var bytes = StrictDocumentJson.GetCanonicalBytes(receipt, ReceiptJson);
        if (bytes.Length > receipts.MaximumReceiptBytes) throw new InvalidOperationException("Transition receipt exceeds the configured byte limit.");
        await using var insert = new NpgsqlCommand($"INSERT INTO {receipts.Table} VALUES ($1,$2,$3,$4,$5,$6,$7,$8)", connection, transaction);
        Add(insert, EntityType, receipts.PartitionKey, operation, commit.Request.Subject.EntityId.Value,
            creation ? commit.Request.Subject.EntityId.Value : DBNull.Value, bytes, Hash(bytes), EntityStorageJson.FormatVersion);
        await insert.ExecuteNonQueryAsync(context.CancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(context.CancellationToken).ConfigureAwait(false);
        return EntityTransitionOperationResult.Committed(receipt);

        async Task Lock(string key)
        {
            await using var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended($1,0))", connection, transaction);
            Add(command, receipts.Table + "/" + EntityType + "/" + receipts.PartitionKey + "/" + key);
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
    static EntityTransitionOperationResult Reject(EntityTransitionOperationDisposition disposition, string code, string message) =>
        EntityTransitionOperationResult.Rejected(disposition, new(code, DiagnosticSeverity.Error, message, "/commit"));
}
