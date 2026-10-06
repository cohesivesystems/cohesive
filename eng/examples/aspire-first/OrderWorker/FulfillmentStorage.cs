using Cohesive.Adapters.Postgres;

namespace AspireFirst.Orders;

/// <summary>Native physical mappings attached to the canonical fulfillment entities.</summary>
public static class FulfillmentStorage
{
    /// <summary>Physical stock mapping, also projected into native query bindings.</summary>
    public static PostgresEntityRepositoryMapping Inventory { get; } = PostgresEntityRepositoryMapping.For(FulfillmentDomain.Inventory)
        .Table("public", "cohesive_inventory")
        .Identity(item => item.Sku, "sku")
        .Partition(item => item.Partition, "partition_key")
        .Column(item => item.Available, "available")
        .Build();

    /// <summary>Physical reservation mapping, sharing its columns with query bindings.</summary>
    public static PostgresEntityRepositoryMapping Reservations { get; } = PostgresEntityRepositoryMapping.For(FulfillmentDomain.Reservations)
        .Table("public", "cohesive_reservations")
        .Identity(reservation => reservation.Id, "reservation_id")
        .Partition(reservation => reservation.Partition, "partition_key")
        .Column(reservation => reservation.OrderId, "order_id")
        .Column(reservation => reservation.Sku, "sku")
        .Column(reservation => reservation.Quantity, "quantity")
        .Build();
}
