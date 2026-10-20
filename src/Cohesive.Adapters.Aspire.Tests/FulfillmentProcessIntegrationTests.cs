using Cohesive.Adapters.Marten;
using Marten;
using JasperFx.Events;
using Cohesive.Host.Composition;
using System.Net;
using Cohesive.Host.Services;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Cohesive.Adapters.AspNet;
using Cohesive.Adapters.AspNet.Services;
using Cohesive.Adapters.Postgres.Services;
using Cohesive.Api.Execution.Services;
using Cohesive.Identity;
using AspireFirst.Orders;
using Cohesive.Adapters.Postgres;
using Cohesive.Api;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Prelude;
using Cohesive.Processes.IR;
using Cohesive.Processes.Execution;
using Cohesive.Storage;
using Npgsql;

namespace Cohesive.Adapters.Aspire.Tests;

[Collection("Order PostgreSQL schema")]
public sealed class FulfillmentProcessIntegrationTests
{
    static readonly AspireFirst.Orders.FulfillmentApplication Model = ModelActivator.Create<AspireFirst.Orders.FulfillmentApplication>();

    static ServiceBuilder TerminalService() => Service.Define(new("terminal-test"), new("1"), Model.Process.Provenance)
        .Use(new ServiceDependencies().Process(Model.Process))
        .Operation("fulfill").Run(Model.Process.Definition).ExecuteEphemerally(TimeSpan.FromSeconds(15));

