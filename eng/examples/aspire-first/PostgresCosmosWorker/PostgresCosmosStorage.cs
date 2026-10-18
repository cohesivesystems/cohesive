using AspireFirst.Orders;
using Cohesive.Adapters.Cosmos;
using Cohesive.Adapters.Postgres;
using Cohesive.Adapters.Postgres.Services;
using Cohesive.Relations.Physical;
using Cohesive.Storage;

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
    /// <summary>Places orders in SQL and defines bounded inventory acquisition.</summary>
    /// <param name="domain">Shared entity declarations.</param>
    /// <param name="endpoints">Portable query selections and consistency requirements.</param>
    /// <param name="mappings">Native table mappings, independent of placement.</param>
    public PostgresCosmosStorage(FulfillmentDomain domain, FulfillmentPostgresMappings mappings, OrderEndpoints endpoints)
    {
        PostgresBindings = Storage.Define(() => PostgresPersistenceBindings.Define(new(FulfillmentStoragePolicy.OrdersDatabaseName), storage => storage
            .SchemaPreparation(async (database, cancellationToken) =>
            {
                using var schema = new StreamReader(typeof(PostgresCosmosStorage).Assembly.GetManifestResourceStream("Orders.PostgresCosmos.schema.sql")
                    ?? throw new InvalidOperationException("The product schema resource is missing."));
                await using var initialize = database.CreateCommand(await schema.ReadToEndAsync(cancellationToken));
                await initialize.ExecuteNonQueryAsync(cancellationToken);
            })
            .TransitionReceipts(new("public", "cohesive_process_receipts", FulfillmentStoragePolicy.LocalPartition),
                initialization: PostgresSchemaInitialization.CreateIfMissing)
            .Entity(domain.Orders, mappings.Orders)
            .Entity(domain.Reservations, mappings.Reservations)));
        Inventory = Storage.Define(() => new CosmosEntityStorageBinding(
            entity: domain.Inventory.Definition, identityField: FulfillmentDomain.InventoryIdField,
            account: CosmosAccount, database: InventoryDatabase, container: InventoryContainer,
            logicalPartition: new(FulfillmentStoragePolicy.LogicalPartition),
            partitionKey: FulfillmentStoragePolicy.LocalPartition, partitionField: FulfillmentDomain.PartitionField,
            maximumEnumerationRows: 1000, maximumKeysPerQuery: 100, maximumQueryChunks: 10, maximumSdkPageSize: 100));
        Storage.Compose(() => PostgresBindings.ComposeServices(endpoints.Plan, [Inventory], Policy));
    }
    /// <summary>PostgreSQL placement of orders and reservations only.</summary>
    public PostgresPersistenceBindings PostgresBindings { get; }
    /// <summary>One inventory placement supplying repository access, acquisition and infrastructure evidence.</summary>
    public CosmosEntityStorageBinding Inventory { get; }

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
