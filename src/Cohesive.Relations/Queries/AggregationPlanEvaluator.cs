using System.Globalization;
using System.Text.Json;
using Cohesive.Relations.Model;

namespace Cohesive.Relations.Queries;

/// <summary>
/// In-memory evaluator for relation aggregation plans.
/// </summary>
public static class AggregationPlanEvaluator
{
    static readonly AggregationBackendCapability Capabilities = new(
        Backend: "In-memory",
        Features: AggregationFeatureSet.CountIf
                  | AggregationFeatureSet.FilteredMetric
                  | AggregationFeatureSet.TopHit
                  | AggregationFeatureSet.DateHistogram
                  | AggregationFeatureSet.Histogram
                  | AggregationFeatureSet.MetricOrder,
        PredicateCapabilities: new(
            QueryCapability.Equality
            | QueryCapability.Prefix
            | QueryCapability.Suffix
            | QueryCapability.Contains
            | QueryCapability.FullText
            | QueryCapability.Exists
            | QueryCapability.NumberRange
            | QueryCapability.DateRange
            | QueryCapability.SetMembership
            | QueryCapability.NestedAny
            | QueryCapability.GeoDistance
            | QueryCapability.ScopedFields
            | QueryCapability.Negation
            | QueryCapability.Aggregation
            | QueryCapability.CaseInsensitiveStringComparison));

    /// <summary>
    /// Evaluates an aggregation plan against observations in memory.
    /// </summary>
    public static IReadOnlyDictionary<string, AggregationResult> Evaluate(
        IEnumerable<Observation> observations,
        AggregationPlan plan
        )
    {
        ArgumentNullException.ThrowIfNull(observations);
        ArgumentNullException.ThrowIfNull(plan);
        plan.Validate(Capabilities);

        var filtered = observations
            .Where(observation => plan.Predicate is null || EntityPredicateEvaluator.Evaluate(observation, plan.Predicate))
            .ToArray();

        Dictionary<string, AggregationResult> results = new(StringComparer.Ordinal);
        foreach (var root in plan.Roots)
        {
            results[root.Name] = root.Root switch
            {
                GlobalAggregationPlan global => EvaluateGlobal(global, root.Statistics, filtered),
                TermsGroupAggregationPlan terms => EvaluateTerms(terms, root.Statistics, filtered),
                HistogramAggregationPlan histogram => EvaluateHistogram(histogram, root.Statistics, filtered),
                DateHistogramAggregationPlan dateHistogram => EvaluateDateHistogram(dateHistogram, root.Statistics, filtered),
                _ => throw new InvalidOperationException($"Unknown aggregation root type '{root.Root.GetType().Name}'.")
            };
        }

        return results;
    }

    static AggregationResult EvaluateGlobal(
        GlobalAggregationPlan root,
        IReadOnlyList<AggregationStatistic> statistics,
        IReadOnlyList<Observation> observations
        ) =>
        new SingletonAggregationResult(EvaluateRow(root.Name, observations, statistics));

    static AggregationResult EvaluateTerms(
        TermsGroupAggregationPlan root,
        IReadOnlyList<AggregationStatistic> statistics,
        IReadOnlyList<Observation> observations
        )
    {
        var buckets = observations
            .Select(observation => TryCreateTermsKey(observation, root.GroupByField, out var key) ? (HasKey: true, Key: key, Observation: observation) : (HasKey: false, Key: "", Observation: observation))
            .Where(static item => item.HasKey)
            .GroupBy(static item => item.Key, static item => item.Observation, StringComparer.Ordinal)
            .Select(group => EvaluateRow(group.Key, [.. group], statistics));

        return new BucketedAggregationResult(ApplyBucketWindow(buckets, root.Order, root.Take));
    }

    static AggregationResult EvaluateHistogram(
        HistogramAggregationPlan root,
        IReadOnlyList<AggregationStatistic> statistics,
        IReadOnlyList<Observation> observations
        )
    {
        var interval = root.Interval;
        var offset = root.Offset ?? 0d;
        var buckets = observations
            .Select(observation => TryResolveField(observation, root.Field, out var value, out var exists)
                                   && exists
                                   && value.TryGetDouble(out var number)
                ? (HasKey: true, Key: FormatBucketKey(Math.Floor((number - offset) / interval) * interval + offset), Observation: observation)
                : (HasKey: false, Key: "", Observation: observation))
            .Where(static item => item.HasKey)
            .GroupBy(static item => item.Key, static item => item.Observation, StringComparer.Ordinal)
            .Select(group => EvaluateRow(group.Key, [.. group], statistics));

        return new BucketedAggregationResult(ApplyBucketWindow(buckets, root.Order, root.Take));
    }