    [PostgresTheory]
    [InlineData(EntityStateAuthority.State, OrderStatus.Draft, 5, 2, "Submitted", 3, false, FulfillmentOutcome.ReservedAndSubmitted)]
    [InlineData(EntityStateAuthority.State, OrderStatus.Submitted, 5, 2, "Rejected", 5, false, FulfillmentOutcome.OrderRejected)]
    [InlineData(EntityStateAuthority.State, OrderStatus.Draft, 1, 2, "Rejected", 1, false, FulfillmentOutcome.InsufficientStock)]
    [InlineData(EntityStateAuthority.State, OrderStatus.Draft, 5, 0, "Rejected", 5, false, FulfillmentOutcome.InvalidQuantity)]
    [InlineData(EntityStateAuthority.State, OrderStatus.Draft, 2, 2, "Submitted", 0, true, FulfillmentOutcome.ReservedAndSubmitted)]
    [InlineData(EntityStateAuthority.Events, OrderStatus.Draft, 5, 2, "Submitted", 3, false, FulfillmentOutcome.ReservedAndSubmitted)]
    [InlineData(EntityStateAuthority.Events, OrderStatus.Submitted, 5, 2, "Rejected", 5, false, FulfillmentOutcome.OrderRejected)]
    [InlineData(EntityStateAuthority.Events, OrderStatus.Draft, 1, 2, "Rejected", 1, false, FulfillmentOutcome.InsufficientStock)]
    [InlineData(EntityStateAuthority.Events, OrderStatus.Draft, 5, 0, "Rejected", 5, false, FulfillmentOutcome.InvalidQuantity)]
    [InlineData(EntityStateAuthority.Events, OrderStatus.Draft, 2, 2, "Submitted", 0, true, FulfillmentOutcome.ReservedAndSubmitted)]
    [InlineData(EntityStateAuthority.State, OrderStatus.Draft, -1, 2, "Rejected", -1, false, FulfillmentOutcome.InventoryMissing)]
    [InlineData(EntityStateAuthority.Events, OrderStatus.Draft, -1, 2, "Rejected", -1, false, FulfillmentOutcome.InventoryMissing)]
    [InlineData(EntityStateAuthority.State, OrderStatus.Draft, 5, 2, "Rejected", 5, false, FulfillmentOutcome.ReservationConflict, true)]
    [InlineData(EntityStateAuthority.Events, OrderStatus.Draft, 5, 2, "Rejected", 5, false, FulfillmentOutcome.ReservationConflict, true)]
    public async Task Canonical_process_commits_compensates_and_replays(EntityStateAuthority authority, OrderStatus initialStatus, int stock, int quantity,
        string expectedStatus, int remaining, bool interruptReservation, FulfillmentOutcome expectedOutcome, bool existingReservation = false)
    {
        await using var database = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("COHESIVE_ORDER_EXAMPLE_TEST_CONNECTION_STRING")!);
        using var schema = new StreamReader(typeof(FulfillmentApplication).Assembly.GetManifestResourceStream("Orders.schema.sql")!);
        await using (var command = database.CreateCommand(await schema.ReadToEndAsync())) await command.ExecuteNonQueryAsync();
        var receipts = new PostgresTransitionReceiptOptions("public", "cohesive_process_receipts", FulfillmentStoragePolicy.LocalPartition);
        await using (var command = database.CreateCommand(receipts.SchemaSql)) await command.ExecuteNonQueryAsync();
        var receiptStorage = await receipts.BindAsync(database);
        var historySchema = "reservation_" + Guid.NewGuid().ToString("N");
        using var eventStore = DocumentStore.For(options =>
        {
            options.Connection(database); options.DatabaseSchemaName = historySchema;
            options.AutoCreateSchemaObjects = JasperFx.AutoCreate.None;
            options.UseCohesiveEntityHistory();
        });
        await eventStore.Storage.ApplyAllConfiguredChangesToDatabaseAsync();
        var history = await MartenEntityHistoryStorage.BindAsync(database, eventStore, authority);
        var persistence = Model.Storage.Bindings.Bind(database, authority: "test/fulfillment");
        var servicePersistence = persistence.ForServices(receiptStorage, history);
        IEntityRepository<T> Repository<T>(Cohesive.Transitions.Authoring.DomainEntity<T> entity) where T : notnull =>
            new TypedEntityRepository<T>(servicePersistence.Repository(ValueContract.FromShape(entity.Definition.Shape)));
        var orders = Repository(Model.Domain.Orders);
        var inventory = Repository(Model.Domain.Inventory);
        var reservations = Repository(Model.Domain.Reservations);
        var orderId = Guid.NewGuid().ToString("D");
        var sku = "sku-" + Guid.NewGuid().ToString("N");
        var context = OperationContext.Create().WithIdentityContext(FulfillmentDemoIdentity.Context);
        try
        {
            await orders.Upsert(context, new Order(orderId, FulfillmentStoragePolicy.LocalPartition, initialStatus));
            // Same ID in another partition must not confuse the trusted process read binding.
            var foreign = await orders.Upsert(context, new Order(orderId, "other", OrderStatus.Draft));
            if (stock >= 0) await inventory.Upsert(context, new InventoryItem(sku, FulfillmentStoragePolicy.LocalPartition, stock));
            if (existingReservation) await reservations.Upsert(context,
                new Reservation(orderId, FulfillmentStoragePolicy.LocalPartition, orderId, sku, 1));
            var binding = TerminalService().Bind(Model.Process.Definition, orders, inventory, reservations)
                .Build("aspire-first", new IdentityServiceInvocationAuthorization("demo", new(FulfillmentDomain.PartitionField)));
            var identity = new ProcessContinuationIdentity(new("test/" + orderId), new("attempt/1"));
            var input = ObservationValue.FromObject(new FulfillOrder(orderId, sku, quantity));
            if (interruptReservation)
            {
                // Fail the reservation insert after stock and order have committed. This is a physical
                // failure, not a rejection: remove the fault, then replay the exact process occurrence.
                await using (var fault = database.CreateCommand($"""
                    CREATE FUNCTION {historySchema}.reject_reservation() RETURNS trigger LANGUAGE plpgsql AS
                    'BEGIN RAISE EXCEPTION ''injected reservation failure''; END';
                    CREATE TRIGGER {historySchema} BEFORE INSERT ON public.cohesive_reservations
                    FOR EACH ROW WHEN (NEW.order_id = '{orderId}') EXECUTE FUNCTION {historySchema}.reject_reservation();
                    """)) await fault.ExecuteNonQueryAsync();
                var interrupted = await Assert.ThrowsAsync<EphemeralProcessExecutionException>(() =>
                    binding.Runtime.ExecuteProcessAsync(context, "fulfill", identity, input).AsTask());
                Assert.Equal("P0001", Assert.IsType<PostgresException>(interrupted.InnerException).SqlState);
                Assert.Equal(remaining, (await inventory.TryGetEntity(context, sku, new(partitionKey: FulfillmentStoragePolicy.LocalPartition)))?.Available ?? -1);
                Assert.Null(await reservations.TryGet(context, orderId, new(partitionKey: FulfillmentStoragePolicy.LocalPartition)));
                await using var removeFault = database.CreateCommand($"DROP TRIGGER {historySchema} ON public.cohesive_reservations");
                await removeFault.ExecuteNonQueryAsync();
                binding = TerminalService().Bind(Model.Process.Definition, orders, inventory, reservations)
                    .Build("aspire-first", new IdentityServiceInvocationAuthorization("demo", new(FulfillmentDomain.PartitionField)));
            }
            var result = await binding.Runtime.ExecuteProcessAsync(context, "fulfill", identity, input);
            Assert.True(result.Kind == ApiResultKind.Success,
                result.Kind + ": " + string.Join("; ", result.Diagnostics.Select(d => d.Code + ": " + d.Message)));
            var output = Assert.IsType<ObservationValue>(result.Outcome!.Decision.State.Terminal.Detail!.Value!.Value);
            Assert.Equal(expectedStatus, output.GetProperty("Status").GetRequiredString());
            Assert.Equal(expectedOutcome.ToString(), output.GetProperty("Outcome").GetRequiredString());
            Assert.Equal(remaining, (await inventory.TryGetEntity(context, sku, new(partitionKey: FulfillmentStoragePolicy.LocalPartition)))?.Available ?? -1);
            Assert.Equal(foreign, await orders.TryGet(context, orderId, new(partitionKey: "other")));
            var detailsReader = persistence.Query(Model.Queries.OrderDetails, maximumRows: 1000, maximumBytes: 1_000_000);
            var details = await detailsReader.ReadAsync(orderId);
            var availabilityReader = persistence.Query(Model.Queries.ReservationAvailability, maximumRows: 1000, maximumBytes: 1_000_000);
            var availability = await availabilityReader.ReadAsync(orderId);
            if (expectedStatus == "Submitted")
            {
                var fact = Assert.Single(details!.Reservations);
                Assert.Equal(orderId, fact.Id); Assert.Equal(sku, fact.Sku);
                Assert.Equal(quantity, fact.Quantity); Assert.Equal(remaining, fact.AvailableStock);
                Assert.Equal(orderId, Assert.Single(availability!).ReservationId);
            }
            else if (existingReservation) Assert.Equal(1, Assert.Single(details!.Reservations).Quantity);
            else Assert.Empty(details!.Reservations);
            var reservationBeforeReplay = await reservations.TryGet(context, orderId, new(partitionKey: FulfillmentStoragePolicy.LocalPartition));
            var beforeReplay = await inventory.TryGet(context, sku, new(partitionKey: FulfillmentStoragePolicy.LocalPartition));
            await using var eventCount = database.CreateCommand($"SELECT count(*) FROM {historySchema}.mt_events");
            var eventsBeforeReplay = await eventCount.ExecuteScalarAsync();
            // Recreate all preparation to prove receipts are native durable evidence, not an in-memory cache.
            var replay = await TerminalService().Bind(Model.Process.Definition, orders, inventory, reservations)
                .Build("aspire-first", new IdentityServiceInvocationAuthorization("demo", new(FulfillmentDomain.PartitionField))).Runtime.ExecuteProcessAsync(context, "fulfill", identity, input);
            Assert.Equal(ApiResultKind.Success, replay.Kind);
            Assert.Equal(output, replay.Outcome!.Decision.State.Terminal.Detail!.Value!.Value);
            Assert.Equal(beforeReplay, await inventory.TryGet(context, sku, new(partitionKey: FulfillmentStoragePolicy.LocalPartition)));
            Assert.Equal(reservationBeforeReplay, await reservations.TryGet(context, orderId, new(partitionKey: FulfillmentStoragePolicy.LocalPartition)));
            Assert.Equal(eventsBeforeReplay, await eventCount.ExecuteScalarAsync());
            await using var reservationCount = database.CreateCommand("SELECT count(*) FROM public.cohesive_reservations WHERE order_id=$1");
            reservationCount.Parameters.AddWithValue(orderId);
            Assert.Equal(expectedStatus == "Submitted" || existingReservation ? 1L : 0L, await reservationCount.ExecuteScalarAsync());
        }
        finally
        {
            await using (var drop = database.CreateCommand($"DROP SCHEMA {historySchema} CASCADE")) await drop.ExecuteNonQueryAsync();
            foreach (var sql in new[] {
                "DELETE FROM public.cohesive_process_receipts WHERE subject_id=$1 OR subject_id=$2",
                "DELETE FROM public.cohesive_reservations WHERE order_id=$1 AND $2 IS NOT NULL",
                "DELETE FROM public.cohesive_orders WHERE order_id=$1 AND $2 IS NOT NULL",
                "DELETE FROM public.cohesive_inventory WHERE sku=$2 AND $1 IS NOT NULL" })
            {
                await using var cleanup = database.CreateCommand(sql);
                cleanup.Parameters.AddWithValue(orderId);
                cleanup.Parameters.AddWithValue(sku);
                await cleanup.ExecuteNonQueryAsync();
            }
        }
    }

    [PostgresTheory]
    [InlineData(5, 2, "Submitted", 3)]
    public async Task Unified_api_resolves_storage_and_executes_fulfillment_over_http(int stock, int quantity,
        string expectedStatus, int remaining)
    {
        var model = ModelActivator.Create<FulfillmentApplication>();
        await using var database = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("COHESIVE_ORDER_EXAMPLE_TEST_CONNECTION_STRING")!);
        using var schema = new StreamReader(typeof(FulfillmentApplication).Assembly.GetManifestResourceStream("Orders.schema.sql")!);
        await using (var command = database.CreateCommand(await schema.ReadToEndAsync())) await command.ExecuteNonQueryAsync();
        var receipts = new PostgresTransitionReceiptOptions("public", "cohesive_process_receipts", FulfillmentStoragePolicy.LocalPartition);
        await using (var command = database.CreateCommand(receipts.SchemaSql)) await command.ExecuteNonQueryAsync();
        var receiptStorage = await receipts.BindAsync(database);
        var persistence = model.Storage.Bindings.Bind(database, authority: "test/fulfillment");
        var inventory = persistence.Repository(model.Domain.Inventory, transitionReceipts: receiptStorage);
        var sku = "http-sku-" + Guid.NewGuid().ToString("N");
        string? orderId = null;
        var context = OperationContext.Create();
        try
        {
            await inventory.Upsert(context, new InventoryItem(sku, FulfillmentStoragePolicy.LocalPartition, stock));
            var builder = WebApplication.CreateBuilder();
            builder.Services.AddRequestOperationContext();
            builder.Services.AddFixedRequestIdentity(FulfillmentDemoIdentity.Context);
            var registered = model.Endpoints.Build(builder.Services, persistence.ForServices(receiptStorage));
            await using var app = builder.Build();
            app.UseRequestOperationContext();
            app.UseAuthorization();
            app.MapServices(registered);
            app.Urls.Add("http://127.0.0.1:0");
            await app.StartAsync();
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            var created = await client.PostAsync("/orders", null);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            orderId = (await created.Content.ReadFromJsonAsync<OrderCreated>())!.Id;
            var invalid = await client.PostAsJsonAsync("/fulfillment", new ServiceProcessCommand<FulfillOrder>("", orderId, orderId, "1", new(orderId, sku, quantity)));
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            var response = await client.PostAsJsonAsync("/fulfillment", new ServiceProcessCommand<FulfillOrder>(orderId, orderId, orderId, "1", new(orderId, sku, quantity)));
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            var receipt = await response.Content.ReadFromJsonAsync<ServiceProcessReceipt>();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            do
            {
                response = await client.GetAsync(receipt!.ResultLocation, deadline.Token);
                if (response.StatusCode == HttpStatusCode.Accepted) await Task.Delay(50, deadline.Token);
            } while (response.StatusCode == HttpStatusCode.Accepted);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains(expectedStatus, await response.Content.ReadAsStringAsync());
            Assert.Equal(remaining, (await inventory.TryGetEntity(context, sku, new(partitionKey: FulfillmentStoragePolicy.LocalPartition)))?.Available ?? -1);
            var details = await client.GetFromJsonAsync<OrderDetails>($"/orders/{orderId}/details");
            Assert.Equal(OrderStatus.Submitted, details!.Status);
            var reservation = Assert.Single(details.Reservations);
            Assert.Equal(orderId, reservation.Id);
            Assert.Equal(sku, reservation.Sku);
            Assert.Equal(quantity, reservation.Quantity);
            Assert.Equal(remaining, reservation.AvailableStock);
            await app.StopAsync();
        }
        finally
        {
            foreach (var sql in new[] {
                "DELETE FROM public.cohesive_process_receipts WHERE subject_id=$1 OR subject_id=$2",
                "DELETE FROM public.cohesive_reservations WHERE order_id=$1 AND $2 IS NOT NULL",
                "DELETE FROM public.cohesive_orders WHERE order_id=$1 AND $2 IS NOT NULL",
                "DELETE FROM public.cohesive_inventory WHERE sku=$2 AND $1 IS NOT NULL" })
            {
                await using var cleanup = database.CreateCommand(sql);
                cleanup.Parameters.AddWithValue(orderId ?? "none");
                cleanup.Parameters.AddWithValue(sku);
                await cleanup.ExecuteNonQueryAsync();
            }
        }
    }

    sealed class PostgresTheoryAttribute : TheoryAttribute
    {
        public PostgresTheoryAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("COHESIVE_ORDER_EXAMPLE_TEST_CONNECTION_STRING")))
                Skip = "Requires the disposable example PostgreSQL fixture.";
        }
    }
}
