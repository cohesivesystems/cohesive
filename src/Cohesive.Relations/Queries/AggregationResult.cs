using System.Text.Json;

namespace Cohesive.Relations.Queries;

/// <summary>
/// Result for a single aggregation root.
/// </summary>
public abstract record AggregationResult
{
    /// <summary>
    /// Tries to read the result as a singleton.
    /// </summary>
    public SingletonAggregationResult? TrySingleton() => TryFold(onSingleton: static result => result);

    /// <summary>
    /// Reads the result as a singleton.
    /// </summary>
    public SingletonAggregationResult Singleton() =>
        TrySingleton() ?? throw new InvalidOperationException($"Aggregation result is not a singleton: {GetType().Name}.");

    /// <summary>
    /// Tries to read the result as bucketed rows.
    /// </summary>
    public BucketedAggregationResult? TryBucketed() => TryFold(onBucketed: static result => result);

    /// <summary>
    /// Reads the result as bucketed rows.
    /// </summary>
    public BucketedAggregationResult Bucketed() =>
        TryBucketed() ?? throw new InvalidOperationException($"Aggregation result is not bucketed: {GetType().Name}.");

    /// <summary>
    /// Returns every result row.
    /// </summary>
    public IReadOnlyList<AggregationResultRow> GetRows() =>
        Fold(static singleton => [singleton.Row], static bucketed => bucketed.Rows);

    TResult? TryFold<TResult>(
        Func<SingletonAggregationResult, TResult?>? onSingleton = null,
        Func<BucketedAggregationResult, TResult?>? onBucketed = null
    ) => this switch
    {
        SingletonAggregationResult singleton when onSingleton is not null => onSingleton(singleton),
        BucketedAggregationResult bucketed when onBucketed is not null => onBucketed(bucketed),
        _ => default
    };

    TResult Fold<TResult>(
        Func<SingletonAggregationResult, TResult> onSingleton,
        Func<BucketedAggregationResult, TResult> onBucketed
    ) =>
        TryFold(onSingleton, onBucketed)
        ?? throw new InvalidOperationException($"Unknown aggregation result type '{GetType().Name}'.");
}

/// <summary>
/// Aggregation result row containing computed statistics.
/// </summary>
/// <param name="Key">Bucket key.</param>
/// <param name="DocCount">Number of records in the bucket.</param>
/// <param name="Statistics">Numeric statistics by name.</param>
/// <param name="Samples">Sample documents by statistic name.</param>
public sealed record AggregationResultRow(
    string Key,
    long DocCount,
    IReadOnlyDictionary<string, double?> Statistics,
    IReadOnlyDictionary<string, JsonElement?> Samples
);

/// <summary>
/// Bucketed aggregation result.
/// </summary>
/// <param name="Rows">Bucket rows.</param>
public sealed record BucketedAggregationResult(IReadOnlyList<AggregationResultRow> Rows) : AggregationResult;

/// <summary>
/// Singleton aggregation result.
/// </summary>
/// <param name="Row">Singleton row.</param>
public sealed record SingletonAggregationResult(AggregationResultRow Row) : AggregationResult;