    static AggregationResult EvaluateDateHistogram(
        DateHistogramAggregationPlan root,
        IReadOnlyList<AggregationStatistic> statistics,
        IReadOnlyList<Observation> observations
        )
    {
        var buckets = observations
            .Select(observation => TryResolveField(observation, root.TimestampField, out var value, out var exists)
                                   && exists
                                   && value.TryGetDateTimeOffset(out var timestamp)
                ? (HasKey: true, Key: FormatDateBucketKey(timestamp, root.CalendarInterval, root.TimeZone), Observation: observation)
                : (HasKey: false, Key: "", Observation: observation))
            .Where(static item => item.HasKey)
            .GroupBy(static item => item.Key, static item => item.Observation, StringComparer.Ordinal)
            .Select(group => EvaluateRow(group.Key, [.. group], statistics));

        return new BucketedAggregationResult(ApplyBucketWindow(buckets, root.Order, root.Take));
    }

    static AggregationResultRow EvaluateRow(
        string key,
        IReadOnlyList<Observation> observations,
        IReadOnlyList<AggregationStatistic> statistics
        )
    {
        Dictionary<string, double?> values = new(StringComparer.Ordinal);
        Dictionary<string, JsonElement?> samples = new(StringComparer.Ordinal);
        foreach (var statistic in statistics)
        {
            switch (statistic)
            {
                case CountAggregationStatistic:
                    values[statistic.Name] = observations.Count;
                    break;
                case CountIfAggregationStatistic countIf:
                    values[statistic.Name] = observations.Count(observation => EntityPredicateEvaluator.Evaluate(observation, countIf.Filter));
                    break;
                case SumAggregationStatistic sum:
                    values[statistic.Name] = Sum(observations, sum.Field);
                    break;
                case SumIfAggregationStatistic sumIf:
                    values[statistic.Name] = Sum(
                        observations.Where(observation => EntityPredicateEvaluator.Evaluate(observation, sumIf.Filter)),
                        sumIf.Field);
                    break;
                case TopHitAggregationStatistic topHit:
                    samples[statistic.Name] = ReadTopHit(observations, topHit);
                    break;
                default:
                    throw new InvalidOperationException($"Unknown statistic type '{statistic.GetType().Name}'.");
            }
        }

        return new(key, observations.Count, values, samples);
    }

    static IReadOnlyList<AggregationResultRow> ApplyBucketWindow(
        IEnumerable<AggregationResultRow> rows,
        OrderSpec? order,
        int? take
        )
    {
        if (order is not null)
        {
            rows = order.Descending
                ? rows.OrderByDescending(row => ReadOrderValue(row, order.StatisticName)).ThenBy(static row => row.Key, StringComparer.Ordinal)
                : rows.OrderBy(row => ReadOrderValue(row, order.StatisticName)).ThenBy(static row => row.Key, StringComparer.Ordinal);
        }
        else
        {
            rows = rows.OrderBy(static row => row.Key, StringComparer.Ordinal);
        }

        if (take is { } limit)
            rows = rows.Take(limit);

        return [.. rows];
    }

    static double ReadOrderValue(AggregationResultRow row, string statisticName)
    {
        if (string.Equals(statisticName, "count", StringComparison.Ordinal))
            return row.DocCount;

        return row.Statistics.TryGetValue(statisticName, out var value) && value is not null
            ? value.Value
            : double.MinValue;
    }

    static double? Sum(IEnumerable<Observation> observations, FieldPath field)
    {
        double total = 0;
        var hasValue = false;
        foreach (var observation in observations)
        {
            if (!TryResolveField(observation, field, out var value, out var exists) || !exists || !value.TryGetDouble(out var number))
                continue;

            total += number;
            hasValue = true;
        }

        return hasValue ? total : null;
    }

    static JsonElement? ReadTopHit(IReadOnlyList<Observation> observations, TopHitAggregationStatistic statistic)
    {
        var observation = observations.FirstOrDefault();
        if (observation is null)
            return null;

        Dictionary<string, ObservationValue> fields = new(StringComparer.Ordinal);
        foreach (var field in statistic.Fields)
        {
            if (!TryResolveField(observation, field, out var value, out var exists) || !exists)
                continue;

            fields[field.ToString()] = value;
        }

        return JsonSerializer.SerializeToElement(fields);
    }

