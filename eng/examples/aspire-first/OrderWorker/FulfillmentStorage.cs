using Cohesive.Adapters.Postgres;
using Npgsql;

namespace AspireFirst.Orders;

/// <summary>Native physical mappings attached to the canonical fulfillment entities.</summary>
public static class FulfillmentStorage
{
    /// <summary>Native Aspire database reference.</summary>
    public const string DatabaseName = "orders";
    /// <summary>Attaches each canonical entity once for repository and query dependency resolution.</summary>
    /// <param name="database">Caller-owned Aspire data source, retained by the resulting readers.</param>
    /// <returns>A native persistence registration; query preparation selects only its consumed entities.</returns>
    /// <exception cref="ArgumentNullException">Database is null.</exception>
    /// <exception cref="ArgumentException">An entity attachment or runtime binding is invalid.</exception>
    public static PostgresPersistenceRegistration Bind(NpgsqlDataSource database) =>
        new PostgresPersistenceRegistration(new(new(DatabaseName), database, "aspire-first/apphost"))
            .Entity(FulfillmentDomain.Orders, Orders)
            .Entity(FulfillmentDomain.Reservations, Reservations)
            .Entity(FulfillmentDomain.Inventory, Inventory);

    /// <summary>Attaches only inventory to the separate remote database.</summary>
    /// <param name="database">Caller-owned inventory data source.</param>
    /// <returns>An inventory-only registration; order and reservation requests fail at setup.</returns>
    public static PostgresPersistenceRegistration BindInventory(NpgsqlDataSource database) =>
        new PostgresPersistenceRegistration(new(new("inventory"), database, "aspire-first/inventory"))
            .Entity(FulfillmentDomain.Inventory, Inventory);

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
    /// <summary>Complete physical field mapping; schema lifecycle is explicit in schema.sql.</summary>
    public static PostgresEntityRepositoryMapping Orders { get; } = PostgresEntityRepositoryMapping.For(FulfillmentDomain.Orders)
        .Table("public", "cohesive_orders")
        .Identity(order => order.Id, "order_id")
        .Partition(order => order.Partition, "partition_key")
        .Column(order => order.Status, "status")
        .Build();

}
