using Cohesive.Identity;
using Cohesive.Adapters.FeatureManagement;
using System.Net;
using System.Text.Json;
using AspireFirst.Materialization;
using Cohesive.Adapters.Elastic;
using Cohesive.Adapters.Postgres;
using Cohesive.Relations.Execution;
using Cohesive.Storage.Materialization;
using Microsoft.Extensions.Configuration;
using Npgsql.Replication;
using System.Net.Http.Json;
using AspireFirst.Orders;
using Cohesive.Adapters.AspNet;
using Cohesive.Adapters.AspNet.Services;
using Cohesive.Adapters.DurableTask;
using Cohesive.Adapters.Marten;
using Cohesive.Api.Execution.Services;
using Cohesive.Application;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Processes.Execution;
using Cohesive.Storage;
using Marten;
using Microsoft.AspNetCore.Builder;
using Microsoft.DurableTask.Client.AzureManaged;
using Microsoft.DurableTask.Worker.AzureManaged;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Cohesive.Adapters.Aspire.Tests;

[Collection("Order PostgreSQL schema")]
public sealed class DurableFulfillmentIntegrationTests
{
    const string DatabaseVariable = "COHESIVE_ORDER_EXAMPLE_TEST_CONNECTION_STRING";
    const string SchedulerVariable = "DURABLE_TASK_SCHEDULER_CONNECTION_STRING";

    [Fact]
    public async Task Missing_durable_provider_fails_before_any_native_storage_is_resolved()
    {
        var model = ModelActivator.Create<FulfillmentApplication>(services => services
            .AddSingleton(new FulfillmentHistoryPolicy())
            .AddSingleton(new FulfillmentExecutionPolicy { Lifetime = ProcessExecutionLifetime.Durable }));
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddCohesiveServices(model);
        await using var app = builder.Build();
        var error = await Assert.ThrowsAsync<ServiceBindingValidationException>(() => app.MapServicesAsync());
        Assert.Contains("services.binding.durableProviderMissing", error.Message);
    }

    [SchedulerPostgresTheory]
    [InlineData(EntityStateAuthority.State)]
    [InlineData(EntityStateAuthority.Events)]
    public Task Accepted_fulfillment_and_receipts_survive_application_replacement(EntityStateAuthority authority) => Run(authority, Scenario.CompletedReplacement);

    [SchedulerPostgresTheory]
    [InlineData(EntityStateAuthority.State)]
    [InlineData(EntityStateAuthority.Events)]
    public Task Interrupted_inventory_commit_recovers_without_reserving_twice(EntityStateAuthority authority) => Run(authority, Scenario.InterruptedCommit);

    [CqrsTheory]
    [InlineData(EntityStateAuthority.State)]
    [InlineData(EntityStateAuthority.Events)]
    public Task Fulfillment_converges_through_CDC_then_reads_can_select_and_roll_back(EntityStateAuthority authority) =>
        Run(authority, Scenario.MaterializedReads);

    enum Scenario { CompletedReplacement, InterruptedCommit, MaterializedReads }

