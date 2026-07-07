namespace Cohesive.Relations.Model;

/// <summary>
/// Materialization strategy kind.
/// </summary>
public enum MaterializationStrategy
{
    OnDemand = 0,
    AsyncIndex = 1,
    ChangeFeed = 2,
    Snapshot = 3
}

/// <summary>
/// Materialization freshness semantics.
/// </summary>
public enum FreshnessPolicy
{
    Strong = 0,
    Eventual = 1,
    BoundedStaleness = 2
}

/// <summary>
/// Materialization metadata for a relation.
/// </summary>
public sealed record MaterializationSpec
{
    /// <summary>
    /// Creates materialization metadata.
    /// </summary>
    public MaterializationSpec(
        bool isEnabled,
        MaterializationStrategy strategy,
        FreshnessPolicy freshness,
        bool allowCodegen
        )
    {
        IsEnabled = isEnabled;
        Strategy = strategy;
        Freshness = freshness;
        AllowCodegen = allowCodegen;
    }

    /// <summary>
    /// True if this relation should be materialized.
    /// </summary>
    public bool IsEnabled { get; init; }

    /// <summary>
    /// Preferred materialization strategy.
    /// </summary>
    public MaterializationStrategy Strategy { get; init; }

    /// <summary>
    /// Freshness policy.
    /// </summary>
    public FreshnessPolicy Freshness { get; init; }

    /// <summary>
    /// True when adapters may generate code for materialization.
    /// </summary>
    public bool AllowCodegen { get; init; }
}
