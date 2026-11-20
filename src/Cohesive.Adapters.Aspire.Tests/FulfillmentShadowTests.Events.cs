using Cohesive.Adapters.Marten;
using System.Text.Json;
using AspireFirst.Materialization;
using AspireFirst.Orders;
using Cohesive.Adapters.Postgres;
using Cohesive.Application;
using Cohesive.Prelude;
using Cohesive.Storage.Materialization;
using JasperFx.Events;
using Marten;
using Marten.Services;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Npgsql.Replication;

namespace Cohesive.Adapters.Aspire.Tests;

public sealed partial class FulfillmentShadowTests
{
    [ShadowFact]
    public async Task Reservation_move_replays_both_event_derived_views_after_target_write_before_checkpoint()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var ct = timeout.Token;
        var connection = Environment.GetEnvironmentVariable("COHESIVE_SHADOW_POSTGRES")!;
        await using var database = NpgsqlDataSource.Create(connection);
        using var http = new HttpClient { BaseAddress = new(Environment.GetEnvironmentVariable("COHESIVE_SHADOW_ELASTIC")!) };
        var app = ModelActivator.Create<OrderMaterializationApplication>(s => s.AddSingleton(new FulfillmentHistoryPolicy()));
        await app.Orders.Storage.Bindings.PrepareSchemaAsync(database, ct);
        await ShadowBootstrap.InitializeTargetAsync(app.Materializations.OrderDetails, http, ct);
        using var store = DocumentStore.For(o =>
        {
            o.Connection(database);
            o.Events.StreamIdentity = StreamIdentity.AsString;
            o.Events.AddEventType<ReservationChanged>();
        });
        await store.Storage.ApplyAllConfiguredChangesToDatabaseAsync();

