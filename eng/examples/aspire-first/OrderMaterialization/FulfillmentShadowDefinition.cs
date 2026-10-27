using Cohesive.Model.Serialization;
using AspireFirst.Orders;
using Cohesive.Storage.Materialization;

namespace AspireFirst.Materialization;

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
        var declaration = Cohesive.Storage.Materialization.Materialization.Define(new("fulfillment/order-details/shadow"), queries.OrderDetailsRelation)
            .WithUpdatePolicy(new(MaterializationSynchronizationMode.All,
                MaterializationConsistencyKind.BaselinePlusCatchUp,
                MaterializationIdempotencyKind.StableOutputIdentityAndVersion))
            .WithBoundedRelationBaselineCatchUpSources(MaximumItems, MaximumBytes, MaximumItems, MaximumBytes)
            .WithGenerationalIndexTarget(MaximumItems, MaximumBytes)
            .WithFailurePolicy(new(3, MaterializationFailureDisposition.Stop))
            .WithFreshnessPolicy(new(maximumLagMilliseconds: 300_000))
            .WithProvenance(new(new("aspire-first/shadow", "1"), new("fulfillment/order-details/shadow"), DocumentOrigin.Generated))
            .Build();
        Prepared = declaration.Prepare(new(new("fulfillment/order-details/shadow/impact/v1"),
            [MaterializationImpactStrategyKind.InverseTraversal], MaximumItems, MaximumBytes));
    }

    /// <summary>Shared admitted declaration and change-impact preparation.</summary>
    public PreparedMaterialization Prepared { get; }
    /// <summary>Canonical shadow update and recovery requirements.</summary>
    public MaterializationDocument Document => Prepared.Document;
    /// <summary>Compiled contributor-to-root impact routes.</summary>
    public MaterializationImpactPlan Impact => Prepared.Impact;
}
