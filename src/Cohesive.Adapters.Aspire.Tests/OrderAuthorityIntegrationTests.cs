using Microsoft.Extensions.DependencyInjection;
using Cohesive.Adapters.Marten;
using Marten;
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
        foreach (var authority in new[] { EntityStateAuthority.State, EntityStateAuthority.Events })
        {
            var model = TestModels.Fulfillment(authority);
            var policy = Assert.IsType<MartenEntityHistoryConfiguration>(model.Storage.Bindings.HistoryConfiguration);
            Assert.Equal(authority, policy.Authority);
            var resource = authority == EntityStateAuthority.Events ? "orders_events" : "orders";
            Assert.Equal(resource, model.Storage.Bindings.Database.Value);
            using var store = DocumentStore.For(options =>
            {
                options.Connection(database);
                policy.Configure(options);
            });
            string? id = null;
            try
            {
                var builder = WebApplication.CreateBuilder();
                builder.Services.AddRequestOperationContext();
                builder.Services.AddFixedRequestIdentity(FulfillmentDemoIdentity.Context);
                builder.Services.AddKeyedSingleton<NpgsqlDataSource>(resource, database);
                builder.Services.AddKeyedSingleton<IDocumentStore>(resource, store);
                builder.Services.AddCohesiveServices(model.Endpoints, model.Storage);
                await using var app = builder.Build();
                app.UseRequestOperationContext(); app.UseAuthorization();
                await app.MapServicesAsync<OrderEndpoints>();
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
                await using var cleanup = database.CreateCommand("DELETE FROM public.cohesive_orders WHERE order_id=$1");
                cleanup.Parameters.AddWithValue(id ?? "unused");
                await cleanup.ExecuteNonQueryAsync();
            }
        }
    }
}
