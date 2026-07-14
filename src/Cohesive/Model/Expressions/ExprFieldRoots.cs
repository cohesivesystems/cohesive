namespace Cohesive.Model.Expressions;

/// <summary>Reserved root names with expression-language semantics.</summary>
public static class ExprFieldRoots
{
    /// <summary>
    /// Root used by <see cref="FieldExpr"/> paths evaluated against the current item of a scoped function argument.
    /// </summary>
    public const string CurrentItem = "item";
}
