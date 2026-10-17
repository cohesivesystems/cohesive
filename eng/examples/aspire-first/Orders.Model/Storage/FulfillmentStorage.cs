using Cohesive.Adapters.Postgres;
using Npgsql;
using Cohesive.Storage;

namespace AspireFirst.Orders;

/// <summary>Instance-owned native storage attachments to the exact domain supplied by composition.</summary>
public sealed class FulfillmentStorage : StorageModel
{
    /// <summary>Builds immutable offline mappings without opening a connection.</summary>
    /// <param name="domain">Shared canonical entities.</param>
    /// <param name="mappings">Shared native table mappings.</param>
    public FulfillmentStorage(FulfillmentDomain domain, FulfillmentPostgresMappings mappings)
    {
        Bindings = Storage.Define(() => PostgresPersistenceBindings.Define(
            new(FulfillmentStoragePolicy.OrdersDatabaseName), storage =>
            {
                storage.Entity(domain.Orders, mappings.Orders);
                storage.Entity(domain.Reservations, mappings.Reservations);
                storage.Entity(domain.Inventory, mappings.Inventory);
            }));
    }

    /// <summary>Native mappings for the primary database.</summary>
    public PostgresPersistenceBindings Bindings { get; }

    /// <summary>Attaches the configured native data source; ownership remains with the caller.</summary>
    /// <param name="database">Native data source.</param><returns>A runtime persistence registration.</returns>
    public PostgresPersistenceRegistration Bind(NpgsqlDataSource database) =>
        Bindings.Bind(new(new(FulfillmentStoragePolicy.OrdersDatabaseName), database, "aspire-first/apphost"));

}

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
