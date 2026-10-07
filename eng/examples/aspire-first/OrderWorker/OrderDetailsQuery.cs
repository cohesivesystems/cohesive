using Cohesive.Relations.Authoring;

namespace AspireFirst.Orders;

/// <summary>Backend-independent query with a typed invocation and explicit public result.</summary>
public static class OrderDetailsQuery
{
    /// <summary>Canonical query plus local presentation projection; no compilation or PostgreSQL configuration.</summary>
    public static RelationQuery<string, OrderDetails?> Definition { get; } = Define();

    static RelationQuery<string, OrderDetails?> Define()
    {
        var query = RelationQuery.Expression();
        var orderShape = FulfillmentDomain.Orders.QueryShape(query);
        FulfillmentDomain.Reservations.QueryShape(query);
        FulfillmentDomain.Inventory.QueryShape(query);
        var orderId = query.Parameter<string>("orderId");

        var orders = query.Where(query.Source(orderShape),
            order => order.Id == orderId.Value && order.Partition == OrderStorage.LocalPartition);
        var reservations = query.TraverseInverse(orders, FulfillmentDomain.ReservationOrder);
        var inventory = query.Traverse(reservations, FulfillmentDomain.ReservationItem);
        var rows = query.Project(inventory,
            (Order order, Reservation reservation, InventoryItem item) => new
            {
                order.Id, order.Status, ReservationId = (string?)reservation.Id,
                Sku = (string?)item.Sku, Quantity = (int?)reservation.Quantity, AvailableStock = (int?)item.Available
            }, orders.Binding, reservations.Binding);

        // The query owns the public result, including empty and collection behavior. This final
        // presentation projection is local CLR code; the joins above remain canonical Relations.
        return query.BuildQuery(id: new("fulfillment/order-details"), name: new("OrderDetails"), rows, orderId,
            result: values => values.Count == 0 ? null : new OrderDetails(values[0].Id, values[0].Status,
                values.Where(row => row.ReservationId is not null)
                    .OrderBy(row => row.ReservationId, StringComparer.Ordinal)
                    .Select(row => new ReservationSummary(row.ReservationId!, row.Sku!, row.Quantity!.Value, row.AvailableStock!.Value))
                    .ToArray()));
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
