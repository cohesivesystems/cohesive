using System.Collections.Immutable;
using Cohesive.Model.Serialization;

namespace Cohesive.Model;

public static partial class ObservationValidator
{
    /// <summary>Projects a typed input and validates it against its prebuilt CLR-derived shape.</summary>
    /// <param name="input">Record or POCO input; a null input produces a validation diagnostic.</param>
    /// <param name="shapes">Previously built graph and CLR-to-shape bindings.</param>
    /// <typeparam name="T">Registered input type.</typeparam>
    /// <returns>Structural and constraint admission result, before application side effects.</returns>
    /// <exception cref="ArgumentNullException">Shapes is null.</exception>
    /// <exception cref="InvalidOperationException">The input type is not registered.</exception>
    public static DocumentValidationResult ValidateInput<T>(T input, ClrShapeGraphBuildResult shapes) where T : notnull
    {
        ArgumentNullException.ThrowIfNull(shapes);
        return ValidateInput(ObservationValue.FromObject(input), shapes.GetShape<T>());
    }

    /// <summary>Validates an input before application admission, returning structured field-path diagnostics.</summary>
    /// <param name="value">Portable input, for example projected from a typed record.</param>
    /// <param name="shape">Exact graph-scoped input shape.</param>
    /// <returns>A shared successful result or the first structural/constraint diagnostic. No input is retained.</returns>
    /// <exception cref="ArgumentNullException">The shape has no graph.</exception>
    public static DocumentValidationResult ValidateInput(ObservationValue value, GraphShapeId shape)
    {
        ArgumentNullException.ThrowIfNull(shape.Graph);
        var definition = shape.Graph.GetShape(shape.ShapeId);
        NoDiagnostics fast = default;
        if (TryValidateRoot(value, definition, shape.Graph, ref fast)) return DocumentValidationResult.Valid;
        DetailedDiagnostics diagnostics = new();
        if (value.Kind != ObservationValueKind.Object || value.Fields is null
            || TryValidatePortableTree(value, MaxValidationDepth, ref diagnostics))
            _ = TryValidateRoot(value, definition, shape.Graph, ref diagnostics);
        return new([new(diagnostics.Code ?? "observation.invalid", DiagnosticSeverity.Error,
            diagnostics.Error ?? "Invalid input.", Location: diagnostics.Location)]);
    }

    static bool TryValidateConstraints<TDiagnostics>(in ObservationValue value,
        ImmutableArray<ShapeConstraint> constraints, ref TDiagnostics diagnostics, string? field = null)
        where TDiagnostics : IValidationDiagnostics =>
        TryValidateConstraints(new ConstraintValues(value), constraints, ref diagnostics, field);

    static bool TryValidateConstraints<TDiagnostics>(ConstraintValues values,
        ImmutableArray<ShapeConstraint> constraints, ref TDiagnostics diagnostics, string? field = null)
        where TDiagnostics : IValidationDiagnostics
    {
        if (constraints.IsDefaultOrEmpty) return true;
        foreach (var constraint in constraints)
        {
            var target = constraint is null ? null : ConstraintSemantics.Target(constraint);
            var validTarget = values.TryResolve(target, out var value);
            var code = !validTarget ? "constraint.unsupportedTarget"
                : ConstraintSemantics.Check(constraint!, value);
            if (code is null) continue;
            if (field is not null) diagnostics.PushField(field);
            if (target is { } path)
                foreach (var segment in path.Segments)
                    if (segment.Kind == SegmentKind.Field) diagnostics.PushField(segment.Segment!);
            return Fail(ref diagnostics, new(ErrorCode.ConstraintViolation, code,
                constraint?.GetType().Name ?? "null"));
        }
        return true;
    }

    // Stack-only view over each existing storage representation; length validation
    // does not allocate a root object/dictionary for ordinal or prepared admission.
    readonly ref struct ConstraintValues
    {
        readonly ObservationValue root;
        readonly IReadOnlyDictionary<string, ObservationValue>? fields;
        readonly ReadOnlySpan<ObservationValue> values;
        readonly ReadOnlySpan<ulong> presence;
        readonly ObservationLayout? layout;
        readonly Shape? shape;
        readonly TypeDefinition.Structural? structural;

        internal ConstraintValues(ObservationValue root, TypeDefinition.Structural? structural = null)
        { this.root = root; this.structural = structural; }
        internal ConstraintValues(IReadOnlyDictionary<string, ObservationValue> fields, Shape shape)
        { this.fields = fields; this.shape = shape; }
        internal ConstraintValues(ReadOnlySpan<ObservationValue> values, ReadOnlySpan<ulong> presence, ObservationLayout layout)
        { this.values = values; this.presence = presence; this.layout = layout; }

        internal bool TryResolve(FieldPath? target, out ObservationValue value)
        {
            value = root;
            if (target is null)
            {
                // A shape root is a present object; supported root constraints inspect only kind.
                if (fields is not null || layout is not null) value = ObservationValue.EmptyObject;
                return true;
            }
            // Nested constraints belong on the nested shape; wildcard navigation is
            // deliberately unsupported rather than implicitly quantified.
            var segments = target.Value.Segments;
            if (segments.Length != 1 || segments[0].Kind != SegmentKind.Field) return false;
            var name = segments[0].Segment!;
            if (layout is not null)
            {
                if (!layout.Graph.GetShape(layout.ShapeId).TryGetField(name, out _)) return false;
                value = layout.TryGetOrdinal(name, out var ordinal)
                    && (presence.IsEmpty || HasValue(presence, ordinal)) ? values[ordinal] : default;
            }
            else if (fields is not null)
            {
                if (!shape!.TryGetField(name, out _)) return false;
                fields.TryGetValue(name, out value);
            }
            else
            {
                if (structural is not null && !structural.TryGetField(name, out _)) return false;
                if (root.Kind != ObservationValueKind.Object || root.Fields is null) return false;
                if (structural is not null) TryGetPropertyIgnoreCase(root.Fields, name, out value);
                else root.TryGetProperty(name, out value);
            }
            return true;
        }
    }
}
