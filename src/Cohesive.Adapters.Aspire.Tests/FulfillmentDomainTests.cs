using Microsoft.Extensions.DependencyInjection;
using Cohesive.Host.Composition;
using AspireFirst.Orders;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.Execution;

namespace Cohesive.Adapters.Aspire.Tests;

public sealed class FulfillmentDomainTests(Xunit.Abstractions.ITestOutputHelper output)
{
    static readonly AspireFirst.Orders.FulfillmentApplication Model = TestModels.Fulfillment();

    [Fact]
    public void Remote_inventory_registration_prepares_its_declared_key_without_an_Id_convention()
    {
        using var database = Npgsql.NpgsqlDataSource.Create("Host=localhost;Database=unused;Username=test");
        var remote = PostgresSplitFixture.BindInventory(Model, database);
        Cohesive.Storage.IEntityRepository<InventoryItem> inventory = remote.Repository(Model.Domain.Inventory);
        Assert.Same(Model.Domain.Inventory.Definition, inventory.EntityDefinition);
        Assert.Throws<InvalidOperationException>(() => remote.Repository(Model.Domain.Orders));
        Assert.Throws<InvalidOperationException>(() => remote.Repository(Model.Domain.Reservations));
    }

    [Fact]
    public void Query_uses_canonical_entity_graphs_and_compiles_two_native_joins()
    {
        var author = RelationQuery.Expression();
        var shape = Model.Domain.Orders.QueryShape(author);
        Assert.Equal(Model.Domain.Orders.Definition.StateShape.QualifiedId, shape.Id);
        Assert.Equal(3, Model.Domain.Definition.Entities.Length);
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var started = System.Diagnostics.Stopwatch.StartNew();
        using var database = Npgsql.NpgsqlDataSource.Create("Host=localhost;Database=unused;Username=test");
        var reader = Model.Storage.Bindings.Bind(database, authority: "test/fulfillment").Query(Model.Queries.OrderDetails, maximumRows: 1000, maximumBytes: 1_000_000);
        IRelationQueryReader<string, OrderDetails?> contract = reader;
        Assert.Same(Model.Queries.OrderDetails, contract.Definition);
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
        var registration = Cohesive.Adapters.Postgres.PostgresPersistenceBindings.Define(new("orders"), bindings =>
        {
            bindings.Entity(Model.Domain.Orders, Model.Storage.Bindings.Mapping(Model.Domain.Orders));
            Assert.Throws<ArgumentException>(() => bindings.Entity(Model.Domain.Orders, Model.Storage.Bindings.Mapping(Model.Domain.Orders)));
        });
        Assert.Throws<InvalidOperationException>(() => registration.Bind(new(new("orders"), database, "test")).Query(Model.Queries.OrderDetails, 100, 10000));
    }
    [Fact]
    public void Persistence_reuses_entity_authority_and_selects_only_consumed_mappings()
    {
        using var database = Npgsql.NpgsqlDataSource.Create("Host=localhost;Database=unused;Username=test");
        var persistence = Model.Storage.Bindings.Bind(database, authority: "test/fulfillment");
        Cohesive.Storage.IEntityRepository<Order> repository = persistence.Repository(Model.Domain.Orders);
        Assert.Same(Model.Domain.Orders.Definition, repository.EntityDefinition);
        var prefix = Cohesive.Relations.Compilation.RelationQuerySubplan.Compile(
            Model.Queries.ReservationAvailability.CompilationRequest, SubplanTests.DemandProjection(Model.Queries));
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
            d => ReferenceEquals(d.Graph, Model.Domain.Inventory.Definition.StateShape.Graph));

        var author = RelationQuery.Expression();
        var foreign = new Cohesive.Transitions.Authoring.DomainModelBuilder().Entity<Order>("example/order");
        var order = author.Source(foreign.QueryShape(author));
        Assert.Throws<ArgumentException>(() => author.TraverseInverse(order, Model.Domain.ReservationOrder));

        static RelationQuery<string, ImportResult[]> Build(bool explicitImports)
        {
            var author = RelationQuery.Expression();
            var orderShape = Model.Domain.Orders.QueryShape(author);
            if (explicitImports)
            {
                Model.Domain.Reservations.QueryShape(author);
                Model.Domain.Inventory.QueryShape(author);
            }
            var id = author.Parameter<string>("id");
            var order = author.Where(author.Source(orderShape), value => value.Id == id.Value);
            var reservation = author.TraverseInverse(order, Model.Domain.ReservationOrder);
            var inventory = author.Traverse(reservation, Model.Domain.ReservationItem);
            var result = author.Project(inventory.Node, (Order o, InventoryItem item) => new ImportResult(o.Id, item.Sku),
                order.Binding, inventory.Binding);
            return result.BuildArrayQuery(id: new("test/import"), name: new("Import"), parameter: id);
        }
    }

    public sealed record ImportResult(string Order, string? Sku);

}
