using Cohesive.Model.Serialization;
using Cohesive.Model;
using AspireFirst.Orders;
using Cohesive.Execution;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Execution;
using Cohesive.Storage.Materialization;

namespace AspireFirst.Shadow;

/// <summary>One shadow interpretation of the same rooted relation used by the live order endpoint.</summary>
public sealed class FulfillmentShadowDefinition
{
    /// <summary>Maximum items in one source or target page.</summary>
    public const int MaximumItems = 100;
    /// <summary>Maximum bytes in one source or target page.</summary>
    public const long MaximumBytes = 1_048_576;

    /// <summary>Compiles the existing rooted relation and its bounded inverse-impact routes.</summary>
    /// <param name="queries">Activated query declarations shared with the live API.</param>
    public FulfillmentShadowDefinition(FulfillmentQueries queries)
    {
        var request = queries.OrderDetailsRelation.Definition;
        var compiled = RelationQueryStaticCompiler.Compile(request);
        if (!compiled.IsSuccessful) throw new InvalidOperationException(string.Join("; ", compiled.Diagnostics));
        Plan = compiled.Plan!;
        Output = Plan.RequirementGraph.Outputs.Single(output => output.Field is null);
        var declaration = Materialization.Define(new("fulfillment/order-details/shadow"), queries.OrderDetailsRelation)
            .WithUpdatePolicy(new(MaterializationSynchronizationMode.All,
                MaterializationConsistencyKind.BaselinePlusCatchUp,
                MaterializationIdempotencyKind.StableOutputIdentityAndVersion))
            .WithBoundedRelationBaselineCatchUpSources(MaximumItems, MaximumBytes, MaximumItems, MaximumBytes)
            .WithGenerationalIndexTarget(MaximumItems, MaximumBytes)
            .WithFailurePolicy(new(3, MaterializationFailureDisposition.Stop))
            .WithFreshnessPolicy(new(maximumLagMilliseconds: 300_000))
            .WithProvenance(new(new("aspire-first/shadow", "1"), new("fulfillment/order-details/shadow"), DocumentOrigin.Generated))
            .Build();
        if (!declaration.Validation.IsValid)
            throw new InvalidOperationException(string.Join("; ", declaration.Validation.Diagnostics));
        Document = declaration.CreateDocument();
        var impact = MaterializationImpactPlanCompiler.Compile(Document,
            new(new("fulfillment/order-details/shadow/impact/v1"),
                [MaterializationImpactStrategyKind.InverseTraversal], MaximumItems, MaximumBytes));
        if (impact.Plan is null) throw new InvalidOperationException(string.Join("; ", impact.Diagnostics));
        Impact = impact.Plan;
    }

    /// <summary>Shared semantic relation plan.</summary>
    public CompiledRelationQueryPlan Plan { get; }
    /// <summary>Rooted output selected for materialization.</summary>
    public RelationQueryOutputReference Output { get; }
    /// <summary>Canonical shadow update and recovery requirements.</summary>
    public MaterializationDocument Document { get; }
    /// <summary>Compiled contributor-to-root impact routes.</summary>
    public MaterializationImpactPlan Impact { get; }
}
