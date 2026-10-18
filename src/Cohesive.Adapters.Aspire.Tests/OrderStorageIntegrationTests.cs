using Cohesive.Host.Composition;
using Cohesive.Api.Execution.Services;
using AspireFirst.Orders;
using System.Diagnostics;
using System.Text.Json;
using Cohesive.Api;
using Cohesive.Model;
using Cohesive.Transitions.Model;
using Cohesive.Adapters.AspNet;
using Cohesive.Adapters.AspNet.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
using Cohesive.Prelude;
using Cohesive.Storage;
using Npgsql;

namespace Cohesive.Adapters.Aspire.Tests;

[Collection("Order PostgreSQL schema")]
public sealed partial class OrderStorageIntegrationTests
{
    readonly AspireFirst.Orders.FulfillmentApplication Model = ModelActivator.Create<AspireFirst.Orders.FulfillmentApplication>();
    const string ConnectionVariable = "COHESIVE_ORDER_EXAMPLE_TEST_CONNECTION_STRING";

    [PostgresFact]
    public async Task Example_schema_round_trip_and_stale_write_rejection()
    {
        // Only point this opt-in test at a disposable example database.
        await using var database = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable(ConnectionVariable)!);
        using var schema = new StreamReader(typeof(FulfillmentApplication).Assembly.GetManifestResourceStream("Orders.schema.sql")!);
        await using (var command = database.CreateCommand(await schema.ReadToEndAsync()))
            await command.ExecuteNonQueryAsync();
        var persistence = Model.Storage.Bindings.Bind(database, authority: "test/fulfillment");
        var repository = persistence.Repository(Model.Domain.Orders);
        var context = OperationContext.Create();
        var id = Guid.NewGuid();
        try
        {
            var first = await repository.Upsert(context, OrderTestFixture.RegisterOrder(Model.Domain, id));
            var loaded = await repository.TryGet(context, id.ToString("D"), new EntityReadOptions(partitionKey: FulfillmentStoragePolicy.LocalPartition));
            Assert.NotNull(loaded);
            var typed = await repository.TryGetEntity(context, id.ToString("D"), new EntityReadOptions(partitionKey: FulfillmentStoragePolicy.LocalPartition));
            Assert.Equal(id.ToString("D"), typed!.Id);
            Assert.Equal(FulfillmentStoragePolicy.LocalPartition, typed.Partition);
            Assert.Equal(first.ConcurrencyToken, loaded.ConcurrencyToken);
            var second = await repository.Upsert(context, new EntityWriteRequest(loaded.Entity, loaded.ConcurrencyToken));
            Assert.NotEqual(first.ConcurrencyToken, second.ConcurrencyToken);
            await Assert.ThrowsAsync<ObservationConcurrencyConflictException>(() => repository.Upsert(context,
                new EntityWriteRequest(loaded.Entity, loaded.ConcurrencyToken)));
            var reloaded = await repository.TryGet(context, id.ToString("D"), new EntityReadOptions(partitionKey: FulfillmentStoragePolicy.LocalPartition));
            Assert.Equal(second.ConcurrencyToken, reloaded!.ConcurrencyToken);

            var builder = WebApplication.CreateBuilder();
            var services = await OrderHttpTestHost.Prepare(builder, Model, persistence, database);
            await using var app = builder.Build();
            app.UseRequestOperationContext();
            app.UseAuthorization();
            app.MapServices(services);
            var routes = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints)
                .OfType<RouteEndpoint>().Select(endpoint => endpoint.RoutePattern.RawText).ToArray();
            Assert.Contains("/fulfillment", routes);
            Assert.Contains("/fulfillment/results/{instanceId}", routes);
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
                Assert.Equal(OrderStatus.Draft, fetched!.Status);
                Assert.Equal(createdOrder.Id, fetched.Id);
                Assert.Equal(HttpStatusCode.NotFound,
                    (await client.GetAsync($"/orders/{Guid.NewGuid():D}")).StatusCode);
            }
            finally
            {
                await using var removeCreated = database.CreateCommand("DELETE FROM public.cohesive_orders WHERE partition_key = $1 AND order_id = $2");
                removeCreated.Parameters.AddWithValue(FulfillmentStoragePolicy.LocalPartition);
                removeCreated.Parameters.AddWithValue(createdOrder.Id);
                await removeCreated.ExecuteNonQueryAsync();
            }
            var submitted = await client.PostAsync($"/orders/{id:D}/submit", null);
            Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);
            var stored = await repository.TryGet(context, id.ToString("D"), new EntityReadOptions(partitionKey: FulfillmentStoragePolicy.LocalPartition));
            Assert.Equal("Submitted", stored!.Entity.Observation.GetField("status").GetRequiredString());
            var rejected = await client.PostAsync($"/orders/{id:D}/submit", null);
            Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
            var unchanged = await repository.TryGet(context, id.ToString("D"), new EntityReadOptions(partitionKey: FulfillmentStoragePolicy.LocalPartition));
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
            cleanup.Parameters.AddWithValue(FulfillmentStoragePolicy.LocalPartition);
            cleanup.Parameters.AddWithValue(id.ToString("D"));
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [PostgresFact]
    public async Task Concurrent_HTTP_submits_return_one_commit_and_one_sanitized_conflict_without_middleware()
    {
        await using var database = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable(ConnectionVariable)!);
        using var schema = new StreamReader(typeof(FulfillmentApplication).Assembly.GetManifestResourceStream("Orders.schema.sql")!);
        await using (var command = database.CreateCommand(await schema.ReadToEndAsync()))
            await command.ExecuteNonQueryAsync();
        var persistence = Model.Storage.Bindings.Bind(database, authority: "test/fulfillment");
        var repository = persistence.Repository(Model.Domain.Orders);
        var context = OperationContext.Create();
        var id = Guid.NewGuid();
        var initial = await repository.Upsert(context, OrderTestFixture.RegisterOrder(Model.Domain, id));
        try
        {
            // Both HTTP requests must observe the same token before either can commit.
            SynchronizedReads? racing = null;
            var builder = WebApplication.CreateBuilder();
            var services = await OrderHttpTestHost.Prepare(builder, Model, persistence, database,
                repository => racing = new SynchronizedReads(repository));
            Assert.NotNull(racing);
            await using var app = builder.Build();
            app.UseRequestOperationContext();
            app.UseAuthorization();
            app.MapServices(services);
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
            var stored = await repository.TryGet(context, id.ToString("D"), new EntityReadOptions(partitionKey: FulfillmentStoragePolicy.LocalPartition));
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
        start.ArgumentList.Add(typeof(FulfillmentApplication).Assembly.Location);
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
        var fulfillmentSku = "http-" + Guid.NewGuid().ToString("N");
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(await address.Task.WaitAsync(TimeSpan.FromSeconds(30))) };
            var created = await client.PostAsync("/orders", null);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            id = (await created.Content.ReadFromJsonAsync<OrderCreated>())!.Id;
            Assert.Equal($"/orders/{id}", created.Headers.Location!.OriginalString);
            // Domain identity is an opaque string, even though creation currently allocates UUID text.
            var opaqueId = "order-" + Guid.NewGuid().ToString("N");
            await using (var database = NpgsqlDataSource.Create(connection))
            await using (var rename = database.CreateCommand("UPDATE public.cohesive_orders SET order_id=$1 WHERE order_id=$2"))
            {
                rename.Parameters.AddWithValue(opaqueId);
                rename.Parameters.AddWithValue(id);
                Assert.Equal(1, await rename.ExecuteNonQueryAsync());
            }
            id = opaqueId;
            Assert.Equal(OrderStatus.Draft, (await client.GetFromJsonAsync<OrderSummary>($"/orders/{id}"))!.Status);
            Assert.Empty((await client.GetFromJsonAsync<OrderDetails>($"/orders/{id}/details"))!.Reservations);
            await using (var database = NpgsqlDataSource.Create(connection))
            await using (var seed = database.CreateCommand("INSERT INTO public.cohesive_inventory VALUES ($1, 'local', 5, 0)"))
            {
                seed.Parameters.AddWithValue(fulfillmentSku);
                await seed.ExecuteNonQueryAsync();
            }
            var fulfillment = await client.PostAsJsonAsync("/fulfillment", new ServiceProcessCommand<FulfillOrder>(id, id, id, "1", new(id, fulfillmentSku, 2)));
            Assert.Equal(HttpStatusCode.Accepted, fulfillment.StatusCode);
            var location = fulfillment.Headers.Location!;
            using var completionDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            do
            {
                fulfillment = await client.GetAsync(location, completionDeadline.Token);
                if (fulfillment.StatusCode == HttpStatusCode.Accepted) await Task.Delay(50, completionDeadline.Token);
            } while (fulfillment.StatusCode == HttpStatusCode.Accepted);
            Assert.Equal(HttpStatusCode.OK, fulfillment.StatusCode);
            Assert.Equal("Submitted", (await fulfillment.Content.ReadFromJsonAsync<FulfillmentResult>())!.Status);
            Assert.Null(Assert.Single((await client.GetFromJsonAsync<ReservationAvailability[]>($"/orders/{id}/availability"))!).ReservationId);
            await AssertProblem(await client.PostAsync($"/orders/{id}/submit", null), "orders.submit.NotDraft");
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
                await using (var removeReceipts = database.CreateCommand("DELETE FROM public.cohesive_process_receipts WHERE subject_id=$1 OR subject_id=$2"))
                {
                    removeReceipts.Parameters.AddWithValue(id);
                    removeReceipts.Parameters.AddWithValue(fulfillmentSku);
                    await removeReceipts.ExecuteNonQueryAsync();
                }
                await using var removeStock = database.CreateCommand("DELETE FROM public.cohesive_inventory WHERE sku=$1");
                removeStock.Parameters.AddWithValue(fulfillmentSku);
                await removeStock.ExecuteNonQueryAsync();
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
        command.Parameters.AddWithValue(FulfillmentStoragePolicy.LocalPartition);
        command.Parameters.AddWithValue(id);
        await command.ExecuteNonQueryAsync();
    }

    sealed class SynchronizedReads(IEntityRepository inner) : IEntityTransitionOperationRepository
    {
        readonly TaskCompletionSource bothRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int reads;
        int writes;
        public int ReadCount => reads;
        public int WriteCount => writes;
        public EntityDefinition EntityDefinition => inner.EntityDefinition;
        public string? IdentityField => inner.IdentityField;
        public EntityCreationCapabilities CreationCapabilities => inner.CreationCapabilities;
        public EntityTransitionOperationCapabilities TransitionOperationCapabilities => inner.TransitionOperationCapabilities;
        public Task<EntityTransitionOperationResult> TryGetTransitionOperation(OperationContext context, EntityTransitionOperationRequest request) => inner.TryGetTransitionOperation(context, request);
        public Task<EntityTransitionOperationResult> TryGetCreationTransitionOperation(OperationContext context, EntityTransitionOperationRequest request) => inner.TryGetCreationTransitionOperation(context, request);
        public Task<EntityTransitionOperationResult> ResolveTransitionOperation(OperationContext context, EntityTransitionOperationReference reference) => inner.ResolveTransitionOperation(context, reference);
        public Task<EntityTransitionOperationResult> CommitTransitionOperation(OperationContext context, EntityTransitionOperationCommit commit) => inner.CommitTransitionOperation(context, commit);
        public Task<EntitySnapshot> Create(OperationContext context, EntityObservationSnapshot entity, EntityCreationPolicy policy) => inner.Create(context, entity, policy);
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
