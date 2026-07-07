namespace Cohesive.Relations.Queries;

/// <summary>
/// Capabilities of an aggregation backend.
/// </summary>
/// <param name="Backend">Human-readable backend name.</param>
/// <param name="Features">Supported aggregation features.</param>
/// <param name="PredicateCapabilities">Supported predicate features for aggregation filters.</param>
public sealed record AggregationBackendCapability(
    string Backend,
    AggregationFeatureSet Features,
    QueryCapabilitySet PredicateCapabilities
)
{
    /// <summary>
    /// Creates a backend capability descriptor from individual feature flags.
    /// </summary>
    public AggregationBackendCapability(
        string backend,
        bool supportsCountIf,
        bool supportsTopHit,
        bool supportsPipelineMetrics,
        bool supportsNestedAggregations,
        QueryCapabilitySet? predicateCapabilities = null
        )
        : this(
            Backend: backend,
            Features:
            (supportsCountIf ? AggregationFeatureSet.CountIf | AggregationFeatureSet.FilteredMetric : AggregationFeatureSet.None)
            | (supportsTopHit ? AggregationFeatureSet.TopHit : AggregationFeatureSet.None)
            | (supportsPipelineMetrics ? AggregationFeatureSet.MetricOrder : AggregationFeatureSet.None)
            | (supportsNestedAggregations ? AggregationFeatureSet.Nested : AggregationFeatureSet.None),
            PredicateCapabilities: predicateCapabilities ?? QueryCapabilitySet.None)
    {
    }

    /// <summary>
    /// Returns true when every requested aggregation feature is present.
    /// </summary>
    public bool Supports(AggregationFeatureSet features) =>
        (Features & features) == features;

    /// <summary>
    /// Throws when the requested feature set is not available.
    /// </summary>
    public void EnsureSupports(AggregationFeatureSet required, string operation)
    {
        if (Supports(required))
            return;

        var missing = required & ~Features;
        throw new AggregationPlanValidationException(
            $"Aggregation backend '{Backend}' cannot {operation}; missing features: {missing}.");
    }
}

/// <summary>
/// Backend features required by aggregation plans.
/// </summary>
[Flags]
public enum AggregationFeatureSet
{
    /// <summary>
    /// No special aggregation features are required.
    /// </summary>
    None = 0,

    /// <summary>
    /// Filtered count metrics.
    /// </summary>
    CountIf = 1 << 0,

    /// <summary>
    /// Filtered field metrics.
    /// </summary>
    FilteredMetric = 1 << 1,

    /// <summary>
    /// Top-hit sample metrics.
    /// </summary>
    TopHit = 1 << 2,

    /// <summary>
    /// Metrics or buckets that require nested aggregation support.
    /// </summary>
    Nested = 1 << 3,

    /// <summary>
    /// Date histogram buckets.
    /// </summary>
    DateHistogram = 1 << 4,

    /// <summary>
    /// Numeric histogram buckets.
    /// </summary>
    Histogram = 1 << 5,

    /// <summary>
    /// Bucket ordering by metric values.
    /// </summary>
    MetricOrder = 1 << 6
}