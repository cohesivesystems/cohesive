using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using AspireFirst.Orders;
using Cohesive.Adapters.Postgres;
using Cohesive.Model;
using Cohesive.Relations.Acquisition;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Physical;
using Cohesive.Storage.Materialization;
using Npgsql;
using Npgsql.Replication;

namespace AspireFirst.Materialization;

/// <summary>Native shadow bindings borrow the host's pool; all progression is interpreted by Cohesive's existing executors.</summary>
public static class FulfillmentMaterializationBindings
{
    /// <summary>Dedicated local pgoutput publication.</summary>
    public const string Publication = "cohesive_order_shadow";
    /// <summary>Durable shadow state authority.</summary>
    public const string Authority = "aspire-first/order-shadow/v1";
    /// <summary>Derives a stable PostgreSQL-safe slot name from a canonical input.</summary>
    /// <param name="input">Canonical relation acquisition input.</param>
    /// <returns>Dedicated replication slot name.</returns>
    public static string Slot(RelationQueryInputId input) => "orders_shadow_" +
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(input.Value)))[..16];

    /// <summary>Preflights source capabilities and binds existing rebuild and catch-up interpreters.</summary>
    /// <param name="application">Activated shared application declarations.</param>
    /// <param name="definition">Prepared declaration shared with target binding.</param>
    /// <param name="database">Borrowed pool, owned by the command host.</param>
    /// <param name="connectionString">Native logical-replication connection configuration.</param>
    /// <param name="target">Admitted dedicated shadow target.</param>
    /// <param name="authenticationKey">Persistent position-token authentication key.</param>
    /// <param name="slotGeneration">Operator-owned slot incarnation, stable across restart.</param>
    /// <param name="cancellationToken">Cancels native preflight.</param>
    /// <returns>One reusable native interpretation with durable state.</returns>
    public static async Task<BoundMaterialization> BindAsync(FulfillmentApplication application, FulfillmentMaterializations definition,
        NpgsqlDataSource database, string connectionString, IMaterializationTarget target,
        ReadOnlyMemory<byte> authenticationKey, string slotGeneration, CancellationToken cancellationToken)
    {
        var prepared = new PostgresMaterializationRelation(application.Storage.Bindings,
            definition.Prepared, FulfillmentDomain.PartitionField,
            new(new("fulfillment/shadow/physical/v1"), "fulfillment/shadow/v1", 100, 1000, 1000, 100, 100, 1));
        var policy = new PostgresRelationQuerySourcePolicy(100, 1000, 100, FulfillmentMaterializations.MaximumBytes,
            partitionScope: new(new(FulfillmentStoragePolicy.LogicalPartition), FulfillmentDomain.PartitionField,
                FulfillmentStoragePolicy.LocalPartition));
        var native = new PostgresNpgsqlRuntimeBinding(application.Storage.Bindings.Database, database, Authority,
            logicalReplicationConnectionFactory: () => new LogicalReplicationConnection(connectionString));
        var partition = await prepared.BindAsync(definition.Document, database, native, policy,
            FulfillmentStoragePolicy.LocalPartition, authenticationKey,
            input => new(Publication, Slot(input), slotGeneration,
                expectedReplicaIdentity: new(PostgresLogicalReplicationReplicaIdentityKind.Full),
                beforeImageRequirement: PostgresLogicalReplicationBeforeImageRequirement.Required),
            new(readInactivityTimeout: TimeSpan.FromSeconds(3)), cancellationToken);
        var compilation = MaterializationPartitionedRebuildCompiler.Compile(definition.Document, prepared.Plan,
            new(100, FulfillmentMaterializations.MaximumBytes, 100, FulfillmentMaterializations.MaximumBytes, 128,
                MaterializationRebuildProcessFactory.MaximumStartsPerActivation, MaterializationRebuildProcessFactory.MaximumParallelism, 16),
            "fulfillment-shadow", target.Descriptor, [partition], definition.Impact,
            new(new("fulfillment-shadow/capacity"), 1, ["postgres-session-advisory-lock/order-shadow"]),
            new(new("aspire-first/shadow", "1"), new("fulfillment/order-details/shadow")));
        var state = new PostgresMaterializationStateStore(database, new(Authority));
        return new(compilation, target, state, state);
    }
}
