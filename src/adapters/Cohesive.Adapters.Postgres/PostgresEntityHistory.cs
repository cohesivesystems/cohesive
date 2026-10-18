using System.Data;
using System.Globalization;
using System.Text.Json;
using Cohesive.Adapters.Sql;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Storage;
using Npgsql;

namespace Cohesive.Adapters.Postgres;

/// <summary>Native storage policy for ordered replacement actions and optional checkpoint acquisition.</summary>
/// <remarks>Changing authority requires an explicit migration. No history is synthesized from an existing row.
/// Current mapped rows remain synchronous query projections in event-authoritative mode. Direct SQL writes bypass
/// the protocol and are unsupported. History is retained indefinitely; retention/compaction is not implicit.</remarks>
public sealed class PostgresEntityHistoryOptions
{
    /// <summary>Declares auxiliary storage without performing I/O.</summary>
    /// <param name="schema">Existing PostgreSQL schema.</param>
    /// <param name="tablePrefix">Prefix for the head and action tables.</param>
    /// <param name="authority">Authoritative state representation.</param>
    /// <param name="reconstitution">Event read strategy; does not change authority.</param>
    /// <param name="checkpointInterval">Number of actions between retained checkpoint positions.</param>
    /// <param name="maximumReplayActions">Maximum actions read by one reconstitution.</param>
    /// <param name="maximumActionBytes">Maximum canonical bytes per action.</param>
    /// <param name="maximumReplayBytes">Aggregate encoded byte bound per reconstitution.</param>
    /// <exception cref="ArgumentException">A native identifier is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A policy or bound is invalid.</exception>
    public PostgresEntityHistoryOptions(string schema, string tablePrefix, EntityStateAuthority authority,
        EntityReconstitutionMode reconstitution = EntityReconstitutionMode.CheckpointAndTail,
        int checkpointInterval = 100, int maximumReplayActions = 10_000, int maximumActionBytes = 1_048_576, long maximumReplayBytes = 16_777_216)
    {
        if (!Enum.IsDefined(authority)) throw new ArgumentOutOfRangeException(nameof(authority));
        if (!Enum.IsDefined(reconstitution)) throw new ArgumentOutOfRangeException(nameof(reconstitution));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(checkpointInterval);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumReplayActions);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumActionBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumReplayBytes);
        MaximumReplayBytes = maximumReplayBytes;
        Heads = Name("_heads"); Actions = Name("_actions");
        var actionInsert = new SqlInsertBuilder(new SqlQualifiedTable(schema, tablePrefix + "_actions"));
        foreach (var column in new[] { "entity_type", "partition_key", "subject_id", "position", "content", "content_hash", "format_version" })
            actionInsert.Value(column, SqlExpression.RuntimeParameter(column));
        InsertActionSql = actionInsert.BuildTemplate(PostgresSqlDialect.Instance).Text;
        var headInsert = new SqlInsertBuilder(new SqlQualifiedTable(schema, tablePrefix + "_heads"));
        foreach (var column in new[] { "entity_type", "partition_key", "subject_id", "authority", "position", "checkpoint" })
            headInsert.Value(column, SqlExpression.RuntimeParameter(column));
        headInsert.OnConflictDoUpdate(["entity_type", "partition_key", "subject_id"], ["position", "checkpoint"]);
        UpsertHeadSql = headInsert.BuildTemplate(PostgresSqlDialect.Instance).Text;
        Authority = authority; Reconstitution = reconstitution; CheckpointInterval = checkpointInterval;
        MaximumReplayActions = maximumReplayActions; MaximumActionBytes = maximumActionBytes;
        string Name(string suffix)
        {
            _ = PostgresSqlDialect.Identifier(schema);
            _ = PostgresSqlDialect.Identifier(tablePrefix + suffix);
            return new SqlQualifiedTable(schema, tablePrefix + suffix).ToSql(PostgresSqlDialect.Instance);
        }
    }
    /// <summary>Selected durable authority.</summary>
    public EntityStateAuthority Authority { get; }
    /// <summary>Selected event acquisition strategy.</summary>
    public EntityReconstitutionMode Reconstitution { get; }
    /// <summary>Checkpoint cadence in stream actions.</summary>
    public int CheckpointInterval { get; }
    /// <summary>Per-read action bound; exceeding it fails rather than truncates.</summary>
    public int MaximumReplayActions { get; }
    /// <summary>Per-action canonical byte bound.</summary>
    public int MaximumActionBytes { get; }
    /// <summary>Aggregate encoded-byte bound for reconstitution.</summary>
    public long MaximumReplayBytes { get; }
    internal string InsertActionSql { get; }
    internal string UpsertHeadSql { get; }
    internal string Heads { get; }
    internal string Actions { get; }
    /// <summary>Explicit native migration, separate from entity tables. Apply before binding.</summary>
    public string SchemaSql => $"""
        CREATE TABLE IF NOT EXISTS {Heads} (
          entity_type text NOT NULL, partition_key text NOT NULL, subject_id text NOT NULL,
          authority integer NOT NULL, position bigint NOT NULL, checkpoint bigint NOT NULL,
          PRIMARY KEY(entity_type, partition_key, subject_id));
        CREATE TABLE IF NOT EXISTS {Actions} (
          entity_type text NOT NULL, partition_key text NOT NULL, subject_id text NOT NULL,
          position bigint NOT NULL, content bytea NOT NULL, content_hash text NOT NULL, format_version integer NOT NULL,
          PRIMARY KEY(entity_type, partition_key, subject_id, position));
        """;

    /// <summary>Validates columns and immediate primary keys before admitting traffic.</summary>
    /// <param name="dataSource">Caller-owned native data source.</param>
    /// <param name="cancellationToken">Cancellation of metadata reads.</param>
    /// <returns>Validated immutable storage tied to this data source.</returns>
    /// <exception cref="InvalidOperationException">A table requires migration.</exception>
    /// <exception cref="NpgsqlException">Metadata access fails.</exception>
    public async Task<PostgresEntityHistoryStorage> BindAsync(NpgsqlDataSource dataSource, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        await PostgresEntityAuxiliarySchema.ValidateTable(dataSource, Heads, ["entity_type:text", "partition_key:text", "subject_id:text", "authority:integer", "position:bigint", "checkpoint:bigint"],
            ["entity_type", "partition_key", "subject_id"], cancellationToken);
        await PostgresEntityAuxiliarySchema.ValidateTable(dataSource, Actions, ["entity_type:text", "partition_key:text", "subject_id:text", "position:bigint", "content:bytea", "content_hash:text", "format_version:integer"],
            ["entity_type", "partition_key", "subject_id", "position"], cancellationToken);
        await using var authority = dataSource.CreateCommand($"SELECT EXISTS(SELECT 1 FROM {Heads} WHERE authority <> $1)");
        authority.Parameters.AddWithValue((int)Authority);
        if (await authority.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true)
            throw new InvalidOperationException("Retained state authority differs; migrate explicitly before binding.");
        return new(dataSource, this);
    }
}

