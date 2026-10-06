using Cohesive.Adapters.Postgres;
using Npgsql;
using Cohesive.Model;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.IR;
using Cohesive.Relations.Realization;

namespace AspireFirst.Orders;

/// <summary>Canonical order/reservation/inventory query compiled once to a native PostgreSQL statement.</summary>
public static class OrderDetailsQuery
{
    /// <summary>Canonical invocation parameter, bound separately from SQL text.</summary>
    public const string OrderIdParameter = "orderId";
    /// <summary>Registration-lifetime native interpretation of the canonical query.</summary>
    public static PostgresRelationQueryCompiledArtifact Artifact { get; } = Prepare();

    /// <summary>Binds the prepared query to the caller-owned Aspire database with explicit result bounds.</summary>
    /// <param name="database">Caller-owned native data source supplied by Aspire.</param>
    /// <param name="maximumRows">Complete-result row bound; overflow fails without returning partial rows.</param>
    /// <returns>A concurrency-safe reader sharing the prepared artifact.</returns>
    public static PostgresQueryRowsReader Bind(NpgsqlDataSource database, int maximumRows = 1000) =>
        new(Artifact, new(new("orders"), database, "aspire-first/apphost"), maximumRows, maximumBytes: 1_000_000);

    static PostgresRelationQueryCompiledArtifact Prepare()
    {
        var author = RelationQuery.Expression();
        var orderShape = FulfillmentDomain.Orders.QueryShape(author);
        var reservationShape = FulfillmentDomain.Reservations.QueryShape(author);
        var inventoryShape = FulfillmentDomain.Inventory.QueryShape(author);
        // Relationships are declared once, then traversed by the query rather than rewritten as join predicates.
        var belongsToOrder = FulfillmentDomain.ReservationOrder;
        var reservesItem = FulfillmentDomain.ReservationItem;
        var id = author.Parameter<string>(OrderIdParameter);
        var orders = author.Source(orderShape);
        var selected = author.Filter(orders.Node,
            order => order.Id == id.Value && order.Partition == OrderStorage.LocalPartition, orders.Binding);
        var reservations = author.TraverseInverse(selected, orders.Binding, belongsToOrder);
        var inventory = author.Traverse(reservations.Node, reservations.Binding, reservesItem);
        var rows = author.Project(inventory.Node,
            (Order order, Reservation reservation, InventoryItem item) => new OrderDetailRow
            {
                Id = order.Id, Status = order.Status, ReservationId = reservation.Id,
                Sku = item.Sku, Quantity = reservation.Quantity, AvailableStock = item.Available
            }, orders.Binding, reservations.Binding, inventory.Binding);
        var query = author.BuildQuery(new("fulfillment/order-details"), new("OrderDetails"), author.Rows(rows));
        var compilation = RelationQueryStaticCompiler.Compile(new(query.CreateDocument(), author.ShapeDocuments,
            author.CreateRelationshipCatalogDocument()));
        var plan = compilation.Plan ?? throw new InvalidOperationException(string.Join("; ", compilation.Diagnostics));
        var placementBuilder = RelationQueryPlacement.For(plan);
        var source = placementBuilder.Source("aspire/orders", PostgresRelationQuerySourceTargetProfile.Default,
            new("aspire/orders"));
        var orderInput = placementBuilder.PlaceSource(source, orderShape).Identity(order => order.Id).FieldsBySemanticPath();
        var reservationInput = placementBuilder.Place(plan.InputContract.Traversals.Single(t => t.Definition.Id == belongsToOrder.Id),
            source, reservationShape).Identity(reservation => reservation.Id).FieldsBySemanticPath();
        var inventoryInput = placementBuilder.Place(plan.InputContract.Traversals.Single(t => t.Definition.Id == reservesItem.Id),
            source, inventoryShape).Identity(item => item.Sku).FieldsBySemanticPath();
        var placement = placementBuilder.Build().RequireValue();
        var storage = PostgresRelationQueryBinding.For(placement).Database(new("orders"))
            .Table(placement.GetInput(orderInput), OrderStorage.Mapping)
            .Table(placement.GetInput(reservationInput), FulfillmentStorage.Reservations)
            .Table(placement.GetInput(inventoryInput), FulfillmentStorage.Inventory)
            .Build().RequireValue();
        var feasibility = RelationQueryRealizationCompiler.Compile(plan, PostgresRelationQueryTargetProfile.Default,
            PostgresRelationQueryTargetProfile.Policy, RelationQueryResultObservability.NotRequested);
        var compiler = new PostgresRelationQueryCompiler();
        var bound = compiler.Realize(new(plan, feasibility, placement.Placement), storage);
        var native = compiler.Compile(new RelationQueryNativeCompilationRequest(plan, bound, placement.Placement), storage);
        if (!native.IsSuccessful) throw new InvalidOperationException(string.Join("; ", native.Diagnostics));
        return native.Artifacts.Single();
    }
}

/// <summary>Flat canonical query projection; the HTTP representation nests its reservation rows.</summary>
public sealed record OrderDetailRow
{
    /// <summary>Order identity.</summary>
    public string Id { get; init; } = "";
    /// <summary>Current order state.</summary>
    public string Status { get; init; } = "";
    /// <summary>Absent for an order with no reservation.</summary>
    public string? ReservationId { get; init; }
    /// <summary>Joined inventory identity.</summary>
    public string? Sku { get; init; }
    /// <summary>Reserved quantity.</summary>
    public int? Quantity { get; init; }
    /// <summary>Current available stock, read in the same statement snapshot.</summary>
    public int? AvailableStock { get; init; }
}
