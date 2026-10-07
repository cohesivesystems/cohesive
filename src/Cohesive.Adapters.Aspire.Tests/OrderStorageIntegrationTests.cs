using AspireFirst.Orders;
using System.Diagnostics;
using System.Text.Json;
using Cohesive.Api;
using Cohesive.Transitions.Model;
using Cohesive.Adapters.AspNet;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
using Cohesive.Prelude;
using Cohesive.Storage;
using Npgsql;

namespace Cohesive.Adapters.Aspire.Tests;

public sealed partial class OrderStorageIntegrationTests
{
    const string ConnectionVariable = "COHESIVE_ORDER_EXAMPLE_TEST_CONNECTION_STRING";

    [PostgresFact]
    public async Task Example_schema_round_trip_and_stale_write_rejection()
    {
        // Only point this opt-in test at a disposable example database.
        await using var database = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable(ConnectionVariable)!);
        using var schema = new StreamReader(typeof(OrderStorage).Assembly.GetManifestResourceStream("Orders.schema.sql")!);
        await using (var command = database.CreateCommand(await schema.ReadToEndAsync()))
            await command.ExecuteNonQueryAsync();
        var repository = OrderStorage.Bind(database);
        var context = OperationContext.Create();
        var id = Guid.NewGuid();
        try
        {
            var first = await repository.Upsert(context, OrderStorage.Register(id));
            var loaded = await repository.TryGet(context, id.ToString("D"), new EntityReadOptions(partitionKey: OrderStorage.LocalPartition));
            Assert.NotNull(loaded);
            Assert.Equal(first.ConcurrencyToken, loaded.ConcurrencyToken);
            var second = await repository.Upsert(context, new EntityWriteRequest(loaded.Entity, loaded.ConcurrencyToken));
            Assert.NotEqual(first.ConcurrencyToken, second.ConcurrencyToken);
            await Assert.ThrowsAsync<ObservationConcurrencyConflictException>(() => repository.Upsert(context,
                new EntityWriteRequest(loaded.Entity, loaded.ConcurrencyToken)));
            var reloaded = await repository.TryGet(context, id.ToString("D"), new EntityReadOptions(partitionKey: OrderStorage.LocalPartition));
            Assert.Equal(second.ConcurrencyToken, reloaded!.ConcurrencyToken);

            var builder = WebApplication.CreateBuilder();
            builder.Services.AddRequestOperationContext();
            await using var app = builder.Build();
            app.UseRequestOperationContext();
            OrderEndpoints.Map(app, repository, OrderQueryInfrastructure.Bind(database).ReadAsync);
            app.Urls.Add("http://127.0.0.1:0");
            await app.StartAsync();
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            var created = await client.PostAsync("/orders", null);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var createdOrder = await created.Content.ReadFromJsonAsync<OrderCreated>();
            Assert.NotNull(createdOrder);
            Assert.True(Guid.TryParse(createdOrder.Id, out _));
            try
            {
                var fetched = await client.GetFromJsonAsync<OrderSummary>($"/orders/{createdOrder.Id}");
                Assert.Equal("Draft", fetched!.Status);
                Assert.Equal(createdOrder.Id, fetched.Id);
                Assert.Equal(HttpStatusCode.NotFound,
                    (await client.GetAsync($"/orders/{Guid.NewGuid():D}")).StatusCode);
            }
            finally
            {
                await using var removeCreated = database.CreateCommand("DELETE FROM public.cohesive_orders WHERE partition_key = $1 AND order_id = $2");
                removeCreated.Parameters.AddWithValue(OrderStorage.LocalPartition);
                removeCreated.Parameters.AddWithValue(createdOrder.Id);
                await removeCreated.ExecuteNonQueryAsync();
            }
            var submitted = await client.PostAsync($"/orders/{id:D}/submit", null);
            Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);
            var stored = await repository.TryGet(context, id.ToString("D"), new EntityReadOptions(partitionKey: OrderStorage.LocalPartition));
            Assert.Equal("Submitted", stored!.Entity.Observation.GetField("status").GetRequiredString());
            var rejected = await client.PostAsync($"/orders/{id:D}/submit", null);
            Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
            var unchanged = await repository.TryGet(context, id.ToString("D"), new EntityReadOptions(partitionKey: OrderStorage.LocalPartition));
            Assert.Equal(stored.ConcurrencyToken, unchanged!.ConcurrencyToken);
            await Assert.ThrowsAsync<ObservationConcurrencyConflictException>(() => repository.Upsert(context,
                new EntityWriteRequest(reloaded.Entity, reloaded.ConcurrencyToken)));
            Assert.Equal(HttpStatusCode.NotFound,
                (await client.PostAsync($"/orders/{Guid.NewGuid():D}/submit", null)).StatusCode);
            await app.StopAsync();
        }
        finally
        {
            await using var cleanup = database.CreateCommand("DELETE FROM public.cohesive_orders WHERE partition_key = $1 AND order_id = $2");
            cleanup.Parameters.AddWithValue(OrderStorage.LocalPartition);
            cleanup.Parameters.AddWithValue(id.ToString("D"));
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [PostgresFact]
    public async Task Concurrent_HTTP_submits_return_one_commit_and_one_sanitized_conflict_without_middleware()
    {
        await using var database = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable(ConnectionVariable)!);
        using var schema = new StreamReader(typeof(OrderStorage).Assembly.GetManifestResourceStream("Orders.schema.sql")!);
        await using (var command = database.CreateCommand(await schema.ReadToEndAsync()))
            await command.ExecuteNonQueryAsync();
        var repository = OrderStorage.Bind(database);
        var context = OperationContext.Create();
        var id = Guid.NewGuid();
        var initial = await repository.Upsert(context, OrderStorage.Register(id));
        try
        {
            // Both HTTP requests must observe the same token before either can commit.
            var racing = new SynchronizedReads(repository);
            var builder = WebApplication.CreateBuilder();
            builder.Services.AddRequestOperationContext();
            await using var app = builder.Build();
            app.UseRequestOperationContext();
            OrderEndpoints.Map(app, racing, OrderQueryInfrastructure.Bind(database).ReadAsync);
            app.Urls.Add("http://127.0.0.1:0");
            await app.StartAsync();
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            var responses = await Task.WhenAll(client.PostAsync($"/orders/{id:D}/submit", null),
                client.PostAsync($"/orders/{id:D}/submit", null));
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
            var conflict = Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
            await AssertProblem(conflict, ApiProblemCodes.ConcurrencyConflict);
            var body = await conflict.Content.ReadAsStringAsync();
            Assert.DoesNotContain(initial.ConcurrencyToken!.ToString()!, body);
            Assert.DoesNotContain(id.ToString("D"), body);
            Assert.Contains("traceId", body);
            Assert.Equal(2, racing.ReadCount);
            Assert.Equal(2, racing.WriteCount); // No hidden retry.
            var stored = await repository.TryGet(context, id.ToString("D"), new EntityReadOptions(partitionKey: OrderStorage.LocalPartition));
            Assert.Equal("Submitted", stored!.Entity.Observation.GetField("status").GetRequiredString());
            Assert.Equal(initial.Entity.Version + 1, stored.Entity.Version);
            await app.StopAsync();
        }
        finally { await DeleteOrder(database, id.ToString("D")); }
    }

    [PostgresFact]
    public async Task Actual_program_bootstraps_schema_context_and_order_HTTP_contract()
    {
        var connection = Environment.GetEnvironmentVariable(ConnectionVariable)!;
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(typeof(OrderStorage).Assembly.Location);
        start.Environment["ConnectionStrings__orders"] = connection;
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
            address.TrySetException(new InvalidOperationException("Example exited before listening."));
        });
        var errors = process.StandardError.ReadToEndAsync();
        string? id = null;
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(await address.Task.WaitAsync(TimeSpan.FromSeconds(30))) };
            var created = await client.PostAsync("/orders", null);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            id = (await created.Content.ReadFromJsonAsync<OrderCreated>())!.Id;
            Assert.Equal($"/orders/{id}", created.Headers.Location!.OriginalString);
            Assert.Equal("Draft", (await client.GetFromJsonAsync<OrderSummary>($"/orders/{id}"))!.Status);
            Assert.Empty((await client.GetFromJsonAsync<OrderDetails>($"/orders/{id}/details"))!.Reservations);
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/orders/{id}/submit", null)).StatusCode);
            await AssertProblem(await client.PostAsync($"/orders/{id}/submit", null), "orders.submit.rejected");
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/orders/{Guid.NewGuid():D}")).StatusCode);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await output;
            await errors;
            if (id is not null)
            {
                await using var database = NpgsqlDataSource.Create(connection);
                await DeleteOrder(database, id);
            }
        }
    }

    static async Task AssertProblem(HttpResponseMessage response, string code)
    {
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(409, body.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(code, body.RootElement.GetProperty("code").GetString());
    }

    static async Task DeleteOrder(NpgsqlDataSource database, string id)
    {
        await using var command = database.CreateCommand("DELETE FROM public.cohesive_orders WHERE partition_key = $1 AND order_id = $2");
        command.Parameters.AddWithValue(OrderStorage.LocalPartition);
        command.Parameters.AddWithValue(id);
        await command.ExecuteNonQueryAsync();
    }

    sealed class SynchronizedReads(IEntityRepository inner) : IEntityRepository
    {
        readonly TaskCompletionSource bothRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int reads;
        int writes;
        public int ReadCount => reads;
        public int WriteCount => writes;
        public EntityDefinition EntityDefinition => inner.EntityDefinition;
        public async Task<EntitySnapshot?> TryGet(OperationContext context, string id, EntityReadOptions? options = null)
        {
            var result = await inner.TryGet(context, id, options);
            if (Interlocked.Increment(ref reads) == 2) bothRead.TrySetResult();
            await bothRead.Task.WaitAsync(TimeSpan.FromSeconds(10), context.CancellationToken);
            return result;
        }
        public Task<EntitySnapshot> Upsert(OperationContext context, EntityWriteRequest write)
        {
            Interlocked.Increment(ref writes);
            return inner.Upsert(context, write);
        }
    }

    sealed class PostgresFactAttribute : FactAttribute
    {
        public PostgresFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionVariable)))
                Skip = $"Set {ConnectionVariable} to a disposable PostgreSQL database.";
        }
    }
}
