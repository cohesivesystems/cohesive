namespace Cohesive.Model.Expressions;

/// <summary>Shared path-sensitive presence and nullability facts for canonical short-circuit guards.</summary>
/// <remarks>
/// This analysis recognizes field equality/inequality, negation, true conjunctions and false disjunctions.
/// It does not execute expressions or infer schema constraints. Consumers own field resolution and the
/// lifetime of refined contracts; facts must remain local to the selected branch. A present field also
/// proves its containing binding is present, but does not prove other optional fields exist.
/// </remarks>
public static class ExprGuardRefinement
{
    /// <summary>Applies facts guaranteed when a predicate has the selected Boolean result.</summary>
    /// <param name="predicate">Canonical guard expression.</param>
    /// <param name="whenTrue">The branch result for which facts are required.</param>
    /// <param name="resolve">Resolves current field or parameter contracts, including facts already applied by this invocation.
    /// Returns null when unknown. Literal presence/nullability is determined from canonical values.</param>
    /// <param name="refine">Receives a field expression and its narrowed contract. Apply changes to branch-local state so
    /// subsequent resolver calls see them. Shape-field optionality must be retained when only binding presence is inferred.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static void Apply(Expr predicate, bool whenTrue, Func<Expr, ValueContract?> resolve, Action<Expr, ValueContract> refine)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        ArgumentNullException.ThrowIfNull(resolve);
        ArgumentNullException.ThrowIfNull(refine);
        Visit(predicate, whenTrue);

        void Visit(Expr expression, bool truth)
        {
            if (expression is UnaryExpr { Operator: UnaryOperator.Not } not)
            {
                Visit(not.Operand, !truth);
                return;
            }
            if (expression is not BinaryExpr binary) return;
            if ((binary.Operator == BinaryOperator.And && truth) || (binary.Operator == BinaryOperator.Or && !truth))
            {
                Visit(binary.Left, truth);
                Visit(binary.Right, truth);
                return;
            }
            if (binary.Operator is not (BinaryOperator.Eq or BinaryOperator.Ne)) return;
            var equal = truth == (binary.Operator == BinaryOperator.Eq);
            Operand(binary.Left, binary.Right, equal);
            Operand(binary.Right, binary.Left, equal);
        }

        ValueContract? Contract(Expr expression) => expression switch
        {
            ConstantExpr constant => Literal(constant.Value),
            LiteralExpr literal => Literal(literal.Value),
            _ => resolve(expression)
        };

        void Operand(Expr expression, Expr other, bool equal)
        {
            if (expression is not (FieldExpr or FieldRefExpr) || resolve(expression) is not { } value) return;
            var otherValue = Contract(other);
            var present = equal && otherValue?.Presence == FieldPresence.Required;
            var nonNull = present && otherValue?.Nullability == FieldNullability.NonNullable
                || !equal && IsNull(other) && value.Presence == FieldPresence.Required;
            if (present || nonNull)
                refine(expression, new(value.Type, value.Shape, value.Cardinality,
                    present ? FieldPresence.Required : value.Presence,
                    nonNull ? FieldNullability.NonNullable : value.Nullability));
        }
    }

    static bool IsNull(Expr expression) => expression is ConstantExpr { Value.Kind: ObservationValueKind.Null }
        or LiteralExpr { Value.Kind: ObservationValueKind.Null };

    static ValueContract Literal(ObservationValue value) => new(
        presence: value.Kind == ObservationValueKind.Undefined ? FieldPresence.Optional : FieldPresence.Required,
        nullability: value.Kind is ObservationValueKind.Null or ObservationValueKind.Undefined ? FieldNullability.Nullable : FieldNullability.NonNullable);
}
