using static Cohesive.Adapters.SQLite.SqliteEntityRepositorySql;
using Cohesive.Adapters.Sql;
using System.Collections.Immutable;
using Cohesive.Model;
using Cohesive.Storage;
using Cohesive.Transitions.Model;
using Microsoft.Data.Sqlite;
using static Cohesive.Adapters.SQLite.SqliteEntityRepositoryMapping;

namespace Cohesive.Adapters.SQLite;

/// <summary>Canonical entity repository with native SQLite compare-and-swap writes and atomic ordered batches.</summary>
/// <remarks>
/// Each operation owns one connection; each write unit owns an immediate transaction. Native operations execute
/// synchronously and return completed tasks. Cancellation is cooperative between native operations and before
/// commit. A commit already completed is successful even if cancellation arrives afterward. This repository does
/// not migrate schemas, retry writes, provide insert-only semantics, or persist outbox/operation receipts.
/// </remarks>
public sealed class SqliteEntityRepository : IEntityRepository
{
    readonly SqliteDatabase database;
    readonly SqliteEntityRepositorySql sql;
    readonly string graph;
    readonly string shape;

    /// <summary>Creates a repository over an explicitly initialized scalar table.</summary>
    /// <param name="database">Shared immutable connection/command runtime; no connection is opened by this constructor.</param>
    /// <param name="mapping">Validated physical realization and its canonical entity definition.</param>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="EntityShapeGraphValidationException">The mapping's entity graph is invalid.</exception>
    public SqliteEntityRepository(SqliteDatabase database, SqliteEntityRepositoryMapping mapping)
    {
        this.database = database ?? throw new ArgumentNullException(nameof(database));
        Mapping = mapping ?? throw new ArgumentNullException(nameof(mapping));
        graph = SqliteScalarCodec.RequireText(EntityDefinition.StateShape.QualifiedId.GraphId.Value);
        shape = SqliteScalarCodec.RequireText(EntityDefinition.StateShape.QualifiedId.ShapeId.Value);
        sql = SqliteEntityRepositorySql.Create(mapping);
    }

    /// <summary>Inspectable immutable physical realization and limits.</summary>
    public SqliteEntityRepositoryMapping Mapping { get; }
    /// <inheritdoc />
    public EntityDefinition EntityDefinition => Mapping.EntityDefinition;
    /// <inheritdoc />
    public string EntityType => EntityDefinition.Shape.Id.Value;
    /// <inheritdoc />
    public EntityBatchCapabilities BatchCapabilities => Mapping.BatchCapabilities;

