namespace Cohesive.Relations.IR;

/// <summary>
/// Stable discriminator property names and values used by persisted relation/query IR.
/// </summary>
public static class RelationQueryWireNames
{
    /// <summary>Definition discriminator property.</summary>
    public const string DefinitionDiscriminator = "$definition";
    /// <summary>Relation definition discriminator value.</summary>
    public const string RelationDefinition = "relation";
    /// <summary>Query definition discriminator value.</summary>
    public const string QueryDefinition = "query";

    /// <summary>Logical node discriminator property.</summary>
    public const string NodeDiscriminator = "$node";
    /// <summary>Source node discriminator value.</summary>
    public const string SourceNode = "source";
    /// <summary>Filter node discriminator value.</summary>
    public const string FilterNode = "filter";
    /// <summary>Relationship traversal node discriminator value.</summary>
    public const string TraverseRelationshipNode = "traverseRelationship";
    /// <summary>Explicit join node discriminator value.</summary>
    public const string JoinNode = "join";
    /// <summary>Temporal join node discriminator value.</summary>
    public const string TemporalJoinNode = "temporalJoin";
    /// <summary>Collection-expansion node discriminator value.</summary>
    public const string ExpandCollectionNode = "expandCollection";
    /// <summary>Projection node discriminator value.</summary>
    public const string ProjectNode = "project";
    /// <summary>Distinct node discriminator value.</summary>
    public const string DistinctNode = "distinct";
    /// <summary>Ordered representative-selection node discriminator value.</summary>
    public const string SelectRepresentativeNode = "selectRepresentative";
    /// <summary>Aggregate node discriminator value.</summary>
    public const string AggregateNode = "aggregate";
    /// <summary>Order node discriminator value.</summary>
    public const string OrderNode = "order";
    /// <summary>Page node discriminator value.</summary>
    public const string PageNode = "page";

    /// <summary>Temporal-join match discriminator property.</summary>
    public const string TemporalMatchDiscriminator = "$temporalMatch";
    /// <summary>Point-in-interval temporal match discriminator value.</summary>
    public const string TemporalPointInIntervalMatch = "pointInInterval";
    /// <summary>Interval-overlap temporal match discriminator value.</summary>
    public const string TemporalIntervalOverlapMatch = "intervalOverlap";

    /// <summary>Temporal-interval bound discriminator property.</summary>
    public const string TemporalBoundDiscriminator = "$temporalBound";
    /// <summary>Unbounded temporal-interval bound discriminator value.</summary>
    public const string UnboundedTemporalBound = "unbounded";
    /// <summary>Expression temporal-interval bound discriminator value.</summary>
    public const string ExpressionTemporalBound = "expression";

    /// <summary>Page-definition discriminator property.</summary>
    public const string PageDiscriminator = "$page";
    /// <summary>Offset page discriminator value.</summary>
    public const string OffsetPage = "offset";
    /// <summary>Keyset page discriminator value.</summary>
    public const string KeysetPage = "keyset";

    /// <summary>Query-result discriminator property.</summary>
    public const string ResultDiscriminator = "$result";
    /// <summary>Rows result discriminator value.</summary>
    public const string RowsResult = "rows";
    /// <summary>Aggregation result discriminator value.</summary>
    public const string AggregationResult = "aggregation";
}