/// <summary>Schema-validated history attachment; the caller owns the native data source lifetime.</summary>
public sealed class PostgresEntityHistoryStorage
{
    internal PostgresEntityHistoryStorage(NpgsqlDataSource dataSource, PostgresEntityHistoryOptions options)
    { DataSource = dataSource; Options = options; }
    internal NpgsqlDataSource DataSource { get; }
    /// <summary>Immutable authority, bounds and replay policy.</summary>
    public PostgresEntityHistoryOptions Options { get; }
}

public sealed partial class PostgresEntityRepository
{
    readonly PostgresEntityHistoryOptions? history;

    async Task<EntitySnapshot?> ReadHistory(OperationContext context, string id, EntityReadOptions? options)
    {
        if (options?.PartitionKey is not { } partition)
            throw new ArgumentException("Event-authoritative reads require an explicit partition.", nameof(options));
        await using var connection = await runtime.DataSource.OpenConnectionAsync(context.CancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, context.CancellationToken).ConfigureAwait(false);
        var action = await Reconstitute(context, connection, transaction, partition, id).ConfigureAwait(false);
        if (action is null) return null;
        var result = CreateValidatedReadSnapshot(action.State, partition, StreamToken(action.Position), options.Fields);
        ValidateReadPreconditions(id, result, options);
        return result;
    }