    /// <summary>Loads a complete validated observation and retains any requested field-selection metadata.</summary>
    /// <param name="context">Operation cancellation and attribution context.</param>
    /// <param name="id">Nonempty exact identity; values are bound as parameters.</param>
    /// <param name="options">Optional partition, logical version, storage token, and field-selection preconditions.</param>
    /// <returns>A complete persisted snapshot, or null when no matching identity/partition exists.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is null.</exception>
    /// <exception cref="ArgumentException">The identity/text or selected field names are invalid.</exception>
    /// <exception cref="InvalidOperationException">Identity is ambiguous, the stored shape differs, or the runtime profile cannot be established.</exception>
    /// <exception cref="SemanticRuleViolationException">Stored state violates the semantic entity definition.</exception>
    /// <exception cref="ObservationConcurrencyConflictException">A read version/token precondition fails.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed before returning the read.</exception>
    /// <exception cref="SqliteException">Schema, locking, or database access fails.</exception>
    public Task<EntitySnapshot?> TryGet(OperationContext context, string id, EntityReadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        context.ThrowIfCancellationRequested();
        SqliteScalarCodec.RequireText(id);
        var fields = options?.Fields?.ToImmutableHashSet(StringComparer.Ordinal);
        if (fields is not null)
            foreach (var field in fields)
                if (!Mapping.FieldColumns.ContainsKey(field))
                    throw new ArgumentException($"Read selection refers to unknown entity field '{field}'.", nameof(options));
        var partition = options?.PartitionKey;
        if (partition is not null) SqliteScalarCodec.RequireText(partition);
        using var connection = database.OpenConnection(context.CancellationToken);
        using var command = CreateReadCommand(connection, transaction: null, id, partition);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            context.ThrowIfCancellationRequested();
            return Task.FromResult<EntitySnapshot?>(null);
        }
        var snapshot = ReadSnapshot(reader, fields);
        if (reader.Read())
            throw new InvalidOperationException($"Observation '{EntityType}:{id}' exists in multiple partitions; supply an explicit partition key.");
        if (options?.ExpectedVersion is { } version && snapshot.Entity.Version != version)
            throw Conflict(id, "logical version precondition failed");
        if (options?.ExpectedConcurrencyToken is { } token && snapshot.ConcurrencyToken != token)
            throw Conflict(id, "storage concurrency precondition failed");
        context.ThrowIfCancellationRequested();
        return Task.FromResult<EntitySnapshot?>(snapshot);
    }

    /// <summary>Upserts one complete observation, conditionally replacing it when an expected storage token is supplied.</summary>
    /// <param name="context">Operation cancellation and attribution context.</param>
    /// <param name="write">Validated complete candidate and optional opaque compare-and-swap token.</param>
    /// <returns>The committed candidate with a newly generated opaque storage token.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">Keys, token, or scalar representations are invalid.</exception>
    /// <exception cref="SemanticRuleViolationException">The candidate does not satisfy the canonical entity shape.</exception>
    /// <exception cref="ObservationConcurrencyConflictException">The expected token is stale or its target does not exist.</exception>
    /// <exception cref="InvalidOperationException">The identity field disagrees with the snapshot, stored shape differs, or runtime profile is unavailable.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed before commit; the write rolls back.</exception>
    /// <exception cref="SqliteException">SQL, locking, storage, or commit fails; uncommitted changes roll back.</exception>
    public Task<EntitySnapshot> Upsert(OperationContext context, EntityWriteRequest write)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.ThrowIfCancellationRequested();
        var partition = ValidateWrite(write);
        using var connection = database.OpenConnection(context.CancellationToken);
        using var transaction = connection.BeginTransaction(deferred: false);
        var snapshot = UpsertCore(context, connection, transaction, write, partition);
        context.ThrowIfCancellationRequested();
        transaction.Commit();
        return Task.FromResult(snapshot);
    }

    /// <summary>Writes candidates in request order with independent commits or the requested atomic transaction.</summary>
    /// <param name="context">Operation cancellation and attribution context.</param>
    /// <param name="request">Ordered candidates and required atomicity; None commits each write independently.</param>
    /// <returns>Committed snapshots in request order and the requested atomicity contract.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">Writes, keys, tokens, or scalar representations are invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The requested atomicity enum is unknown.</exception>
    /// <exception cref="NotSupportedException">The batch exceeds the limit or SamePartition spans multiple partitions.</exception>
    /// <exception cref="SemanticRuleViolationException">A candidate does not satisfy its canonical entity shape.</exception>
    /// <exception cref="ObservationConcurrencyConflictException">An expected token fails; atomic batches roll back, while earlier None writes remain.</exception>
    /// <exception cref="InvalidOperationException">An identity is inconsistent, a stored shape differs, or runtime profile is unavailable.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed before commit; None batches may retain earlier committed writes.</exception>
    /// <exception cref="SqliteException">SQL, locking, storage, or commit fails; uncommitted writes roll back, while earlier None writes remain.</exception>
    public Task<EntityBatchWriteResult> UpsertBatch(OperationContext context, EntityBatchWriteRequest request)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        context.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request.Writes);
        if (!Enum.IsDefined(request.Atomicity))
            throw new ArgumentOutOfRangeException(nameof(request), request.Atomicity, "Unknown entity batch atomicity.");
        if (request.Writes.Count > Mapping.MaximumBatchItems)
            throw new NotSupportedException($"Repository '{EntityType}' accepts at most {Mapping.MaximumBatchItems} writes per batch.");
        // Snapshot caller-owned list entries once, before connection acquisition; never reread mutable request storage.
        var writes = new EntityWriteRequest[request.Writes.Count];
        var partitions = new string[writes.Length];
        for (var index = 0; index < writes.Length; index++)
        {
            context.ThrowIfCancellationRequested();
            writes[index] = request.Writes[index];
            partitions[index] = ValidateWrite(writes[index]);
            if (request.Atomicity == EntityBatchAtomicity.SamePartition && partitions[index] != partitions[0])
                throw new NotSupportedException("SamePartition atomicity cannot span multiple logical partitions.");
        }
        if (writes.Length == 0) return Task.FromResult(new EntityBatchWriteResult([], request.Atomicity));
        using var connection = database.OpenConnection(context.CancellationToken);
        using var transaction = request.Atomicity == EntityBatchAtomicity.None
            ? null : connection.BeginTransaction(deferred: false);
        var snapshots = ImmutableArray.CreateBuilder<EntitySnapshot>(writes.Length);
        for (var index = 0; index < writes.Length; index++)
        {
            if (transaction is not null)
            {
                snapshots.Add(UpsertCore(context, connection, transaction, writes[index], partitions[index]));
                continue;
            }
            using var itemTransaction = connection.BeginTransaction(deferred: false);
            var snapshot = UpsertCore(context, connection, itemTransaction, writes[index], partitions[index]);
            context.ThrowIfCancellationRequested();
            itemTransaction.Commit();
            snapshots.Add(snapshot);
        }
        if (transaction is not null)
        {
            context.ThrowIfCancellationRequested();
            transaction.Commit();
        }
        return Task.FromResult(new EntityBatchWriteResult(snapshots.MoveToImmutable(), request.Atomicity));
    }

    internal bool Exists(SqliteConnection connection, SqliteTransaction transaction, string id)
    {
        using var command = CreateReadCommand(connection, transaction, id, partition: null);
        using var reader = command.ExecuteReader();
        return reader.Read();
    }

    SqliteCommand CreateReadCommand(SqliteConnection connection, SqliteTransaction? transaction, string id, string? partition)
    {
        var template = partition is null ? sql.ReadByIdentity : sql.ReadByIdentityAndPartition;
        var command = database.CreateCommand(connection, transaction, template.Text);
        foreach (var slot in template.Parameters)
            command.Parameters.Add(new SqliteParameter(slot.Placeholder, SqliteType.Text)
            {
                Value = slot.Binding switch
                {
                    IdentityBinding => id,
                    PartitionBinding => partition!,
                    _ => throw new InvalidOperationException($"Unknown SQLite read parameter binding '{slot.Binding}'.")
                }
            });
        return command;
    }

    internal EntitySnapshot UpsertCore(OperationContext context, SqliteConnection connection, SqliteTransaction transaction,
        EntityWriteRequest write, string partition)
    {
        context.ThrowIfCancellationRequested();
        var token = new EntityConcurrencyToken(Guid.NewGuid().ToString("N"));
        var template = write.ExpectedConcurrencyToken is null ? sql.Upsert : sql.Replace;
        using var command = database.CreateCommand(connection, transaction, template.Text);
        foreach (var slot in template.Parameters)
        {
            if (sql.FieldIndexByBinding.TryGetValue(slot.Binding!, out var index))
            {
                var binding = Mapping.Bindings[index];
                var observation = write.Entity.Observation;
                var value = observation.Fields is IOrdinalObservationFieldReader ordinalReader
                    && ReferenceEquals(ordinalReader.Layout, Mapping.Layout)
                    && ordinalReader.TryGetField(index, out var ordinalValue)
                        ? ordinalValue : observation.GetField(binding.Field.Name.Value);
                command.Parameters.Add(SqliteScalarCodec.CreateParameter(slot.Placeholder, binding.Contract, value));
                continue;
            }
            var parameter = slot.Binding switch
            {
                VersionBinding => new SqliteParameter(slot.Placeholder, SqliteType.Integer) { Value = write.Entity.Version },
                TokenBinding => new SqliteParameter(slot.Placeholder, SqliteType.Text) { Value = token.Value },
                GraphBinding => new SqliteParameter(slot.Placeholder, SqliteType.Text) { Value = graph },
                ShapeBinding => new SqliteParameter(slot.Placeholder, SqliteType.Text) { Value = shape },
                IdentityBinding => new SqliteParameter(slot.Placeholder, SqliteType.Text) { Value = write.Entity.EntityId.Value },
                PartitionBinding => new SqliteParameter(slot.Placeholder, SqliteType.Text) { Value = partition },
                ExpectedConcurrencyBinding => new SqliteParameter(slot.Placeholder, SqliteType.Text) { Value = write.ExpectedConcurrencyToken!.Value.Value },
                _ => throw new InvalidOperationException($"Unknown SQLite entity parameter binding '{slot.Binding}'.")
            };
            command.Parameters.Add(parameter);
        }
        var written = command.ExecuteScalar();
        if (written is not string storedToken || storedToken != token.Value)
        {
            if (write.ExpectedConcurrencyToken is not null)
            {
                // Diagnose the failed predicate under the same immediate transaction, without a racing reread.
                using var read = CreateReadCommand(connection, transaction, write.Entity.EntityId.Value, partition);
                using var reader = read.ExecuteReader();
                if (reader.Read() && (reader.GetString(Mapping.Bindings.Length + 2) != graph
                    || reader.GetString(Mapping.Bindings.Length + 3) != shape))
                    throw ShapeMismatch(write.Entity.EntityId.Value);
                throw Conflict(write.Entity.EntityId.Value, "compare-and-swap target is absent or changed");
            }
            throw ShapeMismatch(write.Entity.EntityId.Value);
        }
        return new(write.Entity, partition, token);
    }

    internal string ValidateWrite(EntityWriteRequest write)
    {
        ArgumentNullException.ThrowIfNull(write);
        ArgumentNullException.ThrowIfNull(write.Entity);
        EntityDefinition.ValidateObservation(write.Entity.Observation);
        var identity = write.Entity.Observation.GetField(Mapping.IdentityField).GetRequiredString();
        if (identity != write.Entity.EntityId.Value)
            throw new InvalidOperationException($"Identity field '{Mapping.IdentityField}' must equal snapshot identity '{write.Entity.EntityId.Value}'.");
        var partition = write.Entity.Observation.GetField(Mapping.PartitionField).GetRequiredString();
        ArgumentException.ThrowIfNullOrWhiteSpace(partition);
        if (write.ExpectedConcurrencyToken is { } token)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(token.Value);
            SqliteScalarCodec.RequireText(token.Value);
        }
        return partition;
    }

    EntitySnapshot ReadSnapshot(SqliteDataReader reader, IReadOnlySet<string>? fields)
    {
        var count = Mapping.Bindings.Length;
        if (reader.GetString(count + 2) != graph || reader.GetString(count + 3) != shape)
            throw new InvalidOperationException($"Stored observation does not belong to expected entity shape '{EntityDefinition.StateShape.QualifiedId}'.");
        var values = ImmutableArray.CreateBuilder<ObservationValue>(count);
        for (var index = 0; index < count; index++)
            values.Add(SqliteScalarCodec.Decode(Mapping.Bindings[index].Contract, reader.GetValue(index)));
        var identity = values[Mapping.IdentityOrdinal].GetRequiredString();
        var partition = values[Mapping.PartitionOrdinal].GetRequiredString();
        ArgumentException.ThrowIfNullOrWhiteSpace(partition);
        var observation = Observation.Create(EntityDefinition.StateShape, Mapping.Layout, values.MoveToImmutable());
        EntityDefinition.ValidateObservation(observation);
        var token = reader.GetString(count + 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        return new(new(new(identity), reader.GetInt64(count), observation), partition, new(token), fields);
    }

    InvalidOperationException ShapeMismatch(string id) =>
        new($"Observation '{EntityType}:{id}' belongs to a different stored shape revision.");

    ObservationConcurrencyConflictException Conflict(string id, string reason) =>
        new($"Observation '{EntityType}:{id}' failed optimistic concurrency validation: {reason}.");
}
