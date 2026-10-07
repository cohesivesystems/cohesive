using AspireFirst.Orders;
using Cohesive.Adapters.Postgres;
using Cohesive.Api;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Prelude;
using Cohesive.Processes.IR;
using Cohesive.Storage;
using Npgsql;

namespace Cohesive.Adapters.Aspire.Tests;

[Collection("Order PostgreSQL schema")]
public sealed class FulfillmentProcessIntegrationTests
{
    [PostgresTheory]
    [InlineData("Draft", 5, 2, "Submitted", 3)]
    [InlineData("Submitted", 5, 2, "Rejected", 5)]
    [InlineData("Draft", 1, 2, "Rejected", 1)]
    public async Task Canonical_process_commits_compensates_and_replays(string initialStatus, int stock, int quantity,
        string expectedStatus, int remaining)
    {
        await using var database = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("COHESIVE_ORDER_EXAMPLE_TEST_CONNECTION_STRING")!);
        using var schema = new StreamReader(typeof(FulfillmentStorage).Assembly.GetManifestResourceStream("Orders.schema.sql")!);
        await using (var command = database.CreateCommand(await schema.ReadToEndAsync())) await command.ExecuteNonQueryAsync();
        var receipts = new PostgresTransitionReceiptOptions("public", "cohesive_process_receipts", FulfillmentDemo.LocalPartition);
        await using (var command = database.CreateCommand(receipts.SchemaSql)) await command.ExecuteNonQueryAsync();
        var persistence = FulfillmentStorage.Bind(database);
        var orders = persistence.Repository(FulfillmentDomain.Orders, transitionReceipts: receipts);
        var inventory = persistence.Repository(FulfillmentDomain.Inventory, transitionReceipts: receipts);
        var orderId = Guid.NewGuid().ToString("D");
        var sku = "sku-" + Guid.NewGuid().ToString("N");
        var context = FulfillmentDemoIdentity.Attach(OperationContext.Create());
        try
        {
            await orders.Upsert(context, new Order(orderId, FulfillmentDemo.LocalPartition, initialStatus));
            // Same ID in another partition must not confuse the trusted process read binding.
            var foreign = await orders.Upsert(context, new Order(orderId, "other", "Draft"));
            await inventory.Upsert(context, new InventoryItem(sku, FulfillmentDemo.LocalPartition, stock));
            var binding = new FulfillmentProcessBindings(orders, inventory);
            var identity = new ProcessContinuationIdentity(new("test/" + orderId), new("attempt/1"));
            var input = ObservationValue.FromObject(new FulfillOrder(orderId, sku, quantity));
            var result = await binding.Runtime.ExecuteProcessAsync(context, "fulfill", identity, input);
            Assert.True(result.Kind == ApiResultKind.Success,
                result.Kind + ": " + string.Join("; ", result.Diagnostics.Select(d => d.Code + ": " + d.Message)));
            var output = Assert.IsType<ObservationValue>(result.Outcome!.Decision.State.Terminal.Detail!.Value!.Value);
            Assert.Equal(expectedStatus, output.GetProperty("Status").GetRequiredString());
            Assert.Equal(remaining, (await inventory.TryGetEntity(context, sku, new(partitionKey: FulfillmentDemo.LocalPartition)))!.Available);
            Assert.Equal(foreign, await orders.TryGet(context, orderId, new(partitionKey: "other")));
            var beforeReplay = await inventory.TryGet(context, sku, new(partitionKey: FulfillmentDemo.LocalPartition));
            // Recreate all preparation to prove receipts are native durable evidence, not an in-memory cache.
            var replay = await new FulfillmentProcessBindings(orders, inventory).Runtime.ExecuteProcessAsync(context, "fulfill", identity, input);
            Assert.Equal(ApiResultKind.Success, replay.Kind);
            Assert.Equal(output, replay.Outcome!.Decision.State.Terminal.Detail!.Value!.Value);
            Assert.Equal(beforeReplay, await inventory.TryGet(context, sku, new(partitionKey: FulfillmentDemo.LocalPartition)));
        }
        finally
        {
            foreach (var sql in new[] {
                "DELETE FROM public.cohesive_process_receipts WHERE subject_id=$1 OR subject_id=$2",
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

    sealed class PostgresTheoryAttribute : TheoryAttribute
    {
        public PostgresTheoryAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("COHESIVE_ORDER_EXAMPLE_TEST_CONNECTION_STRING")))
                Skip = "Requires the disposable example PostgreSQL fixture.";
        }
    }
}
