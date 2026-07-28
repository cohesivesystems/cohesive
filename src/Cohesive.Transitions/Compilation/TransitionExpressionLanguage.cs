using System.Collections.Immutable;
using Cohesive.Model.Expressions;

namespace Cohesive.Transitions.Compilation;

/// <summary>
/// Canonical restricted expression-language closure accepted by Transition v1 compilation and reference execution.
/// </summary>
public static class TransitionExpressionLanguage
{
    internal static ImmutableHashSet<string> SupportedFunctionNames { get; } =
        ImmutableHashSet.Create(
            StringComparer.Ordinal,
            ExprFunctionNames.Contains,
            ExprFunctionNames.Count,
            ExprFunctionNames.EndsWith,
            ExprFunctionNames.StartsWith,
            ExprFunctionNames.TextContains,
            ExprFunctionNames.Object,
            ExprFunctionNames.Select,
            ExprFunctionNames.Append,
            ExprFunctionNames.AppendRange,
            ExprFunctionNames.InsertAt,
            ExprFunctionNames.InsertRangeAt,
            ExprFunctionNames.Concat,
            ExprFunctionNames.Sum,
            ExprFunctionNames.Min,
            ExprFunctionNames.Max,
            ExprFunctionNames.Avg,
            ExprFunctionNames.Any,
            ExprFunctionNames.All);

    /// <summary>
    /// Gets the exact pure capabilities admitted by Transition v1. Ambient identity, source-set, grouping, and join
    /// operations are intentionally excluded.
    /// </summary>
    public static ExprCapabilityProfile Capabilities { get; } = new(
    [
        ExprCapabilities.Binding,
        ExprCapabilities.Field,
        ExprCapabilities.NestedFieldPath,
        ExprCapabilities.Parameter,
        ExprCapabilities.Constant,
        ExprCapabilities.TypedField,
        ExprCapabilities.TypedLiteral,
        ExprCapabilities.Conditional,
        ExprCapabilities.CurrentItem,
        ExprCapabilities.ForUnary(UnaryOperator.Not),
        ExprCapabilities.ForBinary(BinaryOperator.Eq),
        ExprCapabilities.ForBinary(BinaryOperator.Ne),
        ExprCapabilities.ForBinary(BinaryOperator.Gt),
        ExprCapabilities.ForBinary(BinaryOperator.Ge),
        ExprCapabilities.ForBinary(BinaryOperator.Lt),
        ExprCapabilities.ForBinary(BinaryOperator.Le),
        ExprCapabilities.ForBinary(BinaryOperator.And),
        ExprCapabilities.ForBinary(BinaryOperator.Or),
        ExprCapabilities.ForBinary(BinaryOperator.Add),
        ExprCapabilities.ForBinary(BinaryOperator.Sub),
        ExprCapabilities.ForBinary(BinaryOperator.Mul),
        ExprCapabilities.ForBinary(BinaryOperator.Div),
        ExprCapabilities.ForAggregate(AggregateOperator.Count),
        ExprCapabilities.ForAggregate(AggregateOperator.Sum),
        ExprCapabilities.ForAggregate(AggregateOperator.Min),
        ExprCapabilities.ForAggregate(AggregateOperator.Max),
        ExprCapabilities.ForAggregate(AggregateOperator.Any),
        ExprCapabilities.ForAggregate(AggregateOperator.All),
        ExprCapabilities.ForAggregate(AggregateOperator.Average),
        .. SupportedFunctionNames.Select(ExprCapabilities.ForFunction)
    ]);
}
