using AspireFirst.Orders;
using Cohesive.Adapters.Postgres;
using Cohesive.Adapters.Postgres.Services;
using Cohesive.Api.Execution.Services;
using Cohesive.Execution;
using Cohesive.Identity;
using Cohesive.Model;
using Cohesive.Prelude;
using Cohesive.Processes.Execution;
using Cohesive.Storage;
using Cohesive.Application;
using Microsoft.Extensions.DependencyInjection;
using Cohesive.Storage.Processes;
using Npgsql;

namespace Cohesive.Adapters.Aspire.Tests;

[Collection("Order PostgreSQL schema")]
public sealed class CancellationIntegrationTests
{
    [FulfillmentProcessIntegrationTests.PostgresTheory]
    [InlineData("none")]
    [InlineData("before-release")]
    [InlineData("after-release")]
    [InlineData("after-mark")]
    public async Task Cancellation_receipts_resume_after_partial_failure_and_fence_other_occurrences(string failureStage)
    {
        await using var fixture = await Fixture.Create();
        var identity = new ProcessContinuationIdentity(new("cancel/" + fixture.OrderId), new("attempt/1"));
        Task<CancellationOutcome> Execute(ProcessContinuationIdentity occurrence) => fixture.Execute(occurrence);
        if (failureStage != "none")
        {
            var table = failureStage switch
            {
                "before-release" => "cohesive_inventory",
                "after-release" => "cohesive_reservations",
                _ => "cohesive_orders"
            };
            var predicate = failureStage switch
            {
                "before-release" => $"NEW.sku = '{fixture.Sku}'",
                "after-release" => $"NEW.reservation_id = '{fixture.OrderId}'",
                _ => $"NEW.order_id = '{fixture.OrderId}' AND NEW.status = 'Cancelled'"
            };
            var fault = "cancellation_" + Guid.NewGuid().ToString("N");
            try
            {
                await using (var command = fixture.Database.CreateCommand($"""
                    CREATE FUNCTION public.{fault}() RETURNS trigger LANGUAGE plpgsql AS
                    'BEGIN RAISE EXCEPTION ''injected cancellation failure''; END';
                    CREATE TRIGGER {fault} BEFORE UPDATE ON public.{table}
                    FOR EACH ROW WHEN ({predicate}) EXECUTE FUNCTION public.{fault}();
                    """)) await command.ExecuteNonQueryAsync();
                var interrupted = await Assert.ThrowsAsync<PostgresException>(() => Execute(identity));
                Assert.Equal("P0001", interrupted.SqlState);
                Assert.Equal(OrderStatus.Cancelling, (await fixture.Order()).Status);
                var expectedStock = failureStage == "before-release" ? 3 : 5;
                Assert.Equal(expectedStock, (await fixture.Inventory()).Available);
                Assert.Equal(failureStage == "after-mark", (await fixture.Reservation()).Released);
                Assert.Equal(CancellationOutcome.InProgress, await Execute(new(new("different/" + fixture.OrderId), new("attempt/1"))));
                Assert.Equal(expectedStock, (await fixture.Inventory()).Available);
            }
            finally
            {
                await using var cleanup = fixture.Database.CreateCommand($"DROP TRIGGER IF EXISTS {fault} ON public.{table}; DROP FUNCTION IF EXISTS public.{fault}()");
                await cleanup.ExecuteNonQueryAsync();
            }
        }
        Assert.Equal(CancellationOutcome.Cancelled, await Execute(identity));
        Assert.Equal(OrderStatus.Cancelled, (await fixture.Order()).Status);
        Assert.Equal(5, (await fixture.Inventory()).Available);
        Assert.True((await fixture.Reservation()).Released);
        Assert.Equal(CancellationOutcome.Cancelled, await Execute(identity));
        Assert.Equal(CancellationOutcome.AlreadyCancelled, await Execute(new(new("new/" + fixture.OrderId), new("attempt/1"))));
        Assert.Equal(5, (await fixture.Inventory()).Available);
    }

