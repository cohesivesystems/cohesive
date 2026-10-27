using Cohesive.Model;
using Cohesive.Relations.Acquisition;
using Npgsql;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Execution;
using Cohesive.Relations.Physical;
using Cohesive.Relations.Realization;
using Cohesive.Storage.Materialization;

namespace Cohesive.Adapters.Postgres;

/// <summary>Offline hydration, baseline and affected-root reads derived from one relation and repository mapping catalog.</summary>
public sealed class PostgresMaterializationRelation
{
    readonly MaterializationImpactPlan impact;
    /// <summary>Prepares the bounded, partition-scoped interpretation without opening a database connection.</summary>
    /// <param name="bindings">Canonical repository mappings; the runtime must admit their schema before use.</param>
    /// <param name="request">The rooted relation shared with live reads.</param>
    /// <param name="impact">Impact plan compiled from that relation's materialization declaration.</param>
    /// <param name="partitionField">Declared partition field shared by every mapped entity.</param>
    /// <param name="policy">Bounded physical execution policy.</param>
    /// <exception cref="ArgumentException">A mapping, partition or plan reference differs.</exception>
    /// <exception cref="RelationQueryPreparationException">The relation cannot be physically prepared.</exception>
    public PostgresMaterializationRelation(PostgresPersistenceBindings bindings, RelationQueryCompilationRequest request,
        MaterializationImpactPlan impact, string partitionField, RelationQueryPhysicalPlanningPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(impact);
        this.impact = impact;
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentException.ThrowIfNullOrWhiteSpace(partitionField);
        var compilation = RelationQueryStaticCompiler.Compile(request);
        if (!compilation.IsSuccessful) throw new RelationQueryPreparationException("materialization", compilation);
        Plan = compilation.Plan!;
        var authoring = RelationQueryPlacement.For(Plan);
        var source = authoring.Source("postgres/materialization", PostgresRelationQuerySourceTargetProfile.Default,
            new(bindings.Database.Value), new(policy.MaximumBatchSize, policy.MaximumBufferedRows, policy.MaximumBatchSize, 1));
        foreach (var input in Plan.InputContract.Sources) bindings.Place(authoring.Place(input, source), input.Shape, partitionField);
        foreach (var input in Plan.InputContract.Traversals) bindings.Place(authoring.Place(input, source), input.ResultShape, partitionField);
        var placement = authoring.Build().RequireValue();
        var prepared = new RelationQueryPhysicalPreparation(compilation, placement.Placement, policy);
        Hydration = prepared.Physical.Plan!;
        Realization = prepared.Realization;
        Reads = MaterializationRelationReadPlans.Create(Plan, Hydration, impact,
            (shape, path) => bindings.Mapping(shape).Fields.Single(field => FieldPath.FromField(field.FieldName) == path).FieldName);
        HydrationStorage = Storage(Hydration);
        ReadStorage = Storage(Reads.Impact);
        BaselineStorage = Storage(Reads.Baseline);

        PostgresRelationQueryStorageBinding Storage(CompiledRelationQueryPhysicalPlan physical)
        {
            var placed = RelationQueryPlacement.ForPhysicalReads(Plan, physical);
            var native = PostgresRelationQueryBinding.For(placed).Database(bindings.Database);
            foreach (var input in placed.Inputs)
                if (input.Binding.Acquisition != RelationQuerySourceAcquisitionKind.Supplied)
                    native.Table(input, bindings.Mapping(input.Shape));
            return native.Build().RequireValue();
        }
    }

