using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
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
    [RequiresUnreferencedCode("Typed projection requires preserved CLR members. Use portable ObservationValue admission in trimmed applications.")]
    [RequiresDynamicCode("Typed projection may compile CLR projection delegates. Use portable ObservationValue admission with an authored graph for NativeAOT.")]
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
        ImmutableArray<ShapeConstraint> constraints, ref TDiagnostics diagnostics, string? field = null, ObjectTypeRef? objectType = null)
        where TDiagnostics : IValidationDiagnostics =>
        TryValidateConstraints(new ConstraintValues(value, objectType: objectType), constraints, ref diagnostics, field);

    static bool TryValidateConstraints<TDiagnostics>(ConstraintValues values,
        ImmutableArray<ShapeConstraint> constraints, ref TDiagnostics diagnostics, string? field = null)
        where TDiagnostics : IValidationDiagnostics
    {
        var failure = ConstraintSemantics.Evaluate(values, constraints);
        if (failure is null) return true;
        var constraint = constraints[failure.Value.Index];
        var target = ConstraintSemantics.Target(constraint);
        if (field is not null) diagnostics.PushField(field);
        if (target is { } path)
            foreach (var segment in path.Segments)
                if (segment.Kind == SegmentKind.Field) diagnostics.PushField(segment.Segment!);
        return Fail(ref diagnostics, new(ErrorCode.ConstraintViolation, failure.Value.Code,
            constraint?.GetType().Name ?? "null"));
    }
}
