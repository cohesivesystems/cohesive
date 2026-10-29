using AspireFirst.Materialization;
using Cohesive.Adapters.Elastic;
using Cohesive.Api.Execution.Services;
using Cohesive.Model;
using Cohesive.Prelude;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.Execution;
using Cohesive.Storage.Materialization;

namespace AspireFirst.Orders;

/// <summary>Explicitly selects one generation for order details; all writes and other queries stay native.</summary>
public static class OrderDetailsReadSelection
{
    /// <summary>Checks operator comparison evidence before opening Elasticsearch. Evidence is local, not a signed attestation.</summary>
    /// <param name="comparison">Successful bounded comparison produced by the operator command.</param>
    /// <param name="definition">The materialization prepared from this worker's canonical relation.</param>
    /// <param name="now">Admission clock.</param>
    public static void Validate(MaterializationShadowComparison comparison, PreparedMaterialization definition, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        if (!comparison.Matches || comparison.LiveCount < 0 || comparison.LiveCount != comparison.ShadowCount
            || comparison.ObservedAtUtc > now || comparison.Convergence.Materialization != definition.Document.Definition.Id
            || comparison.Convergence.DefinitionFingerprint != definition.Document.DefinitionFingerprint
            || !comparison.Convergence.ValidateFreshness(now).IsValid)
            throw new InvalidOperationException("Read selection requires fresh matching comparison evidence for this exact order-details definition.");
    }

    /// <summary>Binds an explicitly selected generation, retaining native persistence for every other operation.</summary>
    /// <param name="live">Already prepared native persistence.</param>
    /// <param name="queries">Canonical query instance used by endpoint bindings.</param>
    /// <param name="runtime">Exact admitted source, target and rebuild plan. No rebuild is executed here.</param>
    /// <param name="comparison">Validated operator evidence naming the selected generation.</param>
    /// <returns>One host-owned resolver. Backend failures propagate; there is no automatic fallback.</returns>
    public static IServicePersistence Bind(IServicePersistence live, FulfillmentQueries queries,
        BoundMaterialization runtime, MaterializationShadowComparison comparison)
    {
        if (queries.OrderDetailsRelation.Definition.DefinitionDocument.DefinitionFingerprint
                != runtime.Resolved.Plan.Materialization.Definition.Relation.CompilationRequest.DefinitionDocument.DefinitionFingerprint)
            throw new ArgumentException("Selected generation does not realize this query relation.", nameof(queries));
        if (!comparison.Matches || !comparison.Convergence.ValidateAgainst(runtime.Resolved.Plan, DateTimeOffset.UtcNow).IsValid)
            throw new InvalidOperationException("Read selection requires fresh comparison evidence for the exact native rebuild plan.");
        var target = runtime.Resolved.Target as ElasticMaterializationTarget
            ?? throw new ArgumentException("This realization requires the admitted Elasticsearch target.", nameof(runtime));
        return new ServicePersistenceBuilder(live).Query(queries.OrderDetails,
            (_, bytes) => new Reader(queries.OrderDetails, target, runtime.Resolved.Plan.Materialization.DefinitionFingerprint, comparison.Convergence.Generation, bytes)).Build();
    }

    sealed class Reader(RelationQuery<string, OrderDetails?> query, ElasticMaterializationTarget target,
        Cohesive.Execution.ExecutionDefinitionFingerprint definition, MaterializationGenerationId generation, long maximumBytes)
        : IRelationQueryReader<string, OrderDetails?>
    {
        readonly Func<ObservationValue, OrderDetails?> materialize = ObservationMaterializer.PrepareJsonValue<OrderDetails>();
        public RelationQueryConsistency Consistency => RelationQueryConsistency.Eventual;
        public RelationQuery<string, OrderDetails?> Definition => query;
        public async Task<OrderDetails?> ReadAsync(string input, CancellationToken cancellationToken = default)
        {
            var value = await target.ReadValueAsync(OperationContext.Create(cancellationToken: cancellationToken), generation,
                definition, MaterializationItemId.FromRootIdentity(input), (int)Math.Min(maximumBytes, int.MaxValue));
            if (value is null) return null;
            return materialize(value.Value) ?? throw new InvalidOperationException("A retained order-details item cannot contain a null result.");
        }
    }
}
