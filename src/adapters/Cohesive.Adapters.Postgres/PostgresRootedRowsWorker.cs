using System.Linq.Expressions;
using Cohesive.Prelude;
using Cohesive.Storage.Materialization;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace Cohesive.Adapters.Postgres;

/// <summary>Publishes rows by affected root using the relation's impact routes and each input's
/// durable PostgreSQL checkpoint. The target must have been initialized with a complete resync.</summary>
public sealed class PostgresRootedRowsWorker<TRow> : BackgroundService
{
    readonly NpgsqlDataSource database;
    readonly PostgresMaterializationStorage storage;
    readonly PostgresMaterializedRows<TRow> target;
    readonly Expression<Func<TRow, string>> rootKey;
    readonly MaterializationPartitionBinding partition;
    readonly MaterializationImpactPlanInterpreter interpreter;
    readonly PostgresMaterializationStateStore checkpoints;
    readonly long leaseKey;
    readonly MaterializationGenerationId generation;
    readonly TimeSpan interval;
    readonly PostgresRowsSchedule? schedule;

    internal PostgresRootedRowsWorker(NpgsqlDataSource database, PostgresMaterializationStorage storage,
        PostgresMaterializedRows<TRow> target, Expression<Func<TRow, string>> rootKey,
        MaterializationPartitionBinding partition, long leaseKey, string generation, TimeSpan interval, PostgresRowsSchedule? schedule)
    {
        this.database = database;
        this.storage = storage;
        this.target = target;
        this.rootKey = rootKey;
        this.partition = partition;
        this.leaseKey = leaseKey;
        this.generation = new(generation);
        this.interval = interval;
        this.schedule = schedule;
        checkpoints = storage.CheckpointStore(database);
        interpreter = new(storage.Materialization.Impact, storage.Materialization.Document.Definition,
            partition.CreateImpactRuntime(storage.Materialization.Impact));
    }

    /// <summary>Catch every bound feed up to its currently visible head. Publish first, checkpoint
    /// second, acknowledge WAL last. Reapplying a page replaces exactly its affected key groups.</summary>
    public async Task RunOnceAsync(CancellationToken cancellationToken = default)
    {
        await using var lease = await PostgresSessionLease.AcquireAsync(database, leaseKey, cancellationToken);
        var context = OperationContext.Create(cancellationToken: cancellationToken);
        var definition = storage.Materialization.Document.Definition;
        var fingerprint = MaterializationDefinitionFingerprinter.Compute(definition);
        var cut = schedule is null ? ((PostgresRowsCheckpoint Cut, bool Repair)?)null
            : await schedule.CaptureAsync(cancellationToken);
        if (cut is { Cut: var status } && status.Requested == status.Applied) return;
        var baseline = new Dictionary<MaterializationPartitionSourceBinding, MaterializationSourcePosition>();
        if (cut is { Repair: true })
        {
            foreach (var input in partition.Sources)
                baseline.Add(input, await input.Source.CaptureCurrentPositionAsync(context, input.Scope));
            await target.ResyncAsync(cancellationToken);
        }
        foreach (var input in partition.Sources)
        {
            var key = new MaterializationProgressKey(definition.Id, fingerprint, generation, input.Scope);
            var current = await checkpoints.LoadAsync(context, key);
            const string owner = "postgres-rooted-rows";
            var progress = current ?? Require(await checkpoints.AcquireFenceAsync(context, key,
                new(Guid.NewGuid().ToString("N")), null, owner));
            if (baseline.TryGetValue(input, out var initialPosition))
            {
                await SaveAsync(initialPosition, []);
                continue;
            }
            var source = (IMaterializationRetainedChangeSource)input.Source;
            var position = progress.LatestChangeCheckpoint?.Position
                ?? await source.CaptureRetainedStartPositionAsync(context, input.Scope);
            if (progress.LatestChangeCheckpoint is { } pending)
                await SettleAsync(context, input, pending);

            while (true)
            {
                var page = await source.ReadChangesAsync(context, new(input.Scope, position,
                    storage.ReadLimits.MaximumPageItems, storage.ReadLimits.MaximumPageBytes));
                if (page.ThroughPosition == position)
                {
                    if (page.State != MaterializationChangePageState.CaughtUp || !page.Deliveries.IsEmpty)
                        throw new InvalidOperationException("Change feed returned a non-advancing page.");
                    break;
                }
                var roots = await interpreter.ResolveRootsAsync(context, input.Scope, generation, page);
                await target.RefreshKeysAsync(rootKey, roots.Select(root => root.Identity).ToArray(), cancellationToken);
                await SaveAsync(page.ThroughPosition, [.. page.Deliveries.Select(item => item.Id)]);
                position = page.ThroughPosition;
                if (page.State == MaterializationChangePageState.CaughtUp) break;
            }
            async Task SaveAsync(MaterializationSourcePosition through, System.Collections.Immutable.ImmutableArray<MaterializationDeliveryId> deliveries)
            {
                var id = Guid.NewGuid().ToString("N");
                var checkpoint = new MaterializationApplicationCheckpoint(new(id), MaterializationCheckpointKind.ChangeProgress,
                    null, null, through, deliveries, context.UtcNow,
                    channelProgress: MaterializationChannelSemantics.CreatePositionedDurableProgress(through));
                progress = Require(await checkpoints.SaveCheckpointAsync(context, key, new(id), progress.Revision,
                    owner, progress.Fence, checkpoint));
                await SettleAsync(context, input, checkpoint);
            }
        }
        if (cut.HasValue) await schedule!.AcknowledgeAsync(cut.Value.Cut.Requested, cancellationToken);
    }

    static MaterializationProgressSnapshot Require(MaterializationProgressMutationResult result) =>
        result.Disposition is MaterializationProgressMutationDisposition.Applied or MaterializationProgressMutationDisposition.Replayed
            ? result.Snapshot! : throw new InvalidOperationException($"Checkpoint mutation failed: {result.Disposition}.");

    static async Task SettleAsync(OperationContext context, MaterializationPartitionSourceBinding input,
        MaterializationApplicationCheckpoint checkpoint)
    {
        var position = checkpoint.Position!;
        var source = (IMaterializationSettlingSource)input.Source;
        var receipt = await source.SettleAsync(context, new(
            PostgresLogicalReplicationMaterializationChangeSource.CreateSettlementId(checkpoint.Id, position),
            checkpoint.Id, position, checkpoint.CommittedAtUtc));
        if (receipt.Disposition is not (MaterializationSourceSettlementDisposition.Acknowledged or MaterializationSourceSettlementDisposition.Replayed))
            throw new InvalidOperationException($"Change feed settlement failed: {receipt.Disposition}.");
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(interval);
        do { await RunOnceAsync(stoppingToken); } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
