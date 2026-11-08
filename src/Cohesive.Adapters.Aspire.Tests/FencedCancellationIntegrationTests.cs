using AspireFirst.Orders;
using Cohesive.Adapters.Postgres;
using Cohesive.Adapters.Postgres.Services;
using Cohesive.Api.Execution.Services;
using Cohesive.Application;
using Cohesive.Execution;
using Cohesive.Identity;
using Cohesive.Model;
using Cohesive.Processes.Execution;
using Cohesive.Storage;
using Cohesive.Storage.Processes;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Cohesive.Adapters.Aspire.Tests;

[Collection("Order PostgreSQL schema")]
public sealed class FencedCancellationIntegrationTests
{
    [OrderStorageIntegrationTests.PostgresFact]
    public async Task Fence_binding_requires_acquired_ownership_receipts_and_the_same_data_source()
    {
        await using var fixture = await Fixture.Create();
        var unacquired = (await fixture.Store.LoadAsync(fixture.Context, fixture.Continuation.ProcessInstanceId))!;
        Assert.Throws<ArgumentException>(() => fixture.Store.BindWorkerFence(unacquired));
        var acquired = (await fixture.AcquireReplacement()).Snapshot!;
        var fence = fixture.Store.BindWorkerFence(acquired);
        var repository = (PostgresEntityRepository)fixture.persistence.Repository(ValueContract.FromShape(fixture.Model.Domain.Inventory.Definition.Shape), fixture.receipts);
        Assert.NotSame(repository, repository.WithWorkerFence(fence));
        await using var other = NpgsqlDataSource.Create(fixture.Database.ConnectionString);
        var foreignFence = new PostgresProcessDurableStore(other, fixture.options).BindWorkerFence(acquired);
        Assert.Throws<ArgumentException>(() => repository.WithWorkerFence(foreignFence));
        var noReceipts = (PostgresEntityRepository)fixture.persistence.Repository(ValueContract.FromShape(fixture.Model.Domain.Inventory.Definition.Shape));
        Assert.Throws<InvalidOperationException>(() => noReceipts.WithWorkerFence(fence));
    }

