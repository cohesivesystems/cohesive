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

namespace AspireFirst.Shadow;

/// <summary>Native shadow bindings borrow the host's pool; all progression is interpreted by Cohesive's existing executors.</summary>
public sealed class FulfillmentShadowRuntime
{
    /// <summary>Dedicated local pgoutput publication.</summary>
    public const string Publication = "cohesive_order_shadow";
    /// <summary>Durable shadow state authority.</summary>
    public const string Authority = "aspire-first/order-shadow/v1";
    /// <summary>Admitted rebuild, placement and partition evidence.</summary>
    public MaterializationPartitionedRebuild Compilation { get; }
    /// <summary>Native interpretations bound to the admitted plan.</summary>
    public ResolvedMaterializationRebuildPlan Resolved { get; }
    /// <summary>Durable progress and prepared-work store.</summary>
    public PostgresMaterializationStateStore State { get; }

    FulfillmentShadowRuntime(MaterializationPartitionedRebuild compilation, IMaterializationTarget target,
        PostgresMaterializationStateStore state)
    { Compilation = compilation; State = state; Resolved = compilation.Resolve(target, state); }

    /// <summary>Derives a stable PostgreSQL-safe slot name from a canonical input.</summary>
    /// <param name="input">Canonical relation acquisition input.</param>
    /// <returns>Dedicated replication slot name.</returns>
    public static string Slot(RelationQueryInputId input) => "orders_shadow_" +
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(input.Value)))[..16];

    /// <summary>Preflights source capabilities and binds existing rebuild and catch-up interpreters.</summary>
    /// <param name="application">Activated shared application declarations.</param>
    /// <param name="database">Borrowed pool, owned by the command host.</param>
    /// <param name="connectionString">Native logical-replication connection configuration.</param>
    /// <param name="target">Admitted dedicated shadow target.</param>
    /// <param name="authenticationKey">Persistent position-token authentication key.</param>
    /// <param name="slotGeneration">Operator-owned slot incarnation, stable across restart.</param>
    /// <param name="cancellationToken">Cancels native preflight.</param>
    /// <returns>One reusable native interpretation with durable state.</returns>
    public static async Task<FulfillmentShadowRuntime> BindAsync(FulfillmentApplication application,
        NpgsqlDataSource database, string connectionString, IMaterializationTarget target,
        ReadOnlyMemory<byte> authenticationKey, string slotGeneration, CancellationToken cancellationToken)
    {
        var definition = new FulfillmentShadowDefinition(application.Queries);
        var prepared = new PostgresMaterializationRelation(application.Storage.Bindings,
            application.Queries.OrderDetailsRelation.Definition, definition.Impact, FulfillmentDomain.PartitionField,
            new(new("fulfillment/shadow/physical/v1"), "fulfillment/shadow/v1", 100, 1000, 1000, 100, 100, 1));
        var policy = new PostgresRelationQuerySourcePolicy(100, 1000, 100, FulfillmentShadowDefinition.MaximumBytes,
            partitionScope: new(new(FulfillmentStoragePolicy.LogicalPartition), FulfillmentDomain.PartitionField,
                FulfillmentStoragePolicy.LocalPartition));
        var native = new PostgresNpgsqlRuntimeBinding(application.Storage.Bindings.Database, database, Authority,
            logicalReplicationConnectionFactory: () => new LogicalReplicationConnection(connectionString));
        var hydrationReaders = prepared.Hydration.Placement.SourceInstances.Select(source =>
            (IRelationQuerySourceReader)new PostgresRelationQuerySourceReader(prepared.Plan, prepared.Hydration,
                source.Id, prepared.HydrationStorage, database, native, policy)).ToArray();
        var readers = prepared.Reads.Impact.Placement.SourceInstances.Select(source =>
            new PostgresRelationQuerySourceReader(prepared.Plan, prepared.Reads.Impact,
                source.Id, prepared.ReadStorage, database, native, policy)).ToDictionary(reader => reader.Descriptor.Source);
        var impactReader = new RelationQueryMaterializationImpactReader(prepared.Plan, prepared.Reads.Impact, readers.Values);
        var readEvidence = prepared.Reads.Impact.Placement.Bindings.Select(binding =>
            new PostgresMaterializationSource(readers[binding.Source], binding, authenticationKey.Span)).ToArray();
        var root = prepared.Plan.InputContract.Sources.Single(source => source.Role == RelationQuerySourceInputRole.RelationRoot);
        var baselineBinding = prepared.Reads.Baseline.Placement.Bindings.Single(binding => binding.Input == root.Input.Id);
        var baselineReader = new PostgresRelationQuerySourceReader(prepared.Plan, prepared.Reads.Baseline,
            baselineBinding.Source, prepared.BaselineStorage, database, native, policy);
        var sources = new List<MaterializationPartitionSourceBinding>();
        foreach (var impactBinding in prepared.Reads.Impact.Placement.Bindings)
        {
            var isRoot = impactBinding.Input == root.Input.Id;
            var binding = isRoot ? baselineBinding : impactBinding;
            var source = await PostgresLogicalReplicationMaterializationChangeSource.CreateAsync(isRoot ? baselineReader : readers[binding.Source], binding,
                native, new(Publication, Slot(binding.Input), slotGeneration,
                    expectedReplicaIdentity: new(PostgresLogicalReplicationReplicaIdentityKind.Full),
                    beforeImageRequirement: PostgresLogicalReplicationBeforeImageRequirement.Required),
                authenticationKey, policy: new(readInactivityTimeout: TimeSpan.FromSeconds(3)), cancellationToken: cancellationToken);
            sources.Add(new(binding.Input, source.Scope, new PostgresMaterializationImpactSource(source, prepared.Reads, readEvidence,
                isRoot
                    ? definition.Document.Definition.Sources.Single(item => item.Input == binding.Input) : null,
                isRoot ? impactReader.ReadAsync : null)));
        }
        var placed = baselineBinding;
        var stage = prepared.Reads.Baseline.Stages.Single(candidate => candidate.PlacementBinding == placed.Id
            && candidate.Kind == RelationQueryPhysicalStageKind.SourceRead);
        var read = new RelationQuerySourceReadRequest(prepared.Reads.Baseline.Fingerprint, stage.Id, placed.Id,
            placed.Source, placed.Shape, placed.Identity!.SourceSelector,
            [.. placed.Fields.Select(field => new RelationQuerySourceReadField(field.Input, field.SemanticPath,
                field.SourceSelector, RelationQuerySourceReadFieldPurpose.SemanticInput))], new RelationQueryBoundedEnumeration(1000), 1000);
        var hydrator = new RelationQueryMaterializationRebuildHydrator(prepared.Plan, prepared.Hydration, prepared.Realization,
            root.Input.Id, definition.Output, hydrationReaders);
        var partition = new MaterializationPartitionBinding(FulfillmentStoragePolicy.LocalPartition, new(FulfillmentStoragePolicy.LogicalPartition), read, hydrator, sources,
            impact => new RelationQueryMaterializationImpactRuntime(impact, definition.Document.Definition,
                prepared.Hydration, prepared.Realization, hydrationReaders,
                new MaterializationImpactRootExecutor(impact, definition.Document.Definition, impactReader.ReadAsync).ResolveRootsAsync));
        var compilation = MaterializationPartitionedRebuildCompiler.Compile(definition.Document, prepared.Plan,
            new(100, FulfillmentShadowDefinition.MaximumBytes, 100, FulfillmentShadowDefinition.MaximumBytes, 128,
                MaterializationRebuildProcessFactory.MaximumStartsPerActivation, MaterializationRebuildProcessFactory.MaximumParallelism, 16),
            "fulfillment-shadow", target.Descriptor, [partition], definition.Impact);
        return new(compilation, target, new(database, new(Authority)));
    }
}