    [FulfillmentProcessIntegrationTests.PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Multiple_reservations_sharing_inventory_resume_without_releasing_twice(bool failAfterSecondRelease)
    {
        await using var fixture = await Fixture.Create(extraReservation: true);
        var occurrence = new ProcessContinuationIdentity(new("multi/" + fixture.OrderId), new("attempt/1"));
        var secondId = (await fixture.CaptureReservationIds(occurrence))[1];
        var fault = "cancellation_" + Guid.NewGuid().ToString("N");
        var table = failAfterSecondRelease ? "cohesive_reservations" : "cohesive_inventory";
        var predicate = failAfterSecondRelease
            ? $"NEW.reservation_id = '{secondId}'"
            : $"NEW.sku = '{fixture.Sku}' AND NEW.available > 5";
        try
        {
            await using (var command = fixture.Database.CreateCommand($"""
                CREATE FUNCTION public.{fault}() RETURNS trigger LANGUAGE plpgsql AS
                'BEGIN RAISE EXCEPTION ''injected second reservation failure''; END';
                CREATE TRIGGER {fault} BEFORE UPDATE ON public.{table}
                FOR EACH ROW WHEN ({predicate}) EXECUTE FUNCTION public.{fault}();
                """)) await command.ExecuteNonQueryAsync();
            var failure = await Assert.ThrowsAsync<PostgresException>(() => fixture.Execute(occurrence));
            Assert.Equal("P0001", failure.SqlState);
            var partialStock = failAfterSecondRelease ? 6 : secondId == fixture.ExtraReservationId ? 5 : 4;
            Assert.Equal(partialStock, (await fixture.Inventory()).Available);
            Assert.Equal(OrderStatus.Cancelling, (await fixture.Order()).Status);
            Assert.Equal(CancellationOutcome.InProgress, await fixture.Execute(new(new("other/" + fixture.OrderId), new("attempt/1"))));
            Assert.Equal(partialStock, (await fixture.Inventory()).Available);
        }
        finally
        {
            await using var cleanup = fixture.Database.CreateCommand($"DROP TRIGGER IF EXISTS {fault} ON public.{table}; DROP FUNCTION IF EXISTS public.{fault}()");
            await cleanup.ExecuteNonQueryAsync();
        }
        Assert.Equal(CancellationOutcome.Cancelled, await fixture.Execute(occurrence));
        Assert.Equal(CancellationOutcome.Cancelled, await fixture.Execute(occurrence));
        Assert.Equal(6, (await fixture.Inventory()).Available);
        await using var released = fixture.Database.CreateCommand("SELECT count(*) FROM public.cohesive_reservations WHERE order_id=$1 AND released");
        released.Parameters.AddWithValue(fixture.OrderId);
        Assert.Equal(2L, await released.ExecuteScalarAsync());
    }

    [OrderStorageIntegrationTests.PostgresFact]
    public async Task Multiple_inventory_items_each_receive_their_recorded_quantity()
    {
        await using var fixture = await Fixture.Create(extraReservation: true);
        await using (var setup = fixture.Database.CreateCommand("""
            WITH added AS (INSERT INTO public.cohesive_inventory(sku,partition_key,available,observation_version) VALUES ($1,$2,7,1))
            UPDATE public.cohesive_reservations SET sku=$1 WHERE reservation_id=$3
            """))
        {
            setup.Parameters.AddWithValue(fixture.Sku + "-second");
            setup.Parameters.AddWithValue(FulfillmentStoragePolicy.LocalPartition);
            setup.Parameters.AddWithValue(fixture.ExtraReservationId);
            await setup.ExecuteNonQueryAsync();
        }
        Assert.Equal(CancellationOutcome.Cancelled, await fixture.Execute(new(new(fixture.OrderId), new("attempt/1"))));
        Assert.Equal(5, (await fixture.Inventory()).Available);
        await using var second = fixture.Database.CreateCommand("SELECT available FROM public.cohesive_inventory WHERE sku=$1");
        second.Parameters.AddWithValue(fixture.Sku + "-second");
        Assert.Equal(8, await second.ExecuteScalarAsync());
    }

    [OrderStorageIntegrationTests.PostgresFact]
    public async Task Oversized_reservation_set_fails_before_claim_instead_of_truncating()
    {
        await using var fixture = await Fixture.Create();
        await using (var setup = fixture.Database.CreateCommand("""
            INSERT INTO public.cohesive_reservations(reservation_id,partition_key,order_id,sku,quantity,released,observation_version)
            SELECT $1||'-extra-'||n, $2, $1, $3, 1, false, 1 FROM generate_series(1,$4) n
            """))
        {
            setup.Parameters.AddWithValue(fixture.OrderId);
            setup.Parameters.AddWithValue(FulfillmentStoragePolicy.LocalPartition);
            setup.Parameters.AddWithValue(fixture.Sku);
            setup.Parameters.AddWithValue(CancellationProcess.MaximumReservations);
            await setup.ExecuteNonQueryAsync();
        }
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Execute(new(new(fixture.OrderId), new("attempt/1"))));
        Assert.Contains("complete-result row bound", error.Message);
        Assert.Equal(OrderStatus.Submitted, (await fixture.Order()).Status);
        Assert.Equal(3, (await fixture.Inventory()).Available);
    }

