using Cohesive.Adapters.Postgres;
using Cohesive.Adapters.Sql;
namespace AspireFirst.Orders;

/// <summary>Shared native table mappings, independent of which entities a composition places in PostgreSQL.</summary>
public sealed class FulfillmentPostgresMappings
{
    readonly FulfillmentDomain domain;
    /// <summary>Authors each immutable mapping once against the shared domain.</summary>
    /// <param name="domain">Canonical entities owned by this application.</param>
    public FulfillmentPostgresMappings(FulfillmentDomain domain)
    {
        this.domain = domain;
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
    /// <summary>Shared constraints for orders/reservations in either database placement.</summary>
    /// <param name="schema">Native schema declaration for the mapped entities.</param>
    public void ConfigureOrderSchema(PostgresEntitySchemaBuilder schema) => schema
        .ForeignKey(domain.ReservationOrder)
        .Collation(domain.Orders, item => item.Id, "C")
        .Collation(domain.Reservations, item => item.Id, "C")
        .Collation(domain.Reservations, item => item.OrderId, "C")
        .Collation(domain.Reservations, item => item.Sku, "C")
        .Check(domain.Reservations, item => item.Partition, SqlBinaryOperator.Equal, FulfillmentStoragePolicy.LocalPartition)
        .Check(domain.Reservations, item => item.Quantity, SqlBinaryOperator.GreaterThan, 0);

    /// <summary>Native order table mapping.</summary>
    public PostgresEntityRepositoryMapping Orders { get; }
    /// <summary>Native reservation table mapping.</summary>
    public PostgresEntityRepositoryMapping Reservations { get; }
    /// <summary>Native inventory mapping for the PostgreSQL-only placement.</summary>
    public PostgresEntityRepositoryMapping Inventory { get; }
}

/// <summary>Shared local storage placement selected by both example compositions; not tenant authorization.</summary>
public static class FulfillmentStoragePolicy
{
    /// <summary>Native orders database connection name.</summary>
    public const string OrdersDatabaseName = "orders";
    /// <summary>Fixed demonstration partition used by query declarations and endpoint bindings.</summary>
    public const string LocalPartition = "local";
    /// <summary>Logical scope shared by the composed acquisition sources.</summary>
    public const string LogicalPartition = "aspire-first/" + LocalPartition;

}
