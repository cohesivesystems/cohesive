using AspireFirst.Orders;
using Cohesive.Adapters.Postgres;
using Cohesive.Prelude;
using Cohesive.Relations.IR;
using Npgsql;

namespace Cohesive.Adapters.Aspire.Tests;

public sealed partial class OrderStorageIntegrationTests
{
    const string InventoryConnectionVariable = "COHESIVE_ORDER_EXAMPLE_INVENTORY_CONNECTION_STRING";

    [TwoDatabasePostgresFact]
    public async Task Native_join_subplan_then_separate_inventory_matches_one_database_query_without_repeating_join()
    {
        await using var orders = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable(ConnectionVariable)!);
        await using var inventoryDatabase = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable(InventoryConnectionVariable)!);
        foreach (var database in new[] { orders, inventoryDatabase })
        {
            using var schema = new StreamReader(typeof(OrderStorage).Assembly.GetManifestResourceStream("Orders.schema.sql")!);
            await using var command = database.CreateCommand(await schema.ReadToEndAsync());
            await command.ExecuteNonQueryAsync();
        }
        var id = Guid.NewGuid().ToString("D");
        var sku = "subplan-" + Guid.NewGuid().ToString("N");
        var context = OperationContext.Create();
        var orderRepository = OrderStorage.Bind(orders);
        var reservations = new PostgresEntityRepository(FulfillmentDomain.Reservations.Definition,
            new(new("orders"), orders, "test"), FulfillmentStorage.Reservations);
        try
        {
            await orderRepository.Upsert(context, OrderStorage.Register(Guid.Parse(id)));
            var native = ReservationAvailabilityInfrastructure.BindNative(orders);
            var composed = ReservationAvailabilityInfrastructure.BindComposed(orders, inventoryDatabase);
            var emptyNative = await native.ReadAsync(id);
            var emptyComposed = await composed.ReadAsync(id);
            Assert.Equal(emptyNative, emptyComposed);
            Assert.Null(Assert.Single(emptyComposed).ReservationId);
            foreach (var database in new[] { orders, inventoryDatabase })
            {
                var repository = new PostgresEntityRepository(FulfillmentDomain.Inventory.Definition,
                    new(new("inventory"), database, "test"), FulfillmentStorage.Inventory);
                await repository.Upsert(context, new(FulfillmentDomain.Inventory.Definition.CreateState(sku,
                    new InventoryItem(sku, OrderStorage.LocalPartition, 8), 1).Snapshot));
            }
            for (var quantity = 1; quantity <= 2; quantity++)
            {
                var reservationId = Guid.NewGuid().ToString("D");
                await reservations.Upsert(context, new(FulfillmentDomain.Reservations.Definition.CreateState(reservationId,
                    new Reservation(reservationId, OrderStorage.LocalPartition, id, sku, quantity), 1).Snapshot));
            }
            var expected = await native.ReadAsync(id);
            var actual = await composed.ReadAsync(id);
            Assert.Equal(expected.OrderBy(row => row.ReservationId), actual.OrderBy(row => row.ReservationId));
            var evidence = await composed.EvaluateAsync(id, new("tests/native-subplan"));
            Assert.True(evidence.IsSuccessful, string.Join("; ", evidence.Remainder.Result?.Diagnostics.Select(d => d.Message) ?? []));
            Assert.Equal(2, evidence.PrefixRows.Length);
            Assert.Equal(2, evidence.Remainder.PhysicalExecution!.SourceReads.Length); // projected rowset + one inventory enumeration
            Assert.Single(evidence.Plan.Prefix.Plan!.Definition.Body.Nodes.OfType<TraverseRelationshipQueryNode>());
            Assert.Empty(evidence.Plan.Remainder.Plan!.Definition.Body.Nodes.OfType<TraverseRelationshipQueryNode>());
            Assert.Single(evidence.Plan.Remainder.Plan.Definition.Body.Nodes.OfType<JoinQueryNode>());
            Assert.Empty(await composed.ReadAsync(Guid.NewGuid().ToString("D")));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => composed.ReadAsync(id, new(true)));
            // The separately bound inventory source is authoritative for the remaining read.
            await using (var command = inventoryDatabase.CreateCommand("UPDATE cohesive_inventory SET available=11 WHERE sku=$1"))
            { command.Parameters.AddWithValue(sku); await command.ExecuteNonQueryAsync(); }
            Assert.All(await composed.ReadAsync(id), row => Assert.Equal(11, row.Available));
            Assert.All(await native.ReadAsync(id), row => Assert.Equal(8, row.Available));
        }
        finally
        {
            await using (var command = orders.CreateCommand("DELETE FROM cohesive_reservations WHERE order_id=$1"))
            { command.Parameters.AddWithValue(id); await command.ExecuteNonQueryAsync(); }
            await using (var command = orders.CreateCommand("DELETE FROM cohesive_orders WHERE order_id=$1"))
            { command.Parameters.AddWithValue(id); await command.ExecuteNonQueryAsync(); }
            foreach (var database in new[] { orders, inventoryDatabase })
            {
                await using var command = database.CreateCommand("DELETE FROM cohesive_inventory WHERE sku=$1");
                command.Parameters.AddWithValue(sku); await command.ExecuteNonQueryAsync();
            }
        }
    }

    sealed class TwoDatabasePostgresFactAttribute : FactAttribute
    {
        public TwoDatabasePostgresFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionVariable))
                || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(InventoryConnectionVariable)))
                Skip = "Set the order and inventory connection variables to two disposable PostgreSQL databases.";
        }
    }
}
