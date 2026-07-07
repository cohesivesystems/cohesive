using System.Globalization;
using System.Text.Json;
using Cohesive.Relations.Queries;
using Elastic.Clients.Elasticsearch;
using Elastic.Clients.Elasticsearch.Aggregations;
using Elastic.Clients.Elasticsearch.Core.Search;
using Elastic.Clients.Elasticsearch.QueryDsl;
using ElasticQuery = Elastic.Clients.Elasticsearch.QueryDsl.Query;
using SortOrder = Elastic.Clients.Elasticsearch.SortOrder;

namespace Cohesive.Adapters.Elastic;

/// <summary>
/// Compiled Elasticsearch aggregation request fragments.
/// </summary>
/// <param name="Query">Optional top-level query constraining the result set.</param>
/// <param name="Aggregations">Elasticsearch aggregation tree keyed by aggregation root name.</param>
public sealed record ElasticAggregationQuery(
    ElasticQuery? Query,
    IReadOnlyDictionary<string, Aggregation> Aggregations
);

/// <summary>
/// Compiles relation aggregation plans to Elasticsearch query DSL.
/// </summary>
public sealed class ElasticAggregationCompiler : IAggregationCompiler<ElasticAggregationQuery>
{
    internal const string FilteredSumAggregationValueName = "value";

    readonly ElasticQueryCompiler queryCompiler = new();

    /// <inheritdoc />
    public AggregationBackendCapability Capabilities { get; } = new(
        Backend: "Elasticsearch",
        Features: AggregationFeatureSet.CountIf
                  | AggregationFeatureSet.FilteredMetric
                  | AggregationFeatureSet.TopHit
                  | AggregationFeatureSet.DateHistogram
                  | AggregationFeatureSet.Histogram
                  | AggregationFeatureSet.MetricOrder,
        PredicateCapabilities: new ElasticQueryCompiler().Capabilities
        );

    /// <inheritdoc />
    public ElasticAggregationQuery Compile(AggregationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        plan.Validate(Capabilities);

        Dictionary<string, Aggregation> aggregations = new(StringComparer.Ordinal);
        foreach (var root in plan.Roots)
            aggregations[root.Name] = CompileRoot(root);

        return new(
            Query: plan.Predicate is null ? null : queryCompiler.Compile(plan.Predicate),
            Aggregations: aggregations
            );
    }

    static Aggregation CompileRoot(AggregationRoot root) => root.Root switch
    {
        GlobalAggregationPlan => BuildSingleton(root.Statistics),
        TermsGroupAggregationPlan terms => BuildTerms(terms, root.Statistics),
        DateHistogramAggregationPlan date => BuildDateHistogram(date, root.Statistics),
        HistogramAggregationPlan histogram => BuildHistogram(histogram, root.Statistics),
        _ => throw new InvalidOperationException($"Unknown aggregation root type '{root.Root.GetType().Name}'.")
    };

    static Aggregation BuildSingleton(IReadOnlyList<AggregationStatistic> statistics) => new()
    {
        Filter = new MatchAllQuery(),
        Aggregations = BuildAggregations(statistics)
    };

    static Aggregation BuildTerms(TermsGroupAggregationPlan root, IReadOnlyList<AggregationStatistic> statistics) => new()
    {
        Terms = new()
        {
            Field = new(ElasticQueryCompiler.ToElasticFieldName(root.GroupByField)),
            Size = root.Take,
            Order = root.Order is null ? null : new Dictionary<Field, SortOrder>
            {
                [new(CompileOrderPath(root.Order))] = root.Order.Descending ? SortOrder.Desc : SortOrder.Asc
            }
        },
        Aggregations = BuildAggregations(statistics)
    };

    static Aggregation BuildDateHistogram(DateHistogramAggregationPlan root, IReadOnlyList<AggregationStatistic> statistics)
    {
        if (root.Take is not null)
            throw new NotSupportedException("Elasticsearch date histogram compilation does not yet support bucket limits.");

        return new()
        {
            DateHistogram = new()
            {
                Field = new Field(ElasticQueryCompiler.ToElasticFieldName(root.TimestampField)),
                CalendarInterval = ParseCalendarInterval(root.CalendarInterval),
                TimeZone = root.TimeZone
            },
            Aggregations = BuildAggregations(statistics)
        };
    }

    static Aggregation BuildHistogram(HistogramAggregationPlan root, IReadOnlyList<AggregationStatistic> statistics)
    {
        if (root.Take is not null)
            throw new NotSupportedException("Elasticsearch histogram compilation does not yet support bucket limits.");

        return new()
        {
            Histogram = new()
            {
                Field = new(ElasticQueryCompiler.ToElasticFieldName(root.Field)),
                Interval = root.Interval,
                Offset = root.Offset
            },
            Aggregations = BuildAggregations(statistics)
        };
    }

