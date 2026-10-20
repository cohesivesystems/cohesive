using Microsoft.Extensions.DependencyInjection;
using Cohesive.Adapters.Marten;
using Marten;
using JasperFx.Events;
using AspireFirst.Orders;
using Cohesive.Adapters.AspNet;
using Cohesive.Adapters.AspNet.Services;
using Cohesive.Adapters.Postgres;
using Cohesive.Storage;
using Microsoft.AspNetCore.Builder;
using System.Net;
using System.Net.Http.Json;
using Npgsql;

namespace Cohesive.Adapters.Aspire.Tests;

public sealed partial class OrderStorageIntegrationTests
{
    [PostgresFact]
    public async Task Same_OrderApi_and_transition_use_state_or_events_without_parallel_bindings()
    {
        await using var database = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable(ConnectionVariable)!);
        await Model.Storage.Bindings.PrepareSchemaAsync(database);
        foreach (var authority in new[] { EntityStateAuthority.State, EntityStateAuthority.Events })
        {
            var model = Cohesive.Host.Composition.ModelActivator.Create<FulfillmentApplication>(services => services.AddSingleton(new FulfillmentHistoryPolicy()));
            var prefix = "authority_" + Guid.NewGuid().ToString("N");
            using var store = DocumentStore.For(options =>
            {
                options.AutoCreateSchemaObjects = JasperFx.AutoCreate.None;
                options.Connection(database); options.DatabaseSchemaName = prefix;
                options.UseCohesiveEntityHistory();
            });
            await store.Storage.ApplyAllConfiguredChangesToDatabaseAsync();
            var history = await MartenEntityHistoryStorage.BindAsync(database, store, authority, checkpointInterval: 1);
            string? id = null;
            try
            {
                var persistence = model.Storage.Bindings.Bind(database, authority: "test/fulfillment");
                var builder = WebApplication.CreateBuilder();
                var services = await OrderHttpTestHost.Prepare(builder, model, persistence, database, history: history);
                await using var app = builder.Build();
                app.UseRequestOperationContext(); app.UseAuthorization(); app.MapServices(services);
                app.Urls.Add("http://127.0.0.1:0");
                await app.StartAsync();
                using var client = new HttpClient { BaseAddress = new(app.Urls.Single()) };
                var create = await client.PostAsync("/orders", null);
                Assert.Equal(HttpStatusCode.Created, create.StatusCode);
                id = (await create.Content.ReadFromJsonAsync<OrderCreated>())!.Id;
                Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/orders/{id}/submit", null)).StatusCode);
                Assert.Equal(OrderStatus.Submitted, (await client.GetFromJsonAsync<OrderSummary>($"/orders/{id}"))!.Status);
                Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/orders/{id}/submit", null)).StatusCode);
                await using (var delete = database.CreateCommand("DELETE FROM public.cohesive_orders WHERE order_id=$1"))
                { delete.Parameters.AddWithValue(id); await delete.ExecuteNonQueryAsync(); }
                var reconstructed = await client.GetAsync($"/orders/{id}");
                Assert.Equal(authority == EntityStateAuthority.Events ? HttpStatusCode.OK : HttpStatusCode.NotFound, reconstructed.StatusCode);
                if (authority == EntityStateAuthority.Events)
                    Assert.Equal(OrderStatus.Submitted, (await reconstructed.Content.ReadFromJsonAsync<OrderSummary>())!.Status);
                await app.StopAsync();
            }
            finally
            {
                await using (var drop = database.CreateCommand($"DROP SCHEMA {prefix} CASCADE")) await drop.ExecuteNonQueryAsync();
                await using var cleanup = database.CreateCommand("DELETE FROM public.cohesive_orders WHERE order_id=$1");
                cleanup.Parameters.AddWithValue(id ?? "unused");
                await cleanup.ExecuteNonQueryAsync();
            }
        }
    }
}