    /// <summary>Binds all native hydration, impact and CDC readers for one logical partition.</summary>
    /// <param name="document">Materialization of this exact rooted relation.</param>
    /// <param name="database">Borrowed native pool.</param>
    /// <param name="native">Attested native runtime, including replication connection factory.</param>
    /// <param name="policy">Bounds and required logical partition scope.</param>
    /// <param name="partition">Stable placement-subject identity.</param>
    /// <param name="authenticationKey">Persistent continuation authentication key.</param>
    /// <param name="slotFactory">Explicit slot/publication incarnation per canonical input.</param>
    /// <param name="replicationPolicy">Native feed bounds and inactivity policy.</param>
    /// <param name="cancellationToken">Cancels native preflight.</param>
    /// <returns>A complete partition binding usable by rebuild and catch-up.</returns>
    /// <exception cref="ArgumentException">The document or partition does not match this preparation.</exception>
    public async Task<MaterializationPartitionBinding> BindAsync(MaterializationDocument document,
        NpgsqlDataSource database, PostgresNpgsqlRuntimeBinding native, PostgresRelationQuerySourcePolicy policy,
        string partition, ReadOnlyMemory<byte> authenticationKey,
        Func<RelationQueryInputId, PostgresLogicalReplicationBinding> slotFactory,
        PostgresLogicalReplicationSourcePolicy replicationPolicy, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        _ = MaterializationImpactPlanLinker.Link(impact, document.Definition);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(slotFactory);
        if (policy.PartitionScope is null)
            throw new ArgumentException("Materialization requires explicit logical partition scope.", nameof(policy));
        if (document.Definition.Relation.CompiledPlanFingerprint !=
            RelationQueryCompiledPlanReferenceFingerprinter.Compute(RelationQueryCompiledPlanReference.From(Plan)))
            throw new ArgumentException("The materialization belongs to another relation.", nameof(document));
        var hydrationReaders = Hydration.Placement.SourceInstances.Select(source =>
            (IRelationQuerySourceReader)new PostgresRelationQuerySourceReader(Plan, Hydration,
                source.Id, HydrationStorage, database, native, policy)).ToArray();
        var readers = Reads.Impact.Placement.SourceInstances.Select(source =>
            new PostgresRelationQuerySourceReader(Plan, Reads.Impact,
                source.Id, ReadStorage, database, native, policy)).ToDictionary(reader => reader.Descriptor.Source);
        var impactReader = new RelationQueryMaterializationImpactReader(Plan, Reads.Impact, readers.Values);
        var readEvidence = Reads.Impact.Placement.Bindings.Select(binding =>
            new PostgresMaterializationSource(readers[binding.Source], binding, authenticationKey.Span)).ToArray();
        var root = Plan.InputContract.Sources.Single(source => source.Role == RelationQuerySourceInputRole.RelationRoot);
        var baselineBinding = Reads.Baseline.Placement.Bindings.Single(binding => binding.Input == root.Input.Id);
        var baselineReader = new PostgresRelationQuerySourceReader(Plan, Reads.Baseline,
            baselineBinding.Source, BaselineStorage, database, native, policy);
        var sources = new List<MaterializationPartitionSourceBinding>();
        // CreateAsync performs scoped inspection only; sources retain factories, not open connections.
        // Each later stream owns its connection for the lifetime of its async enumeration.
        foreach (var impactBinding in Reads.Impact.Placement.Bindings)
        {
            var isRoot = impactBinding.Input == root.Input.Id;
            var binding = isRoot ? baselineBinding : impactBinding;
            var source = await PostgresLogicalReplicationMaterializationChangeSource.CreateAsync(isRoot ? baselineReader : readers[binding.Source], binding,
                native, slotFactory(binding.Input),
                authenticationKey, policy: replicationPolicy, cancellationToken: cancellationToken);
            sources.Add(new(binding.Input, source.Scope, new PostgresMaterializationImpactSource(source, Reads, readEvidence,
                isRoot
                    ? document.Definition.Sources.Single(item => item.Input == binding.Input) : null,
                isRoot ? impactReader.ReadAsync : null)));
        }
        var placed = baselineBinding;
        var stage = Reads.Baseline.Stages.Single(candidate => candidate.PlacementBinding == placed.Id
            && candidate.Kind == RelationQueryPhysicalStageKind.SourceRead);
        var read = new RelationQuerySourceReadRequest(Reads.Baseline.Fingerprint, stage.Id, placed.Id,
            placed.Source, placed.Shape, placed.Identity!.SourceSelector,
            [.. placed.Fields.Select(field => new RelationQuerySourceReadField(field.Input, field.SemanticPath,
                field.SourceSelector, RelationQuerySourceReadFieldPurpose.SemanticInput))], new RelationQueryBoundedEnumeration(policy.MaximumRowsPerRead), policy.MaximumRowsPerRead);
        var hydrator = new RelationQueryMaterializationRebuildHydrator(Plan, Hydration, Realization,
            root.Input.Id, document.Definition.Relation.Output, hydrationReaders);
        return new MaterializationPartitionBinding(partition, policy.PartitionScope!.LogicalPartition, read, hydrator, sources,
            impact => new RelationQueryMaterializationImpactRuntime(impact, document.Definition,
                Hydration, Realization, hydrationReaders,
                new MaterializationImpactRootExecutor(impact, document.Definition, impactReader.ReadAsync).ResolveRootsAsync));
    }

    /// <summary>Exact semantic authority.</summary>
    public CompiledRelationQueryPlan Plan { get; }
    /// <summary>Canonical in-memory relation interpretation used for hydration.</summary>
    public RelationQueryRealizationReport Realization { get; }
    /// <summary>Supplied-root hydration plan.</summary>
    public CompiledRelationQueryPhysicalPlan Hydration { get; }
    /// <summary>Derived baseline and affected-root read plans.</summary>
    public MaterializationRelationReadPlans Reads { get; }
    /// <summary>Native hydration mappings.</summary>
    public PostgresRelationQueryStorageBinding HydrationStorage { get; }
    /// <summary>Native affected-root mappings.</summary>
    public PostgresRelationQueryStorageBinding ReadStorage { get; }
    /// <summary>Native baseline mappings.</summary>
    public PostgresRelationQueryStorageBinding BaselineStorage { get; }
}
