using Cohesive.Execution;
using Cohesive.Storage;
using Cohesive.Storage.Processes;
using Npgsql;

namespace Cohesive.Adapters.Postgres;

/// <summary>Activation-local worker ownership attached to atomic PostgreSQL entity/receipt commits.</summary>
/// <remarks>Created by the process store after lease acquisition. The process root remains locked until the entity
/// transaction completes, serializing mutation with lease takeover. This is physical authority, never part of the
/// logical operation identity or receipt fingerprint. The caller owns the store and its data source.</remarks>
public sealed class PostgresProcessWorkerFence
{
    readonly PostgresProcessDurableStore store;
    readonly ProcessDurableStoreSnapshot acquired;
    internal PostgresProcessWorkerFence(PostgresProcessDurableStore store, ProcessDurableStoreSnapshot acquired)
    { this.store = store; this.acquired = acquired; }

    internal void RequireDataSource(NpgsqlDataSource source) => store.RequireFenceDataSource(source);
    internal Task ValidateAsync(OperationContext context, NpgsqlConnection connection, NpgsqlTransaction transaction,
        EntityTransitionOperationRequest request) => store.ValidateWorkerFenceAsync(context, connection, transaction, acquired, request);
}

public sealed partial class PostgresProcessDurableStore
{
    /// <summary>Binds acquired worker ownership for subsequent atomic entity/receipt commits on this data source.</summary>
    /// <param name="acquired">Snapshot returned by successful worker acquisition; checked again under the commit lock.</param>
    /// <returns>An immutable activation-local guard; it owns no connection or transaction.</returns>
    /// <exception cref="ArgumentNullException">The snapshot is null.</exception>
    /// <exception cref="ArgumentException">The snapshot has no acquired worker lease.</exception>
    public PostgresProcessWorkerFence BindWorkerFence(ProcessDurableStoreSnapshot acquired)
    {
        ArgumentNullException.ThrowIfNull(acquired);
        if (acquired.WorkerLease is null) throw new ArgumentException("Worker ownership must be acquired before binding its fence.", nameof(acquired));
        return new(this, acquired);
    }

    internal void RequireFenceDataSource(NpgsqlDataSource source)
    {
        if (!ReferenceEquals(source, dataSource))
            throw new ArgumentException("Worker fencing requires the process and entity repositories to share one native data source.", nameof(source));
    }

    internal async Task ValidateWorkerFenceAsync(OperationContext context, NpgsqlConnection connection,
        NpgsqlTransaction transaction, ProcessDurableStoreSnapshot acquired, EntityTransitionOperationRequest request)
    {
        var expected = acquired.Checkpoint;
        var expectedLease = acquired.WorkerLease!;
        if (request.Operation.Continuation != expected.ContinuationIdentity
            || request.AuthorityScope != expected.Start.Request.Context.Authorization.AuthorityScope)
            throw new ProcessWorkerFenceException(ProcessStoreMutationDisposition.StaleFence);
        var execute = PostgresNpgsqlExecution.CreateTransactionExecutor(connection, transaction);
        var root = await LoadRootAsync(execute, expected.ContinuationIdentity.ProcessInstanceId,
            context.CancellationToken, forUpdate: true).ConfigureAwait(false);
        if (root is null) throw new ProcessWorkerFenceException(ProcessStoreMutationDisposition.StaleFence);
        var current = await LoadAggregateAsync(execute, expected.ContinuationIdentity.ProcessInstanceId, root,
            context.CancellationToken).ConfigureAwait(false);
        if (current.WorkerLease is not { } lease || lease.Owner != expectedLease.Owner || lease.Fence != expectedLease.Fence
            || current.Checkpoint.ContinuationIdentity != expected.ContinuationIdentity
            || current.Checkpoint.Start.Request.Definition != expected.Start.Request.Definition
            || current.Checkpoint.Start.Request.Context.Authorization.AuthorityScope != request.AuthorityScope
            || current.Checkpoint.Control.Revision != expected.Control.Revision
            || current.Checkpoint.Continuation.CompletedActivationCount != expected.Continuation.CompletedActivationCount)
            throw new ProcessWorkerFenceException(ProcessStoreMutationDisposition.StaleFence);
        var providerNow = await ReadProviderNowAsync(connection, transaction, context.CancellationToken).ConfigureAwait(false);
        if (providerNow >= lease.ExpiresAtUtc)
            throw new ProcessWorkerFenceException(ProcessStoreMutationDisposition.LeaseExpired);
    }
}

public sealed partial class PostgresEntityRepository
{
    readonly PostgresProcessWorkerFence? workerFence;

    /// <summary>Scopes atomic transition receipt commits to an acquired Process worker.</summary>
    /// <param name="fence">Guard bound by the process store after worker acquisition.</param>
    /// <returns>A repository sharing immutable preparation and the caller-owned data source with this repository.</returns>
    /// <remarks>Only atomic transition receipt commits are fenced. Read-only receipt replay remains available;
    /// ordinary CRUD is not a Process operation and receives no worker guarantee.</remarks>
    /// <exception cref="ArgumentNullException">The guard is null.</exception>
    /// <exception cref="ArgumentException">Process and entity storage use different data sources.</exception>
    /// <exception cref="InvalidOperationException">Transition receipts are not configured.</exception>
    public PostgresEntityRepository WithWorkerFence(PostgresProcessWorkerFence fence)
    {
        ArgumentNullException.ThrowIfNull(fence);
        if (receipts is null) throw new InvalidOperationException("Worker-fenced writes require atomic transition receipts.");
        fence.RequireDataSource(runtime.DataSource);
        return new(this, fence);
    }

    PostgresEntityRepository(PostgresEntityRepository source, PostgresProcessWorkerFence fence)
    {
        runtime = source.runtime; mapping = source.mapping; sql = source.sql; layout = source.layout;
        identityOrdinal = source.identityOrdinal; partitionOrdinal = source.partitionOrdinal;
        history = source.history; receipts = source.receipts; receiptFenceNames = source.receiptFenceNames;
        EntityDefinition = source.EntityDefinition; BatchCapabilities = source.BatchCapabilities;
        workerFence = fence;
    }
}
