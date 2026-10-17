using Cohesive.Adapters.Postgres;
namespace AspireFirst.Orders;

/// <summary>Shared native table mappings, independent of which entities a composition places in PostgreSQL.</summary>
public sealed class FulfillmentPostgresMappings
{
    /// <summary>Authors each immutable mapping once against the shared domain.</summary>
    /// <param name="domain">Canonical entities owned by this application.</param>
    public FulfillmentPostgresMappings(FulfillmentDomain domain)
    {
        Orders = PostgresEntityRepositoryMapping.For(domain.Orders)
            .Table("public", "cohesive_orders").Identity(x => x.Id, "order_id")
            .Partition(x => x.Partition, "partition_key").Column(x => x.Status, "status").Build();
        Reservations = PostgresEntityRepositoryMapping.For(domain.Reservations)
            .Table("public", "cohesive_reservations").Identity(x => x.Id, "reservation_id")
            .Partition(x => x.Partition, "partition_key").Column(x => x.OrderId, "order_id")
            .Column(x => x.Sku, "sku").Column(x => x.Quantity, "quantity").Build();
        Inventory = PostgresEntityRepositoryMapping.For(domain.Inventory)
            .Table("public", "cohesive_inventory").Identity(x => x.Sku, "sku")
            .Partition(x => x.Partition, "partition_key").Column(x => x.Available, "available").Build();
    }
    /// <summary>Native order table mapping.</summary>
    public PostgresEntityRepositoryMapping Orders { get; }
    /// <summary>Native reservation table mapping.</summary>
    public PostgresEntityRepositoryMapping Reservations { get; }
    /// <summary>Native inventory mapping for the PostgreSQL-only placement.</summary>
    public PostgresEntityRepositoryMapping Inventory { get; }
}
