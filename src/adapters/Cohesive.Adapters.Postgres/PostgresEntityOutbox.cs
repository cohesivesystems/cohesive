using System.Collections.Immutable;
using System.Text.Json;
using Cohesive.Adapters.Sql;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Storage;
using Cohesive.Transitions.Model;
using Npgsql;

namespace Cohesive.Adapters.Postgres;

/// <summary>Explicit delivery-intent storage, independent of entity state authority.</summary>
public sealed class PostgresEntityOutboxOptions
{
    /// <summary>Declares a native outbox without performing I/O.</summary>
    /// <param name="schema">Existing PostgreSQL schema.</param>
    /// <param name="prefix">Auxiliary table prefix.</param>
    /// <param name="maximumCommitBytes">Bound for one retained commit and aggregate read payload.</param>
    /// <exception cref="ArgumentException">A native identifier is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Byte bound is not positive.</exception>
    public PostgresEntityOutboxOptions(string schema, string prefix, int maximumCommitBytes = 1_048_576)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCommitBytes);
        MaximumCommitBytes = maximumCommitBytes;
        Commits = Name("_commits"); Emissions = Name("_emissions");
        catalog = new([
            new(Commits, [new("sequence", "bigint", true, Identity: "a"), new("entity_type", "text", true),
                new("content", "bytea", true), new("content_hash", "text", true), new("format_version", "integer", true)], [new(["sequence"], "p")]),
            new(Emissions, [new("emission_id", "text", true), new("sequence", "bigint", true)], [new(["emission_id"], "p")],
                [new(["sequence"], Commits, ["sequence"])])]);
        var commit = new SqlInsertBuilder(new SqlQualifiedTable(schema, prefix + "_commits"));
        foreach (var column in new[] { "entity_type", "content", "content_hash", "format_version" })
            commit.Value(column, SqlExpression.RuntimeParameter(column));
        commit.Returning(SqlExpression.UnqualifiedColumn("sequence"), "sequence");
        InsertCommitSql = commit.BuildTemplate(PostgresSqlDialect.Instance).Text;
        var emission = new SqlInsertBuilder(new SqlQualifiedTable(schema, prefix + "_emissions"));
        emission.Value("emission_id", SqlExpression.RuntimeParameter("emission_id"));
        emission.Value("sequence", SqlExpression.RuntimeParameter("sequence"));
        InsertEmissionSql = emission.BuildTemplate(PostgresSqlDialect.Instance).Text;
        string Name(string suffix)
        {
            _ = PostgresSqlDialect.Identifier(schema); _ = PostgresSqlDialect.Identifier(prefix + suffix);
            return new SqlQualifiedTable(schema, prefix + suffix).ToSql(PostgresSqlDialect.Instance);
        }
    }
    readonly PostgresCatalogSchema catalog;
    internal string InsertCommitSql { get; }
    internal string InsertEmissionSql { get; }
    internal string Commits { get; }
    internal string Emissions { get; }
    /// <summary>Maximum encoded commit size and aggregate payload per read.</summary>
    public int MaximumCommitBytes { get; }
    /// <summary>Explicit native migration; never applied by repository construction.</summary>
    public string SchemaSql => $"""
        CREATE TABLE IF NOT EXISTS {Commits} (sequence bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
          entity_type text NOT NULL, content bytea NOT NULL, content_hash text NOT NULL, format_version integer NOT NULL);
        CREATE TABLE IF NOT EXISTS {Emissions} (emission_id text PRIMARY KEY, sequence bigint NOT NULL REFERENCES {Commits}(sequence));
        """;
    /// <summary>Checks the storage schema before admitting direct transition emissions.</summary>
    /// <param name="dataSource">Native data source owned by the caller.</param>
    /// <param name="cancellationToken">Metadata-read cancellation.</param>
    /// <returns>Validated storage on this exact data source.</returns>
    /// <exception cref="PostgresSchemaValidationException">A table, column, identity, key or foreign key needs explicit migration.</exception>
    /// <exception cref="NpgsqlException">Metadata access fails.</exception>
    public async Task<PostgresEntityOutboxStorage> BindAsync(NpgsqlDataSource dataSource, CancellationToken cancellationToken = default)
    {
        await catalog.ValidateAsync(dataSource, PostgresSchemaInitialization.ValidateOnly, cancellationToken).ConfigureAwait(false);
        return new(dataSource, this);
    }

    internal async Task<PostgresEntityOutboxStorage?> AdmitAsync(NpgsqlDataSource dataSource,
        PostgresSchemaInitialization policy, CancellationToken cancellationToken)
    {
        var admission = await catalog.ValidateAsync(dataSource, policy, cancellationToken).ConfigureAwait(false);
        return admission.MissingCount == 0 ? new(dataSource, this) : null;
    }

}

