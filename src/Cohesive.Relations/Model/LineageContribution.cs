using Cohesive.Model;

namespace Cohesive.Relations.Model;

/// <summary>
/// One lineage contribution from expression evaluation.
/// </summary>
public sealed record LineageContribution
{
    /// <summary>
    /// Creates a lineage contribution.
    /// </summary>
    public LineageContribution(
        string nodeId,
        IReadOnlyList<FieldPath> sourcePaths,
        Expr expression,
        string? reason = null)
    {
        NodeId = Guard.RequireNotNullOrWhiteSpace(nodeId);
        SourcePaths = Guard.RequireNotNull(sourcePaths);
        Expression = Guard.RequireNotNull(expression);
        Reason = reason;
    }

    /// <summary>
    /// Stable IR node id.
    /// </summary>
    public string NodeId { get; init; }

    /// <summary>
    /// Referenced source paths.
    /// </summary>
    public IReadOnlyList<FieldPath> SourcePaths { get; init; }

    /// <summary>
    /// Source expression that produced the value.
    /// </summary>
    public Expr Expression { get; init; }

    /// <summary>
    /// Optional user-facing reason text.
    /// </summary>
    public string? Reason { get; init; }
}