    static async Task Run(EntityStateAuthority authority, Scenario scenario)
    {
        var interruptAfterCommit = scenario == Scenario.InterruptedCommit;
        var materialize = scenario == Scenario.MaterializedReads;
        await using var database = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable(DatabaseVariable)!);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var model = CreateModel();
        FulfillmentApplication CreateModel(RelationQueryReadPolicy? reads = null) => ModelActivator.Create<FulfillmentApplication>(services => services
            .AddSingleton(new FulfillmentHistoryPolicy(authority))
            .AddSingleton(new FulfillmentExecutionPolicy { Lifetime = ProcessExecutionLifetime.Durable })
            .AddSingleton(reads ?? new RelationQueryReadPolicy()));
        var history = Assert.IsType<MartenEntityHistoryConfiguration>(model.Storage.Bindings.HistoryConfiguration);
        using var store = DocumentStore.For(options => { options.Connection(database); history.Configure(options); });
        var sku = "durable-" + Guid.NewGuid().ToString("N");
        string? orderId = null;
        CommitBarrier? barrier = null;
        var hosts = 0;
        using var elastic = materialize ? new HttpClient { BaseAddress = new(Environment.GetEnvironmentVariable("COHESIVE_SHADOW_ELASTIC")!) } : null;
        BoundMaterialization? shadow = null;
        ElasticMaterializationTarget? target = null;
        var runId = Guid.NewGuid().ToString("N");
        var attempt = new MaterializationRebuildAttempt(new(new(runId), new(runId)), DateTimeOffset.UtcNow);
        var comparisonFile = Path.GetTempFileName();
        try
        {
            ServiceProcessCommand<FulfillOrder> command;
            string location;
            string? result = null;
            await using (var first = await Start())
            {
                var prepared = await model.Storage.Bindings.PrepareAsync(first.Services, database, "tests/durable-fulfillment");
                await prepared.Persistence.Repository(model.Domain.Inventory, transitionReceipts: prepared.Receipts, stateHistory: prepared.History).Upsert(OperationContext.Create(),
                    new InventoryItem(sku, FulfillmentStoragePolicy.LocalPartition, 5));
                if (materialize)
                {
                    var shadowModel = ModelActivator.Create<OrderMaterializationApplication>(services => services.AddSingleton(new FulfillmentHistoryPolicy(authority)));
                    await ShadowBootstrap.InitializeAsync(shadowModel, database, elastic!, deadline.Token);
                    target = await ShadowElastic.BindAsync(elastic!, shadowModel.Materializations.OrderDetails, deadline.Token);
                    shadow = await shadowModel.Storage.OrderDetails.BindAsync(database,
                        () => new LogicalReplicationConnection(Environment.GetEnvironmentVariable(DatabaseVariable)!), target,
                        new byte[32], "test-slots/v1", deadline.Token);
                    await Synchronize(MaterializationShadowOperation.Rebuild, "baseline");
                }
                using var client = new HttpClient { BaseAddress = new(first.Urls.Single()) };
                var created = await client.PostAsync("/orders", null, deadline.Token);
                Assert.Equal(HttpStatusCode.Created, created.StatusCode);
                orderId = (await created.Content.ReadFromJsonAsync<OrderCreated>(deadline.Token))!.Id;
                command = new(orderId, orderId, orderId, "1", new(orderId, sku, 2));
                var accepted = await client.PostAsJsonAsync("/fulfillment", command, deadline.Token);
                Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
                var receipt = (await accepted.Content.ReadFromJsonAsync<ServiceProcessReceipt>(deadline.Token))!;
                location = receipt.ResultLocation;
                if (interruptAfterCommit)
                {
                    await barrier!.Committed.Task.WaitAsync(deadline.Token);
                    var stock = await prepared.Persistence.Repository(model.Domain.Inventory, stateHistory: prepared.History)
                        .TryGetEntity(OperationContext.Create(), sku, new(partitionKey: FulfillmentStoragePolicy.LocalPartition));
                    Assert.Equal(3, stock!.Available);
                }
                else result = await AwaitResult(client, location);
                await first.StopAsync(deadline.Token);
            }
            // A new application/provider/catalog reads the retained scheduler result and replays admission.
            await using (var replacement = await Start())
            {
                using var client = new HttpClient { BaseAddress = new(replacement.Urls.Single()) };
                var recovered = await AwaitResult(client, location);
                if (result is not null) Assert.Equal(result, recovered);
                result = recovered;
                Assert.Contains("ReservedAndSubmitted", result);
                var repeated = await client.PostAsJsonAsync("/fulfillment", command, deadline.Token);
                Assert.Equal(HttpStatusCode.Accepted, repeated.StatusCode);
                Assert.Equal(location, (await repeated.Content.ReadFromJsonAsync<ServiceProcessReceipt>(deadline.Token))!.ResultLocation);
                Assert.Equal(result, await AwaitResult(client, location));
                var details = await client.GetFromJsonAsync<OrderDetails>($"/orders/{orderId}/details", deadline.Token);
                Assert.Equal(OrderStatus.Submitted, details!.Status);
                var reservation = Assert.Single(details.Reservations);
                Assert.Equal(2, reservation.Quantity);
                Assert.Equal(3, reservation.AvailableStock);
                await replacement.StopAsync(deadline.Token);
            }
            if (materialize)
            {
                var caughtUp = await Synchronize(MaterializationShadowOperation.CatchUp, "after-fulfillment");
                var context = OperationContext.Create(cancellationToken: deadline.Token);
                var comparison = await MaterializationShadowVerification.CompareAsync(shadow!, context, caughtUp.Receipt!,
                    (generation, limit, token) => target!.ReadValuesAsync(OperationContext.Create(cancellationToken: token), generation, limit));
                Assert.True(comparison.Matches, JsonSerializer.Serialize(comparison));
                await File.WriteAllTextAsync(comparisonFile, JsonSerializer.Serialize(comparison), deadline.Token);
                string? selectedResult = null;
                foreach (var selected in new[] { true, false })
                {
                    await using var reader = await Start(new() { Source = selected ? RelationQueryReadSource.Materialized : RelationQueryReadSource.Live, AcceptEventualConsistency = selected });
                    using var client = new HttpClient { BaseAddress = new(reader.Urls.Single()) };
                    var response = await client.GetAsync($"/orders/{orderId}/details", deadline.Token);
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                    var body = await response.Content.ReadAsStringAsync(deadline.Token);
                    if (selected) selectedResult = body;
                    else Assert.Equal(selectedResult, body);
                    var details = await response.Content.ReadFromJsonAsync<OrderDetails>(deadline.Token);
                    Assert.Equal(OrderStatus.Submitted, details!.Status);
                    Assert.Equal(3, Assert.Single(details.Reservations).AvailableStock);
                    await reader.StopAsync(deadline.Token);
                }
                // One running host and one prepared reader pair: changes affect the next HTTP invocation.
                await using var switching = await Start(new() { Source = RelationQueryReadSource.Materialized, FeatureFlag = "materialized", AcceptEventualConsistency = true });
                using var switchingClient = new HttpClient { BaseAddress = new(switching.Urls.Single()) };
                foreach (var enabled in new[] { false, true, false })
                {
                    switching.Configuration["FeatureManagement:materialized"] = enabled.ToString();
                    ((IConfigurationRoot)switching.Configuration).Reload();
                    var response = await switchingClient.GetAsync($"/orders/{orderId}/details", deadline.Token);
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                    Assert.Equal(selectedResult, await response.Content.ReadAsStringAsync(deadline.Token));
                }
                await switching.StopAsync(deadline.Token);
            }
        }
        finally
        {
            File.Delete(comparisonFile);
            foreach (var sql in new[] {
                "DELETE FROM public.cohesive_process_receipts WHERE subject_id=$1 OR subject_id=$2",
                "DELETE FROM public.cohesive_reservations WHERE order_id=$1 AND $2 IS NOT NULL",
                "DELETE FROM public.cohesive_orders WHERE order_id=$1 AND $2 IS NOT NULL",
                "DELETE FROM public.cohesive_inventory WHERE sku=$2 AND $1 IS NOT NULL" })
            {
                await using var cleanup = database.CreateCommand(sql);
                cleanup.Parameters.AddWithValue(orderId ?? "unused"); cleanup.Parameters.AddWithValue(sku);
                await cleanup.ExecuteNonQueryAsync();
            }
        }

