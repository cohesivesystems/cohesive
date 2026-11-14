using System.Collections.Immutable;

namespace Cohesive.Model;

// One interpretation of the portable required/length subset. No CLR attribute or
// reflection semantics survive lowering. Unsupported declarations fail closed.
internal static class ConstraintSemantics
{
    internal static FieldPath? Target(ShapeConstraint constraint) => constraint switch
    {
        RequiredConstraint required => required.Field,
        MinLengthConstraint minimum => minimum.Field,
        MaxLengthConstraint maximum => maximum.Field,
        _ => null
    };

    internal static string? Check(ShapeConstraint constraint, in ObservationValue value)
    {
        switch (constraint)
        {
            case RequiredConstraint required:
                return value.Kind is ObservationValueKind.Null or ObservationValueKind.Undefined
                       || (required.RejectEmptyStrings && value.Kind == ObservationValueKind.String
                           && string.IsNullOrWhiteSpace(value.GetString()))
                    ? "constraint.required" : null;
            case MinLengthConstraint minimum when minimum.Value >= 0:
                return Length(value, minimum.Value, minimum: true);
            case MaxLengthConstraint maximum when maximum.Value >= 0:
                return Length(value, maximum.Value, minimum: false);
            case MinLengthConstraint or MaxLengthConstraint:
                return "constraint.invalidBound";
            default:
                return "constraint.unsupported";
        }
    }

    static string? Length(in ObservationValue value, int bound, bool minimum)
    {
        if (value.Kind is ObservationValueKind.Null or ObservationValueKind.Undefined) return null;
        var length = value.Kind switch
        {
            ObservationValueKind.String => value.GetString()!.Length,
            ObservationValueKind.Array => value.GetArrayLength(),
            ObservationValueKind.Bytes => value.GetBytes().Length,
            _ => -1
        };
        if (length < 0) return "constraint.lengthType";
        return minimum ? (length < bound ? "constraint.minLength" : null)
            : (length > bound ? "constraint.maxLength" : null);
    }

    internal static bool IsSatisfied(ObservationValue value, ImmutableArray<ShapeConstraint> constraints)
    {
        if (constraints.IsDefaultOrEmpty) return true;
        foreach (var constraint in constraints)
        {
            var candidate = value;
            if (Target(constraint) is { } path)
            {
                if (value.Kind != ObservationValueKind.Object || path.Segments.Length != 1 || path.Segments[0].Kind != SegmentKind.Field) return false;
                value.TryGetProperty(path.Segments[0].Segment!, out candidate);
            }
            if (Check(constraint, candidate) is not null) return false;
        }
        return true;
    }

    internal static ShapeConstraint At(ShapeConstraint constraint, FieldPath target) => constraint switch
    {
        RequiredConstraint required => required with { Field = target },
        MinLengthConstraint minimum => minimum with { Field = target },
        MaxLengthConstraint maximum => maximum with { Field = target },
        _ => throw new NotSupportedException("Cannot project an unsupported constraint into a value contract.")
    };

    // Authoring-time conjunction: never silently override explicit semantic bounds.
    internal static void RequireConsistent(ImmutableArray<ShapeConstraint> constraints)
    {
        if (constraints.IsDefaultOrEmpty) return;
        foreach (var left in constraints)
            foreach (var right in constraints)
                if (left is MinLengthConstraint minimum && right is MaxLengthConstraint maximum
                    && minimum.Field == maximum.Field && minimum.Value > maximum.Value)
                    throw new ArgumentException("constraint.conflictingBounds: minimum length exceeds maximum length.");
    }
}
