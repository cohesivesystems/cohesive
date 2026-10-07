using Cohesive.Relations.Authoring;
using Cohesive.Relations.IR;
using Cohesive.Relations.Model;
using Cohesive.Transitions.Authoring;

namespace AspireFirst.Orders;

/// <summary>Immutable canonical fulfillment query declarations; backend preparation belongs to host composition.</summary>
public static class FulfillmentQueries
{
    static FulfillmentQueries()
    {
        OrderDetails = DefineOrderDetails();
        (ReservationAvailability, ReservationDemandProjection) = DefineReservationAvailability();
    }

    /// <summary>Canonical query plus portable nested-result assembly; no compilation or PostgreSQL configuration.</summary>
    public static RelationQuery<string, OrderDetails?> OrderDetails { get; }

    /// <summary>Canonical query shared by native and composed registrations.</summary>
    public static RelationQuery<string, ReservationAvailability[]> ReservationAvailability { get; }
    /// <summary>Closed logical view available as an optional physical subplan boundary.</summary>
    public static QueryNodeId ReservationDemandProjection { get; }

    static RelationQuery<string, OrderDetails?> DefineOrderDetails()
    {
        var query = RelationQuery.Expression();
        var orderId = query.Parameter<string>("orderId");

        var (orders, reservations) = OrderReservations(query, orderId);
        var inventory = query.Traverse(reservations, FulfillmentDomain.ReservationItem);
        return query.SingleOrDefault<OrderDetails>()
            .From(inventory, orders, order => order.Id)
            .Identity(result => result.Id)
            .Field(result => result.Status, orders, order => order.Status)
            .Collection(result => result.Reservations, reservations, reservation => reservation.Id,
                items => items
                    .Identity(result => result.Id)
                    .Field(result => result.Sku, inventory, item => item.Sku)
                    .Field(result => result.Quantity, reservations, reservation => reservation.Quantity)
                    .Field(result => result.AvailableStock, inventory, item => item.Available))
            .Build(id: new("fulfillment/order-details"), name: new("OrderDetails"), parameter: orderId);
    }

    static (RelationQuery<string, ReservationAvailability[]> Query, QueryNodeId DemandProjection) DefineReservationAvailability()
    {
        var query = RelationQuery.Expression();
        var orderId = query.Parameter<string>("orderId");
        var (orders, reservations) = OrderReservations(query, orderId);
        var demand = query.Project(reservations.Node,
            (Order order, Reservation reservation) => new ReservationDemand(order.Id, reservation.Id, reservation.Sku, reservation.Quantity),
            orders.Binding, reservations.Binding);
        var inventory = query.Source(FulfillmentDomain.Inventory).Where(item => item.Partition == FulfillmentDemo.LocalPartition);
        var joined = query.Join(demand.Node, inventory.Node, JoinKind.Left,
            (row, item) => row.Sku == item.Sku, demand.Binding, inventory.Binding);
        var result = query.Project(joined,
            (ReservationDemand row, InventoryItem item) => new ReservationAvailability(row.OrderId, row.ReservationId, row.Sku, row.Quantity, item.Available),
            demand.Binding, inventory.Binding);
        var definition = query.BuildQuery(new("fulfillment/reservation-availability"), new("ReservationAvailability"),
            result, orderId, rows => rows.ToArray());
        return (definition, demand.Node.Id);
    }

    static (RelationQueryExpressionBoundNode<FilterQueryNode, Order> Orders,
        RelationQueryExpressionBoundNode<TraverseRelationshipQueryNode, Reservation> Reservations)
        OrderReservations(RelationQueryExpressionAuthoring query, RelationQueryExpressionParameter<string> orderId)
    {
        var orders = query.Source(FulfillmentDomain.Orders)
            .Where(order => order.Id == orderId.Value && order.Partition == FulfillmentDemo.LocalPartition);
        return (orders, query.TraverseInverse(orders, FulfillmentDomain.ReservationOrder));
    }

}

/// <summary>Joined order view with zero or more reservations.</summary>
/// <param name="Id">Order identity.</param>
/// <param name="Status">Current lifecycle state.</param>
/// <param name="Reservations">Joined reservations ordered by identity.</param>
public sealed record OrderDetails(string Id, string Status, IReadOnlyList<ReservationSummary> Reservations);

/// <summary>One reservation and its current inventory availability, not an event-sourced snapshot.</summary>
/// <param name="Id">Reservation identity.</param>
/// <param name="Sku">Inventory identity.</param>
/// <param name="Quantity">Reserved quantity.</param>
/// <param name="AvailableStock">Current availability in the query snapshot.</param>
public sealed record ReservationSummary(string Id, string Sku, int Quantity, int AvailableStock);

/// <summary>Reservation demand before inventory enrichment; absent reservations remain distinguishable.</summary>
/// <param name="OrderId">Owning order.</param><param name="ReservationId">Reservation identity when present.</param>
/// <param name="Sku">Referenced inventory identity when present.</param><param name="Quantity">Demand when present.</param>
public sealed record ReservationDemand(string OrderId, string? ReservationId, string? Sku, int? Quantity);

/// <summary>Reservation demand enriched with independently placeable current inventory.</summary>
/// <param name="OrderId">Owning order.</param><param name="ReservationId">Reservation identity when present.</param>
/// <param name="Sku">Referenced inventory identity when present.</param><param name="Quantity">Demand when present.</param>
/// <param name="Available">Inventory when present; separate reads do not imply a distributed snapshot.</param>
public sealed record ReservationAvailability(string OrderId, string? ReservationId, string? Sku, int? Quantity, int? Available);