        async Task<MaterializationSynchronizationRunResult> Synchronize(MaterializationShadowOperation operation, string invocation)
        {
            var result = await MaterializationShadowExecution.RunAsync(shadow!, operation,
                OperationContext.Create(cancellationToken: deadline.Token), attempt, new(runId + "/" + invocation), new("tests/cqrs"));
            Assert.True(result.Disposition == MaterializationSynchronizationRunDisposition.Converged, JsonSerializer.Serialize(result.Diagnostics));
            return result;
        }

        async Task<WebApplication> Start(RelationQueryReadPolicy? reads = null)
        {
            reads ??= new();
            var selected = reads.UsesMaterializedReads;
            model = CreateModel(reads);
            var builder = WebApplication.CreateBuilder();
            builder.Services.AddRequestOperationContext();
            builder.Services.AddFixedRequestIdentity(FulfillmentDemoIdentity.Context);
            builder.Services.AddKeyedSingleton<NpgsqlDataSource>(history.Authority == EntityStateAuthority.Events ? "orders_events" : "orders", database);
            builder.Services.AddKeyedSingleton<IDocumentStore>(history.Authority == EntityStateAuthority.Events ? "orders_events" : "orders", store);
            var scheduler = Environment.GetEnvironmentVariable(SchedulerVariable)!;
            builder.Services.AddCohesiveDurableTaskServices(client => client.UseDurableTaskScheduler(scheduler),
                worker => worker.UseDurableTaskScheduler(scheduler));
            if (interruptAfterCommit && hosts++ == 0)
                builder.Services.AddSingleton<IAsyncProcessReferenceHost>(services => barrier = new(
                    services.GetRequiredService<DurableTaskServiceProcessProvider>(), model.Domain.InventoryTransitions.Reserve.Reference));
            if (selected)
            {
                builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["OrderReads:ComparisonFile"] = comparisonFile,
                    ["Shadow:PositionKey"] = Convert.ToBase64String(new byte[32]),
                    ["Shadow:SlotGeneration"] = "test-slots/v1"
                });
                builder.Services.AddKeyedSingleton<HttpClient>(ShadowElastic.Resource, elastic!);
                builder.Services.AddKeyedSingleton<PostgresLogicalReplicationConnectionFactory>(model.Storage.Bindings.Database.Value,
                    (_, _) => () => new LogicalReplicationConnection(Environment.GetEnvironmentVariable(DatabaseVariable)!));
            }
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["FeatureManagement:materialized"] = "false" });
            builder.Services.AddCohesiveFeatureManagement<IdentityContext>();
            builder.Services.AddCohesiveServices(model);
            var app = builder.Build();
            try
            {
                app.UseRequestOperationContext(); app.UseAuthorization();
                await app.MapServicesAsync();
                app.Urls.Add("http://127.0.0.1:0");
                await app.StartAsync(deadline.Token);
                _ = app.Services.GetRequiredService<IAsyncProcessReferenceHost>();
                return app;
            }
            catch { await app.DisposeAsync(); throw; }
        }
        async Task<string> AwaitResult(HttpClient client, string location)
        {
            while (true)
            {
                using var response = await client.GetAsync(location, deadline.Token);
                if (response.StatusCode != HttpStatusCode.Accepted)
                {
                    var body = await response.Content.ReadAsStringAsync(deadline.Token);
                    Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {body}");
                    return body;
                }
                await Task.Delay(100, deadline.Token);
            }
        }
    }

    // Fail between the atomic database commit and scheduler activity acknowledgement, not before the write.
    sealed class CommitBarrier(IAsyncProcessReferenceHost inner, ExecutionDefinitionReference reserve) : IAsyncProcessReferenceHost
    {
        public TaskCompletionSource Committed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<ProcessOperationResult> InvokeTransitionAsync(OperationContext context, ProcessTransitionInvocation invocation)
        {
            var result = await inner.InvokeTransitionAsync(context, invocation);
            if (invocation.Definition == reserve)
            {
                Committed.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken);
            }
            return result;
        }
        public ValueTask<ProcessOperationResult> EvaluateRelationAsync(OperationContext context, ProcessRelationEvaluation evaluation) => inner.EvaluateRelationAsync(context, evaluation);
        public ValueTask<ProcessSignalTargetResult> ResolveSignalTargetAsync(OperationContext context, ProcessSignalTargetResolution resolution) => inner.ResolveSignalTargetAsync(context, resolution);
    }

    sealed class CqrsTheoryAttribute : SchedulerPostgresTheoryAttribute
    {
        public CqrsTheoryAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("COHESIVE_SHADOW_ELASTIC")))
                Skip = "Requires local PostgreSQL, Durable Task and Elasticsearch.";
        }
    }

    class SchedulerPostgresTheoryAttribute : TheoryAttribute
    {
        public SchedulerPostgresTheoryAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(DatabaseVariable)) ||
                string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(SchedulerVariable)))
                Skip = "Requires local PostgreSQL and Durable Task Scheduler.";
        }
    }
}
