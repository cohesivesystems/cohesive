namespace Cohesive.Relations.Queries;

/// <summary>
/// A free boolean expression tree whose leaves are atomic propositions of type <typeparamref name="T" />.
/// </summary>
public abstract record BoolExpr<T>
{
    /// <summary>
    /// Converts an atomic value to a boolean-expression leaf.
    /// </summary>
    public static implicit operator BoolExpr<T>(T term) => new Atom<T>(term);
}

/// <summary>
/// Boolean-expression helpers.
/// </summary>
public static class BoolExpr
{
    /// <summary>
    /// Creates a conjunction from atomic values.
    /// </summary>
    public static And<T> And<T>(IEnumerable<T> terms) =>
        new([..Guard.RequireNotNull(terms).Select(static term => new Atom<T>(term))]);
}

/// <summary>
/// Atomic boolean-expression node.
/// </summary>
public sealed record Atom<T>(T Term) : BoolExpr<T>;

/// <summary>
/// Conjunction node.
/// </summary>
public sealed record And<T>(IReadOnlyList<BoolExpr<T>> Terms) : BoolExpr<T>;

/// <summary>
/// Disjunction node.
/// </summary>
public sealed record Or<T>(IReadOnlyList<BoolExpr<T>> Terms) : BoolExpr<T>;

/// <summary>
/// Negation node.
/// </summary>
public sealed record Not<T>(BoolExpr<T> Term) : BoolExpr<T>;

/// <summary>
/// Boolean-expression normalization helpers.
/// </summary>
public static class BoolExprNormalizer
{
    extension<T>(BoolExpr<T> expr)
    {
        /// <summary>
        /// Converts a boolean expression to negation-normal form and flattens nested conjunctions/disjunctions.
        /// </summary>
        public BoolExpr<T> Normalize() =>
            Guard.RequireNotNull(expr).ToNegationNormalForm().Simplify();

        BoolExpr<T> ToNegationNormalForm() =>
            PushNot(expr, isNegated: false);

        BoolExpr<T> Simplify() => expr switch
        {
            Atom<T> => expr,
            Not<T> negation when negation.Term is Atom<T> => expr,
            Not<T> => throw new InvalidOperationException("Boolean-expression simplification expects negation-normal form."),
            And<T> conjunction => SimplifyNary([..conjunction.Terms.Select(static term => term.Simplify())], isAnd: true),
            Or<T> disjunction => SimplifyNary([..disjunction.Terms.Select(static term => term.Simplify())], isAnd: false),
            _ => throw new InvalidOperationException($"Unknown boolean-expression node '{expr.GetType().Name}'.")
        };
    }

    static BoolExpr<T> PushNot<T>(BoolExpr<T> expr, bool isNegated) => expr switch
    {
        Atom<T> atom => isNegated ? new Not<T>(atom) : atom,
        Not<T> negation => PushNot(negation.Term, !isNegated),
        And<T> conjunction when !isNegated => new And<T>([..conjunction.Terms.Select(static term => term.Normalize())]),
        And<T> conjunction => new Or<T>([..conjunction.Terms.Select(static term => PushNot(term, isNegated: true))]),
        Or<T> disjunction when !isNegated => new Or<T>([..disjunction.Terms.Select(static term => term.Normalize())]),
        Or<T> disjunction => new And<T>([..disjunction.Terms.Select(static term => PushNot(term, isNegated: true))]),
        _ => throw new InvalidOperationException($"Unknown boolean-expression node '{expr.GetType().Name}'.")
    };

    static BoolExpr<T> SimplifyNary<T>(IReadOnlyList<BoolExpr<T>> terms, bool isAnd)
    {
        List<BoolExpr<T>> flattened = [];
        foreach (var term in terms)
        {
            if (isAnd && term is And<T> conjunction)
            {
                flattened.AddRange(conjunction.Terms);
                continue;
            }

            if (!isAnd && term is Or<T> disjunction)
            {
                flattened.AddRange(disjunction.Terms);
                continue;
            }

            flattened.Add(term);
        }

        if (flattened.Count == 0)
            throw new InvalidOperationException(isAnd
                ? "An AND expression must contain at least one term."
                : "An OR expression must contain at least one term.");

        if (flattened.Count == 1)
            return flattened[0];

        return isAnd ? new And<T>(flattened) : new Or<T>(flattened);
    }
}

/// <summary>
/// Pretty-printing helpers for boolean expressions.
/// </summary>
public static class BoolExprPrettyPrinter
{
    /// <summary>
    /// Pretty-prints a boolean expression using the supplied atomic formatter.
    /// </summary>
    public static string PrettyPrint<T>(this BoolExpr<T> expr, Func<T, string> atomFormatter) => Print( 
        Guard.RequireNotNull(expr),
        Guard.RequireNotNull(atomFormatter),
        parentPrecedence: 0
        );

    static string Print<T>(BoolExpr<T> expr, Func<T, string> atomFormatter, int parentPrecedence)
    {
        var (text, precedence, alwaysWrap) = expr switch
        {
            Atom<T> atom => (atomFormatter(atom.Term), 3, false),
            Not<T> negation => ($"NOT {Print(negation.Term, atomFormatter, 3)}", 3, false),
            And<T> conjunction => (string.Join(" AND ", conjunction.Terms.Select(term => Print(term, atomFormatter, 2))), 2, true),
            Or<T> disjunction => (string.Join(" OR ", disjunction.Terms.Select(term => Print(term, atomFormatter, 1))), 1, true),
            _ => throw new InvalidOperationException($"Unknown boolean-expression node '{expr.GetType().Name}'.")
        };

        return alwaysWrap || precedence < parentPrecedence ? $"({text})" : text;
    }
}
