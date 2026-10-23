using Cohesive.Adapters.Marten;
using Marten;
using JasperFx.Events;
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
    readonly AspireFirst.Orders.FulfillmentApplication Model = TestModels.Fulfillment();
    const string ConnectionVariable = "COHESIVE_ORDER_EXAMPLE_TEST_CONNECTION_STRING";

    [PostgresFact]
    public async Task Model_receipt_bootstrap_creates_validates_and_rejects_incompatible_schema()
    {
        await using var database = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable(ConnectionVariable)!);
        var table = "receipts_" + Guid.NewGuid().ToString("N");
        var bindings = Cohesive.Adapters.Postgres.PostgresPersistenceBindings.Define(new("test"), storage => storage
            .TransitionReceipts(new("public", table, "local"),
                Cohesive.Adapters.Postgres.PostgresSchemaInitialization.CreateIfMissing));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => bindings.PrepareReceiptsAsync(database, canceled.Token));
        await using (var exists = database.CreateCommand("SELECT to_regclass($1) IS NULL"))
        {
            exists.Parameters.AddWithValue("public." + table);
            Assert.Equal(true, await exists.ExecuteScalarAsync());
        }
        try
        {
            Assert.NotNull(await bindings.PrepareReceiptsAsync(database));
            Assert.NotNull(await bindings.PrepareReceiptsAsync(database));
            await using (var alter = database.CreateCommand($"ALTER TABLE public.{table} DROP COLUMN content_hash"))
                await alter.ExecuteNonQueryAsync();
            var failure = await Assert.ThrowsAsync<Cohesive.Adapters.Postgres.PostgresSchemaValidationException>(() => bindings.PrepareReceiptsAsync(database));
            Assert.Equal(Cohesive.Adapters.Postgres.PostgresSchemaDiagnosticCodes.ColumnMismatch, failure.Code);
            await using var column = database.CreateCommand("SELECT count(*) FROM information_schema.columns WHERE table_name=$1 AND column_name='content_hash'");
            column.Parameters.AddWithValue(table);
            Assert.Equal(0L, await column.ExecuteScalarAsync());
        }
        finally { await using var drop = database.CreateCommand($"DROP TABLE IF EXISTS public.{table}"); await drop.ExecuteNonQueryAsync(); }
    }

    [PostgresFact]
    public async Task Model_receipt_policy_validates_without_migrating_and_binds_every_repository()
    {
        await using var database = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable(ConnectionVariable)!);
        var table = "receipts_" + Guid.NewGuid().ToString("N");
        var options = new Cohesive.Adapters.Postgres.PostgresTransitionReceiptOptions("public", table, FulfillmentStoragePolicy.LocalPartition);
        var native = Cohesive.Adapters.Postgres.PostgresPersistenceBindings.Define(new(FulfillmentStoragePolicy.OrdersDatabaseName), storage => storage
            .TransitionReceipts(options)
            .Entity(Model.Domain.Orders, Model.Storage.Bindings.Mapping(Model.Domain.Orders))
            .Entity(Model.Domain.Inventory, Model.Storage.Bindings.Mapping(Model.Domain.Inventory))
            .Entity(Model.Domain.Reservations, Model.Storage.Bindings.Mapping(Model.Domain.Reservations)));
        var composition = new Cohesive.Adapters.Postgres.Services.PostgresStorageComposition(native);
        var registrations = new ServiceCollection();
        registrations.AddKeyedSingleton(FulfillmentStoragePolicy.OrdersDatabaseName, database);
        await using var provider = registrations.BuildServiceProvider();
        var missing = await Assert.ThrowsAsync<Cohesive.Adapters.Postgres.PostgresSchemaValidationException>(() => composition.BindAsync(provider).AsTask());
        Assert.Equal(Cohesive.Adapters.Postgres.PostgresSchemaDiagnosticCodes.TableMissing, missing.Code);
        await using (var exists = database.CreateCommand("SELECT to_regclass($1) IS NULL"))
        {
            exists.Parameters.AddWithValue("public." + table);
            Assert.Equal(true, await exists.ExecuteScalarAsync());
        }
        try
        {
            await using (var migrate = database.CreateCommand(options.SchemaSql)) await migrate.ExecuteNonQueryAsync();
            var persistence = await composition.BindAsync(provider);
            foreach (var entity in new[] { Model.Domain.Orders.Definition, Model.Domain.Inventory.Definition, Model.Domain.Reservations.Definition })
            {
                var repository = persistence.Repository(ValueContract.FromShape(entity.Shape));
                Assert.True(repository.TransitionOperationCapabilities.SupportsAtomicStateAndReceipt);
                Assert.Equal(FulfillmentStoragePolicy.LocalPartition, repository.TransitionOperationCapabilities.PartitionKey);
            }
        }
        finally { await using var drop = database.CreateCommand($"DROP TABLE public.{table}"); await drop.ExecuteNonQueryAsync(); }
    }

    [PostgresFact]
    public async Task Example_schema_round_trip_and_stale_write_rejection()
    {
        // Only point this opt-in test at a disposable example database.
        await using var database = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable(ConnectionVariable)!);
        await Model.Storage.Bindings.PrepareSchemaAsync(database);
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
        await Model.Storage.Bindings.PrepareSchemaAsync(database);
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

    [PostgresTheory]
    [InlineData(EntityStateAuthority.State)]
    [InlineData(EntityStateAuthority.Events)]
    public async Task Actual_program_bootstraps_schema_context_and_order_HTTP_contract(EntityStateAuthority authority)
    {
        var connection = Environment.GetEnvironmentVariable(ConnectionVariable)!;
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(typeof(FulfillmentApplication).Assembly.Location);
        start.Environment["ConnectionStrings__orders"] = connection;
        start.Environment["EntityState__Authority"] = authority.ToString();
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
            if (authority == EntityStateAuthority.State)
            {
                var opaqueId = "order-" + Guid.NewGuid().ToString("N");
                await using (var database = NpgsqlDataSource.Create(connection))
                await using (var rename = database.CreateCommand("UPDATE public.cohesive_orders SET order_id=$1 WHERE order_id=$2"))
                {
                    rename.Parameters.AddWithValue(opaqueId);
                    rename.Parameters.AddWithValue(id);
                    Assert.Equal(1, await rename.ExecuteNonQueryAsync());
                }
                id = opaqueId;
            }
            Assert.Equal(OrderStatus.Draft, (await client.GetFromJsonAsync<OrderSummary>($"/orders/{id}"))!.Status);
            Assert.Empty((await client.GetFromJsonAsync<OrderDetails>($"/orders/{id}/details"))!.Reservations);
            await using (var database = NpgsqlDataSource.Create(connection))
            {
                using var events = DocumentStore.For(options =>
                {
                    options.Connection(database); options.DatabaseSchemaName = "cohesive_events";
                    options.AutoCreateSchemaObjects = JasperFx.AutoCreate.None;
                    options.UseCohesiveEntityHistory();
                });
                var history = await MartenEntityHistoryStorage.BindAsync(database, events, authority);
                var inventory = Model.Storage.Bindings.Bind(database, "test/seed").Repository(Model.Domain.Inventory, stateHistory: history);
                await inventory.Upsert(OperationContext.Create(), new InventoryItem(fulfillmentSku, FulfillmentStoragePolicy.LocalPartition, 5));
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
            Assert.Equal(FulfillmentStatus.Submitted, (await fulfillment.Content.ReadFromJsonAsync<FulfillmentResult>())!.Status);
            Assert.Equal(id, Assert.Single((await client.GetFromJsonAsync<ReservationAvailability[]>($"/orders/{id}/availability"))!).ReservationId);
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
        await using (var reservations = database.CreateCommand("DELETE FROM public.cohesive_reservations WHERE partition_key = $1 AND order_id = $2"))
        {
            reservations.Parameters.AddWithValue(FulfillmentStoragePolicy.LocalPartition);
            reservations.Parameters.AddWithValue(id);
            await reservations.ExecuteNonQueryAsync();
        }
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

    sealed class PostgresTheoryAttribute : TheoryAttribute
    {
        public PostgresTheoryAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionVariable)))
                Skip = $"Set {ConnectionVariable} to a disposable PostgreSQL database.";
        }
    }

    internal sealed class PostgresFactAttribute : FactAttribute
    {
        public PostgresFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionVariable)))
                Skip = $"Set {ConnectionVariable} to a disposable PostgreSQL database.";
        }
    }
}