    [FulfillmentProcessIntegrationTests.PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Expired_or_replaced_worker_cannot_commit_and_replacement_restores_same_continuation(bool replaceBeforeResume)
    {
        await using var fixture = await Fixture.Create();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var old = fixture.Runtime("old", async (context, invocation, inner) =>
        {
            if (invocation.Definition == fixture.Model.Domain.InventoryTransitions.Release.Reference)
            {
                var frozen = new FrozenTimeProvider(context.UtcNow);
                entered.TrySetResult();
                await resume.Task.WaitAsync(context.CancellationToken);
                context = context with { TimeProvider = frozen };
            }
            return await inner.InvokeTransitionAsync(context, invocation);
        });
        var running = fixture.Advance(old);
        await entered.Task.WaitAsync(fixture.Context.CancellationToken);
        var checkpoint = (await fixture.Store.LoadAsync(fixture.Context, fixture.Continuation.ProcessInstanceId))!;
        await fixture.WaitForExpiry();
        if (replaceBeforeResume)
        {
            var acquired = await fixture.AcquireReplacement();
            Assert.True(acquired.Snapshot!.WorkerLease!.Fence.Ordinal > checkpoint.WorkerLease!.Fence.Ordinal);
        }
        resume.SetResult();
        var rejected = await running;
        Assert.Equal(replaceBeforeResume ? ProcessDurableRuntimeDisposition.StaleFence : ProcessDurableRuntimeDisposition.LeaseExpired, rejected.Disposition);
        Assert.Equal(3, await fixture.Stock());
        var retained = (await fixture.Store.LoadAsync(fixture.Context, fixture.Continuation.ProcessInstanceId))!;
        Assert.Equal(checkpoint.Checkpoint.Continuation.CompletedActivationCount, retained.Checkpoint.Continuation.CompletedActivationCount);
        Assert.Equal(ExecutionTerminalOutcomeKind.None, retained.Checkpoint.Continuation.Terminal.Kind);
        await fixture.AssertCompleted(await fixture.Advance(fixture.Runtime("replacement")));
    }

    [FulfillmentProcessIntegrationTests.PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task New_owner_replays_committed_release_and_mark_receipts_after_worker_crash(bool crashAfterMark)
    {
        await using var fixture = await Fixture.Create();
        var selected = crashAfterMark ? fixture.Model.Domain.ReservationTransitions.MarkReleased.Reference : fixture.Model.Domain.InventoryTransitions.Release.Reference;
        var crashed = false;
        var old = fixture.Runtime("old", async (context, invocation, inner) =>
        {
            var result = await inner.InvokeTransitionAsync(context, invocation);
            if (!crashed && invocation.Definition == selected) { crashed = true; throw new InjectedCrash(); }
            return result;
        });
        await Assert.ThrowsAsync<InjectedCrash>(() => fixture.Advance(old));
        Assert.InRange(await fixture.Stock(), 4, 5);
        var oldSnapshot = (await fixture.Store.LoadAsync(fixture.Context, fixture.Continuation.ProcessInstanceId))!;
        await fixture.WaitForExpiry();
        await fixture.AssertCompleted(await fixture.Advance(fixture.Runtime("replacement")));
        var restored = (await fixture.Store.LoadAsync(fixture.Context, fixture.Continuation.ProcessInstanceId))!;
        Assert.Equal(oldSnapshot.Checkpoint.ContinuationIdentity, restored.Checkpoint.ContinuationIdentity);
        Assert.True(restored.WorkerLease!.Fence.Ordinal > oldSnapshot.WorkerLease!.Fence.Ordinal);
    }

    [OrderStorageIntegrationTests.PostgresFact]
    public async Task Already_authorized_entity_transaction_blocks_takeover_until_receipt_commit()
    {
        await using var fixture = await Fixture.Create();
        await using var gate = await fixture.Database.OpenConnectionAsync(fixture.Context.CancellationToken);
        var key = Random.Shared.NextInt64(1, long.MaxValue);
        await using (var acquire = new NpgsqlCommand("SELECT pg_advisory_lock($1)", gate))
        { acquire.Parameters.AddWithValue(key); await acquire.ExecuteNonQueryAsync(fixture.Context.CancellationToken); }
        var fault = "fenced_" + Guid.NewGuid().ToString("N");
        Task<ProcessDurableActivationResult>? running = null;
        try
        {
            await using (var install = fixture.Database.CreateCommand($"""
                CREATE FUNCTION public.{fault}() RETURNS trigger LANGUAGE plpgsql AS
                'BEGIN PERFORM pg_advisory_xact_lock({key}); RETURN NEW; END';
                CREATE TRIGGER {fault} BEFORE UPDATE ON public.cohesive_inventory
                FOR EACH ROW WHEN (NEW.sku = '{fixture.Sku}') EXECUTE FUNCTION public.{fault}();
                """)) await install.ExecuteNonQueryAsync(fixture.Context.CancellationToken);
            running = fixture.Advance(fixture.Runtime("old"));
            var writerPid = await fixture.WaitForBlockedBy(gate.ProcessID);
            await fixture.WaitForExpiry();
            var takeover = fixture.AcquireReplacement();
            await fixture.WaitForBlockedBy(writerPid);
            Assert.False(takeover.IsCompleted);
            await using (var release = new NpgsqlCommand("SELECT pg_advisory_unlock($1)", gate))
            { release.Parameters.AddWithValue(key); await release.ExecuteNonQueryAsync(fixture.Context.CancellationToken); }
            Assert.Equal(ProcessStoreMutationDisposition.Applied, (await takeover).Disposition);
            var oldResult = await running;
            Assert.Contains(oldResult.Disposition, new[] { ProcessDurableRuntimeDisposition.StaleFence, ProcessDurableRuntimeDisposition.LeaseExpired });
            Assert.InRange(await fixture.Stock(), 4, 5);
            await fixture.AssertCompleted(await fixture.Advance(fixture.Runtime("replacement")));
        }
        finally
        {
            await using (var unlock = new NpgsqlCommand("SELECT pg_advisory_unlock($1)", gate))
            { unlock.Parameters.AddWithValue(key); await unlock.ExecuteNonQueryAsync(); }
            if (running is not null) try { await running; } catch (Exception) { }
            await using var cleanup = fixture.Database.CreateCommand($"DROP TRIGGER IF EXISTS {fault} ON public.cohesive_inventory; DROP FUNCTION IF EXISTS public.{fault}()");
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    sealed class Fixture : IAsyncDisposable
    {
        internal readonly FulfillmentApplication Model = ModelActivator.Create<FulfillmentApplication>(services => services
            .AddSingleton(new FulfillmentHistoryPolicy(EntityStateAuthority.State))
            .AddSingleton(new FulfillmentExecutionPolicy { Lifetime = ProcessExecutionLifetime.Durable }));
        internal readonly NpgsqlDataSource Database = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("COHESIVE_ORDER_EXAMPLE_TEST_CONNECTION_STRING")!);
        readonly CancellationTokenSource deadline = new(TimeSpan.FromSeconds(40));
        internal readonly string OrderId = Guid.NewGuid().ToString("D");
        internal readonly string Sku = "fenced-" + Guid.NewGuid().ToString("N");
        internal readonly PostgresProcessDurableStoreOptions options = new("fenced/" + Guid.NewGuid().ToString("N"), maximumAggregateBytes: 64L * 1024 * 1024);
        internal OperationContext Context => OperationContext.Create(cancellationToken: deadline.Token).WithIdentityContext(FulfillmentDemoIdentity.Context);
        internal ProcessContinuationIdentity Continuation => new(new(OrderId), new("attempt/1"));
        internal PostgresProcessDurableStore Store => new(Database, options);
        internal PostgresPersistenceRegistration persistence = null!;
        internal PostgresTransitionReceiptStorage receipts = null!;
        IServicePersistence servicePersistence = null!;
        PreparedServiceProcess prepared = null!;
        static readonly TimeSpan Lease = TimeSpan.FromSeconds(3);

        internal static async Task<Fixture> Create()
        {
            var fixture = new Fixture();
            await fixture.Model.Storage.Bindings.PrepareSchemaAsync(fixture.Database, cancellationToken: fixture.deadline.Token);
            var receiptOptions = new PostgresTransitionReceiptOptions("public", "cohesive_process_receipts", FulfillmentStoragePolicy.LocalPartition);
            await using (var command = fixture.Database.CreateCommand(receiptOptions.SchemaSql)) await command.ExecuteNonQueryAsync(fixture.deadline.Token);
            fixture.receipts = await receiptOptions.BindAsync(fixture.Database);
            fixture.persistence = fixture.Model.Storage.Bindings.Bind(fixture.Database, authority: "test/fenced-cancellation");
            fixture.servicePersistence = fixture.persistence.ForServices(fixture.receipts);
            await fixture.persistence.Repository(fixture.Model.Domain.Orders).Upsert(fixture.Context, new Order(fixture.OrderId, FulfillmentStoragePolicy.LocalPartition, OrderStatus.Submitted));
            await fixture.persistence.Repository(fixture.Model.Domain.Inventory).Upsert(fixture.Context, new InventoryItem(fixture.Sku, FulfillmentStoragePolicy.LocalPartition, 3));
            var reservations = fixture.persistence.Repository(fixture.Model.Domain.Reservations);
            await reservations.Upsert(fixture.Context, new Reservation(fixture.OrderId, FulfillmentStoragePolicy.LocalPartition, fixture.OrderId, fixture.Sku, 2));
            await reservations.Upsert(fixture.Context, new Reservation(Guid.NewGuid().ToString("D"), FulfillmentStoragePolicy.LocalPartition, fixture.OrderId, fixture.Sku, 1));
            fixture.prepared = fixture.Prepare(null);
            await fixture.Store.EnsureCreatedAsync(fixture.Context);
            var plan = fixture.prepared.Plan;
            var request = new ProcessStartRequest(ProcessStartRequest.CurrentSchemaVersion, plan.DefinitionReference,
                new(new(fixture.OrderId), new(fixture.OrderId), new(fixture.OrderId), new("local-demo", new("aspire-first", FulfillmentStoragePolicy.LocalPartition), "test"),
                    fixture.Context.UtcNow, plan.Document.Metadata.Provenance), fixture.Continuation,
                PortableValue.Concrete(plan.Definition.Input, ObservationValue.FromObject(new CancelOrder(fixture.OrderId))));
            var start = new ProcessStartReferenceEvaluator().Evaluate(request, new(), fixture.Context.UtcNow);
            Assert.Equal(ProcessDurableRuntimeDisposition.Applied, (await fixture.Runtime("initialize").InitializeAsync(fixture.Context, plan, start.Receipt!)).Disposition);
            return fixture;
        }

        PreparedServiceProcess Prepare(PostgresProcessWorkerFence? fence) => Model.Api.Cancellation!.Service.PrepareProcess(
            Model.Api.Cancellation.Process.Definition, contract =>
            {
                var repository = (PostgresEntityRepository)persistence.Repository(contract, receipts);
                return fence is null ? repository : repository.WithWorkerFence(fence);
            }, persistence: servicePersistence);

        internal ProcessDurableRuntime Runtime(string owner,
            Func<OperationContext, ProcessTransitionInvocation, IAsyncProcessReferenceHost, ValueTask<ProcessOperationResult>>? hook = null)
        {
            var store = Store;
            return new(store, activationHostFactory: snapshot =>
            {
                var host = Prepare(store.BindWorkerFence(snapshot)).Host;
                return hook is null ? host : new HookHost(host, hook);
            }, new(owner, owner == "old" ? Lease : TimeSpan.FromSeconds(20)));
        }
        internal Task<ProcessDurableActivationResult> Advance(ProcessDurableRuntime runtime) => runtime.AdvanceAsync(Context, prepared.Plan, Continuation,
            new(new("aspire-first", FulfillmentStoragePolicy.LocalPartition), new(OrderId),
                new(InteractionDurabilityDemand.Durable, InteractionVisibilityDemand.AfterOriginCommit), prepared.Plan.Document.Metadata.Provenance));
        internal async Task WaitForExpiry()
        {
            var lease = (await Store.LoadAsync(Context, Continuation.ProcessInstanceId))!.WorkerLease!;
            await using var now = Database.CreateCommand("SELECT clock_timestamp()");
            var delay = lease.ExpiresAtUtc - (DateTime)(await now.ExecuteScalarAsync(deadline.Token))! + TimeSpan.FromMilliseconds(75);
            if (delay > TimeSpan.Zero) await Task.Delay(delay, deadline.Token);
        }
        internal async Task<ProcessStoreMutationResult> AcquireReplacement()
        {
            var store = Store;
            var loaded = (await store.LoadAsync(Context, Continuation.ProcessInstanceId))!;
            return await store.AcquireWorkerAsync(Context, Continuation.ProcessInstanceId, loaded.Revision, "replacement", TimeSpan.FromSeconds(20), Context.UtcNow);
        }
        internal async Task<int> WaitForBlockedBy(int blocker)
        {
            while (true)
            {
                await using var query = Database.CreateCommand("SELECT pid FROM pg_stat_activity WHERE $1=ANY(pg_blocking_pids(pid)) LIMIT 1");
                query.Parameters.AddWithValue(blocker);
                if (await query.ExecuteScalarAsync(deadline.Token) is int pid) return pid;
                await Task.Delay(20, deadline.Token);
            }
        }
        internal async Task<int> Stock() => (await persistence.Repository(Model.Domain.Inventory).TryGetEntity(Context, Sku, new(partitionKey: FulfillmentStoragePolicy.LocalPartition)))!.Available;
        internal async Task AssertCompleted(ProcessDurableActivationResult result)
        {
            Assert.Equal(ExecutionTerminalOutcomeKind.Completed, result.Snapshot!.Checkpoint.Continuation.Terminal.Kind);
            Assert.Equal(Continuation, result.Snapshot.Checkpoint.ContinuationIdentity);
            Assert.Equal(6, await Stock());
            Assert.Equal(OrderStatus.Cancelled, (await persistence.Repository(Model.Domain.Orders).TryGetEntity(Context, OrderId, new(partitionKey: FulfillmentStoragePolicy.LocalPartition)))!.Status);
            await using var count = Database.CreateCommand("SELECT count(*) FROM public.cohesive_process_receipts WHERE subject_id=$1");
            count.Parameters.AddWithValue(Sku);
            Assert.Equal(2L, await count.ExecuteScalarAsync(deadline.Token));
        }
        public async ValueTask DisposeAsync()
        {
            foreach (var sql in new[] {
                "DELETE FROM public.cohesive_process_receipts WHERE subject_id=$1 OR subject_id=$2 OR subject_id IN (SELECT reservation_id FROM public.cohesive_reservations WHERE order_id=$1)",
                "DELETE FROM public.cohesive_reservations WHERE order_id=$1 AND $2 IS NOT NULL",
                "DELETE FROM public.cohesive_orders WHERE order_id=$1 AND $2 IS NOT NULL",
                "DELETE FROM public.cohesive_inventory WHERE sku=$2 AND $1 IS NOT NULL" })
            {
                await using var command = Database.CreateCommand(sql);
                command.Parameters.AddWithValue(OrderId); command.Parameters.AddWithValue(Sku);
                await command.ExecuteNonQueryAsync();
            }
            foreach (var table in new[] { options.InstanceTable, options.PageTable })
            {
                await using var cleanup = Database.CreateCommand($"DELETE FROM cohesive.\"{table}\" WHERE authority_id=$1");
                cleanup.Parameters.AddWithValue(options.AuthorityId); await cleanup.ExecuteNonQueryAsync();
            }
            await Database.DisposeAsync(); deadline.Dispose();
        }
    }

    sealed class HookHost(IAsyncProcessReferenceHost inner,
        Func<OperationContext, ProcessTransitionInvocation, IAsyncProcessReferenceHost, ValueTask<ProcessOperationResult>> hook) : IAsyncProcessReferenceHost
    {
        public ValueTask<ProcessOperationResult> InvokeTransitionAsync(OperationContext context, ProcessTransitionInvocation invocation) => hook(context, invocation, inner);
        public ValueTask<ProcessOperationResult> EvaluateRelationAsync(OperationContext context, ProcessRelationEvaluation evaluation) => inner.EvaluateRelationAsync(context, evaluation);
        public ValueTask<ProcessSignalTargetResult> ResolveSignalTargetAsync(OperationContext context, ProcessSignalTargetResolution resolution) => inner.ResolveSignalTargetAsync(context, resolution);
    }
    sealed class FrozenTimeProvider(DateTimeOffset now) : TimeProvider
    { public override DateTimeOffset GetUtcNow() => now; }
    sealed class InjectedCrash : Exception;
}