    static IDictionary<string, Aggregation> BuildAggregations(IReadOnlyList<AggregationStatistic> statistics)
    {
        Dictionary<string, Aggregation> aggregations = new(StringComparer.Ordinal);
        var queryCompiler = new ElasticQueryCompiler();
        foreach (var statistic in statistics)
        {
            aggregations[statistic.Name] = statistic switch
            {
                CountAggregationStatistic => new()
                {
                    ValueCount = new() { Field = new("_id") }
                },
                SumAggregationStatistic sum => new()
                {
                    Sum = new() { Field = new(ElasticQueryCompiler.ToElasticFieldName(sum.Field)) }
                },
                CountIfAggregationStatistic countIf => new()
                {
                    Filter = queryCompiler.Compile(countIf.Filter)
                },
                SumIfAggregationStatistic sumIf => BuildFilteredSum(sumIf, queryCompiler),
                TopHitAggregationStatistic topHit => new()
                {
                    TopHits = new()
                    {
                        Size = topHit.Size,
                        Source = new SourceConfig(new SourceFilter
                        {
                            Includes = Fields.FromFields([.. topHit.Fields.Select(field => new Field(ElasticQueryCompiler.ToElasticFieldName(field)))])
                        })
                    }
                },
                _ => throw new InvalidOperationException($"Unknown statistic type '{statistic.GetType().Name}'.")
            };
        }

        return aggregations;
    }

    static Aggregation BuildFilteredSum(SumIfAggregationStatistic aggregationStatistic, ElasticQueryCompiler queryCompiler) => new()
    {
        Filter = queryCompiler.Compile(aggregationStatistic.Filter),
        Aggregations = new Dictionary<string, Aggregation>
        {
            [FilteredSumAggregationValueName] = new()
            {
                Sum = new() { Field = new Field(ElasticQueryCompiler.ToElasticFieldName(aggregationStatistic.Field)) }
            }
        }
    };

    static string CompileOrderPath(OrderSpec order) =>
        string.Equals(order.StatisticName, "count", StringComparison.Ordinal)
            ? "_count"
            : order.StatisticName;

    static CalendarInterval ParseCalendarInterval(string value)
    {
        var trimmed = value.Trim();
        if (string.Equals(trimmed, "1M", StringComparison.Ordinal))
            return CalendarInterval.Month;

        return trimmed.ToLowerInvariant() switch
        {
            "second" or "1s" => CalendarInterval.Second,
            "minute" or "1m" => CalendarInterval.Minute,
            "hour" or "1h" => CalendarInterval.Hour,
            "day" or "1d" => CalendarInterval.Day,
            "week" or "1w" => CalendarInterval.Week,
            "month" => CalendarInterval.Month,
            "quarter" or "1q" => CalendarInterval.Quarter,
            "year" or "1y" => CalendarInterval.Year,
            _ => throw new NotSupportedException($"Unsupported Elasticsearch calendar interval '{value}'.")
        };
    }
}

/// <summary>
/// Reads Elasticsearch aggregation responses into relation aggregation results.
/// </summary>
/// <typeparam name="TDocument">Search document type.</typeparam>
public static class ElasticAggregationResultReader<TDocument>
{
    /// <summary>
    /// Reads aggregation results from an Elasticsearch search response.
    /// </summary>
    public static IReadOnlyDictionary<string, AggregationResult> Read(
        SearchResponse<TDocument> response,
        AggregationPlan plan
        )
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(plan);

        Dictionary<string, AggregationResult> results = new(StringComparer.Ordinal);
        foreach (var root in plan.Roots)
        {
            var aggregate = response.Aggregations![root.Name];
            results[root.Name] = root.Root switch
            {
                GlobalAggregationPlan global => new SingletonAggregationResult(ReadSingleton(aggregate, root, global)),
                TermsGroupAggregationPlan => new BucketedAggregationResult(ReadTerms(aggregate, root)),
                DateHistogramAggregationPlan => new BucketedAggregationResult(ReadDateHistogram(aggregate, root)),
                HistogramAggregationPlan => new BucketedAggregationResult(ReadHistogram(aggregate, root)),
                _ => throw new InvalidOperationException($"Unknown aggregation root type '{root.Root.GetType().Name}'.")
            };
        }

