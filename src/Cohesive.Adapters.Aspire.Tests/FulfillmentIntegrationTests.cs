using System.Net;
using System.Net.Http.Json;
using AspireFirst.Orders;
using Cohesive.Adapters.AspNet;
using Cohesive.Adapters.Postgres;
using Cohesive.Model;
using Cohesive.Prelude;
using Cohesive.Relations.IR;
using Cohesive.Relations.Execution;
using Cohesive.Storage;
using Microsoft.AspNetCore.Builder;
using Npgsql;

namespace Cohesive.Adapters.Aspire.Tests;

public sealed partial class OrderStorageIntegrationTests
{
    [PostgresFact]
    public async Task Declared_join_returns_empty_and_multiple_reservations_with_local_partition_isolation()
    {
        await using var database = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable(ConnectionVariable)!);
        using var schema = new StreamReader(typeof(OrderStorage).Assembly.GetManifestResourceStream("Orders.schema.sql")!);
        await using (var command = database.CreateCommand(await schema.ReadToEndAsync())) await command.ExecuteNonQueryAsync();
        var context = OperationContext.Create();
        var orders = OrderStorage.Bind(database);
        var runtime = new PostgresNpgsqlRuntimeBinding(new("orders"), database, "tests/fulfillment");
        var inventory = new PostgresEntityRepository(FulfillmentDomain.Inventory.Definition, runtime, FulfillmentStorage.Inventory);
        var reservations = new PostgresEntityRepository(FulfillmentDomain.Reservations.Definition, runtime, FulfillmentStorage.Reservations);
        var id = Guid.NewGuid().ToString("D");
        var outsideId = Guid.NewGuid().ToString("D");
        var sku = "book-" + Guid.NewGuid().ToString("N");
        try
        {
            await orders.Upsert(context, OrderStorage.Register(Guid.Parse(id)));
            await orders.Upsert(context, new(OrderStorage.Entity.CreateState(id, new Order(id, "outside", "Private"), 1).Snapshot));
            await orders.Upsert(context, new(OrderStorage.Entity.CreateState(outsideId, new Order(outsideId, "outside"), 1).Snapshot));
            var queryReader = OrderQueryInfrastructure.Bind(database);
            IRelationQueryRowsReader nativeReader = new PostgresQueryRowsReader(queryReader.Artifact, runtime, maximumRows: 1000, maximumBytes: 1_000_000);
            var initialRows = await nativeReader.ReadAsync(new Dictionary<QueryParameterId, ObservationValue>
            { [OrderDetailsQuery.Definition.Parameter] = ObservationValue.FromString(id) });
            Assert.False(Assert.Single(initialRows).TryGetField(FieldPath.FromField("ReservationId"), out _));
            var builder = WebApplication.CreateBuilder();
            builder.Services.AddRequestOperationContext();
            await using var app = builder.Build();
            app.UseRequestOperationContext();
            OrderEndpoints.Map(app, orders, queryReader);
            app.Urls.Add("http://127.0.0.1:0");
            await app.StartAsync();
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            var empty = await client.GetFromJsonAsync<OrderDetails>($"/orders/{id}/details");
            Assert.Equal("Draft", empty!.Status);
            Assert.Empty(empty.Reservations);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/orders/{outsideId}/details")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/orders/{Guid.NewGuid():D}/details")).StatusCode);
            await inventory.Upsert(context, new(FulfillmentDomain.Inventory.Definition.CreateState(sku,
                new InventoryItem(sku, "local", 8), 1).Snapshot));
            for (var index = 1; index <= 2; index++)
            {
                var reservationId = Guid.NewGuid().ToString("D");
                await reservations.Upsert(context, new(FulfillmentDomain.Reservations.Definition.CreateState(reservationId,
                    new Reservation(reservationId, "local", id, sku, index), 1).Snapshot));
            }
            var joined = await client.GetFromJsonAsync<OrderDetails>($"/orders/{id}/details");
            Assert.Equal(2, joined!.Reservations.Count);
            Assert.Equal(new[] { 1, 2 }, joined.Reservations.Select(item => item.Quantity).Order());
            Assert.All(joined.Reservations, item => { Assert.Equal(sku, item.Sku); Assert.Equal(8, item.AvailableStock); });
            var values = new Dictionary<QueryParameterId, ObservationValue> { [OrderDetailsQuery.Definition.Parameter] = ObservationValue.FromString(id) };
            await Assert.ThrowsAsync<InvalidOperationException>(() => OrderQueryInfrastructure.Bind(database, maximumRows: 1).ReadAsync(id));
            IRelationQueryRowsReader tiny = new PostgresQueryRowsReader(queryReader.Artifact, runtime, maximumRows: 10, maximumBytes: 1);
            await Assert.ThrowsAnyAsync<InvalidOperationException>(() => tiny.ReadAsync(values));
            Assert.Throws<ArgumentException>(() => new PostgresQueryRowsReader(queryReader.Artifact,
                new(new("wrong-database"), database, "tests/fulfillment"), 10, 1000));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queryReader.ReadAsync(id, new CancellationToken(true)));
            Assert.Null(await queryReader.ReadAsync("' OR true --"));
            await app.StopAsync();
        }
        finally
        {
            foreach (var (sql, value) in new[]
            {
                ("DELETE FROM public.cohesive_reservations WHERE order_id = $1", id),
                ("DELETE FROM public.cohesive_inventory WHERE sku = $1", sku),
                ("DELETE FROM public.cohesive_orders WHERE order_id = $1", id),
                ("DELETE FROM public.cohesive_orders WHERE order_id = $1", outsideId)
            })
            {
                await using var cleanup = database.CreateCommand(sql);
                cleanup.Parameters.AddWithValue(value);
                await cleanup.ExecuteNonQueryAsync();
            }
        }
    }
}
