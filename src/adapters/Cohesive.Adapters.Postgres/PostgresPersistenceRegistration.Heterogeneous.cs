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

public sealed partial class PostgresPersistenceRegistration
{
    /// <summary>Composes PostgreSQL acquisition with explicitly registered heterogeneous entity sources.</summary>
    /// <typeparam name="TInput">Invocation parameter.</typeparam><typeparam name="TResult">Declared result.</typeparam>
    /// <param name="query">Shared logical query.</param>
    /// <param name="remote">One active source per remotely placed entity.</param>
    /// <param name="composedPolicy">Explicit partition and bounded acquisition policy.</param>
    /// <param name="projection">Optional closed projection to execute as one native SQL prefix.</param>
    /// <returns>Prepared reader retaining native and composed execution evidence.</returns>
    /// <remarks>No global snapshot or cross-store transaction is implied. Source registration must attest the
    /// same logical partition; the prefix must enforce that scope. No IO occurs during preparation.</remarks>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="RelationQueryArtifactAuthoringException">Native selector or placement authoring is invalid.</exception>
    /// <exception cref="RelationQueryPreparationException">Preparation, coverage or partition affinity fails.</exception>
    public IRelationQueryReader<TInput, TResult> QueryComposed<TInput, TResult>(
        RelationQuery<TInput, TResult> query, EntityRelationQuerySourceCatalog remote,
        PostgresRelationQueryComposedPolicy composedPolicy, QueryNodeId? projection = null)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(remote);
        ArgumentNullException.ThrowIfNull(composedPolicy);
        var cut = projection is { } node ? RelationQuerySubplan.Compile(query.CompilationRequest, node) : null;
        var compilation = cut?.Remainder ?? RelationQueryStaticCompiler.Compile(query.CompilationRequest);
        if (!compilation.IsSuccessful) throw new RelationQueryPreparationException("semantic", compilation);
        var plan = compilation.Plan!;
        var scope = composedPolicy.PartitionScope;
        var builder = RelationQueryPlacement.For(plan);
        var physicalPolicy = composedPolicy.PhysicalPlanningPolicy;
        var limits = new RelationQuerySourcePlacementLimits(physicalPolicy.MaximumBatchSize,
            physicalPolicy.MaximumBufferedRows, physicalPolicy.MaximumFanOut, physicalPolicy.MaximumConcurrency);
        var local = builder.Source("postgres/composed/local",
            cut is null ? PostgresRelationQuerySourceTargetProfile.Default : RelationQueryProjectedRowset.Profile,
            new(runtime.Database.Value), limits: limits);
        var handles = new Dictionary<QualifiedShapeId, RelationQueryPlacementSourceHandle>();
        var used = new Dictionary<QualifiedShapeId, EntityRelationQuerySourceRegistration>();
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
        if (cut is not null)
            return new RelationQuerySubplanReader<TInput, TResult>(query, cut,
                Prepare(cut.Prefix.Request, composedPolicy.SourcePolicy.MaximumRowsPerRead, composedPolicy.SourcePolicy.MaximumPageBytes),
                placement, physicalPolicy, _ => used.Values.Select(source => source.Reader), scope.LogicalPartition,
                composedPolicy.SourcePolicy.MaximumRowsPerRead, composedPolicy.SourcePolicy.MaximumPageBytes);

        var realization = RelationQueryInMemoryInterpreter.Default.Realize(plan);
        var physical = RelationQueryPhysicalPlanner.Compile(plan, realization, placement, physicalPolicy);
        if (!physical.IsSuccessful) throw new RelationQueryPreparationException("physical", compilation, physical);
        var storage = PostgresRelationQueryBinding.For(authored).ForSource(local.Id).Database(runtime.Database);
        foreach (var input in authored.Inputs.Where(input => input.Source.Id == local.Id))
            storage.Table(input, bindings.Mapping(input.Shape));
        var bound = storage.Build().RequireValue();
        IRelationQuerySourceReader[] readers = [new PostgresRelationQuerySourceReader(plan, physical.Plan!, local.Id,
            bound, runtime.DataSource, runtime, composedPolicy.SourcePolicy), .. used.Values.Select(source => source.Reader)];
        return new ComposedReader<TInput, TResult>(query, compilation, realization, placement, physical, readers,
            composedPolicy.SourcePolicy.MaximumRowsPerRead, composedPolicy.SourcePolicy.MaximumPageBytes);

        void Place(QualifiedShapeId shape, Func<RelationQueryPlacementSourceHandle, RelationQueryPlacementInputBuilder> place,
            ImmutableArray<RelationQueryFieldInputContract> fields, RelationQueryInputId input, FieldPath? relationship)
        {
            if (!remote.TryGetSource(shape, out var registration))
            {
                if (cut is not null || !bindings.Tables.ContainsKey(shape))
                    throw new RelationQueryPreparationException("composed mapping", compilation,
                        code: "postgres.composed.remoteMappingMissing", detail: $"No source registered for shape '{shape}'.");
                bindings.Place(place(local), shape, scope.SourceSelector);
                return;
            }
            if (bindings.Tables.ContainsKey(shape))
                throw new RelationQueryPreparationException("composed mapping", compilation,
                    code: "postgres.composed.placementAmbiguous", detail: $"Shape '{shape}' is placed in both PostgreSQL and a remote source.");
            if (registration.Reader.Descriptor.LogicalPartition != scope.LogicalPartition)
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

    sealed class ComposedReader<TInput, TResult>(RelationQuery<TInput, TResult> query,
        RelationQueryCompilationResult compilation, RelationQueryRealizationReport realization,
        RelationQuerySourcePlacement placement, RelationQueryPhysicalPlanningResult physical,
        IRelationQuerySourceReader[] readers, int maximumRows, long maximumBytes) : IRelationQueryReader<TInput, TResult>
    {
        readonly RelationQueryPhysicalExecutor executor = new(readers);
        readonly ImmutableArray<RelationQueryCapabilityEvidence> capabilities = RelationQueryRealizationRuntimeEvidence.ProjectCapabilities(compilation.Plan!, realization);
        public RelationQuery<TInput, TResult> Definition => query;
        public async Task<TResult> ReadAsync(TInput input, CancellationToken cancellationToken = default)
        {
            var id = new RelationQueryEvaluationId(Guid.NewGuid().ToString("N"));
            var evaluation = query.CompilationRequest.Evaluate(id).Set(query.Parameter, ObservationValue.FromObject(input)).Build();
            var execution = await executor.ExecuteAsync(new(compilation.Plan!, physical.Plan!, realization, id,
                parameters: evaluation.Parameters, capabilities: capabilities),
                cancellationToken).ConfigureAwait(false);
            return query.AssembleResult(new(evaluation, compilation, realization, placement, physical, execution), maximumRows, maximumBytes);
        }
    }
}