        return results;
    }

    static AggregationResultRow ReadSingleton(IAggregate aggregate, AggregationRoot root, GlobalAggregationPlan spec)
    {
        var bucket = (FilterAggregate)aggregate;
        var statistics = new Dictionary<string, double?>(StringComparer.Ordinal);
        var samples = new Dictionary<string, JsonElement?>(StringComparer.Ordinal);
        foreach (var statistic in root.Statistics)
            ReadStatistic(bucket.Aggregations!, bucket.DocCount, statistic, statistics, samples);
        return new(spec.Name, bucket.DocCount, statistics, samples);
    }

    static IReadOnlyList<AggregationResultRow> ReadTerms(IAggregate aggregate, AggregationRoot root)
    {
        List<AggregationResultRow> rows = [];
        foreach (var bucket in TermsBuckets(aggregate))
            rows.Add(ReadBucket(bucket.Key, bucket.DocCount, bucket.Aggregations, root.Statistics));
        return rows;
    }

    static IReadOnlyList<AggregationResultRow> ReadDateHistogram(IAggregate aggregate, AggregationRoot root)
    {
        var histogram = (DateHistogramAggregate)aggregate;
        return [.. histogram.Buckets.Select(bucket => ReadBucket(
            bucket.KeyAsString ?? bucket.Key.ToString(CultureInfo.InvariantCulture),
            bucket.DocCount,
            bucket.Aggregations!,
            root.Statistics)
        )];
    }

    static IReadOnlyList<AggregationResultRow> ReadHistogram(IAggregate aggregate, AggregationRoot root)
    {
        var histogram = (HistogramAggregate)aggregate;
        return [.. histogram.Buckets.Select(bucket => ReadBucket(
            bucket.Key.ToString(CultureInfo.InvariantCulture),
            bucket.DocCount,
            bucket.Aggregations!,
            root.Statistics))];
    }

    static AggregationResultRow ReadBucket(
        string key,
        long docCount,
        IReadOnlyDictionary<string, IAggregate> aggregations,
        IReadOnlyList<AggregationStatistic> statistics
        )
    {
        var values = new Dictionary<string, double?>(StringComparer.Ordinal);
        var samples = new Dictionary<string, JsonElement?>(StringComparer.Ordinal);
        foreach (var statistic in statistics)
            ReadStatistic(aggregations, docCount, statistic, values, samples);
        return new(key, docCount, values, samples);
    }

    static void ReadStatistic(
        IReadOnlyDictionary<string, IAggregate> aggregations,
        long docCount,
        AggregationStatistic aggregationStatistic,
        Dictionary<string, double?> statistics,
        Dictionary<string, JsonElement?> samples
        )
    {
        if (aggregationStatistic is CountAggregationStatistic)
        {
            statistics[aggregationStatistic.Name] = docCount;
            return;
        }

        if (!aggregations.TryGetValue(aggregationStatistic.Name, out var aggregate))
            return;

        switch (aggregationStatistic)
        {
            case SumAggregationStatistic:
                statistics[aggregationStatistic.Name] = (aggregate as SumAggregate)?.Value;
                break;
            case CountIfAggregationStatistic:
                statistics[aggregationStatistic.Name] = (aggregate as FilterAggregate)?.DocCount;
                break;
            case SumIfAggregationStatistic:
                if (aggregate is FilterAggregate filterAggregate
                    && filterAggregate.Aggregations!.TryGetValue(ElasticAggregationCompiler.FilteredSumAggregationValueName, out var sumAggregate))
                {
                    statistics[aggregationStatistic.Name] = (sumAggregate as SumAggregate)?.Value;
                }
                break;
            case TopHitAggregationStatistic:
                samples[aggregationStatistic.Name] = ReadTopHit(aggregate);
                break;
        }
    }

    static JsonElement? ReadTopHit(IAggregate aggregate)
    {
        var hit = (aggregate as TopHitsAggregate)?.Hits.Hits.FirstOrDefault();
        var source = hit?.Source;
        return source switch
        {
            null => null,
            JsonElement element => element,
            _ => JsonSerializer.SerializeToElement(source)
        };
    }

    static IReadOnlyList<(string Key, long DocCount, IReadOnlyDictionary<string, IAggregate> Aggregations)> TermsBuckets(IAggregate aggregate) => aggregate switch
    {
        StringTermsAggregate terms => [.. terms.Buckets.Select(bucket => (
            bucket.Key.ToString(),
            bucket.DocCount,
            (IReadOnlyDictionary<string, IAggregate>)bucket.Aggregations!)
        )],
        LongTermsAggregate terms => [.. terms.Buckets.Select(bucket => (
            bucket.Key.ToString(CultureInfo.InvariantCulture),
            bucket.DocCount,
            (IReadOnlyDictionary<string, IAggregate>)bucket.Aggregations!)
        )],
        DoubleTermsAggregate terms => [.. terms.Buckets.Select(bucket => (
            bucket.Key.ToString(CultureInfo.InvariantCulture),
            bucket.DocCount,
            (IReadOnlyDictionary<string, IAggregate>)bucket.Aggregations!)
        )],
        _ => throw new InvalidOperationException("Aggregate is not a terms aggregate.")
    };
}
