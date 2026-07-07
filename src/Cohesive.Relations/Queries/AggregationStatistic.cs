namespace Cohesive.Relations.Queries;

/// <summary>
/// Computed statistic in an aggregation bucket.
/// </summary>
/// <param name="Name">Statistic name.</param>
public abstract record AggregationStatistic(string Name)
{
    /// <summary>
    /// Backend features required by this statistic.
    /// </summary>
    public abstract AggregationFeatureSet RequiredFeatures { get; }

    /// <summary>
    /// Optional predicate applied only to this statistic.
    /// </summary>
    public virtual EntityPredicate? Filter => null;
}


/// <summary>
/// Count statistic.
/// </summary>
/// <param name="Name">Statistic name.</param>
public sealed record CountAggregationStatistic(string Name = "count") : AggregationStatistic(Name)
{
    /// <inheritdoc />
    public override AggregationFeatureSet RequiredFeatures => AggregationFeatureSet.None;
}

/// <summary>
/// Sum statistic over a numeric field.
/// </summary>
/// <param name="Name">Statistic name.</param>
/// <param name="Field">Field to sum.</param>
/// <param name="NestedPath">Optional nested aggregation path.</param>
public sealed record SumAggregationStatistic(string Name, FieldPath Field, FieldPath? NestedPath = null) : AggregationStatistic(Name)
{
    /// <inheritdoc />
    public override AggregationFeatureSet RequiredFeatures =>
        NestedPath is null ? AggregationFeatureSet.None : AggregationFeatureSet.Nested;
}

/// <summary>
/// Count statistic with an entity predicate.
/// </summary>
public sealed record CountIfAggregationStatistic : AggregationStatistic
{
    /// <summary>
    /// Creates a filtered count statistic.
    /// </summary>
    /// <param name="name">Statistic name.</param>
    /// <param name="filter">Predicate applied to the statistic.</param>
    public CountIfAggregationStatistic(string name, EntityPredicate filter)
        : base(name)
    {
        Filter = Guard.RequireNotNull(filter);
    }

    /// <inheritdoc />
    public override AggregationFeatureSet RequiredFeatures => AggregationFeatureSet.CountIf;

    /// <inheritdoc />
    public override EntityPredicate Filter { get; }
}

/// <summary>
/// Sum statistic with an entity predicate.
/// </summary>
public sealed record SumIfAggregationStatistic : AggregationStatistic
{
    /// <summary>
    /// Creates a filtered sum statistic.
    /// </summary>
    /// <param name="name">Statistic name.</param>
    /// <param name="field">Field to sum when the filter matches.</param>
    /// <param name="filter">Predicate applied to the statistic.</param>
    /// <param name="nestedPath">Optional nested aggregation path.</param>
    public SumIfAggregationStatistic(string name, FieldPath field, EntityPredicate filter, FieldPath? nestedPath = null)
        : base(name)
    {
        Field = field;
        Filter = Guard.RequireNotNull(filter);
        NestedPath = nestedPath;
    }

    /// <summary>
    /// Field to sum when the filter matches.
    /// </summary>
    public FieldPath Field { get; }

    /// <inheritdoc />
    public override EntityPredicate Filter { get; }

    /// <summary>
    /// Optional nested aggregation path.
    /// </summary>
    public FieldPath? NestedPath { get; }

    /// <inheritdoc />
    public override AggregationFeatureSet RequiredFeatures =>
        AggregationFeatureSet.FilteredMetric
        | (NestedPath is null ? AggregationFeatureSet.None : AggregationFeatureSet.Nested);
}

/// <summary>
/// Top-hit statistic returning representative source fields.
/// </summary>
/// <param name="Name">Statistic name.</param>
/// <param name="Fields">Source fields to include.</param>
/// <param name="Size">Number of samples to return.</param>
public sealed record TopHitAggregationStatistic(string Name, IReadOnlyList<FieldPath> Fields, int Size = 1) : AggregationStatistic(Name)
{
    /// <inheritdoc />
    public override AggregationFeatureSet RequiredFeatures => AggregationFeatureSet.TopHit;
}
