using System.Linq.Expressions;

namespace Cohesive.Relations.Queries;

/// <summary>
/// Builder entry point for aggregation plans.
/// </summary>
public static class AggregationPlanBuilder
{
    /// <summary>
    /// Starts an aggregation plan over the given document type.
    /// </summary>
    public static RootSetBuilder<TDocument> From<TDocument>() => new();
}

/// <summary>
/// Builder for the set of aggregation roots in a plan.
/// </summary>
/// <typeparam name="TDocument">Document type used for field selectors.</typeparam>
public sealed class RootSetBuilder<TDocument>
{
    readonly List<AggregationRoot> roots = [];
    EntityPredicate? predicate;

    internal RootSetBuilder()
    {
    }

    /// <summary>
    /// Adds a predicate that constrains the result set being aggregated.
    /// </summary>
    public RootSetBuilder<TDocument> Where(EntityPredicate filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        predicate = predicate is null ? filter : EntityPredicatePlanner.And(predicate, filter);
        return this;
    }

    /// <summary>
    /// Adds one aggregation root.
    /// </summary>
    /// <param name="name">Unique aggregation root name.</param>
    /// <param name="build">Root builder callback.</param>
    public RootSetBuilder<TDocument> Add(string name, Func<RootBuilder<TDocument>, RootBuilder<TDocument>> build)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(build);

        var builder = build(new RootBuilder<TDocument>());
        if (builder.Root is null)
            throw new InvalidOperationException($"Aggregation root '{name}' must declare a bucket shape.");

        roots.Add(new AggregationRoot(name, builder.Root, builder.Statistics));
        return this;
    }

    /// <summary>
    /// Builds the aggregation plan.
    /// </summary>
    public AggregationPlan Build() => new([.. roots], predicate);
}

/// <summary>
/// Builder for a single aggregation root.
/// </summary>
/// <typeparam name="TDocument">Document type used for field selectors.</typeparam>
public sealed class RootBuilder<TDocument>
{
    internal AggregationPlanRoot? Root { get; private set; }

    internal List<AggregationStatistic> Statistics { get; } = [];

    internal RootBuilder()
    {
    }

    /// <summary>
    /// Creates a singleton aggregation across the query result set.
    /// </summary>
    public RootBuilder<TDocument> Global(string name = "global")
    {
        Root = new GlobalAggregationPlan(name);
        return this;
    }

    /// <summary>
    /// Groups the aggregation by a terms field.
    /// </summary>
    public RootBuilder<TDocument> GroupBy<TKey>(Expression<Func<TDocument, TKey>> key)
    {
        Root = new TermsGroupAggregationPlan(Resolve(key));
        return this;
    }

    /// <summary>
    /// Groups the aggregation by a date histogram.
    /// </summary>
    public RootBuilder<TDocument> DateHistogram<TKey>(
        Expression<Func<TDocument, TKey>> timestamp,
        string calendarInterval,
        string? timeZone = null
        )
    {
        Root = new DateHistogramAggregationPlan(Resolve(timestamp), calendarInterval, timeZone);
        return this;
    }

    /// <summary>
    /// Groups the aggregation by a numeric histogram.
    /// </summary>
    public RootBuilder<TDocument> Histogram<TKey>(
        Expression<Func<TDocument, TKey>> field,
        double interval,
        double? offset = null
        )
    {
        Root = new HistogramAggregationPlan(Resolve(field), interval, offset);
        return this;
    }

    /// <summary>
    /// Adds a count statistic.
    /// </summary>
    public RootBuilder<TDocument> Count(string name = "count")
    {
        Statistics.Add(new CountAggregationStatistic(name));
        return this;
    }

    /// <summary>
    /// Adds a sum statistic.
    /// </summary>
    public RootBuilder<TDocument> Sum<TValue>(string name, Expression<Func<TDocument, TValue>> selector)
    {
        Statistics.Add(new SumAggregationStatistic(name, Resolve(selector)));
        return this;
    }

    /// <summary>
    /// Adds a filtered count statistic.
    /// </summary>
    public RootBuilder<TDocument> CountIf(string name, EntityPredicate filter)
    {
        Statistics.Add(new CountIfAggregationStatistic(name, filter));
        return this;
    }

    /// <summary>
    /// Adds a filtered sum statistic.
    /// </summary>
    public RootBuilder<TDocument> SumIf<TValue>(
        string name,
        Expression<Func<TDocument, TValue>> selector,
        EntityPredicate filter
        )
    {
        Statistics.Add(new SumIfAggregationStatistic(name, Resolve(selector), filter));
        return this;
    }

    /// <summary>
    /// Adds a top-hit statistic.
    /// </summary>
    public RootBuilder<TDocument> TopHit(
        string name,
        int size,
        params Expression<Func<TDocument, object?>>[] fields
        )
    {
        Statistics.Add(new TopHitAggregationStatistic(name, fields.Select(Resolve).ToArray(), size));
        return this;
    }

    /// <summary>
    /// Orders buckets by a statistic descending.
    /// </summary>
    public RootBuilder<TDocument> OrderByDesc(string statistic)
    {
        ApplyOrder(new(statistic, Descending: true));
        return this;
    }

    /// <summary>
    /// Orders buckets by a statistic ascending.
    /// </summary>
    public RootBuilder<TDocument> OrderByAsc(string statistic)
    {
        ApplyOrder(new(statistic, Descending: false));
        return this;
    }

    /// <summary>
    /// Limits the number of buckets returned.
    /// </summary>
    public RootBuilder<TDocument> Take(int count)
    {
        Root = Root switch
        {
            TermsGroupAggregationPlan terms => terms with { Take = count },
            DateHistogramAggregationPlan date => date with { Take = count },
            HistogramAggregationPlan histogram => histogram with { Take = count },
            _ => Root
        };
        return this;
    }

    void ApplyOrder(OrderSpec order)
    {
        Root = Root switch
        {
            TermsGroupAggregationPlan terms => terms with { Order = order },
            DateHistogramAggregationPlan date => date with { Order = order },
            HistogramAggregationPlan histogram => histogram with { Order = order },
            _ => Root
        };
    }

    static FieldPath Resolve<TValue>(Expression<Func<TDocument, TValue>> selector)
    {
        ArgumentNullException.ThrowIfNull(selector);

        var body = selector.Body.Type == typeof(object)
            ? selector.Body
            : Expression.Convert(selector.Body, typeof(object));
        var converted = Expression.Lambda<Func<TDocument, object?>>(body, selector.Parameters);
        return FieldPath.Capture(converted);
    }
}
