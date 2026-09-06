using System.Text;

namespace Cohesive.Adapters.Sql;

/// <summary>One SQL ordering term shared by result ordering and window construction.</summary>
public sealed record SqlOrdering
{
    /// <summary>Creates an immutable ordering term with explicit null placement.</summary>
    /// <param name="expression">Expression evaluated as the ordering key.</param>
    /// <param name="direction">Ascending or descending comparison.</param>
    /// <param name="nullPlacement">Placement of SQL null, independent of direction.</param>
    /// <exception cref="ArgumentNullException">The expression is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Direction or null placement is not defined.</exception>
    public SqlOrdering(SqlExpression expression,
        SqlSortDirection direction = SqlSortDirection.Ascending,
        SqlNullPlacement nullPlacement = SqlNullPlacement.Last)
    {
        ArgumentNullException.ThrowIfNull(expression);
        if (!Enum.IsDefined(direction))
            throw new ArgumentOutOfRangeException(nameof(direction), direction, "Unsupported SQL sort direction.");
        if (!Enum.IsDefined(nullPlacement))
            throw new ArgumentOutOfRangeException(nameof(nullPlacement), nullPlacement, "Unsupported SQL null placement.");
        Expression = expression;
        Direction = direction;
        NullPlacement = nullPlacement;
    }

    /// <summary>Expression evaluated as the ordering key.</summary>
    public SqlExpression Expression { get; }
    /// <summary>Direction of comparison.</summary>
    public SqlSortDirection Direction { get; }
    /// <summary>Placement of SQL null, independent of direction.</summary>
    public SqlNullPlacement NullPlacement { get; }

    internal void WriteTo(SqlRenderContext context, StringBuilder builder)
    {
        Expression.WriteTo(context, builder);
        builder.Append(Direction == SqlSortDirection.Ascending ? " ASC" : " DESC");
        builder.Append(NullPlacement == SqlNullPlacement.First ? " NULLS FIRST" : " NULLS LAST");
    }
}
