using System.Collections.Immutable;
using Cohesive.Model;
using Cohesive.Storage;
using Cohesive.Relations.Acquisition;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Diagnostics;
using Cohesive.Relations.Execution;
using Cohesive.Relations.IR;
using Cohesive.Relations.Physical;
using Cohesive.Relations.Realization;

namespace Cohesive.Adapters.Postgres;

public sealed partial class PostgresPersistenceBindings
{
    /// <summary>Prepares heterogeneous acquisition without SDK clients or IO; runtime consumes the same plan.</summary>
    /// <param name="request">Canonical query request.</param><param name="remote">Offline source contracts.</param>
    /// <param name="composedPolicy">Explicit partition and resource bounds.</param><param name="projection">Optional native cut override.</param>
    /// <returns>Validated native/physical preparation with independent-source consistency.</returns>
    public PostgresComposedQueryPreparation PrepareComposed(RelationQueryCompilationRequest request,
        EntityRelationQuerySourceCatalog remote, PostgresRelationQueryComposedPolicy composedPolicy, QueryNodeId? projection = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(remote);
        ArgumentNullException.ThrowIfNull(composedPolicy);
        var original = RelationQueryStaticCompiler.Compile(request);
        var cut = projection is { } node ? RelationQuerySubplan.Compile(original, node)
            : InferComposedPrefix(original, shape => remote.TryGetSource(shape, out _));
        var compilation = cut?.Remainder ?? original;
        if (!compilation.IsSuccessful) throw new RelationQueryPreparationException("semantic", compilation);
        var plan = compilation.Plan!;
        var scope = composedPolicy.PartitionScope;
        var builder = RelationQueryPlacement.For(plan);
        var physicalPolicy = composedPolicy.PhysicalPlanningPolicy;
        var limits = new RelationQuerySourcePlacementLimits(physicalPolicy.MaximumBatchSize,
            physicalPolicy.MaximumBufferedRows, physicalPolicy.MaximumFanOut, physicalPolicy.MaximumConcurrency);
        var local = builder.Source("postgres/composed/local",
            cut is null ? PostgresRelationQuerySourceTargetProfile.Default : RelationQueryProjectedRowset.Profile,
            new(Database.Value), limits: limits);
        var handles = new Dictionary<QualifiedShapeId, RelationQueryPlacementSourceHandle>();
        var used = new Dictionary<QualifiedShapeId, EntityRelationQuerySourceBinding>();
        var diagnostics = new List<RelationQueryArtifactAuthoringDiagnostic>();
        foreach (var input in plan.InputContract.Sources)
        {
            if (cut is not null && input.Node == cut.Cut.Id)
                builder.Place(input, local).Identity("$row").FieldsBySemanticPath();
            else Place(input.Shape, source => builder.Place(input, source), input.Fields, input.Input.Id, null);
        }
        foreach (var input in plan.InputContract.Traversals)
            Place(input.ResultShape, source => builder.Place(input, source), input.Fields, input.Input.Id,
                input.Input.Direction == RelationshipTraversalDirection.Inverse ? input.Definition.SourceReference : null);
        var authored = builder.Build().RequireValue();
        if (diagnostics.Count != 0)
            throw new RelationQueryArtifactAuthoringException([.. diagnostics]);
        var placement = authored.Placement;
        var execution = new RelationQueryPhysicalPreparation(compilation, placement, physicalPolicy);
        PostgresRelationQueryCompiledArtifact? prefix = null;
        PostgresRelationQueryStorageBinding? bound = null;
        if (cut is not null) prefix = Prepare(cut.Prefix.Request);
        else
        {
            var storage = PostgresRelationQueryBinding.For(authored).ForSource(local.Id).Database(Database);
            foreach (var input in authored.Inputs.Where(input => input.Source.Id == local.Id))
                storage.Table(input, Mapping(input.Shape));
            bound = storage.Build().RequireValue();
        }
        return new(request, compilation, cut, prefix, execution, placement, bound, local.Id,
            composedPolicy, used.Values.ToImmutableArray());

        void Place(QualifiedShapeId shape, Func<RelationQueryPlacementSourceHandle, RelationQueryPlacementInputBuilder> place,
            ImmutableArray<RelationQueryFieldInputContract> fields, RelationQueryInputId input, FieldPath? relationship)
        {
            if (!remote.TryGetSource(shape, out var registration))
            {
                if (cut is not null || !Tables.ContainsKey(shape))
                    throw new RelationQueryPreparationException("composed mapping", compilation,
                        code: "postgres.composed.remoteMappingMissing", detail: $"No source registered for shape '{shape}'.");
                this.Place(place(local), shape, scope.SourceSelector);
                return;
            }
            if (Tables.ContainsKey(shape))
                throw new RelationQueryPreparationException("composed mapping", compilation,
                    code: "postgres.composed.placementAmbiguous", detail: $"Shape '{shape}' is placed in both PostgreSQL and a remote source.");
            if (registration.Descriptor.LogicalPartition != scope.LogicalPartition)
                throw new RelationQueryPreparationException("composed scope", compilation,
                    code: "postgres.composed.partitionMismatch", detail: "Remote source logical partition differs from the composed scope.");
            if (!handles.TryGetValue(shape, out var handle))
            {
                var source = registration.Source;
                handle = builder.Source(source.Id.Value, source.TargetProfile, source.ExecutionDomain, source.Limits, source.Id);
                handles.Add(shape, handle);
                used.Add(shape, registration);
            }
            EntityRelationQuerySourceCatalog.Configure(place(handle), registration, fields, input, relationship, diagnostics);
        }
    }
}
