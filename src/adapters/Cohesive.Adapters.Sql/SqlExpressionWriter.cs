using System.Text;

namespace Cohesive.Adapters.Sql;

/// <summary>Scoped output for trusted dialect intrinsics, sharing the containing statement's parameter allocation.</summary>
/// <remarks>
/// Only the construction layer creates usable writers. A dialect must emit one complete expression with explicit
/// parentheses where precedence requires them. Write only trusted compiler-owned grammar through
/// <see cref="WriteSyntax"/>; write all values as constant or runtime parameter expressions through
/// <see cref="WriteExpression"/>. This stack-only view does not expose the mutable rendering context or buffer.
/// </remarks>
public readonly ref struct SqlExpressionWriter
{
    readonly SqlRenderContext? context;
    readonly StringBuilder? builder;

    internal SqlExpressionWriter(SqlRenderContext context, StringBuilder builder)
    {
        this.context = context;
        this.builder = builder;
    }

    SqlRenderContext Context => context
        ?? throw new InvalidOperationException("A SQL expression writer is only valid during dialect rendering.");

    /// <summary>Appends trusted target grammar exactly as supplied.</summary>
    /// <param name="syntax">Compiler-owned syntax; never application values, identifiers, or parameter markers.</param>
    /// <exception cref="ArgumentNullException">The syntax is null.</exception>
    /// <exception cref="InvalidOperationException">The writer is default rather than supplied by the construction layer.</exception>
    public void WriteSyntax(string syntax)
    {
        ArgumentNullException.ThrowIfNull(syntax);
        _ = Context;
        builder!.Append(syntax);
    }

    /// <summary>Renders an operand using the current dialect and containing statement's parameter slots.</summary>
    /// <param name="expression">Operand to render, including nested intrinsics.</param>
    /// <exception cref="ArgumentNullException">The expression is null.</exception>
    /// <exception cref="InvalidOperationException">The writer is default rather than supplied by the construction layer.</exception>
    /// <exception cref="ArgumentException">An operand violates target identifier, value, or intrinsic arity constraints.</exception>
    /// <exception cref="SqlConstructionException">The dialect does not support an operand construct.</exception>
    public void WriteExpression(SqlExpression expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        expression.WriteTo(Context, builder!);
    }

    /// <summary>Renders one safely escaped identifier using the current dialect's constraints.</summary>
    /// <param name="identifier">Unquoted target identifier.</param>
    /// <exception cref="InvalidOperationException">The writer is default rather than supplied by the construction layer.</exception>
    /// <exception cref="ArgumentException">The identifier is default or outside the target domain.</exception>
    public void WriteIdentifier(SqlIdentifier identifier) => identifier.WriteQuoted(Context, builder!);
}
