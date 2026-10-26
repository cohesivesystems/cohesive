using Cohesive.Model;
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
