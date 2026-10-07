using Cohesive.Relations.Authoring;

namespace AspireFirst.Orders;

/// <summary>Backend-independent query with a typed invocation and explicit public result.</summary>
public static class OrderDetailsQuery
{
    /// <summary>Canonical query plus portable nested-result assembly; no compilation or PostgreSQL configuration.</summary>
    public static RelationQuery<string, OrderDetails?> Definition { get; } = Define();

    static RelationQuery<string, OrderDetails?> Define()
    {
        var query = RelationQuery.Expression();
        var orderShape = FulfillmentDomain.Orders.QueryShape(query);
        var orderId = query.Parameter<string>("orderId");

        var orders = query.Where(query.Source(orderShape),
            order => order.Id == orderId.Value && order.Partition == OrderStorage.LocalPartition);
        var reservations = query.TraverseInverse(orders, FulfillmentDomain.ReservationOrder);
        var inventory = query.Traverse(reservations, FulfillmentDomain.ReservationItem);
        return query.SingleOrDefault<OrderDetails>()
            .From(inventory, orders.Binding, order => order.Id)
            .Field(result => result.Id, orders.Binding, order => order.Id)
            .Field(result => result.Status, orders.Binding, order => order.Status)
            .Collection(result => result.Reservations, reservations.Binding, reservation => reservation.Id,
                items => items
                    .Field(result => result.Id, reservations.Binding, reservation => reservation.Id)
                    .Field(result => result.Sku, inventory.Binding, item => item.Sku)
                    .Field(result => result.Quantity, reservations.Binding, reservation => reservation.Quantity)
                    .Field(result => result.AvailableStock, inventory.Binding, item => item.Available))
            .Build(id: new("fulfillment/order-details"), name: new("OrderDetails"), parameter: orderId);
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
