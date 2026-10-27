using System.Text.Json;
using System.Text.Json.Serialization;
using AspireFirst.Orders;
using AspireFirst.Materialization;
using Cohesive.Adapters.Postgres;
using Cohesive.Prelude;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Physical;
using Cohesive.Storage.Materialization;
using Npgsql;

namespace Cohesive.Adapters.Aspire.Tests;

public sealed class FulfillmentShadowTests
{
    static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    [Fact]
    public void Shadow_reads_preserve_the_live_relation_and_map_every_impact_field()
    {
        var model = TestModels.Fulfillment();
        var live = model.Queries.OrderDetails.CompilationRequest;
        var definition = new FulfillmentShadowDefinition(model.Queries);
        var prepared = new PostgresMaterializationRelation(model.Storage.Bindings, model.Queries.OrderDetailsRelation.Definition,
            definition.Impact, FulfillmentDomain.PartitionField, new(new("test/shadow"), "test/v1", 100, 1000, 1000, 100, 100, 1));
        Assert.Same(live, model.Queries.OrderDetails.CompilationRequest);
        Assert.Equal(RelationQueryCompiledPlanReferenceFingerprinter.Compute(definition.Document.Definition.Relation.CompiledPlan),
            RelationQueryCompiledPlanReferenceFingerprinter.Compute(prepared.Reads.Baseline.Plan));
        var root = Assert.Single(prepared.Reads.Baseline.Placement.Bindings.Where(binding =>
            binding.Kind == RelationQuerySourcePlacementBindingKind.SourceSet));
        Assert.Equal(RelationQuerySourceAcquisitionKind.BoundedEnumeration, root.Acquisition);
        var impact = prepared.Reads.Impact.Placement.Bindings.Single(binding => binding.Input == root.Input);
        Assert.Equal(RelationQuerySourceAcquisitionKind.BoundedLookup, impact.Acquisition);
        foreach (var binding in prepared.Reads.Impact.Placement.Bindings)
        {
            Assert.Single(prepared.Reads.Impact.Stages.Where(stage => stage.PlacementBinding == binding.Id
                && stage.Kind == RelationQueryPhysicalStageKind.BatchedIdentityLookup));
            var table = prepared.ReadStorage.ResolveTable(binding.Id);
            Assert.All(binding.RelationshipKeys, key => Assert.Contains(table.RelationshipReferences,
                reference => reference.Input == key.Input && reference.SemanticPath == key.SemanticPath));
        }
        Assert.NotNull(RelationQueryPlacement.ForPhysicalReads(prepared.Plan, prepared.Reads.Impact));
    }

