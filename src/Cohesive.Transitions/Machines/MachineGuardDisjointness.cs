using System.Collections.Immutable;
using Cohesive.Model.Expressions;

namespace Cohesive.Transitions.Machines;

// Registration-time evidence only: never removes runtime guard validation. A conjunction's
// equality constraints are necessary conditions even when its other terms are unsupported.
internal static class MachineGuardDisjointness
{
    internal static bool IsTrue(Expr expression) =>
        expression switch
        {
            ConstantExpr { Value.Kind: ObservationValueKind.Bool } c => c.Value.GetBoolean(),
            LiteralExpr { Value.Kind: ObservationValueKind.Bool } c => c.Value.GetBoolean(),
            _ => false
        };

    internal static bool ProvesExclusive(ImmutableArray<Expr> guards)
    {
        var constraints = guards.Select(Constraints).ToArray();
        for (var i = 0; i < constraints.Length; i++)
            for (var j = i + 1; j < constraints.Length; j++)
                if (!Contradicts(constraints[i], constraints[j])) return false;
        return true;
    }

    static bool Contradicts(List<(FieldExpr Field, ObservationValue Value)> left,
        List<(FieldExpr Field, ObservationValue Value)> right)
    {
        foreach (var l in left)
            foreach (var r in right)
                if (l.Field == r.Field && l.Value.Kind == r.Value.Kind
                    && !ObservationValueSemantics.Equals(l.Value, r.Value)) return true;
        return false;
    }

    static List<(FieldExpr Field, ObservationValue Value)> Constraints(Expr expression)
    {
        var result = new List<(FieldExpr, ObservationValue)>();
        Visit(expression);
        return result;

        void Visit(Expr node)
        {
            if (node is BinaryExpr { Operator: BinaryOperator.And } conjunction)
            {
                Visit(conjunction.Left);
                Visit(conjunction.Right);
            }
            else if (node is BinaryExpr { Operator: BinaryOperator.Eq } equality)
            {
                Add(equality.Left, equality.Right);
                Add(equality.Right, equality.Left);
            }
        }

        void Add(Expr field, Expr constant)
        {
            if (field is not FieldExpr path) return;
            ObservationValue? value = constant switch
            {
                ConstantExpr c => c.Value,
                LiteralExpr c => c.Value,
                _ => null
            };
            // Keep proof within exact scalar equality; no null, numeric cross-kind, collection,
            // ordering, disjunction or coercion assumptions are made here.
            if (value is { Kind: ObservationValueKind.String or ObservationValueKind.Int64 or ObservationValueKind.Bool } scalar)
                result.Add((path, scalar));
        }
    }
}
