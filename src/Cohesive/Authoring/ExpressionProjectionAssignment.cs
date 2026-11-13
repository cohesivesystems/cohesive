using Cohesive.Model;
namespace Cohesive.Authoring;

/// <summary>Structural input for one field assignment in a projection node.</summary>
public sealed record ExpressionProjectionAssignment
{
    /// <summary>Creates a projection-assignment input.</summary>
    /// <param name="target">Target field path in the projected shape.</param>
    /// <param name="value">Canonical expression assigned to the target field.</param>
    /// <param name="assignmentSource">Optional producer-source attribution for the assignment decision.</param>
    /// <param name="valueSource">Optional producer-source attribution for the value-expression site.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    public ExpressionProjectionAssignment(
        FieldPath target,
        Expr value,
        AuthoringSource? assignmentSource = null,
        AuthoringSource? valueSource = null)
    {
        Target = target;
        Value = Guard.RequireNotNull(value);
        AssignmentSource = assignmentSource;
        ValueSource = valueSource;
    }

    /// <summary>Target field path in the projected shape.</summary>
    public FieldPath Target { get; init; }

    /// <summary>Canonical expression assigned to the target field.</summary>
    public Expr Value { get; init; }

    /// <summary>Optional producer-source attribution for the assignment decision.</summary>
    public AuthoringSource? AssignmentSource { get; init; }

    /// <summary>Optional producer-source attribution for the value-expression site.</summary>
    public AuthoringSource? ValueSource { get; init; }
}