    [OrderStorageIntegrationTests.PostgresFact]
    public async Task Reservation_added_after_release_begins_prevents_completed_cancellation()
    {
        await using var fixture = await Fixture.Create(extraReservation: true);
        var occurrence = new ProcessContinuationIdentity(new(fixture.OrderId), new("attempt/1"));
        for (var step = 0; step < 12 && (await fixture.Inventory()).Available == 3; step++)
            await fixture.ExecuteResult(occurrence, maximumActivations: 1);
        Assert.InRange((await fixture.Inventory()).Available, 4, 5);
        await using (var add = fixture.Database.CreateCommand("""
            INSERT INTO public.cohesive_reservations(reservation_id,partition_key,order_id,sku,quantity,released,observation_version)
            VALUES ($1||'-late',$2,$1,$3,1,false,1)
            """))
        {
            add.Parameters.AddWithValue(fixture.OrderId);
            add.Parameters.AddWithValue(FulfillmentStoragePolicy.LocalPartition);
            add.Parameters.AddWithValue(fixture.Sku);
            await add.ExecuteNonQueryAsync();
        }
        Assert.Equal(CancellationOutcome.ReservationConflict, await fixture.Execute(occurrence));
        Assert.Equal(OrderStatus.Cancelling, (await fixture.Order()).Status);
        Assert.Equal(6, (await fixture.Inventory()).Available);
    }

    [OrderStorageIntegrationTests.PostgresFact]
    public async Task Invalid_later_reservation_rejects_entire_set_before_claim()
    {
        await using var fixture = await Fixture.Create(extraReservation: true);
        var occurrence = new ProcessContinuationIdentity(new(fixture.OrderId), new("attempt/1"));
        var lastId = (await fixture.CaptureReservationIds(occurrence))[1];
        await using (var change = fixture.Database.CreateCommand("UPDATE public.cohesive_reservations SET released=true WHERE reservation_id=$1"))
        {
            change.Parameters.AddWithValue(lastId);
            await change.ExecuteNonQueryAsync();
        }
        Assert.Equal(CancellationOutcome.ReservationConflict, await fixture.Execute(new(new(fixture.OrderId), new("attempt/1"))));
        Assert.Equal(OrderStatus.Submitted, (await fixture.Order()).Status);
        Assert.Equal(3, (await fixture.Inventory()).Available);
    }

    [OrderStorageIntegrationTests.PostgresFact]
    public async Task Changed_reservation_after_preflight_is_rejected_without_releasing_stock()
    {
        await using var fixture = await Fixture.Create(extraReservation: true);
        var occurrence = new ProcessContinuationIdentity(new("changed/" + fixture.OrderId), new("attempt/1"));
        ProcessDurableActivationResult? checkpoint = null;
        for (var step = 0; step < 8; step++)
        {
            checkpoint = await fixture.ExecuteResult(occurrence, maximumActivations: 1);
            if (checkpoint.Snapshot!.Checkpoint.Continuation.Tokens.Any(token => token.Node.Value == CancellationProcess.PreflightCompletedNode)) break;
        }
        Assert.Contains(checkpoint!.Snapshot!.Checkpoint.Continuation.Tokens, token => token.Node.Value == CancellationProcess.PreflightCompletedNode);
        Assert.Equal(OrderStatus.Submitted, (await fixture.Order()).Status);
        await using (var change = fixture.Database.CreateCommand("UPDATE public.cohesive_reservations SET quantity=quantity+1 WHERE order_id=$1"))
        {
            change.Parameters.AddWithValue(fixture.OrderId);
            await change.ExecuteNonQueryAsync();
        }
        Assert.Equal(CancellationOutcome.ReservationConflict, await fixture.Execute(occurrence));
        Assert.Equal(3, (await fixture.Inventory()).Available);
        Assert.Equal(OrderStatus.Cancelling, (await fixture.Order()).Status);
    }