/// <summary>Validated native outbox attachment; does not deliver or acknowledge interactions.</summary>
public sealed class PostgresEntityOutboxStorage
{
    internal PostgresEntityOutboxStorage(NpgsqlDataSource dataSource, PostgresEntityOutboxOptions options)
    { DataSource = dataSource; Options = options; }
    internal NpgsqlDataSource DataSource { get; }
    internal PostgresEntityOutboxOptions Options { get; }
}

/// <summary>One retained direct-transition commit. Process receipts use their own publication authority.</summary>
/// <param name="Sequence">Native cursor with possible gaps.</param>
/// <param name="Commit">Original state and delivery envelopes.</param>
public sealed record PostgresEntityOutboxEntry(long Sequence, EntityCommitResult Commit);

/// <summary>Configured direct-transition outbox over the same native entity commit transaction.</summary>
/// <remarks>No dispatch or retention deletion is implied. The caller owns database lifetime. Ambiguous commits
/// must be resolved by exact emission identity; this repository never retries a write automatically.</remarks>
public sealed class PostgresEntityOutboxRepository : IEntityOutboxRepository, IEntityTransitionOperationRepository
{
    readonly PostgresEntityRepository entities;
    readonly PostgresEntityOutboxStorage storage;
    /// <summary>Attaches a validated outbox to an existing repository without opening a connection.</summary>
    /// <param name="entities">Repository with optional receipt/history policy.</param>
    /// <param name="storage">Validated outbox on exactly the same data source.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">The native data sources differ.</exception>
    public PostgresEntityOutboxRepository(PostgresEntityRepository entities, PostgresEntityOutboxStorage storage)
    {
        this.entities = entities ?? throw new ArgumentNullException(nameof(entities));
        this.storage = storage ?? throw new ArgumentNullException(nameof(storage));
        entities.RequireOutboxSource(storage);
    }
    /// <inheritdoc />
    public EntityDefinition EntityDefinition => entities.EntityDefinition;
    /// <inheritdoc />
    public string IdentityField => entities.IdentityField;
    /// <inheritdoc />
    public string EntityType => entities.EntityType;
    /// <inheritdoc />
    public EntityBatchCapabilities BatchCapabilities => entities.BatchCapabilities;
    /// <inheritdoc />
    public EntityCreationCapabilities CreationCapabilities => entities.CreationCapabilities;
    /// <inheritdoc />
    public EntityTransitionOperationCapabilities TransitionOperationCapabilities => entities.TransitionOperationCapabilities;
    /// <inheritdoc />
    public Task<EntitySnapshot?> TryGet(OperationContext context, string id, EntityReadOptions? options = null) => entities.TryGet(context, id, options);
    /// <inheritdoc />
    public Task<EntitySnapshot> Create(OperationContext context, EntityObservationSnapshot entity, EntityCreationPolicy policy) => entities.Create(context, entity, policy);
    /// <inheritdoc />
    public Task<EntitySnapshot> Upsert(OperationContext context, EntityWriteRequest write) => entities.Upsert(context, write);
    /// <inheritdoc />
    public Task<EntityBatchWriteResult> UpsertBatch(OperationContext context, EntityBatchWriteRequest request) => entities.UpsertBatch(context, request);
    /// <inheritdoc />
    public Task<EntityCommitResult> UpsertWithOutbox(OperationContext context, EntityOutboxCommit commit) => entities.CommitOutbox(context, commit, storage);
    /// <inheritdoc />
    public Task<EntityTransitionOperationResult> CommitTransitionOperation(OperationContext context, EntityTransitionOperationCommit commit) => entities.CommitTransitionOperation(context, commit);
    /// <inheritdoc />
    public Task<EntityTransitionOperationResult> TryGetTransitionOperation(OperationContext context, EntityTransitionOperationRequest request) => entities.TryGetTransitionOperation(context, request);
    /// <inheritdoc />
    public Task<EntityTransitionOperationResult> TryGetCreationTransitionOperation(OperationContext context, EntityTransitionOperationRequest request) => entities.TryGetCreationTransitionOperation(context, request);
    /// <inheritdoc />
    public Task<EntityTransitionOperationResult> ResolveTransitionOperation(OperationContext context, EntityTransitionOperationReference reference) => entities.ResolveTransitionOperation(context, reference);
    /// <summary>Reads retained commits without acknowledging delivery. Writes through this adapter serialize cursor allocation
    /// through commit; external writers bypassing that gate are unsupported.</summary>
    /// <param name="context">Cancellation and invocation context.</param>
    /// <param name="afterSequence">Exclusive native cursor.</param>
    /// <param name="maximumCommits">Positive bounded count, at most 1000.</param>
    /// <returns>Retained direct-transition commits for this entity type.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Cursor/count is invalid.</exception>
    /// <exception cref="InvalidOperationException">Retained payload exceeds bounds or fails integrity checks.</exception>
    public Task<ImmutableArray<PostgresEntityOutboxEntry>> ReadOutbox(OperationContext context, long afterSequence = 0, int maximumCommits = 100)
        => entities.ReadOutbox(context, storage, afterSequence, maximumCommits);
}

