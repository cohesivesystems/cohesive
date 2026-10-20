using System.Data;
using System.Globalization;
using System.Text.Json;
using Cohesive.Storage;
using Cohesive.Transitions.Model;
using Npgsql;

namespace Cohesive.Adapters.Postgres;

/// <summary>Enlists entity history in the repository's native transaction. Implementations must never commit it.</summary>
/// <remarks>The caller owns the data source and attachment lifetime. Reads validate canonical state and exact
/// stream identity; appends enforce the supplied consecutive position. Implementations must use the supplied
/// transaction for every storage operation, without side connections or implicit migrations.</remarks>
public abstract class PostgresEntityHistoryStorage
{
    /// <summary>Creates an attachment to one exact data source and immutable authority policy.</summary>
    /// <param name="dataSource">Caller-owned source shared with relational state and receipts.</param>
    /// <param name="authority">Durable authority; never inferred from physical placement.</param>
    /// <param name="lockNamespace">Stable namespace shared by all writers/rebuilders of these streams.</param>
    protected PostgresEntityHistoryStorage(NpgsqlDataSource dataSource, EntityStateAuthority authority, string lockNamespace)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentException.ThrowIfNullOrWhiteSpace(lockNamespace);
        if (!Enum.IsDefined(authority)) throw new ArgumentOutOfRangeException(nameof(authority));
        DataSource = dataSource; Authority = authority; LockNamespace = lockNamespace;
    }
    internal NpgsqlDataSource DataSource { get; }
    internal string LockNamespace { get; }
    /// <summary>The authoritative representation.</summary>
    public EntityStateAuthority Authority { get; }
    /// <summary>Reads and validates an exact stream in the supplied transaction without writing.</summary>
    /// <param name="definition">Canonical entity definition.</param>
    /// <param name="transaction">Borrowed transaction; do not commit or dispose it.</param>
    /// <param name="entityType">Canonical entity type identity.</param>
    /// <param name="partition">Trusted partition.</param>
    /// <param name="id">Subject identity.</param>
    /// <param name="partitionField">Canonical partition field from the relational mapping.</param>
    /// <param name="latestOnly">Acquire the last replacement action for append admission.</param>
    /// <param name="cancellationToken">Invocation cancellation.</param>
    /// <returns>Validated state, or null for an absent stream.</returns>
    /// <exception cref="InvalidOperationException">History violates its identity, authority, integrity or bounded replay contract.</exception>
    /// <exception cref="NpgsqlException">Native storage fails.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    public abstract Task<EntityStateAction?> ReadAsync(EntityDefinition definition, NpgsqlTransaction transaction,
        string entityType, string partition, string id, string partitionField, bool latestOnly, CancellationToken cancellationToken);
    /// <summary>Appends one action, flushing within but never committing the borrowed transaction.</summary>
    /// <param name="transaction">Borrowed transaction.</param>
    /// <param name="entityType">Canonical entity type identity.</param>
    /// <param name="partition">Trusted partition.</param>
    /// <param name="id">Subject identity.</param>
    /// <param name="action">Validated consecutive replacement action.</param>
    /// <param name="cancellationToken">Invocation cancellation.</param>
    /// <exception cref="InvalidOperationException">The action exceeds its admitted encoding or size bounds.</exception>
    /// <exception cref="NpgsqlException">Native storage fails; callers must not retry an ambiguous commit blindly.</exception>
    /// <exception cref="OperationCanceledException">Cancellation is observed.</exception>
    public abstract Task AppendAsync(NpgsqlTransaction transaction, string entityType, string partition, string id,
        EntityStateAction action, CancellationToken cancellationToken);
}

public sealed partial class PostgresEntityRepository
{
    readonly PostgresEntityHistoryStorage? history;

    async Task<EntitySnapshot?> ReadHistory(OperationContext context, string id, EntityReadOptions? options)
    {
        if (options?.PartitionKey is not { } partition || string.IsNullOrWhiteSpace(partition))
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
        var action = await history!.ReadAsync(EntityDefinition, transaction, EntityType, partition, id, mapping.PartitionField, latestOnly, context.CancellationToken).ConfigureAwait(false);
        if (action is null && history.Authority == EntityStateAuthority.Events)
        {
            await using var row = new NpgsqlCommand(sql.ReadByIdentityAndPartition.Text, connection, transaction);
            AddReadParameters(row, sql.ReadByIdentityAndPartition, id, partition);
            await using var reader = await row.ExecuteReaderAsync(context.CancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(context.CancellationToken).ConfigureAwait(false))
                throw new InvalidOperationException("An existing state row has no authoritative history; migrate explicitly.");
        }
        return action;
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
        // First event must not silently adopt an existing row. Event-owned subsequent rows are rebuildable projections.
        var nativeWrite = policy.Authority == EntityStateAuthority.Events ? new EntityWriteRequest(write.Entity) : write;
        var snapshot = await UpsertMapped(context, connection, transaction, nativeWrite,
            createOnly || previous is null && policy.Authority == EntityStateAuthority.Events).ConfigureAwait(false);
        await policy.AppendAsync(transaction, EntityType, partition, id, action, context.CancellationToken).ConfigureAwait(false);
        return policy.Authority == EntityStateAuthority.Events ? new(write.Entity, partition, StreamToken(action.Position)) : snapshot;
    }

    async Task LockHistorySubject(OperationContext context, NpgsqlConnection connection, NpgsqlTransaction transaction, string partition, string id)
    {
        await using var gate = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended($1,0))", connection, transaction);
        Add(gate, JsonSerializer.Serialize(new[] { history!.LockNamespace, EntityType, partition, id }));
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
