using System.Security.Cryptography;
using System.Text;
using AspireFirst.Orders;
using Cohesive.Adapters.Postgres;
using Cohesive.Storage;
using Cohesive.Storage.Materialization;

namespace AspireFirst.Materialization;

/// <summary>Native placement and recovery policy for the optional shadow; live storage remains independent.</summary>
public sealed class FulfillmentMaterializationStorage : StorageModel
{
    /// <summary>Stable checkpoint and native binding authority.</summary>
    public const string Authority = "aspire-first/order-shadow/v1";

    /// <summary>Declares offline materialization storage using existing entity mappings.</summary>
    /// <param name="storage">Live PostgreSQL mappings reused by the shadow.</param>
    /// <param name="materializations">Shared relation-derived declarations.</param>
    public FulfillmentMaterializationStorage(FulfillmentStorage storage, FulfillmentMaterializations materializations)
    {
        OrderDetails = Storage.Define(() => new PostgresMaterializationStorage(storage.Bindings,
            materializations.OrderDetails, new()
            {
                PartitionField = FulfillmentDomain.PartitionField,
                Planning = new(new("fulfillment/shadow/physical/v1"), "fulfillment/shadow/v1", 100, 1000, 1000, 100, 100, 1),
                Reads = new(100, 1000, 100, FulfillmentMaterializations.MaximumBytes,
                    partitionScope: new(new(FulfillmentStoragePolicy.LogicalPartition), FulfillmentDomain.PartitionField,
                        FulfillmentStoragePolicy.LocalPartition)),
                Rebuild = new(100, FulfillmentMaterializations.MaximumBytes, 100, FulfillmentMaterializations.MaximumBytes, 128,
                    MaterializationRebuildProcessFactory.MaximumStartsPerActivation, MaterializationRebuildProcessFactory.MaximumParallelism, 16),
                Publication = "cohesive_order_shadow",
                Slot = input => "orders_shadow_" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(input.Value)))[..16],
                Replication = new(readInactivityTimeout: TimeSpan.FromSeconds(3)),
                Checkpoints = new(Authority),
                Provider = "fulfillment-shadow",
                Capacity = new(new("fulfillment-shadow/capacity"), 1, ["postgres-session-advisory-lock/order-shadow"]),
                Provenance = new(new("aspire-first/shadow", "1"), new("fulfillment/order-details/shadow"))
            }));
    }

    /// <summary>Prepared source placement and native binding configuration; owns no clients.</summary>
    public PostgresMaterializationStorage OrderDetails { get; }
}