    [FulfillmentProcessIntegrationTests.PostgresTheory]
    [InlineData(OrderStatus.Draft, false, false, CancellationOutcome.NotSubmitted)]
        [InlineData(OrderStatus.Submitted, false, true, CancellationOutcome.ReservationConflict)]
    public async Task Unsupported_cancellation_keeps_order_and_inventory_unchanged(OrderStatus status, bool extraReservation, bool alreadyReleased, CancellationOutcome expected)
    {
        await using var fixture = await Fixture.Create(status, extraReservation, alreadyReleased);
        Assert.Equal(expected, await fixture.Execute(new(new(fixture.OrderId), new("attempt/1"))));
        Assert.Equal(status, (await fixture.Order()).Status);
        Assert.Equal(3, (await fixture.Inventory()).Available);
        Assert.Equal(alreadyReleased, (await fixture.Reservation()).Released);
    }

    [FulfillmentProcessIntegrationTests.PostgresTheory]
    [InlineData(false, CancellationOutcome.ReservationConflict)]
    [InlineData(true, CancellationOutcome.OrderMissing)]
    public async Task Missing_cancellation_evidence_rejects_without_inventory_mutation(bool missingOrder, CancellationOutcome expected)
    {
        await using var fixture = await Fixture.Create();
        await using (var delete = fixture.Database.CreateCommand("DELETE FROM public.cohesive_reservations WHERE order_id=$1"))
        {
            delete.Parameters.AddWithValue(fixture.OrderId);
            await delete.ExecuteNonQueryAsync();
        }
        if (missingOrder)
        {
            await using var delete = fixture.Database.CreateCommand("DELETE FROM public.cohesive_orders WHERE order_id=$1");
            delete.Parameters.AddWithValue(fixture.OrderId);
            await delete.ExecuteNonQueryAsync();
        }
        Assert.Equal(expected, await fixture.Execute(new(new(fixture.OrderId), new("attempt/1"))));
        Assert.Equal(3, (await fixture.Inventory()).Available);
    }

    [OrderStorageIntegrationTests.PostgresFact]
    public async Task Concurrent_distinct_cancellations_release_inventory_once()
    {
        await using var fixture = await Fixture.Create();
        var first = new ProcessContinuationIdentity(new("first/" + fixture.OrderId), new("attempt/1"));
        var second = new ProcessContinuationIdentity(new("second/" + fixture.OrderId), new("attempt/1"));
        var runs = new[] { fixture.ExecuteResult(first), fixture.ExecuteResult(second) };
        await Task.WhenAll(runs);
        Assert.Contains(runs, run => run.Result.Snapshot!.Checkpoint.Continuation.Terminal.Kind == ExecutionTerminalOutcomeKind.Completed
            && Outcome(run.Result) == CancellationOutcome.Cancelled);
        foreach (var run in runs)
        {
            var terminal = run.Result.Snapshot!.Checkpoint.Continuation.Terminal;
            if (terminal.Kind == ExecutionTerminalOutcomeKind.Completed)
                Assert.Contains(Outcome(run.Result), new[] { CancellationOutcome.Cancelled, CancellationOutcome.InProgress, CancellationOutcome.AlreadyCancelled, CancellationOutcome.ReservationConflict });
            else
                Assert.Contains(run.Result.Decision!.Diagnostics, diagnostic => diagnostic.Code == EntityTransitionOperationDiagnosticCodes.ConcurrencyConflict);
        }
        Assert.Equal(5, (await fixture.Inventory()).Available);
        Assert.True((await fixture.Reservation()).Released);
        Assert.Equal(OrderStatus.Cancelled, (await fixture.Order()).Status);
    }

