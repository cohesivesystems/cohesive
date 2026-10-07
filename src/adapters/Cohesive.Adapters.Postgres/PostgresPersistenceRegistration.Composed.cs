using Cohesive.Model;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Execution;
using Cohesive.Relations.IR;
using Cohesive.Relations.Physical;

namespace Cohesive.Adapters.Postgres;

public sealed partial class PostgresPersistenceRegistration
{
    /// <summary>Prepares a native projection followed by a bounded query over another explicitly registered database.</summary>
    /// <typeparam name="TInput">Single invocation parameter type.</typeparam>
    /// <typeparam name="TResult">Declared result type.</typeparam>
    /// <param name="query">Canonical query authority, including native-prefix partition predicates.</param>
    /// <param name="projection">Closed nonterminal projection executed on this database.</param>
    /// <param name="remote">Entity mappings and caller-owned runtime for all remaining database inputs.</param>
    /// <param name="composedPolicy">Source bounds, required partition and retained physical policy. Batch size is declared only in the physical policy.</param>
    /// <returns>A host-lifetime prepared reader; no IO occurs during registration.</returns>
    /// <remarks>The host attests that the prefix enforces the same logical partition. Independent reads do not
    /// establish a distributed snapshot. This recipe uses bounded enumeration and sequential acquisition.
    /// No retry or invocation result caching is introduced.</remarks>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">Scope, mapping or plan affinity is invalid.</exception>
    /// <exception cref="RelationQueryPreparationException">Semantic or physical preparation fails.</exception>
    /// <exception cref="PostgresQueryPreparationException">Native prefix compilation fails.</exception>
    public RelationQuerySubplanReader<TInput, TResult> QueryComposed<TInput, TResult>(
        RelationQuery<TInput, TResult> query, QueryNodeId projection,
        PostgresPersistenceRegistration remote, PostgresRelationQueryComposedPolicy composedPolicy)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(remote);
        ArgumentNullException.ThrowIfNull(composedPolicy);
        var policy = composedPolicy.SourcePolicy;
        var cut = RelationQuerySubplan.Compile(query.CompilationRequest, projection);
        var physicalPolicy = composedPolicy.PhysicalPlanningPolicy;
        var scope = policy.PartitionScope ?? throw new RelationQueryPreparationException(
            "composed policy", cut.Original, code: "postgres.composed.partitionScopeMissing",
            detail: "Composed registration requires an explicit partition scope.");
        var prefix = Prepare(cut.Prefix.Request, policy.MaximumRowsPerRead, policy.MaximumPageBytes);
        var builder = RelationQueryPlacement.For(cut.Remainder.Plan!);
        var limits = new RelationQuerySourcePlacementLimits(
            physicalPolicy.MaximumBatchSize, physicalPolicy.MaximumBufferedRows,
            physicalPolicy.MaximumFanOut, physicalPolicy.MaximumConcurrency);
        var projected = builder.Source("postgres/composed/prefix", RelationQueryProjectedRowset.Profile,
            new(runtime.Database.Value), limits: limits);
        var external = builder.Source("postgres/composed/remote", PostgresRelationQuerySourceTargetProfile.Default,
            new(remote.runtime.Database.Value), limits: limits);
        foreach (var input in cut.Remainder.Plan!.InputContract.Sources)
        {
            if (input.Node == cut.Cut.Id)
                builder.Place(input, projected).Identity("$row").FieldsBySemanticPath();
            else Place(builder.Place(input, external), input.Shape);
        }
        foreach (var input in cut.Remainder.Plan.InputContract.Traversals)
            Place(builder.Place(input, external), input.ResultShape);
        var placement = builder.Build().RequireValue();
        var storage = PostgresRelationQueryBinding.For(placement).ForSource(external.Id).Database(remote.runtime.Database);
        foreach (var input in placement.Inputs.Where(input => input.Source.Id == external.Id))
            storage.Table(input, Mapping(input.Shape));
        var bound = storage.Build().RequireValue();
        return new(query, cut, prefix, placement.Placement, physicalPolicy,
            physical => [new PostgresRelationQuerySourceReader(cut.Remainder.Plan, physical, external.Id,
                bound, remote.runtime.DataSource, remote.runtime, policy)], scope.LogicalPartition);

        PostgresEntityRepositoryMapping Mapping(QualifiedShapeId shape) => remote.tables.TryGetValue(shape, out var attachment)
            ? attachment.Mapping : throw new RelationQueryPreparationException("composed mapping", cut.Original,
                code: "postgres.composed.remoteMappingMissing",
                detail: $"No remote PostgreSQL mapping is registered for shape '{shape}'.");
        void Place(RelationQueryPlacementInputBuilder input, QualifiedShapeId shape)
        {
            var mapping = Mapping(shape);
            if (mapping.PartitionField != scope.SourceSelector)
                throw new RelationQueryPreparationException("composed policy", cut.Original,
                    code: "postgres.composed.partitionSelectorMismatch", detail: "Remote entity partition selector differs from the declared scope.");
            input.Identity(FieldPath.FromField(mapping.IdentityField), mapping.IdentityField)
                .FieldsBySemanticPath().Partition(mapping.PartitionField);
        }
    }
}
