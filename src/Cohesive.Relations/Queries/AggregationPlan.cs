namespace Cohesive.Relations.Queries;

/// <summary>
/// Reference to a computed statistic.
/// </summary>
/// <param name="Name">Statistic name.</param>
public sealed record StatisticReference(string Name);

/// <summary>
/// Bucket ordering by statistic.
/// </summary>
/// <param name="StatisticName">Statistic used for ordering.</param>
/// <param name="Descending">Whether to sort descending.</param>
public sealed record OrderSpec(string StatisticName, bool Descending);

/// <summary>
/// Named aggregation root and its computed statistics.
/// </summary>
/// <param name="Name">Aggregation root name.</param>
/// <param name="Root">Bucket shape.</param>
/// <param name="Statistics">Statistics computed for each bucket.</param>
public sealed record AggregationRoot(string Name, AggregationPlanRoot Root, IReadOnlyList<AggregationStatistic> Statistics);

/// <summary>
/// Aggregation plan compiled and executed by query backends.
/// </summary>
/// <param name="Roots">Named aggregation roots.</param>
/// <param name="Predicate">Optional predicate constraining the result set being aggregated.</param>
public sealed record AggregationPlan(
    IReadOnlyList<AggregationRoot> Roots,
    EntityPredicate? Predicate = null
    )
{
    /// <summary>
    /// Validates that the plan can execute on a backend with the supplied capabilities.
    /// </summary>
    /// <exception cref="AggregationPlanValidationException">The plan is invalid or the backend does not support it.</exception>
    public void Validate(AggregationBackendCapability capability)
    {
        ArgumentNullException.ThrowIfNull(capability);

        if (Roots.Count == 0)
            throw new AggregationPlanValidationException("Aggregation plan requires at least one root.");

        HashSet<string> rootNames = new(StringComparer.Ordinal);
        foreach (var root in Roots)
            ValidateRoot(root, capability, rootNames);

        ValidatePredicate(Predicate, capability, $"plan predicate for backend '{capability.Backend}'");
    }

    static void ValidateRoot(AggregationRoot root, AggregationBackendCapability capability, HashSet<string> rootNames)
    {
        if (string.IsNullOrWhiteSpace(root.Name))
            throw new AggregationPlanValidationException("Aggregation root name must not be null, empty, or whitespace.");

        if (!rootNames.Add(root.Name))
            throw new AggregationPlanValidationException($"Duplicate aggregation root name '{root.Name}'.");

        capability.EnsureSupports(root.Root.RequiredFeatures, $"execute aggregation root '{root.Name}'");

        switch (root.Root)
        {
            case TermsGroupAggregationPlan { Take: <= 0 }:
            case DateHistogramAggregationPlan { Take: <= 0 }:
            case HistogramAggregationPlan { Take: <= 0 }:
                throw new AggregationPlanValidationException($"Aggregation root '{root.Name}' has a non-positive bucket limit.");
            case DateHistogramAggregationPlan date when string.IsNullOrWhiteSpace(date.CalendarInterval):
                throw new AggregationPlanValidationException($"Date histogram root '{root.Name}' requires a calendar interval.");
            case HistogramAggregationPlan histogram when histogram.Interval <= 0:
                throw new AggregationPlanValidationException($"Histogram root '{root.Name}' requires a positive interval.");
        }

        if (root.Statistics.Count == 0)
            throw new AggregationPlanValidationException($"Aggregation root '{root.Name}' requires at least one statistic.");

        HashSet<string> statisticNames = new(StringComparer.Ordinal);
        foreach (var statistic in root.Statistics)
            ValidateStatistic(root, statistic, capability, statisticNames);

        var order = root.Root switch
        {
            TermsGroupAggregationPlan terms => terms.Order,
            DateHistogramAggregationPlan date => date.Order,
            HistogramAggregationPlan histogram => histogram.Order,
            _ => null
        };
        if (order is not null && !statisticNames.Contains(order.StatisticName))
            throw new AggregationPlanValidationException($"Aggregation root '{root.Name}' orders by unknown statistic '{order.StatisticName}'.");
    }

    static void ValidateStatistic(
        AggregationRoot root,
        AggregationStatistic aggregationStatistic,
        AggregationBackendCapability capability,
        HashSet<string> statisticNames
        )
    {
        if (string.IsNullOrWhiteSpace(aggregationStatistic.Name))
            throw new AggregationPlanValidationException($"Aggregation root '{root.Name}' has a statistic with an empty name.");

        if (!statisticNames.Add(aggregationStatistic.Name))
            throw new AggregationPlanValidationException(
                $"Aggregation root '{root.Name}' has duplicate statistic name '{aggregationStatistic.Name}'.");

        capability.EnsureSupports(aggregationStatistic.RequiredFeatures, $"execute statistic '{root.Name}.{aggregationStatistic.Name}'");
        ValidatePredicate(aggregationStatistic.Filter, capability, $"statistic filter '{root.Name}.{aggregationStatistic.Name}'");

        if (aggregationStatistic is TopHitAggregationStatistic { Size: <= 0 })
            throw new AggregationPlanValidationException($"Top-hit statistic '{root.Name}.{aggregationStatistic.Name}' requires a positive size.");
    }

    static void ValidatePredicate(EntityPredicate? predicate, AggregationBackendCapability capability, string operation)
    {
        if (predicate is null)
            return;

        capability.PredicateCapabilities.EnsureSupports(
            QueryCapabilityInspector.GetRequiredCapabilities(predicate).Value,
            operation: operation
            );
    }
}

/// <summary>
/// Aggregation validation failure.
/// </summary>
public sealed class AggregationPlanValidationException : Exception
{
    /// <summary>
    /// Creates a validation failure.
    /// </summary>
    public AggregationPlanValidationException(string message)
        : base(message)
    {
    }
}
