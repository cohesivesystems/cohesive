using Cohesive.Model;
using Cohesive.Model.Expressions;

namespace Cohesive.Assurance;

/// <summary>The initial predicate capability boundary; analysis does not evaluate claims.</summary>
public static class AssurancePredicates
{
    /// <summary>Stable version of the operation allowance.</summary>
    public const string ProfileId = "cohesive-assurance-predicates/v1";

    /// <summary>Allowed operations, using core capability identities.</summary>
    public static ExprCapabilityProfile Capabilities { get; } = new(
    [
        ExprCapabilities.Binding, ExprCapabilities.Field, ExprCapabilities.NestedFieldPath,
        ExprCapabilities.Parameter, ExprCapabilities.Constant, ExprCapabilities.TypedField,
        ExprCapabilities.TypedLiteral, ExprCapabilities.CurrentItem, ExprCapabilities.Conditional,
        ExprCapabilities.ForUnary(UnaryOperator.Not),
        .. new[] { BinaryOperator.And, BinaryOperator.Or, BinaryOperator.Eq, BinaryOperator.Ne,
            BinaryOperator.Lt, BinaryOperator.Le, BinaryOperator.Gt, BinaryOperator.Ge,
            BinaryOperator.Add, BinaryOperator.Sub, BinaryOperator.Mul, BinaryOperator.Div }
            .Select(ExprCapabilities.ForBinary),
        .. new[] { ExprFunctionNames.All, ExprFunctionNames.Any, ExprFunctionNames.Count, ExprFunctionNames.Sum }
            .Select(ExprCapabilities.ForFunction)
    ]);

    /// <summary>Validates a Boolean predicate using shared expression semantics.</summary>
    /// <param name="id">Stable semantic expression site.</param>
    /// <param name="predicate">Derived predicate expression.</param>
    /// <param name="scope">Available exact binding contracts.</param>
    /// <returns>Structured validation and expression requirements.</returns>
    /// <exception cref="ArgumentNullException">Predicate or scope is null.</exception>
    /// <exception cref="ArgumentException">The site identity is uninitialized.</exception>
    public static ExprAnalysisResult Analyze(ExprSiteId id, Expr predicate, ExprScope scope) =>
        ExprAnalyzer.Analyze(new(id, predicate, scope, ExprExpectation.Boolean, Capabilities,
            diagnosticLocation: "/predicate"));
}