    [Fact]
    public void Host_profile_omits_cancellation_rather_than_weakening_durable_cuts()
    {
        var model = TestModels.Fulfillment();
        Assert.Null(model.Api.Cancellation);
        Assert.DoesNotContain(model.Api.Definition.Endpoints, endpoint => endpoint.Id.ToString().Contains("cancellation", StringComparison.Ordinal));
    }

    static CancellationOutcome Outcome(ProcessDurableActivationResult result) =>
        Enum.Parse<CancellationOutcome>(Assert.IsType<ObservationValue>(result.Snapshot!.Checkpoint.Continuation.Terminal.Detail!.Value!.Value).GetProperty("Outcome").GetRequiredString());

    sealed class Fixture : IAsyncDisposable
    {
        internal readonly FulfillmentApplication Model = ModelActivator.Create<FulfillmentApplication>(services => services.AddSingleton(new FulfillmentHistoryPolicy(EntityStateAuthority.State)).AddSingleton(new FulfillmentExecutionPolicy { Lifetime = ProcessExecutionLifetime.Durable }));
        internal readonly NpgsqlDataSource Database = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("COHESIVE_ORDER_EXAMPLE_TEST_CONNECTION_STRING")!);
        internal readonly string OrderId = Guid.NewGuid().ToString("D");
        internal readonly string Sku = "cancel-" + Guid.NewGuid().ToString("N");
        internal readonly OperationContext Context = OperationContext.Create().WithIdentityContext(FulfillmentDemoIdentity.Context);
        internal IServicePersistence Persistence = null!;
        internal readonly InMemoryProcessDurableStore Store = new();
        PreparedServiceProcess? prepared;
        internal string ExtraReservationId { get; } = Guid.NewGuid().ToString("D");
        IEntityRepository<Order> orders = null!;
        IEntityRepository<InventoryItem> inventory = null!;
        IEntityRepository<Reservation> reservations = null!;
        internal static async Task<Fixture> Create(OrderStatus status = OrderStatus.Submitted, bool extraReservation = false, bool alreadyReleased = false)
        {
            var fixture = new Fixture();
            await fixture.Model.Storage.Bindings.PrepareSchemaAsync(fixture.Database);
            var receipts = new PostgresTransitionReceiptOptions("public", "cohesive_process_receipts", FulfillmentStoragePolicy.LocalPartition);
            await using (var command = fixture.Database.CreateCommand(receipts.SchemaSql)) await command.ExecuteNonQueryAsync();
            var receiptStorage = await receipts.BindAsync(fixture.Database);
            var persistence = fixture.Model.Storage.Bindings.Bind(fixture.Database, authority: "test/cancellation");
            fixture.Persistence = persistence.ForServices(receiptStorage);
            fixture.orders = persistence.Repository(fixture.Model.Domain.Orders, transitionReceipts: receiptStorage);
            fixture.inventory = persistence.Repository(fixture.Model.Domain.Inventory, transitionReceipts: receiptStorage);
            fixture.reservations = persistence.Repository(fixture.Model.Domain.Reservations, transitionReceipts: receiptStorage);
            await fixture.orders.Upsert(fixture.Context, new Order(fixture.OrderId, FulfillmentStoragePolicy.LocalPartition, status));
            await fixture.inventory.Upsert(fixture.Context, new InventoryItem(fixture.Sku, FulfillmentStoragePolicy.LocalPartition, 3));
            await fixture.reservations.Upsert(fixture.Context, new Reservation(fixture.OrderId, FulfillmentStoragePolicy.LocalPartition, fixture.OrderId, fixture.Sku, 2, alreadyReleased));
            if (extraReservation)
                await fixture.reservations.Upsert(fixture.Context, new Reservation(fixture.ExtraReservationId, FulfillmentStoragePolicy.LocalPartition, fixture.OrderId, fixture.Sku, 1));
            return fixture;
        }
        internal async Task<CancellationOutcome> Execute(ProcessContinuationIdentity occurrence)
        {
            var result = await ExecuteResult(occurrence);
            Assert.True(result.Snapshot!.Checkpoint.Continuation.Terminal.Kind == ExecutionTerminalOutcomeKind.Completed,
                result.Disposition + ": " + string.Join("; ", result.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message + " " + diagnostic.Location)));
            return Outcome(result);
        }
        internal async Task<ProcessDurableActivationResult> ExecuteResult(ProcessContinuationIdentity occurrence, int maximumActivations = 4096)
        {
            if (prepared == null)
            {
                var cancellation = Model.Api.Cancellation!;
                prepared = cancellation.Service.PrepareProcess(cancellation.Process.Definition,
                    Persistence.Repository, persistence: Persistence);
            }
            var plan = prepared.Plan;
            var authority = new InteractionAuthorityScope("aspire-first", FulfillmentStoragePolicy.LocalPartition);
            var runtime = new ProcessDurableRuntime(Store, prepared.Host, new("test-cancellation", TimeSpan.FromMinutes(5)));
            if (await Store.LoadAsync(Context, occurrence.ProcessInstanceId) == null)
            {
                var request = new ProcessStartRequest(ProcessStartRequest.CurrentSchemaVersion, plan.DefinitionReference,
                    new(new(occurrence.ProcessInstanceId.Value), new(occurrence.ProcessInstanceId.Value), occurrence.ProcessInstanceId,
                        new("local-demo", authority, "test"), Context.UtcNow, plan.Document.Metadata.Provenance), occurrence,
                    PortableValue.Concrete(plan.Definition.Input, ObservationValue.FromObject(new CancelOrder(OrderId))));
                var start = new ProcessStartReferenceEvaluator().Evaluate(request, new(), Context.UtcNow);
                var initialized = await runtime.InitializeAsync(Context, plan, start.Receipt!);
                Assert.Equal(ProcessDurableRuntimeDisposition.Applied, initialized.Disposition);
            }
            return await runtime.AdvanceAsync(Context, plan, occurrence,
                new(authority, new(occurrence.ProcessInstanceId.Value),
                    new(InteractionDurabilityDemand.Durable, InteractionVisibilityDemand.AfterOriginCommit), plan.Document.Metadata.Provenance), maximumActivations);
        }
        internal async Task<string[]> CaptureReservationIds(ProcessContinuationIdentity occurrence)
        {
            var result = await ExecuteResult(occurrence, maximumActivations: 1);
            Assert.Equal(ProcessActivationDisposition.DurableCut, result.Decision!.Disposition);
            return result.Snapshot!.Checkpoint.Continuation.Tokens.SelectMany(token => token.Bindings)
                .Select(binding => binding.Value.Value!.Value)
                .Single(value => value.Kind == ObservationValueKind.Array).Array
                .Select(row => row.GetProperty("id").GetRequiredString()).ToArray();
        }
        internal async Task<Order> Order() => (await orders.TryGetEntity(Context, OrderId, new(partitionKey: FulfillmentStoragePolicy.LocalPartition)))!;
        internal async Task<InventoryItem> Inventory() => (await inventory.TryGetEntity(Context, Sku, new(partitionKey: FulfillmentStoragePolicy.LocalPartition)))!;
        internal async Task<Reservation> Reservation() => (await reservations.TryGetEntity(Context, OrderId, new(partitionKey: FulfillmentStoragePolicy.LocalPartition)))!;
        public async ValueTask DisposeAsync()
        {
            foreach (var sql in new[] {
                "DELETE FROM public.cohesive_process_receipts WHERE subject_id=$1 OR subject_id=$2 OR subject_id=$2||'-second' OR subject_id IN (SELECT reservation_id FROM public.cohesive_reservations WHERE order_id=$1)",
                "DELETE FROM public.cohesive_reservations WHERE order_id=$1 AND $2 IS NOT NULL",
                "DELETE FROM public.cohesive_orders WHERE order_id=$1 AND $2 IS NOT NULL",
                "DELETE FROM public.cohesive_inventory WHERE (sku=$2 OR sku=$2||'-second') AND $1 IS NOT NULL" })
            {
                await using var command = Database.CreateCommand(sql);
                command.Parameters.AddWithValue(OrderId);
                command.Parameters.AddWithValue(Sku);
                await command.ExecuteNonQueryAsync();
            }
            await Database.DisposeAsync();
        }
    }
}