        var id = Guid.NewGuid().ToString("N");
        var first = "A-" + id;
        var second = "B-" + id;
        var stream = "reservations/" + id;
        var initial = new Reservation("r-" + id, "local", first, "sku-" + id, 2);
        var moved = initial with { OrderId = second };
        var declaration = app.Materializations.OrderDetails;
        var options = StorageOptions() with
        {
            Replication = new(readInactivityTimeout: TimeSpan.FromSeconds(2)),
            Rebuild = new(100, 1_048_576, 100, 1_048_576, 128, MaterializationRebuildProcessFactory.MaximumStartsPerActivation,
                MaterializationRebuildProcessFactory.MaximumParallelism, 16)
        };
        var bindings = app.Orders.Storage.Bindings;
        var key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        options = ReservationEventFeed.Attach(options, app.Orders.Domain, declaration, store, stream, id, key);
        var reservationInput = Assert.Single(options.EventSources.Keys);
        var storage = new PostgresMaterializationStorage(bindings, declaration, options);
        await storage.InitializeSourcesAsync(database, ct);
        var target = await ShadowElastic.BindAsync(http, declaration, ct);
        Task<BoundMaterialization> Bind() => storage.BindAsync(database, () => new LogicalReplicationConnection(connection), target, key, id, ct);
        var runtime = await Bind();
        var context = OperationContext.Create(cancellationToken: ct);
        var attempt = new MaterializationRebuildAttempt(new(new(id), new(id)), DateTimeOffset.UtcNow);
        var generation = MaterializationRebuildIdentities.Generation(runtime.Resolved.Plan, attempt);
        var live = bindings.Bind(database, "event-proof/live").Query(app.Orders.Queries.OrderDetails, 1000, 1_000_000);
        try
        {
            await using (var conn = await database.OpenConnectionAsync(ct))
            await using (var transaction = await conn.BeginTransactionAsync(ct))
            await using (var session = store.LightweightSession(SessionOptions.ForTransaction(transaction, shouldAutoCommit: false)))
            {
                await Execute(conn, transaction, "INSERT INTO cohesive_orders(order_id,partition_key,status,observation_version) VALUES ($1,'local','Draft',0),($2,'local','Draft',0)", first, second);
                await Execute(conn, transaction, "INSERT INTO cohesive_inventory(sku,partition_key,available,observation_version) VALUES ($1,'local',10,0)", initial.Sku);
                await Execute(conn, transaction, "INSERT INTO cohesive_reservations(reservation_id,order_id,sku,partition_key,quantity,observation_version) VALUES ($1,$2,$3,'local',2,0)", initial.Id, first, initial.Sku);
                session.Events.StartStream(stream, new ReservationChanged(null, initial));
                await session.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
            }
            var rebuilt = await MaterializationShadowExecution.RunAsync(runtime, MaterializationShadowOperation.Rebuild,
                context, attempt, new(id + "/baseline"), new("worker"));
            Assert.Equal(MaterializationSynchronizationRunDisposition.Converged, rebuilt.Disposition);
            await AssertViews(first, second);
            await using (var conn = await database.OpenConnectionAsync(ct))
            await using (var transaction = await conn.BeginTransactionAsync(ct))
            await using (var session = store.LightweightSession(SessionOptions.ForTransaction(transaction, shouldAutoCommit: false)))
            {
                Assert.Equal(1, await Execute(conn, transaction,
                    "UPDATE cohesive_reservations SET order_id=$1,observation_version=1 WHERE reservation_id=$2 AND order_id=$3",
                    second, initial.Id, first));
                session.Events.Append(stream, new ReservationChanged(initial, moved));
                await session.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
            }
            var feed = runtime.Resolved.Plan.ChangeFeeds.Single(f => f.Scope.Input == reservationInput);
            var events = Assert.IsType<MartenMaterializationEventSource<ReservationChanged>>(runtime.Resolved.GetChangeFeed(feed.Id).Source);
            var start = await events.CaptureRetainedStartPositionAsync(context, feed.Scope);
            var creation = await events.ReadChangesAsync(context, new(feed.Scope, start, 1, 1_048_576));
            Assert.Equal(MaterializationChangePageState.MoreAvailable, creation.State);
            Assert.Equal(MaterializationChangeKind.Create, Assert.Single(creation.Deliveries).Change.Kind);
            var move = await events.ReadChangesAsync(context, new(feed.Scope, creation.ThroughPosition, 1, 1_048_576));
            Assert.Equal(MaterializationChangePageState.CaughtUp, move.State);
            Assert.Equal(MaterializationChangeKind.Update, Assert.Single(move.Deliveries).Change.Kind);
            Assert.Equal(move.ThroughPosition, await events.CaptureCurrentPositionAsync(context, feed.Scope));
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await events.ReadChangesAsync(context, new(feed.Scope, creation.ThroughPosition, 1, 1)));
            var progressKey = new MaterializationProgressKey(declaration.Document.Definition.Id,
                declaration.Document.DefinitionFingerprint, generation, feed.Scope);
            var before = await runtime.Resolved.ProgressStore.LoadAsync(context, progressKey);
            var fault = new MaterializationSynchronizationExecutor(runtime.Resolved, runtime.WorkStore,
                MaterializationIndexSyncWorkloadKind.Rebuild, new InterruptAfterWrite());
            var activation = new MaterializationSynchronizationInvocationId(id + "/move");
            await Assert.ThrowsAsync<TestInterruption>(async () =>
                await fault.RunFeedAsync(context, attempt, feed.Id, activation, new("worker")));
            Assert.Equal(before!.LatestChangeCheckpoint, (await runtime.Resolved.ProgressStore.LoadAsync(context, progressKey))!.LatestChangeCheckpoint);
            Assert.NotNull((await runtime.WorkStore.LoadAsync(context, fault.GetWorkKey(attempt)))?.PendingWork);
            await AssertViews(second, first);
            runtime = await Bind();
            var resumed = new MaterializationSynchronizationExecutor(runtime.Resolved, runtime.WorkStore, MaterializationIndexSyncWorkloadKind.Rebuild);
            var result = await resumed.RunFeedAsync(context, attempt, feed.Id, activation, new("worker"));
            Assert.Equal(MaterializationCatchUpFeedDisposition.CaughtUp, result.Disposition);
            Assert.NotEqual(before.LatestChangeCheckpoint, (await runtime.Resolved.ProgressStore.LoadAsync(context, progressKey))!.LatestChangeCheckpoint);
            Assert.Null((await runtime.WorkStore.LoadAsync(context, resumed.GetWorkKey(attempt)))?.PendingWork);
            await AssertViews(second, first);
        }
        finally
        {
            await using var conn = await database.OpenConnectionAsync(ct);
            await Execute(conn, null, "DELETE FROM cohesive_reservations WHERE reservation_id=$1", initial.Id);
            await Execute(conn, null, "DELETE FROM cohesive_inventory WHERE sku=$1", initial.Sku);
            await Execute(conn, null, "DELETE FROM cohesive_orders WHERE order_id IN ($1,$2)", first, second);
        }
        async Task AssertViews(string owner, string other)
        {
            foreach (var order in new[]{ owner, other })
            {
                var expected = await live.ReadAsync(order, ct);
                Assert.NotNull(expected);
                Assert.Equal(order == owner ? 1 : 0, expected.Reservations.Count);
                var actual = await target.ReadValueAsync(context, generation, declaration.Document.DefinitionFingerprint, MaterializationItemId.FromRootIdentity(order));
                Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(expected, Json), JsonSerializer.SerializeToElement(actual, Json)));
            }
        }
        async Task<int> Execute(NpgsqlConnection conn, NpgsqlTransaction? tx, string sql, params string[] values)
        {
            await using var command = new NpgsqlCommand(sql, conn, tx);
            foreach (var value in values) command.Parameters.AddWithValue(value);
            return await command.ExecuteNonQueryAsync(ct);
        }
    }
}
