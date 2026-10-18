using Cohesive.Adapters.Postgres;
using Npgsql;
using Cohesive.Storage;

namespace AspireFirst.Orders;

/// <summary>Instance-owned native storage attachments to the exact domain supplied by composition.</summary>
public sealed class FulfillmentStorage : StorageModel
{
    /// <summary>Builds immutable offline mappings without opening a connection.</summary>
    /// <param name="domain">Shared canonical entities.</param>
    public FulfillmentStorage(FulfillmentDomain domain)
    {
        Bindings = Storage.Define(() => PostgresPersistenceBindings.Define(
            new(FulfillmentResources.DatabaseName), storage =>
            {
                storage.Entity(domain.Orders, order => order
                    .Table("public", "cohesive_orders")
                    .Identity(value => value.Id, "order_id")
                    .Partition(value => value.Partition, "partition_key")
                    .Column(value => value.Status, "status"));
                storage.Entity(domain.Reservations, reservation => reservation
                    .Table("public", "cohesive_reservations")
                    .Identity(value => value.Id, "reservation_id")
                    .Partition(value => value.Partition, "partition_key")
                    .Column(value => value.OrderId, "order_id")
                    .Column(value => value.Sku, "sku")
                    .Column(value => value.Quantity, "quantity"));
                storage.Entity(domain.Inventory, item => item
                    .Table("public", "cohesive_inventory")
                    .Identity(value => value.Sku, "sku")
                    .Partition(value => value.Partition, "partition_key")
                    .Column(value => value.Available, "available"));
            }));
        InventoryBindings = Storage.Define(() => PostgresPersistenceBindings.Define(new(FulfillmentResources.InventoryDatabaseName),
            storage => storage.Entity(domain.Inventory, Bindings.Mapping(domain.Inventory))));
    }

    /// <summary>Native mappings for the primary database.</summary>
    public PostgresPersistenceBindings Bindings { get; }

    /// <summary>Only inventory for the independently placed remote source.</summary>
    public PostgresPersistenceBindings InventoryBindings { get; }

    /// <summary>Attaches the configured native data source; ownership remains with the caller.</summary>
    /// <param name="database">Native data source.</param><returns>A runtime persistence registration.</returns>
    public PostgresPersistenceRegistration Bind(NpgsqlDataSource database) =>
        Bindings.Bind(new(new(FulfillmentResources.DatabaseName), database, "aspire-first/apphost"));

    /// <summary>Attaches inventory-only mappings to the remote source.</summary>
    /// <param name="database">Native inventory data source.</param><returns>An inventory-only registration.</returns>
    public PostgresPersistenceRegistration BindInventory(NpgsqlDataSource database) =>
        InventoryBindings.Bind(new(new(FulfillmentResources.InventoryDatabaseName), database, "aspire-first/inventory"));
}
