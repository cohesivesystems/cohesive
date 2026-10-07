using Cohesive.Relations.Authoring;
using Cohesive.Relations.IR;
using Cohesive.Relations.Model;

namespace AspireFirst.Orders;

/// <summary>A projected reservation-demand view enriched by inventory; one graph supports native or split execution.</summary>
/// <remarks>The projection is a semantic view boundary, not a PostgreSQL instruction. Host placement may execute
/// the entire graph in one database or cut at that view and acquire inventory separately.</remarks>
public static class ReservationAvailabilityQuery
{
    static ReservationAvailabilityQuery()
    {
        var query = RelationQuery.Expression();
        var orderShape = FulfillmentDomain.Orders.QueryShape(query);
        var inventoryShape = FulfillmentDomain.Inventory.QueryShape(query);
        var orderId = query.Parameter<string>("orderId");
        var orders = query.Where(query.Source(orderShape), order => order.Id == orderId.Value && order.Partition == OrderStorage.LocalPartition);
        var reservations = query.TraverseInverse(orders, FulfillmentDomain.ReservationOrder);
        var demand = query.Project(reservations.Node,
            (Order order, Reservation reservation) => new ReservationDemand(order.Id, reservation.Id, reservation.Sku, reservation.Quantity),
            orders.Binding, reservations.Binding);
        DemandProjection = demand.Node.Id;
        var inventory = query.Where(query.Source(inventoryShape), item => item.Partition == OrderStorage.LocalPartition);
        var joined = query.Join(demand.Node, inventory.Node, JoinKind.Left,
            (row, item) => row.Sku == item.Sku, demand.Binding, inventory.Binding);
        var result = query.Project(joined,
            (ReservationDemand row, InventoryItem item) => new ReservationAvailability(row.OrderId, row.ReservationId, row.Sku, row.Quantity, item.Available),
            demand.Binding, inventory.Binding);
        Definition = query.BuildQuery(new("fulfillment/reservation-availability"), new("ReservationAvailability"),
            result, orderId, rows => rows.ToArray());
    }

    /// <summary>Canonical query shared by native and composed registrations.</summary>
    public static RelationQuery<string, ReservationAvailability[]> Definition { get; }
    /// <summary>Closed logical view available as an optional physical subplan boundary.</summary>
    public static QueryNodeId DemandProjection { get; }
}

/// <summary>Reservation demand before inventory enrichment; absent reservations remain distinguishable.</summary>
/// <param name="OrderId">Owning order.</param><param name="ReservationId">Reservation identity when present.</param>
/// <param name="Sku">Referenced inventory identity when present.</param><param name="Quantity">Demand when present.</param>
public sealed record ReservationDemand(string OrderId, string? ReservationId, string? Sku, int? Quantity);

/// <summary>Reservation demand enriched with independently placeable current inventory.</summary>
/// <param name="OrderId">Owning order.</param><param name="ReservationId">Reservation identity when present.</param>
/// <param name="Sku">Referenced inventory identity when present.</param><param name="Quantity">Demand when present.</param>
/// <param name="Available">Inventory when present; separate reads do not imply a distributed snapshot.</param>
public sealed record ReservationAvailability(string OrderId, string? ReservationId, string? Sku, int? Quantity, int? Available);