public sealed partial class PostgresEntityRepository
{
    internal void RequireOutboxSource(PostgresEntityOutboxStorage storage)
    {
        if (!ReferenceEquals(storage.DataSource, runtime.DataSource)) throw new ArgumentException("Outbox belongs to another native data source.", nameof(storage));
    }
    internal async Task<EntityCommitResult> CommitOutbox(OperationContext context, EntityOutboxCommit commit, PostgresEntityOutboxStorage storage)
    {
        ArgumentNullException.ThrowIfNull(context); ArgumentNullException.ThrowIfNull(commit);
        ValidateWrite(commit.Write);
        var options = storage.Options;
        await using var connection = await runtime.DataSource.OpenConnectionAsync(context.CancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(context.CancellationToken).ConfigureAwait(false);
        // A table-scoped commit gate makes the monotonically allocated cursor safe for tailing: no
        // lower sequence can commit after a higher one. This intentionally bounds write concurrency.
        await using (var cursorGate = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended($1,0))", connection, transaction))
        {
            Add(cursorGate, options.Commits);
            await cursorGate.ExecuteNonQueryAsync(context.CancellationToken).ConfigureAwait(false);
        }
        var ids = commit.Envelopes.Select(e => e.Context.EmissionId.Value).ToArray();
        long? retained = null;
        var matches = 0;
        if (ids.Length > 0)
        {
            await using var lookup = new NpgsqlCommand($"SELECT sequence FROM {options.Emissions} WHERE emission_id=ANY($1)", connection, transaction);
            lookup.Parameters.AddWithValue(ids);
            await using var reader = await lookup.ExecuteReaderAsync(context.CancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(context.CancellationToken).ConfigureAwait(false))
            {
                var found = reader.GetInt64(0);
                if (retained is not null && retained != found) throw new InvalidOperationException("Outbox emission identity conflict.");
                retained = found; matches++;
            }
        }
        if (retained is not null)
        {
            if (matches != ids.Length) throw new InvalidOperationException("Partial outbox emission replay is invalid.");
            await using var read = new NpgsqlCommand($"SELECT content,content_hash,format_version FROM {options.Commits} WHERE sequence=$1 AND entity_type=$2", connection, transaction);
            Add(read, retained.Value, EntityType);
            await using var reader = await read.ExecuteReaderAsync(System.Data.CommandBehavior.SequentialAccess, context.CancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(context.CancellationToken).ConfigureAwait(false)) throw new InvalidOperationException("Missing outbox receipt.");
            var replay = DecodeOutbox(reader, options, out _);
            if (replay.Entity.Entity != commit.Write.Entity || !SameOutboxEnvelopes(replay.Envelopes, commit.Envelopes))
                throw new InvalidOperationException("Outbox emission identity was reused with different evidence.");
            return replay;
        }
        var snapshot = await UpsertCore(context, connection, transaction, commit.Write).ConfigureAwait(false);
        var result = new EntityCommitResult(snapshot, commit.Envelopes);
        if (ids.Length > 0)
        {
            var bytes = StrictDocumentJson.GetCanonicalBytes(result, ReceiptJson);
            if (bytes.Length > options.MaximumCommitBytes) throw new InvalidOperationException("Outbox commit exceeds its byte bound.");
            await using var insert = new NpgsqlCommand(options.InsertCommitSql, connection, transaction);
            Add(insert, EntityType, bytes, Hash(bytes), EntityStorageJson.FormatVersion);
            var sequence = (long)(await insert.ExecuteScalarAsync(context.CancellationToken).ConfigureAwait(false))!;
            foreach (var id in ids)
            {
                await using var emission = new NpgsqlCommand(options.InsertEmissionSql, connection, transaction);
                Add(emission, id, sequence);
                await emission.ExecuteNonQueryAsync(context.CancellationToken).ConfigureAwait(false);
            }
        }
        await transaction.CommitAsync(context.CancellationToken).ConfigureAwait(false);
        return result;
    }
    static bool SameOutboxEnvelopes(ImmutableArray<InteractionEnvelope> left, ImmutableArray<InteractionEnvelope> right)
    {
        if (left.Length != right.Length) return false;
        for (var index = 0; index < left.Length; index++)
            if (InteractionEnvelopeJsonSerializer.ComputeContentFingerprint(left[index])
                != InteractionEnvelopeJsonSerializer.ComputeContentFingerprint(right[index])) return false;
        return true;
    }
    EntityCommitResult DecodeOutbox(NpgsqlDataReader reader, PostgresEntityOutboxOptions options, out int size)
    {
        var length = reader.GetBytes(0, 0, null, 0, 0);
        if (length <= 0 || length > options.MaximumCommitBytes) throw new InvalidOperationException("Outbox payload exceeds bounds.");
        size = checked((int)length);
        var bytes = new byte[size];
        if (reader.GetBytes(0, 0, bytes, 0, size) != size || Hash(bytes) != reader.GetString(1) || reader.GetInt32(2) != EntityStorageJson.FormatVersion)
            throw new InvalidOperationException("Outbox integrity or encoding mismatch.");
        var result = JsonSerializer.Deserialize<EntityCommitResult>(bytes, ReceiptJson) ?? throw new InvalidOperationException("Missing outbox result.");
        ValidateWrite(new(result.Entity.Entity));
        if (result.Entity.PartitionKey != GetPartitionKey(result.Entity.Entity))
            throw new InvalidOperationException("Outbox receipt contradicts its entity partition.");
        _ = new EntityOutboxCommit(new(result.Entity.Entity), result.Envelopes);
        return result;
    }
    internal async Task<ImmutableArray<PostgresEntityOutboxEntry>> ReadOutbox(OperationContext context, PostgresEntityOutboxStorage storage, long after, int maximum)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentOutOfRangeException.ThrowIfNegative(after);
        if (maximum is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(maximum));
        await using var command = runtime.DataSource.CreateCommand($"SELECT content,content_hash,format_version,sequence FROM {storage.Options.Commits} WHERE entity_type=$1 AND sequence>$2 ORDER BY sequence LIMIT $3");
        Add(command, EntityType, after, maximum);
        await using var reader = await command.ExecuteReaderAsync(System.Data.CommandBehavior.SequentialAccess, context.CancellationToken).ConfigureAwait(false);
        var result = ImmutableArray.CreateBuilder<PostgresEntityOutboxEntry>();
        long total = 0;
        while (await reader.ReadAsync(context.CancellationToken).ConfigureAwait(false))
        {
            var commit = DecodeOutbox(reader, storage.Options, out var size);
            if (total + size > storage.Options.MaximumCommitBytes) break;
            total += size;
            result.Add(new(reader.GetInt64(3), commit));
        }
        return result.ToImmutable();
    }
}
