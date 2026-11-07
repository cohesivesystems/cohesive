using System.Net;
using System.Net.Http.Json;
using AspireFirst.Orders;
using Cohesive.Adapters.AspNet;
using Cohesive.Adapters.AspNet.Services;
using Cohesive.Adapters.Postgres;
using Cohesive.Adapters.Postgres.Services;
using Cohesive.Api;
using Cohesive.Api.Execution.Services;
using Cohesive.Execution;
using Cohesive.Host.Services;
using Cohesive.Identity;
using Cohesive.Model;
using Cohesive.Prelude;
using Cohesive.Processes.Execution;
using Cohesive.Processes.IR;
using Cohesive.Storage;
using Microsoft.AspNetCore.Builder;
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
                var interrupted = await Assert.ThrowsAsync<EphemeralProcessExecutionException>(() => Execute(identity));
                Assert.Equal("P0001", Assert.IsType<PostgresException>(interrupted.InnerException).SqlState);
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
    [InlineData(OrderStatus.Draft, false, false, CancellationOutcome.NotSubmitted)]
    [InlineData(OrderStatus.Submitted, true, false, CancellationOutcome.ReservationConflict)]
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
        Assert.Contains(runs, run => run.Result.Kind == ApiResultKind.Success && Outcome(run.Result) == CancellationOutcome.Cancelled);
        foreach (var run in runs)
        {
            if (run.Result.Kind == ApiResultKind.Success)
                Assert.Contains(Outcome(run.Result), new[] { CancellationOutcome.Cancelled, CancellationOutcome.InProgress, CancellationOutcome.AlreadyCancelled });
            else
            {
                Assert.Equal(ApiResultKind.DomainError, run.Result.Kind);
                Assert.Contains(run.Result.Diagnostics, diagnostic => diagnostic.Code == EntityTransitionOperationDiagnosticCodes.ConcurrencyConflict);
            }
        }
        Assert.Contains(await fixture.Execute(first), new[] { CancellationOutcome.Cancelled, CancellationOutcome.AlreadyCancelled, CancellationOutcome.InProgress });
        Assert.Contains(await fixture.Execute(second), new[] { CancellationOutcome.Cancelled, CancellationOutcome.AlreadyCancelled, CancellationOutcome.InProgress });
        Assert.Equal(5, (await fixture.Inventory()).Available);
        Assert.True((await fixture.Reservation()).Released);
        Assert.Equal(OrderStatus.Cancelled, (await fixture.Order()).Status);
    }

    [OrderStorageIntegrationTests.PostgresFact]
    public async Task Accepted_cancellation_http_retries_and_polls_the_declared_result()
    {
        await using var fixture = await Fixture.Create();
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddRequestOperationContext();
        builder.Services.AddFixedRequestIdentity(FulfillmentDemoIdentity.Context);
        var registered = fixture.Model.Endpoints.Build(builder.Services, fixture.Persistence);
        await using var app = builder.Build();
        app.UseRequestOperationContext();
        app.UseAuthorization();
        app.MapServices(registered);
        app.Urls.Add("http://127.0.0.1:0");
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new(app.Urls.Single()) };
        var command = new ServiceProcessCommand<CancelOrder>(fixture.OrderId, fixture.OrderId, fixture.OrderId, "1", new(fixture.OrderId));
        var accepted = await client.PostAsJsonAsync("/fulfillment/cancellations", command);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var receipt = (await accepted.Content.ReadFromJsonAsync<ServiceProcessReceipt>())!;
        var retried = await client.PostAsJsonAsync("/fulfillment/cancellations", command);
        Assert.Equal(HttpStatusCode.Accepted, retried.StatusCode);
        Assert.Equal(receipt.ResultLocation, (await retried.Content.ReadFromJsonAsync<ServiceProcessReceipt>())!.ResultLocation);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        HttpResponseMessage response;
        do
        {
            response = await client.GetAsync(receipt.ResultLocation, deadline.Token);
            if (response.StatusCode == HttpStatusCode.Accepted) await Task.Delay(50, deadline.Token);
        } while (response.StatusCode == HttpStatusCode.Accepted);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(CancellationOutcome.Cancelled, (await response.Content.ReadFromJsonAsync<CancellationResult>())!.Outcome);
        Assert.Equal(5, (await fixture.Inventory()).Available);
        Assert.Equal(OrderStatus.Cancelled, (await fixture.Order()).Status);
        await app.StopAsync();
    }

    static CancellationOutcome Outcome(ServiceOperationResult<EphemeralProcessResult> result) =>
        Enum.Parse<CancellationOutcome>(Assert.IsType<ObservationValue>(result.Outcome!.Decision.State.Terminal.Detail!.Value!.Value).GetProperty("Outcome").GetRequiredString());

    sealed class Fixture : IAsyncDisposable
    {
        internal readonly FulfillmentApplication Model = TestModels.Fulfillment();
        internal readonly NpgsqlDataSource Database = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("COHESIVE_ORDER_EXAMPLE_TEST_CONNECTION_STRING")!);
        internal readonly string OrderId = Guid.NewGuid().ToString("D");
        internal readonly string Sku = "cancel-" + Guid.NewGuid().ToString("N");
        internal readonly OperationContext Context = OperationContext.Create().WithIdentityContext(FulfillmentDemoIdentity.Context);
        internal IServicePersistence Persistence = null!;
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
                await fixture.reservations.Upsert(fixture.Context, new Reservation(Guid.NewGuid().ToString("D"), FulfillmentStoragePolicy.LocalPartition, fixture.OrderId, fixture.Sku, 1));
            return fixture;
        }
        internal async Task<CancellationOutcome> Execute(ProcessContinuationIdentity occurrence)
        {
            var result = await ExecuteResult(occurrence);
            Assert.Equal(ApiResultKind.Success, result.Kind);
            return Outcome(result);
        }
        internal async Task<ServiceOperationResult<EphemeralProcessResult>> ExecuteResult(ProcessContinuationIdentity occurrence)
        {
            var process = Model.Api.Cancellation.Process;
            var service = Service.Define(new("cancel-test"), new("1"), process.Provenance)
                .Use(new ServiceDependencies().Process(process))
                .Operation("cancel").Run(process.Definition).ExecuteEphemerally(TimeSpan.FromSeconds(15));
            var host = service.Bind(process.Definition, Persistence)
                .Build("aspire-first", new IdentityServiceInvocationAuthorization("demo", new(FulfillmentDomain.PartitionField)));
            return await host.Runtime.ExecuteProcessAsync(Context, "cancel", occurrence, ObservationValue.FromObject(new CancelOrder(OrderId)));
        }
        internal async Task<Order> Order() => (await orders.TryGetEntity(Context, OrderId, new(partitionKey: FulfillmentStoragePolicy.LocalPartition)))!;
        internal async Task<InventoryItem> Inventory() => (await inventory.TryGetEntity(Context, Sku, new(partitionKey: FulfillmentStoragePolicy.LocalPartition)))!;
        internal async Task<Reservation> Reservation() => (await reservations.TryGetEntity(Context, OrderId, new(partitionKey: FulfillmentStoragePolicy.LocalPartition)))!;
        public async ValueTask DisposeAsync()
        {
            foreach (var sql in new[] {
                "DELETE FROM public.cohesive_process_receipts WHERE subject_id=$1 OR subject_id=$2",
                "DELETE FROM public.cohesive_reservations WHERE order_id=$1 AND $2 IS NOT NULL",
                "DELETE FROM public.cohesive_orders WHERE order_id=$1 AND $2 IS NOT NULL",
                "DELETE FROM public.cohesive_inventory WHERE sku=$2 AND $1 IS NOT NULL" })
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
