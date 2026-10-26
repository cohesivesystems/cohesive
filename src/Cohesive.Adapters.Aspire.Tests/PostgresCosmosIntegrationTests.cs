using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using AspireFirst.Orders;
using AspireFirst.PostgresCosmos;
using Cohesive.Application;
using Cohesive.Adapters.Postgres;
using Cohesive.Api.Execution.Services;
using Cohesive.Model;
using Cohesive.Prelude;
using Cohesive.Storage;
using Microsoft.Azure.Cosmos;
using Npgsql;
using Microsoft.Extensions.DependencyInjection;

namespace Cohesive.Adapters.Aspire.Tests;

public sealed class PostgresCosmosIntegrationTests
{
    const string PostgresVariable = "COHESIVE_MIXED_POSTGRES_CONNECTION_STRING";
    const string CosmosVariable = "COHESIVE_MIXED_COSMOS_CONNECTION_STRING";

    [MixedFact]
    public async Task Shared_HTTP_queries_and_accepted_fulfillment_use_both_real_stores()
    {
        var model = ModelActivator.Create<PostgresCosmosApplication>();
        await using var orders = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable(PostgresVariable)!);
        using var cosmos = new CosmosClient(Environment.GetEnvironmentVariable(CosmosVariable)!,
            new CosmosClientOptions { Serializer = new Cohesive.Adapters.Cosmos.CosmosSystemTextJsonSerializer(), ConnectionMode = ConnectionMode.Gateway, LimitToEndpoint = true, RequestTimeout = TimeSpan.FromSeconds(10) });
        var database = await cosmos.CreateDatabaseIfNotExistsAsync(PostgresCosmosStorage.InventoryDatabase);
        var container = (await database.Database.CreateContainerIfNotExistsAsync(PostgresCosmosStorage.InventoryContainer,
            PostgresCosmosStorage.PartitionPath)).Container;
        await model.Storage.PostgresBindings.PrepareSchemaAsync(orders);
        var receiptOptions = model.Storage.PostgresBindings.ReceiptOptions!;
        await using (var initialize = orders.CreateCommand(receiptOptions.SchemaSql)) await initialize.ExecuteNonQueryAsync();
        var services = new ServiceCollection();
        services.AddKeyedSingleton(FulfillmentStoragePolicy.OrdersDatabaseName, orders);
        services.AddKeyedSingleton(PostgresCosmosStorage.CosmosAccount, cosmos);
        await using var provider = services.BuildServiceProvider();
        var persistence = await model.Storage.RequireComposition<IServiceStorageComposition>().BindAsync(provider);
        var inventory = persistence.Repository(ValueContract.FromShape(model.Domain.Inventory.Definition.Shape));
        var sku = "mixed-" + Guid.NewGuid().ToString("N");
        var context = OperationContext.Create();
        await inventory.Upsert(context, new EntityWriteRequest(model.Domain.Inventory.Definition.CreateState(sku,
            new InventoryItem(sku, FulfillmentStoragePolicy.LocalPartition, 8), 1).Snapshot));
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(typeof(PostgresCosmosStorage).Assembly.Location);
        start.Environment["ConnectionStrings__orders"] = Environment.GetEnvironmentVariable(PostgresVariable)!;
        start.Environment["ConnectionStrings__inventory-cosmos"] = Environment.GetEnvironmentVariable(CosmosVariable)!;
        start.Environment["ASPNETCORE_URLS"] = "http://127.0.0.1:0";
        start.Environment["Logging__LogLevel__Default"] = "Information";
        using var process = Process.Start(start)!;
        var address = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var output = Task.Run(async () =>
        {
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                const string marker = "Now listening on: ";
                var index = line.IndexOf(marker, StringComparison.Ordinal);
                if (index >= 0) address.TrySetResult(line[(index + marker.Length)..].Trim());
            }
            address.TrySetException(new InvalidOperationException("Mixed worker exited before listening."));
        });
        var errors = process.StandardError.ReadToEndAsync();
        string? id = null;
        var reservationId = Guid.NewGuid().ToString("D");
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(await address.Task.WaitAsync(TimeSpan.FromSeconds(45))) };
            var created = await client.PostAsync("/orders", null);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            id = (await created.Content.ReadFromJsonAsync<OrderCreated>())!.Id;
            var empty = await client.GetFromJsonAsync<OrderDetails>($"/orders/{id}/details");
            Assert.Empty(empty!.Reservations);
            var reservations = persistence.Repository(ValueContract.FromShape(model.Domain.Reservations.Definition.Shape));
            await reservations.Upsert(context, new EntityWriteRequest(model.Domain.Reservations.Definition.CreateState(reservationId,
                new Reservation(reservationId, FulfillmentStoragePolicy.LocalPartition, id, sku, 2), 1).Snapshot));
            var details = await client.GetFromJsonAsync<OrderDetails>($"/orders/{id}/details");
            Assert.Equal(8, Assert.Single(details!.Reservations).AvailableStock);
            var availability = await client.GetFromJsonAsync<ReservationAvailability[]>($"/orders/{id}/availability");
            Assert.Equal(8, Assert.Single(availability!).Available);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/orders/{Guid.NewGuid():D}/details")).StatusCode);
            var command = new ServiceProcessCommand<FulfillOrder>(id, id, id, "1", new(id, sku, 2));
            var response = await client.PostAsJsonAsync("/fulfillment", command);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            var receipt = await response.Content.ReadFromJsonAsync<ServiceProcessReceipt>();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            do
            {
                response = await client.GetAsync(receipt!.ResultLocation, deadline.Token);
                if (response.StatusCode == HttpStatusCode.Accepted) await Task.Delay(50, deadline.Token);
            } while (response.StatusCode == HttpStatusCode.Accepted);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("Submitted", await response.Content.ReadAsStringAsync());
            var snapshot = await inventory.TryGet(context, sku, new EntityReadOptions(partitionKey: FulfillmentStoragePolicy.LocalPartition));
            Assert.Equal(6, snapshot!.Entity.Observation.GetField("available").GetInt32());
            var replay = await client.PostAsJsonAsync("/fulfillment", command);
            Assert.Equal(HttpStatusCode.Accepted, replay.StatusCode);
            snapshot = await inventory.TryGet(context, sku, new EntityReadOptions(partitionKey: FulfillmentStoragePolicy.LocalPartition));
            Assert.Equal(6, snapshot!.Entity.Observation.GetField("available").GetInt32());
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await output;
            await errors;
            foreach (var table in new[] { "cohesive_reservations", "cohesive_process_receipts", "cohesive_orders" })
            {
                var key = table == "cohesive_process_receipts" ? "subject_id" : "order_id";
                await using var cleanup = orders.CreateCommand($"DELETE FROM public.{table} WHERE {key}=$1");
                cleanup.Parameters.AddWithValue(id ?? "");
                await cleanup.ExecuteNonQueryAsync();
            }
            // Dedicated disposable emulator fixture: removes inventory and retained per-operation receipts together.
            await container.DeleteContainerAsync();
        }
    }

    sealed class MixedFactAttribute : FactAttribute
    {
        public MixedFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(PostgresVariable)) ||
                string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(CosmosVariable)))
                Skip = "Set both COHESIVE_MIXED_*_CONNECTION_STRING variables to disposable test stores.";
        }
    }
}