    [ShadowFact]
    public async Task Native_shadow_resumes_then_tracks_updates_reparenting_and_deletes_without_changing_live_reads()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var ct = timeout.Token;
        var model = TestModels.Fulfillment();
        var connection = Environment.GetEnvironmentVariable("COHESIVE_SHADOW_POSTGRES")!;
        await using var database = NpgsqlDataSource.Create(connection);
        using var http = new HttpClient { BaseAddress = new(Environment.GetEnvironmentVariable("COHESIVE_SHADOW_ELASTIC")!) };
        await ShadowBootstrap.InitializeAsync(model, database, http, ct);
        var definition = new FulfillmentShadowDefinition(model.Queries);
        var target = await ShadowElastic.BindAsync(http, definition, ct);
        var runtime = await FulfillmentShadowRuntime.BindAsync(model, definition, database, connection, target, new byte[32], "test-slots/v1", ct);
        var id = Guid.NewGuid().ToString("N");
        var first = "first-" + id;
        var second = "second-" + id;
        var reservation = "reservation-" + id;
        var sku = "sku-" + id;
        var attempt = new MaterializationRebuildAttempt(new(new(id), new(id)), DateTimeOffset.UtcNow);
        var context = OperationContext.Create(cancellationToken: ct);
        var index = target.Binding.GetGenerationIndexName(MaterializationRebuildIdentities.Generation(runtime.Compilation.Plan, attempt));
        var live = model.Storage.Bindings.Bind(database, "tests/shadow/live")
            .Query(model.Queries.OrderDetails, maximumRows: 1000, maximumBytes: 1_000_000);
        try
        {
            await Execute("INSERT INTO cohesive_orders(order_id,partition_key,status,observation_version) VALUES ($1,'local','Draft',0),($2,'local','Draft',0)", first, second);
            await Execute("INSERT INTO cohesive_inventory(sku,partition_key,available,observation_version) VALUES ($1,'local',10,0)", sku);
            await Execute("INSERT INTO cohesive_reservations(reservation_id,order_id,sku,partition_key,quantity,observation_version) VALUES ($1,$2,$3,'local',2,0)", reservation, first, sku);
            var rebuild = new MaterializationRebuildExecutor(runtime.Resolved);
            Assert.Equal(MaterializationRebuildInitializationDisposition.Ready, (await rebuild.BeginAttemptAsync(context, attempt)).Disposition);
            var interruption = new InterruptAfterWrite();
            await Assert.ThrowsAsync<TestInterruption>(() => new MaterializationRebuildExecutor(runtime.Resolved, interruption)
                .RunShardAsync(context, attempt, Assert.Single(runtime.Compilation.Plan.Shards).Id));
            // Rebind all native interpretations, simulating restart with the same fingerprint, attempt and key.
            runtime = await FulfillmentShadowRuntime.BindAsync(model, definition, database, connection, target, new byte[32], "test-slots/v1", ct);
            rebuild = new(runtime.Resolved);
            Assert.Equal(MaterializationRebuildInitializationDisposition.Ready, (await rebuild.BeginAttemptAsync(context, attempt)).Disposition);
            Assert.Equal(MaterializationRebuildShardDisposition.BaselineCompleteCatchUpRequired,
                (await rebuild.RunShardAsync(context, attempt, Assert.Single(runtime.Compilation.Plan.Shards).Id)).Disposition);
            await CatchUp("baseline");
            await AssertMatchesLive(first, second);
            await Execute("UPDATE cohesive_inventory SET available=8,observation_version=1 WHERE sku=$1", sku);
            await Execute("UPDATE cohesive_reservations SET order_id=$1,observation_version=1 WHERE reservation_id=$2", second, reservation);
            await CatchUp("reparent");
            await AssertMatchesLive(first, second);
            var beforeReplay = await Snapshot();
            await CatchUp("reparent");
            Assert.True(JsonElement.DeepEquals(beforeReplay, await Snapshot()));
            await Execute("DELETE FROM cohesive_reservations WHERE reservation_id=$1", reservation);
            await Execute("DELETE FROM cohesive_orders WHERE order_id=$1", first);
            await CatchUp("delete");
            await AssertMatchesLive(second);
            Assert.Null(await live.ReadAsync(first, ct));
            Assert.DoesNotContain((await Snapshot()).EnumerateArray(), row =>
                !row.GetProperty("_source").GetProperty("_cohesive").GetProperty("deleted").GetBoolean()
                && row.GetProperty("_source").GetProperty("value").GetProperty("Id").GetString() == first);
            using var aliases = await http.GetAsync("/_alias/orders-shadow", ct);
            Assert.Equal(System.Net.HttpStatusCode.NotFound, aliases.StatusCode);
        }
        finally
        {
            await Execute("DELETE FROM cohesive_reservations WHERE reservation_id=$1", reservation);
            await Execute("DELETE FROM cohesive_orders WHERE order_id IN ($1,$2)", first, second);
            await Execute("DELETE FROM cohesive_inventory WHERE sku=$1", sku);
        }

        async Task Execute(string sql, params object[] parameters)
        {
            await using var command = database.CreateCommand(sql);
            foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter);
            await command.ExecuteNonQueryAsync(ct);
        }
        async Task CatchUp(string invocation)
        {
            var result = await MaterializationShadowExecution.RunAsync(runtime.Resolved, runtime.State,
                invocation == "baseline" ? MaterializationShadowOperation.Rebuild : MaterializationShadowOperation.CatchUp,
                context, attempt, new(id + "/" + invocation), new("test-worker"));
            Assert.True(result.Disposition == MaterializationSynchronizationRunDisposition.Converged, JsonSerializer.Serialize(result.Diagnostics));
        }
        async Task<JsonElement> Snapshot()
        {
            using var refresh = await http.PostAsync("/" + index + "/_refresh", null, ct);
            refresh.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await http.GetStringAsync("/" + index + "/_search?size=1000&sort=_doc", ct));
            return document.RootElement.GetProperty("hits").GetProperty("hits").Clone();
        }
        async Task AssertMatchesLive(params string[] orders)
        {
            var rows = await Snapshot();
            foreach (var order in orders)
            {
                var expected = JsonSerializer.SerializeToElement(await live.ReadAsync(order, ct), Json);
                var actual = rows.EnumerateArray().Select(row => row.GetProperty("_source"))
                    .Where(row => !row.GetProperty("_cohesive").GetProperty("deleted").GetBoolean())
                    .Select(row => row.GetProperty("value")).Single(value => value.GetProperty("Id").GetString() == order);
                Assert.True(JsonElement.DeepEquals(expected, actual), $"Live: {expected}; shadow: {actual}");
            }
        }
    }

    sealed class TestInterruption : Exception;
    sealed class InterruptAfterWrite : IMaterializationExecutionBoundaryObserver
    {
        public ValueTask ObserveAsync(OperationContext context, MaterializationExecutionBoundaryObservation observation) =>
            observation.Point == MaterializationExecutionBoundaryPoint.AfterTargetBatch
                ? ValueTask.FromException(new TestInterruption()) : ValueTask.CompletedTask;
    }
    sealed class ShadowFactAttribute : FactAttribute
    {
        public ShadowFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("COHESIVE_SHADOW_POSTGRES") is null || Environment.GetEnvironmentVariable("COHESIVE_SHADOW_ELASTIC") is null)
                Skip = "Requires dedicated disposable COHESIVE_SHADOW_POSTGRES and COHESIVE_SHADOW_ELASTIC resources.";
        }
    }
}
