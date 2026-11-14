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

    internal readonly record struct Failure(int Index, string Code);

    internal static Failure? Evaluate(ConstraintValues values, ImmutableArray<ShapeConstraint> constraints)
    {
        if (constraints.IsDefaultOrEmpty) return null;
        for (var index = 0; index < constraints.Length; index++)
        {
            var constraint = constraints[index];
            var code = values.TryResolve(Target(constraint), out var candidate)
                ? Check(constraint, candidate) : "constraint.unsupportedTarget";
            if (code is not null) return new(index, code);
        }
        return null;
    }

    internal static bool IsSatisfied(ObservationValue value, ImmutableArray<ShapeConstraint> constraints,
        Shape? shape = null, TypeDefinition.Structural? structural = null, ObjectTypeRef? objectType = null) =>
        Evaluate(new ConstraintValues(value, structural, shape, objectType), constraints) is null;

    // One target grammar and declaration lookup for authoring and every admission representation.
    // Targets name canonical declared fields. Runtime value lookup is exact-first, ordinal-ignore-case.
    internal static bool TryResolveTarget(FieldPath? target, out string? name,
        Shape? shape = null, TypeDefinition.Structural? structural = null, ObjectTypeRef? objectType = null)
    {
        name = null;
        if (target is null) return true;
        var segments = target.Value.Segments;
        if (segments.Length != 1 || segments[0].Kind != SegmentKind.Field) return false;
        name = segments[0].Segment!;
        if (shape is not null)
        {
            if (!shape.TryGetField(name, out var field)) return false;
            name = field.Name.Value;
        }
        else if (structural is not null)
        {
            if (!structural.TryGetField(name, out var field)) return false;
            name = field.Name.Value;
        }
        else if (objectType is not null)
        {
            var found = false;
            foreach (var field in objectType.Fields)
                if (field.Name == name) { found = true; break; }
            if (!found) return false;
        }
        else return false; // A target must belong to a declaration, even when the input happens to contain that key.
        return true;
    }

    internal static bool TryGetField(IReadOnlyDictionary<string, ObservationValue> fields, string name, out ObservationValue value)
    {
        if (fields.TryGetValue(name, out value)) return true;
        foreach (var field in fields)
            if (string.Equals(field.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                value = field.Value;
                return true;
            }
        value = default;
        return false;
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

// Stack-only representation adapter. Target selection and rule evaluation belong to ConstraintSemantics.
internal readonly ref struct ConstraintValues
{
    readonly ObservationValue root;
    readonly IReadOnlyDictionary<string, ObservationValue>? fields;
    readonly ReadOnlySpan<ObservationValue> values;
    readonly ReadOnlySpan<ulong> presence;
    readonly ObservationLayout? layout;
    readonly Shape? shape;
    readonly TypeDefinition.Structural? structural;
    readonly ObjectTypeRef? objectType;

    internal ConstraintValues(ObservationValue root, TypeDefinition.Structural? structural = null,
        Shape? shape = null, ObjectTypeRef? objectType = null)
    { this.root = root; this.structural = structural; this.shape = shape; this.objectType = objectType; }
    internal ConstraintValues(IReadOnlyDictionary<string, ObservationValue> fields, Shape shape)
    { this.fields = fields; this.shape = shape; }
    internal ConstraintValues(ReadOnlySpan<ObservationValue> values, ReadOnlySpan<ulong> presence, ObservationLayout layout)
    { this.values = values; this.presence = presence; this.layout = layout; shape = layout.Graph.GetShape(layout.ShapeId); }

    internal bool TryResolve(FieldPath? target, out ObservationValue value)
    {
        value = root;
        if (!ConstraintSemantics.TryResolveTarget(target, out var name, shape, structural, objectType)) return false;
        if (name is null)
        {
            if (fields is not null || layout is not null) value = ObservationValue.EmptyObject;
            return true;
        }
        if (layout is not null)
        {
            value = layout.TryGetOrdinal(name, out var ordinal)
                && (presence.IsEmpty || (presence[ordinal >> 6] & (1UL << (ordinal & 63))) != 0) ? values[ordinal] : default;
            return true;
        }
        var source = fields ?? root.Fields;
        if (source is null) return false;
        var present = ConstraintSemantics.TryGetField(source, name, out value);
        // A missing declared optional member has undefined value. An undeclared target cannot pass a length rule.
        return present || shape is not null || structural is not null || objectType is not null;
    }
}