    static bool TryCreateTermsKey(Observation observation, FieldPath field, out string key)
    {
        if (TryResolveField(observation, field, out var value, out var exists)
            && exists
            && value.Kind is not ObservationValueKind.Null and not ObservationValueKind.Undefined)
        {
            key = value.ToScalarString(CultureInfo.InvariantCulture, ObservationBytesJsonEncoding.Base64String) ?? "";
            return true;
        }

        key = string.Empty;
        return false;
    }

    static bool TryResolveField(
        Observation observation,
        FieldPath field,
        out ObservationValue value,
        out bool exists
        )
    {
        ArgumentNullException.ThrowIfNull(observation);
        return TryResolveField(ObservationValue.FromObject(observation.Fields), field, out value, out exists);
    }

    static bool TryResolveField(
        ObservationValue current,
        FieldPath field,
        out ObservationValue value,
        out bool exists
        )
    {
        value = current;
        exists = true;

        foreach (var segment in field.Segments)
        {
            switch (segment.Kind)
            {
                case SegmentKind.Field:
                    if (value.Kind != ObservationValueKind.Object || value.Fields is null || !value.Fields.TryGetValue(segment.Segment!, out value))
                    {
                        value = default;
                        exists = false;
                        return true;
                    }

                    break;
                case SegmentKind.Element:
                    throw new NotSupportedException(
                        $"In-memory aggregation field evaluation does not support element segment '{field}'. Use a grouped scalar field or predicate filters for array elements.");
                default:
                    throw new InvalidOperationException($"Unsupported field-path segment kind '{segment.Kind}'.");
            }
        }

        return true;
    }

    static string FormatBucketKey(double value) =>
        value.ToString("G17", CultureInfo.InvariantCulture);

    static string FormatDateBucketKey(DateTimeOffset value, string calendarInterval, string? timeZone)
    {
        var timestamp = string.IsNullOrWhiteSpace(timeZone)
            ? value
            : TimeZoneInfo.ConvertTime(value, TimeZoneInfo.FindSystemTimeZoneById(timeZone));

        var trimmed = calendarInterval.Trim();
        if (string.Equals(trimmed, "1M", StringComparison.Ordinal))
            return new DateTimeOffset(timestamp.Year, timestamp.Month, 1, 0, 0, 0, timestamp.Offset).ToString("O", CultureInfo.InvariantCulture);

        var start = trimmed.ToLowerInvariant() switch
        {
            "second" or "1s" => new DateTimeOffset(timestamp.Year, timestamp.Month, timestamp.Day, timestamp.Hour, timestamp.Minute, timestamp.Second, timestamp.Offset),
            "minute" or "1m" => new DateTimeOffset(timestamp.Year, timestamp.Month, timestamp.Day, timestamp.Hour, timestamp.Minute, 0, timestamp.Offset),
            "hour" or "1h" => new DateTimeOffset(timestamp.Year, timestamp.Month, timestamp.Day, timestamp.Hour, 0, 0, timestamp.Offset),
            "day" or "1d" => new DateTimeOffset(timestamp.Year, timestamp.Month, timestamp.Day, 0, 0, 0, timestamp.Offset),
            "week" or "1w" => StartOfWeek(timestamp),
            "month" => new DateTimeOffset(timestamp.Year, timestamp.Month, 1, 0, 0, 0, timestamp.Offset),
            "quarter" or "1q" => new DateTimeOffset(timestamp.Year, ((timestamp.Month - 1) / 3 * 3) + 1, 1, 0, 0, 0, timestamp.Offset),
            "year" or "1y" => new DateTimeOffset(timestamp.Year, 1, 1, 0, 0, 0, timestamp.Offset),
            _ => throw new NotSupportedException($"Unsupported date histogram calendar interval '{calendarInterval}'.")
        };

        return start.ToString("O", CultureInfo.InvariantCulture);
    }

    static DateTimeOffset StartOfWeek(DateTimeOffset value)
    {
        var delta = ((int)value.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;
        var date = value.Date.AddDays(-delta);
        return new(date.Year, date.Month, date.Day, 0, 0, 0, value.Offset);
    }
}
