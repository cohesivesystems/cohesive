namespace Cohesive.Relations.Model;

/// <summary>
/// Join semantics.
/// </summary>
public enum JoinKind
{
    /// <summary>An inner join.</summary>
    Inner = 0,

    /// <summary>A left outer join.</summary>
    Left = 1,

    /// <summary>A right outer join.</summary>
    Right = 2,

    /// <summary>A full outer join.</summary>
    Full = 3
}
