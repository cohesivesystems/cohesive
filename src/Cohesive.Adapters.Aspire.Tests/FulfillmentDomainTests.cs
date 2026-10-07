using AspireFirst.Orders;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.Execution;

namespace Cohesive.Adapters.Aspire.Tests;

public sealed class FulfillmentDomainTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public void Query_uses_canonical_entity_graphs_and_compiles_two_native_joins()
    {
        var author = RelationQuery.Expression();
        var shape = FulfillmentDomain.Orders.QueryShape(author);
        Assert.Equal(FulfillmentDomain.Orders.Definition.StateShape.QualifiedId, shape.Id);
        Assert.Equal(3, FulfillmentDomain.Definition.Entities.Length);
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var started = System.Diagnostics.Stopwatch.StartNew();
        using var database = Npgsql.NpgsqlDataSource.Create("Host=localhost;Database=unused;Username=test");
        var reader = FulfillmentStorage.Bind(database).Query(FulfillmentQueries.OrderDetails, maximumRows: 1000, maximumBytes: 1_000_000);
        IRelationQueryReader<string, OrderDetails?> contract = reader;
        Assert.Same(FulfillmentQueries.OrderDetails, contract.Definition);
        var artifact = reader.Artifact;
        var bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        output.WriteLine($"Query registration after domain setup: {started.Elapsed.TotalMilliseconds:F1} ms, {bytes} allocated bytes; cold only in an isolated test process.");
        Assert.Same(artifact, reader.Artifact);
        allocated = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 10_000; index++)
            if (!ReferenceEquals(artifact, reader.Artifact))
                throw new InvalidOperationException("The prepared query was not retained.");
        output.WriteLine($"10,000 warm artifact accesses: {GC.GetAllocatedBytesForCurrentThread() - allocated} allocated bytes.");
        Assert.Equal(2, artifact.Statement.Text.Split("LEFT JOIN", StringSplitOptions.None).Length - 1);
        Assert.Single(artifact.Parameters);
    }

    [Fact]
    public void Registration_rejects_missing_and_duplicate_native_mappings_without_opening_a_connection()
    {
        using var database = Npgsql.NpgsqlDataSource.Create("Host=localhost;Database=unused;Username=test");
        var registration = new Cohesive.Adapters.Postgres.PostgresPersistenceRegistration(new(new("orders"), database, "test"))
            .Entity(FulfillmentDomain.Orders, FulfillmentStorage.Orders);
        Assert.Throws<ArgumentException>(() => registration.Entity(FulfillmentDomain.Orders, FulfillmentStorage.Orders));
        Assert.Throws<InvalidOperationException>(() => registration.Query(FulfillmentQueries.OrderDetails, 100, 10000));
    }
    [Fact]
    public void Persistence_reuses_entity_authority_and_selects_only_consumed_mappings()
    {
        using var database = Npgsql.NpgsqlDataSource.Create("Host=localhost;Database=unused;Username=test");
        var persistence = FulfillmentStorage.Bind(database);
        Cohesive.Storage.IEntityRepository<Order> repository = persistence.Repository(FulfillmentDomain.Orders);
        Assert.Same(FulfillmentDomain.Orders.Definition, repository.EntityDefinition);
        var prefix = Cohesive.Relations.Compilation.RelationQuerySubplan.Compile(
            FulfillmentQueries.ReservationAvailability.CompilationRequest, FulfillmentQueries.ReservationDemandProjection);
        var rows = persistence.Prepare(prefix.Prefix.Request, 1000, 1_000_000);
        Assert.DoesNotContain("cohesive_inventory", rows.Artifact.Statement.Text);
        Assert.Contains("cohesive_reservations", rows.Artifact.Statement.Text);
        var foreign = new Cohesive.Transitions.Authoring.DomainModelBuilder().Entity<Order>("example/order");
        Assert.Throws<InvalidOperationException>(() => persistence.Repository(foreign));
    }

    [Fact]
    public void Traversal_imports_exact_endpoint_documents_and_matches_explicit_imports()
    {
        var implicitQuery = Build(false);
        var explicitQuery = Build(true);
        Assert.Equal(explicitQuery.CompilationRequest.DefinitionDocument.DefinitionFingerprint,
            implicitQuery.CompilationRequest.DefinitionDocument.DefinitionFingerprint);
        Assert.Equal(explicitQuery.CompilationRequest.ShapeDocuments.Select(d => d.Graph.Id),
            implicitQuery.CompilationRequest.ShapeDocuments.Select(d => d.Graph.Id));
        Assert.Contains(implicitQuery.CompilationRequest.ShapeDocuments,
            d => ReferenceEquals(d.Graph, FulfillmentDomain.Inventory.Definition.StateShape.Graph));

        var author = RelationQuery.Expression();
        var foreign = new Cohesive.Transitions.Authoring.DomainModelBuilder().Entity<Order>("example/order");
        var order = author.Source(foreign.QueryShape(author));
        Assert.Throws<ArgumentException>(() => author.TraverseInverse(order, FulfillmentDomain.ReservationOrder));

        static RelationQuery<string, ImportResult[]> Build(bool explicitImports)
        {
            var author = RelationQuery.Expression();
            var orderShape = FulfillmentDomain.Orders.QueryShape(author);
            if (explicitImports)
            {
                FulfillmentDomain.Reservations.QueryShape(author);
                FulfillmentDomain.Inventory.QueryShape(author);
            }
            var id = author.Parameter<string>("id");
            var order = author.Where(author.Source(orderShape), value => value.Id == id.Value);
            var reservation = author.TraverseInverse(order, FulfillmentDomain.ReservationOrder);
            var inventory = author.Traverse(reservation, FulfillmentDomain.ReservationItem);
            var result = author.Project(inventory.Node, (Order o, InventoryItem item) => new ImportResult(o.Id, item.Sku),
                order.Binding, inventory.Binding);
            return author.BuildQuery(new("test/import"), new("Import"), result, id, rows => rows.ToArray());
        }
    }

    public sealed record ImportResult(string Order, string? Sku);

}
