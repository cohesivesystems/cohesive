namespace Cohesive.Relations.Model;

/// <summary>
/// Materialization strategy kind.
/// </summary>
public enum MaterializationStrategy
{
    /// <summary>Represents the on-demand materialization strategy.</summary>
    OnDemand = 0,
    
    /// <summary>Represents the async index materialization strategy.</summary>
    AsyncIndex = 1,
    
    /// <summary>Represents the change feed materialization strategy.</summary>
    ChangeFeed = 2,
    
    /// <summary>Represents the snapshot materialization strategy.</summary>
    Snapshot = 3
}

/// <summary>
/// Materialization freshness semantics.
/// </summary>
public enum FreshnessPolicy
{
    /// <summary>Represents the strong option.</summary>
    Strong = 0,
    
    /// <summary>Represents the eventual option.</summary>
    Eventual = 1,
    
    /// <summary>Represents the bounded staleness option.</summary>
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
