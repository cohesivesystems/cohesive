using AspireFirst.Orders;
using Cohesive.Adapters.Cosmos;
using Cohesive.Adapters.Postgres;
using Cohesive.Adapters.Postgres.Services;
using Cohesive.Api.Execution.Services;
using Cohesive.Relations.Physical;
using Cohesive.Storage;
using Microsoft.Azure.Cosmos;
using Npgsql;

namespace AspireFirst.PostgresCosmos;

/// <summary>Orders/reservations in PostgreSQL; inventory and its per-operation receipts in Cosmos.</summary>
public sealed class PostgresCosmosStorage : StorageModel
{
    /// <summary>Native Cosmos account connection name.</summary>
    public const string CosmosAccount = "inventory-cosmos";
    /// <summary>Native Cosmos database name.</summary>
    public const string InventoryDatabase = "inventory";
    /// <summary>Native inventory container name.</summary>
    public const string InventoryContainer = "stock";
    /// <summary>Required native Cosmos partition-key path.</summary>
    public const string PartitionPath = "/partitionKey";
    readonly FulfillmentDomain domain;
    readonly FulfillmentQueries queries;
    /// <summary>Places orders in SQL and defines bounded inventory acquisition.</summary>
    /// <param name="domain">Shared entity declarations.</param>
    /// <param name="queries">Shared logical queries.</param>
    /// <param name="mappings">Native table mappings, independent of placement.</param>
    public PostgresCosmosStorage(FulfillmentDomain domain, FulfillmentQueries queries, FulfillmentPostgresMappings mappings)
    {
        this.domain = domain;
        this.queries = queries;
        PostgresBindings = Storage.Define(() => PostgresPersistenceBindings.Define(new(FulfillmentStoragePolicy.OrdersDatabaseName), storage => storage
            .Entity(domain.Orders, mappings.Orders)
            .Entity(domain.Reservations, mappings.Reservations)));
        InventoryPolicy = Storage.Define(() => new CosmosRelationQuerySourcePolicy(
            partitionSourceSelector: "partitionKey", logicalPartition: new(FulfillmentStoragePolicy.LogicalPartition),
            fixedPartitionKey: new PartitionKey(FulfillmentStoragePolicy.LocalPartition), maximumEnumerationRows: 1000,
            maximumKeysPerQuery: 100, maximumQueryChunks: 10, maximumSdkPageSize: 100));
    }
    /// <summary>PostgreSQL placement of orders and reservations only.</summary>
    public PostgresPersistenceBindings PostgresBindings { get; }
    /// <summary>Native bounded Cosmos source policy for the local demo partition.</summary>
    public CosmosRelationQuerySourcePolicy InventoryPolicy { get; }

    /// <summary>Composes caller-owned SQL and Cosmos backends; metadata must be verified before calling.</summary>
    /// <remarks>This method owns heterogeneous repository/query selection, not a forwarding database-name wrapper.
    /// The PostgreSQL database identity is inferred from PostgresBindings; cross-store query policy remains explicit.</remarks>
    /// <param name="orders">Caller-owned SQL data source.</param>
    /// <param name="inventory">Container with verified partition metadata.</param>
    /// <param name="receipts">Validated SQL receipt storage.</param>
    /// <returns>Host-scoped persistence resolving each shared contract to its selected store.</returns>
    /// <exception cref="ArgumentException">The container address differs from this composition.</exception>
    public IServicePersistence Bind(NpgsqlDataSource orders, Container inventory, PostgresTransitionReceiptStorage receipts)
    {
        if (inventory.Id != InventoryContainer || inventory.Database.Id != InventoryDatabase)
            throw new ArgumentException("Inventory container does not match this composition.", nameof(inventory));
        var postgres = PostgresBindings.Bind(orders, authority: "aspire-first/postgres-cosmos");
        var repository = new CosmosEntityOutboxRepository(domain.Inventory.Definition, inventory,
            partitionKeyPolicy: EntityPartitionKeyPolicy.Fixed(FulfillmentStoragePolicy.LocalPartition, FulfillmentDomain.PartitionField),
            identityField: FulfillmentDomain.InventoryIdField);
        var source = CosmosEntityRelationQuerySourceRegistration.Create(domain.Inventory.Definition.StateShape.QualifiedId,
            inventory, InventoryDatabase, InventoryContainer, InventoryPolicy,
            identitySemanticPath: Cohesive.Model.FieldPath.FromField(FulfillmentDomain.InventoryIdField));
        var catalog = new EntityRelationQuerySourceCatalog([source]);
        return new ServicePersistenceBuilder(postgres.ForServices(receipts)).Repository(repository)
            .Query(queries.OrderDetails, (rows, bytes) => postgres.QueryComposed(queries.OrderDetails, catalog, Policy(rows, bytes)))
            .Query(queries.ReservationAvailability, (rows, bytes) => postgres.QueryComposed(queries.ReservationAvailability,
                catalog, Policy(rows, bytes)))
            .Build();
    }

    /// <summary>Defines bounded acquisition without asserting a cross-store snapshot.</summary>
    /// <param name="rows">Maximum rows for buffering and complete results.</param>
    /// <param name="bytes">Maximum page and complete-result payload bytes.</param>
    /// <returns>Explicit local-partition composition policy.</returns>
    public static PostgresRelationQueryComposedPolicy Policy(int rows, long bytes) => new(
        new RelationQueryPhysicalPlanningPolicy(new("aspire-first/postgres-cosmos/v1"), "aspire-first/postgres-cosmos/v1",
            maximumBatchSize: 100, maximumBufferedRows: rows, maximumLocalRows: rows,
            maximumFanOut: 100, maximumReferenceKeysPerObservation: 100, maximumConcurrency: 1),
        maximumRowsPerRead: rows, maximumPageItems: rows, maximumPageBytes: bytes,
        partitionScope: new(new(FulfillmentStoragePolicy.LogicalPartition), FulfillmentDomain.PartitionField, FulfillmentStoragePolicy.LocalPartition));
}
