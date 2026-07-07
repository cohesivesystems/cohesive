namespace Cohesive.Relations.Queries;

/// <summary>
/// Defines the bucket shape produced by an aggregation root.
/// </summary>
public abstract record AggregationPlanRoot
{
    /// <summary>
    /// Backend features required by this bucket shape.
    /// </summary>
    public abstract AggregationFeatureSet RequiredFeatures { get; }
}

/// <summary>
/// Singleton aggregation across the query result set.
/// </summary>
/// <param name="Name">The key used for the singleton result row.</param>
public sealed record GlobalAggregationPlan(string Name = "global") : AggregationPlanRoot
{
    /// <inheritdoc />
    public override AggregationFeatureSet RequiredFeatures => AggregationFeatureSet.None;
}

/// <summary>
/// Terms aggregation grouped by a field value.
/// </summary>
/// <param name="GroupByField">Field used as the bucket key.</param>
/// <param name="Order">Optional bucket ordering.</param>
/// <param name="Take">Optional maximum bucket count.</param>
public sealed record TermsGroupAggregationPlan(
    FieldPath GroupByField,
    OrderSpec? Order = null,
    int? Take = null
    ) : AggregationPlanRoot
{
    /// <inheritdoc />
    public override AggregationFeatureSet RequiredFeatures =>
        Order is null ? AggregationFeatureSet.None : AggregationFeatureSet.MetricOrder;
}

/// <summary>
/// Date histogram aggregation grouped by calendar interval.
/// </summary>
/// <param name="TimestampField">Timestamp field used as the bucket key.</param>
/// <param name="CalendarInterval">Backend calendar interval, such as day, week, or month.</param>
/// <param name="TimeZone">Optional time-zone identifier.</param>
/// <param name="Take">Optional maximum bucket count.</param>
/// <param name="Order">Optional bucket ordering.</param>
public sealed record DateHistogramAggregationPlan(
    FieldPath TimestampField,
    string CalendarInterval,
    string? TimeZone = null,
    int? Take = null,
    OrderSpec? Order = null
    ) : AggregationPlanRoot
{
    /// <inheritdoc />
    public override AggregationFeatureSet RequiredFeatures =>
        AggregationFeatureSet.DateHistogram
        | (Order is null ? AggregationFeatureSet.None : AggregationFeatureSet.MetricOrder);
}

/// <summary>
/// Numeric histogram aggregation grouped by interval.
/// </summary>
/// <param name="Field">Numeric field used as the bucket key.</param>
/// <param name="Interval">Bucket interval.</param>
/// <param name="Offset">Optional bucket offset.</param>
/// <param name="Take">Optional maximum bucket count.</param>
/// <param name="Order">Optional bucket ordering.</param>
public sealed record HistogramAggregationPlan(
    FieldPath Field,
    double Interval,
    double? Offset = null,
    int? Take = null,
    OrderSpec? Order = null
    ) : AggregationPlanRoot
{
    /// <inheritdoc />
    public override AggregationFeatureSet RequiredFeatures =>
        AggregationFeatureSet.Histogram
        | (Order is null ? AggregationFeatureSet.None : AggregationFeatureSet.MetricOrder);
}