    async Task<EntityStateAction?> Reconstitute(OperationContext context, NpgsqlConnection connection, NpgsqlTransaction transaction,
        string partition, string id, bool latestOnly = false)
    {
        var policy = history!;
        long position = 0, checkpoint = 0;
        bool found;
        await using (var head = new NpgsqlCommand($"SELECT authority, position, checkpoint FROM {policy.Heads} WHERE entity_type=$1 AND partition_key=$2 AND subject_id=$3", connection, transaction))
        {
            Add(head, EntityType, partition, id);
            await using var reader = await head.ExecuteReaderAsync(context.CancellationToken).ConfigureAwait(false);
            found = await reader.ReadAsync(context.CancellationToken).ConfigureAwait(false);
            if (found)
            {
                if (reader.GetInt32(0) != (int)policy.Authority)
                    throw new InvalidOperationException("Entity state authority differs from retained history; migrate explicitly.");
                position = reader.GetInt64(1);
                checkpoint = reader.GetInt64(2);
            }
        }
        if (!found)
        {
            await using var orphan = new NpgsqlCommand($"SELECT EXISTS(SELECT 1 FROM {policy.Actions} WHERE entity_type=$1 AND partition_key=$2 AND subject_id=$3)", connection, transaction);
            Add(orphan, EntityType, partition, id);
            if (await orphan.ExecuteScalarAsync(context.CancellationToken).ConfigureAwait(false) is true)
                throw new InvalidOperationException("History actions exist without their stream head.");
            if (policy.Authority == EntityStateAuthority.Events)
            {
                await using var row = new NpgsqlCommand(sql.ReadByIdentityAndPartition.Text, connection, transaction);
                AddReadParameters(row, sql.ReadByIdentityAndPartition, id, partition);
                await using var reader = await row.ExecuteReaderAsync(context.CancellationToken).ConfigureAwait(false);
                if (await reader.ReadAsync(context.CancellationToken).ConfigureAwait(false))
                    throw new InvalidOperationException("An existing state row has no authoritative history; migrate explicitly.");
            }
            return null;
        }
        if (position < 1 || checkpoint < 0 || checkpoint > position) throw new InvalidOperationException("Invalid stream head.");
        var start = latestOnly ? position : policy.Reconstitution == EntityReconstitutionMode.CheckpointAndTail && checkpoint > 0 ? checkpoint : 1;
        if (position - start + 1 > policy.MaximumReplayActions) throw new InvalidOperationException("Reconstitution exceeds its action bound.");
        await using var command = new NpgsqlCommand($"SELECT position,content,content_hash,format_version FROM {policy.Actions} WHERE entity_type=$1 AND partition_key=$2 AND subject_id=$3 AND position >= $4 AND position <= $5 ORDER BY position", connection, transaction);
        Add(command, EntityType, partition, id, start, position);
        await using var actions = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, context.CancellationToken).ConfigureAwait(false);
        EntityStateAction? current = null;
        long totalBytes = 0;
        while (await actions.ReadAsync(context.CancellationToken).ConfigureAwait(false))
        {
            var sequence = actions.GetInt64(0);
            var length = actions.GetBytes(1, 0, null, 0, 0);
            if (length <= 0 || length > policy.MaximumActionBytes || (totalBytes += length) > policy.MaximumReplayBytes) throw new InvalidOperationException("Invalid state action size.");
            var bytes = new byte[checked((int)length)];
            if (actions.GetBytes(1, 0, bytes, 0, bytes.Length) != length || Hash(bytes) != actions.GetString(2)
                || actions.GetInt32(3) != EntityStorageJson.FormatVersion)
                throw new InvalidOperationException("State action integrity or encoding mismatch.");
            var action = JsonSerializer.Deserialize<EntityStateAction>(bytes, ReceiptJson) ?? throw new InvalidOperationException("Missing action.");
            if (action.State is null || action.Position != sequence || action.State.EntityId.Value != id || GetPartitionKey(action.State) != partition)
                throw new InvalidOperationException("State action contradicts stream identity.");
            if (current is null && start > 1)
            {
                if (sequence != start) throw new InvalidOperationException("Checkpoint action is missing.");
                _ = EntityDefinition.CreateState(action.State);
                current = action; // Checkpoint is the exact retained replacement action, never an unchecked row copy.
            }
            else current = EntityReconstitution.Apply(EntityDefinition, current, action);
        }
        if (current?.Position != position) throw new InvalidOperationException("State history is incomplete.");
        return current;
    }

    async Task<EntitySnapshot> UpsertWithHistory(OperationContext context, NpgsqlConnection connection,
        NpgsqlTransaction transaction, EntityWriteRequest write, bool createOnly)
    {
        var policy = history!;
        var partition = GetPartitionKey(write.Entity);
        var id = write.Entity.EntityId.Value;
        await LockHistorySubject(context, connection, transaction, partition, id).ConfigureAwait(false);
        var previous = await Reconstitute(context, connection, transaction, partition, id, latestOnly: true).ConfigureAwait(false);
        if (createOnly && previous is not null) throw new ObservationConcurrencyConflictException("Entity already exists.");
        if (policy.Authority == EntityStateAuthority.Events)
        {
            if (previous is not null && write.ExpectedConcurrencyToken != StreamToken(previous.Position)
                || previous is null && write.ExpectedConcurrencyToken is not null)
                throw new ObservationConcurrencyConflictException("Entity stream position changed or a required fence is absent.");
        }
        var action = EntityReconstitution.Apply(EntityDefinition, previous, new(checked((previous?.Position ?? 0) + 1), write.Entity));
        var bytes = StrictDocumentJson.GetCanonicalBytes(action, ReceiptJson);
        if (bytes.Length > policy.MaximumActionBytes) throw new InvalidOperationException("State action exceeds its byte bound.");
        // First event must not silently adopt an existing row. Event-owned subsequent rows are rebuildable projections.
        var nativeWrite = policy.Authority == EntityStateAuthority.Events ? new EntityWriteRequest(write.Entity) : write;
        var snapshot = await UpsertMapped(context, connection, transaction, nativeWrite,
            createOnly || previous is null && policy.Authority == EntityStateAuthority.Events).ConfigureAwait(false);
        await using (var insert = new NpgsqlCommand(policy.InsertActionSql, connection, transaction))
        {
            Add(insert, EntityType, partition, id, action.Position, bytes, Hash(bytes), EntityStorageJson.FormatVersion);
            await insert.ExecuteNonQueryAsync(context.CancellationToken).ConfigureAwait(false);
        }
        var checkpoint = action.Position / policy.CheckpointInterval * policy.CheckpointInterval;
        await using (var head = new NpgsqlCommand(policy.UpsertHeadSql, connection, transaction))
        {
            Add(head, EntityType, partition, id, (int)policy.Authority, action.Position, checkpoint);
            await head.ExecuteNonQueryAsync(context.CancellationToken).ConfigureAwait(false);
        }
        return policy.Authority == EntityStateAuthority.Events ? new(write.Entity, partition, StreamToken(action.Position)) : snapshot;
    }

    async Task LockHistorySubject(OperationContext context, NpgsqlConnection connection, NpgsqlTransaction transaction, string partition, string id)
    {
        await using var gate = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended($1,0))", connection, transaction);
        Add(gate, JsonSerializer.Serialize(new[] { history!.Heads, EntityType, partition, id }));
        await gate.ExecuteNonQueryAsync(context.CancellationToken).ConfigureAwait(false);
    }

    /// <summary>Explicitly rebuilds one derived query row from event authority without appending actions or publishing effects.</summary>
    /// <param name="context">Cancellation and invocation scope.</param>
    /// <param name="id">Exact entity identity.</param>
    /// <param name="partition">Exact physical partition.</param>
    /// <returns>Reconstituted snapshot, or null if no entity stream exists.</returns>
    /// <exception cref="NotSupportedException">The repository is not event-authoritative.</exception>
    /// <exception cref="InvalidOperationException">History is corrupt or exceeds the selected replay bounds.</exception>
    /// <exception cref="NpgsqlException">Native I/O fails; no automatic retry occurs.</exception>
    public async Task<EntitySnapshot?> RebuildStateProjection(OperationContext context, string id, string partition)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(partition);
        if (history?.Authority != EntityStateAuthority.Events) throw new NotSupportedException("Only event authority can rebuild a derived state row.");
        await using var connection = await runtime.DataSource.OpenConnectionAsync(context.CancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(context.CancellationToken).ConfigureAwait(false);
        await LockHistorySubject(context, connection, transaction, partition, id).ConfigureAwait(false);
        var action = await Reconstitute(context, connection, transaction, partition, id).ConfigureAwait(false);
        if (action is null) return null;
        await UpsertMapped(context, connection, transaction, new(action.State)).ConfigureAwait(false);
        await transaction.CommitAsync(context.CancellationToken).ConfigureAwait(false);
        return new(action.State, partition, StreamToken(action.Position));
    }

    static EntityConcurrencyToken StreamToken(long position) => new("stream/v1/" + position.ToString(CultureInfo.InvariantCulture));
}
