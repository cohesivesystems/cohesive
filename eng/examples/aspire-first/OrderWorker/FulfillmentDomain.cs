using System.Text.Json.Serialization;
using Cohesive.Transitions.Authoring;
using Cohesive.Transitions.Model;
using Cohesive.Relations.Authoring;

namespace AspireFirst.Orders;

/// <summary>One domain declaration; persistence and queries attach to these exact entity handles.</summary>
public static class FulfillmentDomain
{
    /// <summary>Canonical order identity field.</summary>
    public const string OrderIdField = "id";
    /// <summary>Canonical partition field shared by the example entities.</summary>
    public const string PartitionField = "partition";
    static FulfillmentDomain()
    {
        var domain = new DomainModelBuilder().Version("1");
        Orders = domain.Entity<Order>("example/order");
        Inventory = domain.Entity<InventoryItem>("example/inventory");
        Reservations = domain.Entity<Reservation>("example/reservation");
        ReservationOrder = Reservations.References(reservation => reservation.OrderId, Orders);
        ReservationItem = Reservations.References(reservation => reservation.Sku, Inventory);
        Definition = domain.Build();
    }

    /// <summary>Canonical order state authority.</summary>
    public static DomainEntity<Order> Orders { get; }
    /// <summary>Canonical inventory state authority.</summary>
    public static DomainEntity<InventoryItem> Inventory { get; }
    /// <summary>Canonical reservation state authority.</summary>
    public static DomainEntity<Reservation> Reservations { get; }
    /// <summary>Reservation-to-order semantic reference, reusable by queries and later processes.</summary>
    public static RelationQueryExpressionRelationship<Reservation, Order> ReservationOrder { get; }
    /// <summary>Reservation-to-inventory semantic reference.</summary>
    public static RelationQueryExpressionRelationship<Reservation, InventoryItem> ReservationItem { get; }
    /// <summary>Immutable domain entity catalog.</summary>
    public static DomainModelDefinition Definition { get; }
}

/// <summary>Stock for one SKU in the local demo partition.</summary>
/// <param name="Sku">Inventory observation identity.</param>
/// <param name="Partition">Explicit local-demo partition.</param>
/// <param name="Available">Current available quantity, constrained nonnegative by the native schema.</param>
public sealed record InventoryItem(
    [property: JsonPropertyName("sku")] string Sku,
    [property: JsonPropertyName("partition")] string Partition,
    [property: JsonPropertyName("available")] int Available);

/// <summary>One reservation linking an order to a stock item; multiple reservations per order are permitted.</summary>
/// <param name="Id">Reservation observation identity.</param>
/// <param name="Partition">Explicit local-demo partition.</param>
/// <param name="OrderId">Referenced order observation identity.</param>
/// <param name="Sku">Referenced inventory observation identity.</param>
/// <param name="Quantity">Positive reserved quantity.</param>
public sealed record Reservation(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("partition")] string Partition,
    [property: JsonPropertyName("orderId")] string OrderId,
    [property: JsonPropertyName("sku")] string Sku,
    [property: JsonPropertyName("quantity")] int Quantity);

/// <summary>POCO authoring source for the canonical order state.</summary>
/// <param name="Id">Order identity, serialized under its stable canonical field name.</param>
/// <param name="Status">Current lifecycle state.</param>
/// <param name="Partition">Storage partition, not an authorization boundary.</param>
public sealed record Order(
    [property: JsonPropertyName(FulfillmentDomain.OrderIdField)] string Id,
    [property: JsonPropertyName(FulfillmentDomain.PartitionField)] string Partition,
    [property: JsonPropertyName("status")] string Status = "Draft");
