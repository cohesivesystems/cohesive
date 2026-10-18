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
    /// <param name="projection">Explicit closed projection override; otherwise infer a native prefix from registered entity placement.</param>
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
        return BindComposed(query, bindings.PrepareComposed(query.CompilationRequest, remote, composedPolicy, projection), remote);
    }

    /// <summary>Binds offline heterogeneous preparation to actual source readers without rediscovering placement.</summary>
    /// <typeparam name="TInput">Query input.</typeparam><typeparam name="TResult">Query result.</typeparam>
    /// <param name="query">Exact prepared declaration.</param><param name="prepared">Offline preparation.</param>
    /// <param name="remote">Runtime registrations matching prepared descriptors.</param><returns>A complete-result reader.</returns>
    public IRelationQueryReader<TInput, TResult> BindComposed<TInput, TResult>(RelationQuery<TInput, TResult> query,
        PostgresComposedQueryPreparation prepared, EntityRelationQuerySourceCatalog remote)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        if (!ReferenceEquals(query.CompilationRequest, prepared.Request))
            throw new ArgumentException("The prepared query authority must match exactly.", nameof(query));
        var readers = prepared.Sources.Select(binding =>
        {
            if (!remote.TryGetSource(binding.Shape, out var actual) || actual.Source != binding.Source || actual.Descriptor != binding.Descriptor)
                throw new ArgumentException("Runtime sources must match prepared source and partition contracts.", nameof(remote));
            if (actual.PersistedObservationType != binding.PersistedObservationType
                || actual.IdentitySemanticPath != binding.IdentitySemanticPath || actual.IdentitySourceSelector != binding.IdentitySourceSelector
                || actual.ObservationVersionSemanticPath != binding.ObservationVersionSemanticPath
                || actual.ConcurrencyTokenSemanticPath != binding.ConcurrencyTokenSemanticPath)
                throw new ArgumentException("Runtime entity and identity mappings differ from preparation.", nameof(remote));
            foreach (var placed in prepared.Placement.Bindings.Where(item => item.Source == binding.Source.Id))
                if (placed.Fields.Any(field => actual.FieldSourceSelector(field.SemanticPath) != field.SourceSelector)
                    || placed.RelationshipKeys.Any(key => actual.RelationshipKeySourceSelector(key.SemanticPath) != key.SourceSelector))
                    throw new ArgumentException("Runtime selectors differ from prepared source placement.", nameof(remote));
            return EntityRelationQuerySourceCatalog.RequireReader(actual);
        }).ToArray();
        var policy = prepared.Policy;
        if (prepared.Cut is { } cut)
            return new RelationQuerySubplanReader<TInput, TResult>(query, cut,
                new PostgresQueryRowsReader(prepared.Prefix!, runtime, policy.SourcePolicy.MaximumRowsPerRead, policy.SourcePolicy.MaximumPageBytes),
                prepared.Placement, policy.PhysicalPlanningPolicy, _ => readers, policy.PartitionScope.LogicalPartition,
                policy.SourcePolicy.MaximumRowsPerRead, policy.SourcePolicy.MaximumPageBytes, prepared.Execution);
        IRelationQuerySourceReader[] all = [new PostgresRelationQuerySourceReader(prepared.Compilation.Plan!, prepared.Physical.Plan!,
            prepared.LocalSource, prepared.Storage!, runtime.DataSource, runtime, policy.SourcePolicy), .. readers];
        return new ComposedReader<TInput, TResult>(query, prepared.Compilation, prepared.Realization, prepared.Placement,
            prepared.Physical, all, policy.SourcePolicy.MaximumRowsPerRead, policy.SourcePolicy.MaximumPageBytes);
    }

    sealed class ComposedReader<TInput, TResult>(RelationQuery<TInput, TResult> query,
        RelationQueryCompilationResult compilation, RelationQueryRealizationReport realization,
        RelationQuerySourcePlacement placement, RelationQueryPhysicalPlanningResult physical,
        IRelationQuerySourceReader[] readers, int maximumRows, long maximumBytes) : IRelationQueryReader<TInput, TResult>
    {
        /// <inheritdoc />
        public RelationQueryConsistency Consistency => RelationQueryConsistency.IndependentSources;
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
