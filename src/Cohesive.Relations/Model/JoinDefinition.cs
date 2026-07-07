namespace Cohesive.Relations.Model;

/// <summary>
/// Join semantics.
/// </summary>
public enum JoinKind
{
    Inner = 0,
    Left = 1,
    Right = 2,
    Full = 3
}

/// <summary>
/// Semantic join definition over relation sources.
/// </summary>
public sealed record JoinDefinition
{
    /// <summary>
    /// Creates a semantic join definition.
    /// </summary>
    public JoinDefinition(
        SourceAlias left,
        SourceAlias right,
        JoinKind kind,
        Expr on
    )
    {
        Left = Guard.RequireNotNull(left);
        Right = Guard.RequireNotNull(right);
        Kind = kind;
        On = Guard.RequireNotNull(on);
    }

    /// <summary>
    /// Left source alias.
    /// </summary>
    public SourceAlias Left { get; init; }

    /// <summary>
    /// Right source alias.
    /// </summary>
    public SourceAlias Right { get; init; }

    /// <summary>
    /// Join type.
    /// </summary>
    public JoinKind Kind { get; init; }

    /// <summary>
    /// Join predicate.
    /// </summary>
    public Expr On { get; init; }
}
