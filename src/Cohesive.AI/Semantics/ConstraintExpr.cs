using System.Collections.Immutable;

namespace Cohesive.AI.Semantics;

/// <summary>
/// Typed constraint expression used by semantic path selectors and qualifier semantics.
/// </summary>
public abstract record ConstraintExpr
{
    /// <summary>
    /// Equality constraint (for example <c>N1-01 = ST</c>).
    /// </summary>
    public sealed record EqualsExpr : ConstraintExpr
    {
        /// <summary>
        /// Creates an equality constraint.
        /// </summary>
        public EqualsExpr(string fieldPath, string value)
        {
            FieldPath = Guard.RequireNotNullOrWhiteSpace(fieldPath);
            Value = Guard.RequireNotNullOrWhiteSpace(value);
        }

        /// <summary>
        /// Field or element path being constrained.
        /// </summary>
        public string FieldPath { get; }

        /// <summary>
        /// Required value.
        /// </summary>
        public string Value { get; }
    }

    /// <summary>
    /// Membership constraint (for example <c>N1-01 IN {ST, BT}</c>).
    /// </summary>
    public sealed record InSetExpr : ConstraintExpr
    {
        /// <summary>
        /// Creates a membership constraint.
        /// </summary>
        public InSetExpr(string fieldPath, ImmutableArray<string> values)
        {
            FieldPath = Guard.RequireNotNullOrWhiteSpace(fieldPath);
            Values = NormalizeValues(values);

            if (Values.IsDefaultOrEmpty)
            {
                throw new ArgumentException(
                    message: "Constraint set cannot be empty.",
                    paramName: nameof(values)
                    );
            }
        }

        /// <summary>
        /// Field or element path being constrained.
        /// </summary>
        public string FieldPath { get; }

        /// <summary>
        /// Allowed value set.
        /// </summary>
        public ImmutableArray<string> Values { get; }
    }

    /// <summary>
    /// Pattern constraint.
    /// </summary>
    public sealed record RegexExpr : ConstraintExpr
    {
        /// <summary>
        /// Creates a regex constraint.
        /// </summary>
        public RegexExpr(string fieldPath, string pattern)
        {
            FieldPath = Guard.RequireNotNullOrWhiteSpace(fieldPath);
            Pattern = Guard.RequireNotNullOrWhiteSpace(pattern);
        }

        /// <summary>
        /// Field or element path being constrained.
        /// </summary>
        public string FieldPath { get; }

        /// <summary>
        /// Regex pattern text.
        /// </summary>
        public string Pattern { get; }
    }

    /// <summary>
    /// Composite constraint expression made from multiple sub-constraints.
    /// </summary>
    public sealed record CompositeExpr : ConstraintExpr
    {
        /// <summary>
        /// Creates a composite constraint expression.
        /// </summary>
        public CompositeExpr(ImmutableArray<ConstraintExpr> components)
        {
            Components = components.IsDefault ? [] : components;
            if (Components.IsDefaultOrEmpty)
            {
                throw new ArgumentException(
                    message: "Composite constraints require at least one component.",
                    paramName: nameof(components)
                    );
            }
        }

        /// <summary>
        /// Composite expression components.
        /// </summary>
        public ImmutableArray<ConstraintExpr> Components { get; }
    }

    static ImmutableArray<string> NormalizeValues(ImmutableArray<string> values)
    {
        if (values.IsDefaultOrEmpty)
            return [];

        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        List<string> normalized = [];
        foreach (var raw in values)
        {
            var value = raw?.Trim();
            if (string.IsNullOrWhiteSpace(value))
                continue;
            if (!seen.Add(value))
                continue;
            normalized.Add(value);
        }

        return [.. normalized];
    }
}

/// <summary>
/// Canonical text rendering helpers for constraint expressions.
/// </summary>
public static class ConstraintExprFormatter
{
    /// <summary>
    /// Renders a constraint expression to stable canonical text.
    /// </summary>
    public static string ToCanonicalString(ConstraintExpr expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        return expression switch
        {
            ConstraintExpr.EqualsExpr equals => $"{equals.FieldPath}={Escape(equals.Value)}",
            ConstraintExpr.InSetExpr @in => $"{@in.FieldPath} in ({string.Join('|', @in.Values.Select(Escape))})",
            ConstraintExpr.RegexExpr regex => $"{regex.FieldPath}~/{regex.Pattern}/",
            ConstraintExpr.CompositeExpr composite => string.Join(" & ", composite.Components.Select(ToCanonicalString)),
            _ => throw new InvalidOperationException($"Unsupported constraint expression type '{expression.GetType().Name}'.")
        };
    }

    static string Escape(string value)
    {
        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("|", "\\|", StringComparison.Ordinal)
            .Replace("]", "\\]", StringComparison.Ordinal);
    }
}
