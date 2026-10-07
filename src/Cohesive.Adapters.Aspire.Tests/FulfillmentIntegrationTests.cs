using System.Net;
using System.Net.Http.Json;
using AspireFirst.Orders;
using Cohesive.Adapters.AspNet;
using Cohesive.Adapters.Postgres;
using Cohesive.Model;
using Cohesive.Prelude;
using Cohesive.Relations.IR;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Physical;
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
        using var schema = new StreamReader(typeof(FulfillmentStorage).Assembly.GetManifestResourceStream("Orders.schema.sql")!);
        await using (var command = database.CreateCommand(await schema.ReadToEndAsync())) await command.ExecuteNonQueryAsync();
        var context = OperationContext.Create();
        var persistence = FulfillmentStorage.Bind(database);
        var orders = persistence.Repository(FulfillmentDomain.Orders);
        var runtime = new PostgresNpgsqlRuntimeBinding(new("orders"), database, "tests/fulfillment");
        var inventory = persistence.Repository(FulfillmentDomain.Inventory);
        var reservations = persistence.Repository(FulfillmentDomain.Reservations);
        var id = Guid.NewGuid().ToString("D");
        var outsideId = Guid.NewGuid().ToString("D");
        var sku = "book-" + Guid.NewGuid().ToString("N");
        try
        {
            await orders.Upsert(context, FulfillmentDemo.RegisterOrder(Guid.Parse(id)));
            await orders.Upsert(context, new EntityWriteRequest(FulfillmentDomain.Orders.Definition.CreateState(id, new Order(id, "outside", "Private"), 1).Snapshot));
            await orders.Upsert(context, new EntityWriteRequest(FulfillmentDomain.Orders.Definition.CreateState(outsideId, new Order(outsideId, "outside"), 1).Snapshot));
            var queryReader = persistence.Query(FulfillmentQueries.OrderDetails, maximumRows: 1000, maximumBytes: 1_000_000);
            IRelationQueryRowsReader nativeReader = new PostgresQueryRowsReader(queryReader.Artifact, runtime, maximumRows: 1000, maximumBytes: 1_000_000);
            var initialRows = await nativeReader.ReadAsync(new Dictionary<QueryParameterId, ObservationValue>
            { [FulfillmentQueries.OrderDetails.Parameter] = ObservationValue.FromString(id) });
            Assert.False(Assert.Single(initialRows).TryGetField(((Cohesive.Relations.IR.QueryDefinition)FulfillmentQueries.OrderDetails.CompilationRequest.DefinitionDocument.Definition).Assembly!.Collections[0].Identity, out _));
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
            await inventory.Upsert(context, new InventoryItem(sku, "local", 8));
            for (var index = 1; index <= 2; index++)
            {
                var reservationId = Guid.NewGuid().ToString("D");
                await reservations.Upsert(context, new Reservation(reservationId, "local", id, sku, index));
            }
            var joined = await client.GetFromJsonAsync<OrderDetails>($"/orders/{id}/details");
            Assert.Equal(2, joined!.Reservations.Count);
            Assert.Equal(new[] { 1, 2 }, joined.Reservations.Select(item => item.Quantity).Order());
            Assert.All(joined.Reservations, item => { Assert.Equal(sku, item.Sku); Assert.Equal(8, item.AvailableStock); });
            var plan = RelationQueryStaticCompiler.Compile(FulfillmentQueries.OrderDetails.CompilationRequest).Plan!;
            var placementBuilder = RelationQueryPlacement.For(plan);
            var source = placementBuilder.Source("postgres/query", PostgresRelationQuerySourceTargetProfile.Default, new("orders"), limits: new(100, 1000, 100, 1));
            foreach (var input in plan.InputContract.Sources)
                Place(placementBuilder.Place(input, source), input.Shape);
            foreach (var input in plan.InputContract.Traversals)
                Place(placementBuilder.Place(input, source), input.ResultShape);
            var authoredPlacement = placementBuilder.Build().RequireValue();
            var placement = authoredPlacement.Placement;
            var binding = PostgresRelationQueryBinding.For(authoredPlacement).Database(new("orders"));
            foreach (var input in authoredPlacement.Inputs) binding.Table(input, Mapping(input.Shape));
            var storage = binding.Build().RequireValue();
            var policy = new RelationQueryPhysicalPlanningPolicy(new("tests/nested-result/v1"), "tests/v1",
                maximumBatchSize: 100, maximumBufferedRows: 1000, maximumLocalRows: 1000,
                maximumFanOut: 100, maximumReferenceKeysPerObservation: 100, maximumConcurrency: 1);
            var physical = RelationQueryPhysicalPlanner.Compile(plan, RelationQueryInMemoryInterpreter.Default.Realize(plan), placement, policy);
            Assert.True(physical.IsSuccessful, string.Join("; ", physical.Diagnostics.Select(diagnostic => diagnostic.Message)));
            var sourceReader = new PostgresRelationQuerySourceReader(plan, physical.Plan!, source.Id,
                storage, database, runtime, new(100, 1000, 1000, 1_000_000,
                    partitionScope: new(new("tests/local"), FulfillmentDomain.PartitionField, FulfillmentDemo.LocalPartition)));
            var evaluator = new RelationQueryEvaluator(_ => placement, policy, [sourceReader]);
            var evaluation = FulfillmentQueries.OrderDetails.CompilationRequest.Evaluate(new("tests/nested-result"))
                .Set(FulfillmentQueries.OrderDetails.Parameter, ObservationValue.FromString(id)).Build();
            var outcome = await evaluator.EvaluateAsync(evaluation);
            Assert.True(outcome.IsSuccessful, string.Join("; ", (outcome.PhysicalExecution?.Diagnostics.Select(diagnostic => diagnostic.Message) ?? []).Concat(outcome.Result?.Diagnostics.Select(diagnostic => diagnostic.Message) ?? []).Concat(outcome.Diagnostics.Select(diagnostic => diagnostic.Message))));
            var composed = FulfillmentQueries.OrderDetails.AssembleResult(outcome)!;
            Assert.Equal(joined.Id, composed.Id);
            Assert.Equal(joined.Status, composed.Status);
            Assert.Equal(joined.Reservations, composed.Reservations);
            Assert.NotEmpty(outcome.PhysicalExecution!.SourceReads);
            var failed = await evaluator.EvaluateAsync(FulfillmentQueries.OrderDetails.CompilationRequest
                .Evaluate(new("tests/nested-result/failed-parameter"))
                .SetFailed(FulfillmentQueries.OrderDetails.Parameter, "tests/unavailable-parameter").Build());
            Assert.False(failed.IsSuccessful);
            Assert.Throws<InvalidOperationException>(() => FulfillmentQueries.OrderDetails.AssembleResult(failed));

            void Place(RelationQueryPlacementInputBuilder input, QualifiedShapeId shape)
            {
                var mapping = Mapping(shape);
                input.Identity(FieldPath.FromField(mapping.IdentityField), mapping.IdentityField).FieldsBySemanticPath()
                    .Partition(mapping.PartitionField);
            }
            PostgresEntityRepositoryMapping Mapping(QualifiedShapeId shape) =>
                shape == FulfillmentDomain.Orders.Definition.StateShape.QualifiedId ? FulfillmentStorage.Orders
                    : shape == FulfillmentDomain.Reservations.Definition.StateShape.QualifiedId ? FulfillmentStorage.Reservations
                    : FulfillmentStorage.Inventory;
            var values = new Dictionary<QueryParameterId, ObservationValue> { [FulfillmentQueries.OrderDetails.Parameter] = ObservationValue.FromString(id) };
            await Assert.ThrowsAsync<InvalidOperationException>(() => persistence.Query(FulfillmentQueries.OrderDetails, maximumRows: 1, maximumBytes: 1_000_000).ReadAsync(id));
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
