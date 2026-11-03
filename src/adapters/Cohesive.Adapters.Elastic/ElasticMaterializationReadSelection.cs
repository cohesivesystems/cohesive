using Cohesive.Model;
using Cohesive.Prelude;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Execution;
using Cohesive.Relations.IR;
using Cohesive.Storage.Materialization;

namespace Cohesive.Adapters.Elastic;

/// <summary>Prepares typed eventual reads from a compared generation of the same rooted relation as a live query.</summary>
public static class ElasticMaterializationReadSelection
{
    /// <summary>Admits exact relation/query and native rebuild evidence, then prepares a pinned generation once.</summary>
    /// <typeparam name="TResult">Rooted result projection.</typeparam>
    /// <param name="query">Keyed query derived from the relation.</param><param name="relation">Shared root authority.</param>
    /// <param name="runtime">Admitted native binding.</param><param name="comparison">Trusted comparison evidence.</param>
    /// <param name="cancellationToken">Startup cancellation.</param><returns>Factory honoring native response-byte bounds.</returns>
    public static async Task<Func<int, long, IRelationQueryReader<string, TResult?>>> PrepareAsync<TResult>(
        RelationQuery<string, TResult?> query, RootedRelation<TResult> relation, BoundMaterialization runtime,
        MaterializationShadowComparison comparison, CancellationToken cancellationToken = default) where TResult : class
    {
        ArgumentNullException.ThrowIfNull(query); ArgumentNullException.ThrowIfNull(relation);
        ArgumentNullException.ThrowIfNull(runtime); ArgumentNullException.ThrowIfNull(comparison);
        if (!SamePlan(relation.Definition, runtime.Resolved.Plan.Materialization.Definition.Relation.CompilationRequest))
            throw new ArgumentException("Selected generation does not realize this query relation.", nameof(relation));
        var expected = relation.ReadById(((QueryDefinition)query.CompilationRequest.DefinitionDocument.Definition).Id);
        if (!SamePlan(query.CompilationRequest, expected.CompilationRequest))
            throw new ArgumentException("The selected query must be the rooted relation's keyed read.", nameof(query));
        MaterializationReadSelection.Validate(comparison, runtime.Resolved.Plan.Materialization, DateTimeOffset.UtcNow);
        if (!comparison.Convergence.ValidateAgainst(runtime.Resolved.Plan, DateTimeOffset.UtcNow).IsValid)
            throw new InvalidOperationException("Read selection requires fresh comparison evidence for the exact native rebuild plan.");
        var target = runtime.Resolved.Target as ElasticMaterializationTarget
            ?? throw new ArgumentException("Read selection requires an Elasticsearch target.", nameof(runtime));
        var values = await target.PrepareValueReaderAsync(OperationContext.Create(cancellationToken: cancellationToken),
            comparison.Convergence.Generation, runtime.Resolved.Plan.Materialization.DefinitionFingerprint).ConfigureAwait(false);
        var materialize = ObservationMaterializer.PrepareJsonValue<TResult>();
        return (rows, bytes) =>
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rows); ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);
            return new Reader<TResult>(query, values, materialize, (int)Math.Min(bytes, int.MaxValue));
        };
    }

    static bool SamePlan(RelationQueryCompilationRequest left, RelationQueryCompilationRequest right)
    {
        var actual = RelationQueryStaticCompiler.Compile(left);
        var expected = RelationQueryStaticCompiler.Compile(right);
        return actual.IsSuccessful && expected.IsSuccessful &&
            RelationQueryCompiledPlanReferenceFingerprinter.Compute(RelationQueryCompiledPlanReference.From(actual.Plan!)) ==
            RelationQueryCompiledPlanReferenceFingerprinter.Compute(RelationQueryCompiledPlanReference.From(expected.Plan!));
    }

    sealed class Reader<T>(RelationQuery<string, T?> query, ElasticMaterializationTarget.ValueReader values,
        Func<ObservationValue, T?> materialize, int maximumBytes) : IRelationQueryReader<string, T?> where T : class
    {
        public RelationQueryConsistency Consistency => RelationQueryConsistency.Eventual;
        public RelationQuery<string, T?> Definition => query;
        public async Task<T?> ReadAsync(string input, CancellationToken cancellationToken = default)
        {
            var value = await values.ReadAsync(OperationContext.Create(cancellationToken: cancellationToken),
                MaterializationItemId.FromRootIdentity(input), maximumBytes).ConfigureAwait(false);
            return value is null ? null : materialize(value.Value) ?? throw new InvalidOperationException("A retained rooted item cannot contain a null result.");
        }
    }
}
