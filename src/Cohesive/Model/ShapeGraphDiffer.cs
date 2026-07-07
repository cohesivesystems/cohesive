using System.Collections.Immutable;

namespace Cohesive.Model;

/// <summary>
/// Computes explicit deltas between two shape graphs.
/// </summary>
public static class ShapeGraphDiffer
{
    /// <summary>
    /// Computes the graph delta that transforms <paramref name="source"/> into <paramref name="target"/>.
    /// </summary>
    public static GraphDelta Diff(ShapeGraph source, ShapeGraph target, GraphDeltaKind kind = GraphDeltaKind.Unspecified, string? deltaId = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);

        List<GraphDeltaOperation> operations = [];

        DiffGraphAnnotations(source.Annotations, target.Annotations, operations);
        DiffShapes(source, target, operations);
        DiffNamedTypes(source, target, operations);

        return new(
            id: deltaId ?? $"diff.{source.Id.Value}_to_{target.Id.Value}",
            operations: [.. operations],
            kind: kind,
            sourceGraphId: source.Id,
            targetGraphId: target.Id
            );
    }

    static void DiffShapes(ShapeGraph source, ShapeGraph target, List<GraphDeltaOperation> operations)
    {
        foreach (var sourceShape in source.Shapes)
        {
            if (!target.TryGetShape(sourceShape.Id, out _))
                operations.Add(new RemoveShapeOperation(sourceShape.Id));
        }

        for (var targetOrdinal = 0; targetOrdinal < target.Shapes.Length; targetOrdinal++)
        {
            var targetShape = target.Shapes[targetOrdinal];
            if (!source.TryGetShape(targetShape.Id, out var sourceShape))
            {
                operations.Add(new AddShapeOperation(targetShape, targetOrdinal));
                continue;
            }

            DiffShape(sourceShape, targetShape, operations);
        }
    }

    static void DiffShape(Shape source, Shape target, List<GraphDeltaOperation> operations)
    {
        if (!SameConstraints(source.Constraints, target.Constraints))
        {
            operations.Add(new ReplaceShapeOperation(target));
            return;
        }

        DiffShapeAnnotations(source.Id, source.Annotations, target.Annotations, operations);

        foreach (var sourceField in source.Fields)
        {
            if (!target.TryGetField(sourceField.Name.Value, out _))
                operations.Add(new RemoveShapeFieldOperation(target.Id, sourceField.Name));
        }

        for (var targetOrdinal = 0; targetOrdinal < target.Fields.Length; targetOrdinal++)
        {
            var targetField = target.Fields[targetOrdinal];
            if (!source.TryGetField(targetField.Name.Value, out var sourceField))
            {
                operations.Add(new AddShapeFieldOperation(target.Id, targetField, targetOrdinal));
                continue;
            }

            DiffField(
                sourceField,
                targetField,
                GraphFieldPath.ForShape(target.Id, FieldPath.FromField(targetField.Name.Value)),
                field => new ReplaceShapeFieldOperation(target.Id, field),
                operations);
        }
    }

    static void DiffField(
        FieldDefinition source,
        FieldDefinition target,
        GraphFieldPath path,
        Func<FieldDefinition, GraphDeltaOperation> replace,
        List<GraphDeltaOperation> operations)
    {
        if (source.Role != target.Role || source.Mutability != target.Mutability || source.Compute != target.Compute)
        {
            operations.Add(replace(target));
            return;
        }

        if (!EqualityComparer<TypeRef>.Default.Equals(source.Type, target.Type))
            operations.Add(new SetFieldTypeOperation(path, target.Type));

        if (source.Presence != target.Presence)
            operations.Add(new SetFieldPresenceOperation(path, target.Presence));

        if (source.Cardinality != target.Cardinality)
            operations.Add(new SetFieldCardinalityOperation(path, target.Cardinality));

        if (source.Nullability != target.Nullability)
            operations.Add(new SetFieldNullabilityOperation(path, target.Nullability));

        DiffConstraints(source.Constraints, target.Constraints, operations, path);
        DiffAnnotations(source.Annotations, target.Annotations, operations, path);
    }

    static void DiffNamedTypes(ShapeGraph source, ShapeGraph target, List<GraphDeltaOperation> operations)
    {
        foreach (var sourceType in source.NamedTypes)
        {
            if (!target.TryGetType(sourceType.Id, out _))
                operations.Add(new RemoveNamedTypeOperation(sourceType.Id));
        }

        for (var targetOrdinal = 0; targetOrdinal < target.NamedTypes.Length; targetOrdinal++)
        {
            var targetType = target.NamedTypes[targetOrdinal];
            if (!source.TryGetType(targetType.Id, out var sourceType))
            {
                operations.Add(new AddNamedTypeOperation(targetType, targetOrdinal));
                continue;
            }

            DiffNamedType(sourceType, targetType, operations);
        }
    }

    static void DiffNamedType(TypeDefinition source, TypeDefinition target, List<GraphDeltaOperation> operations)
    {
        if (source.GetType() != target.GetType())
        {
            operations.Add(new ReplaceNamedTypeOperation(target));
            return;
        }

        switch (source, target)
        {
            case (TypeDefinition.Structural sourceStructural, TypeDefinition.Structural targetStructural):
                DiffStructuralType(sourceStructural, targetStructural, operations);
                return;

            case (TypeDefinition.Enum sourceEnum, TypeDefinition.Enum targetEnum):
                DiffEnumType(sourceEnum, targetEnum, operations);
                return;

            case (TypeDefinition.Union sourceUnion, TypeDefinition.Union targetUnion):
                if (sourceUnion.Discriminator != targetUnion.Discriminator
                    || !sourceUnion.Cases.SequenceEqual(targetUnion.Cases))
                {
                    operations.Add(new ReplaceNamedTypeOperation(targetUnion));
                    return;
                }

                DiffTypeAnnotations(target.Id, sourceUnion.Annotations, targetUnion.Annotations, operations);
                return;
        }
    }

    static void DiffStructuralType(TypeDefinition.Structural source, TypeDefinition.Structural target, List<GraphDeltaOperation> operations)
    {
        if (!SameConstraints(source.Constraints, target.Constraints))
        {
            operations.Add(new ReplaceNamedTypeOperation(target));
            return;
        }

        DiffTypeAnnotations(target.Id, source.Annotations, target.Annotations, operations);

        foreach (var sourceField in source.Fields)
        {
            if (!target.TryGetField(sourceField.Name.Value, out _))
                operations.Add(new RemoveTypeFieldOperation(target.Id, sourceField.Name));
        }

        for (var targetOrdinal = 0; targetOrdinal < target.Fields.Length; targetOrdinal++)
        {
            var targetField = target.Fields[targetOrdinal];
            if (!source.TryGetField(targetField.Name.Value, out var sourceField))
            {
                operations.Add(new AddTypeFieldOperation(target.Id, targetField, targetOrdinal));
                continue;
            }

            DiffStructuralField(sourceField, targetField, target.Id, operations);
        }
    }

    static void DiffStructuralField(StructuralField source, StructuralField target, TypeId typeId, List<GraphDeltaOperation> operations)
    {
        if (source.Role != target.Role)
        {
            operations.Add(new ReplaceTypeFieldOperation(typeId, target));
            return;
        }

        var path = GraphFieldPath.ForType(typeId, FieldPath.FromField(target.Name.Value));

        if (!EqualityComparer<TypeRef>.Default.Equals(source.Type, target.Type))
            operations.Add(new SetFieldTypeOperation(path, target.Type));

        if (source.Presence != target.Presence)
            operations.Add(new SetFieldPresenceOperation(path, target.Presence));

        if (source.Cardinality != target.Cardinality)
            operations.Add(new SetFieldCardinalityOperation(path, target.Cardinality));

        if (source.Nullability != target.Nullability)
            operations.Add(new SetFieldNullabilityOperation(path, target.Nullability));

        DiffConstraints(source.Constraints, target.Constraints, operations, path);
        DiffAnnotations(source.Annotations, target.Annotations, operations, path);
    }

    static void DiffEnumType(TypeDefinition.Enum source, TypeDefinition.Enum target, List<GraphDeltaOperation> operations)
    {
        if (source.Underlying != target.Underlying)
        {
            operations.Add(new ReplaceNamedTypeOperation(target));
            return;
        }

        DiffTypeAnnotations(target.Id, source.Annotations, target.Annotations, operations);

        var sourceByName = source.Values.ToDictionary(x => x.Name, StringComparer.Ordinal);
        var targetByName = target.Values.ToDictionary(x => x.Name, StringComparer.Ordinal);

        foreach (var sourceValue in source.Values)
        {
            if (!targetByName.ContainsKey(sourceValue.Name))
                operations.Add(new RemoveEnumValueOperation(target.Id, sourceValue.Name));
        }

        foreach (var targetValue in target.Values)
        {
            if (!sourceByName.TryGetValue(targetValue.Name, out var sourceValue))
            {
                operations.Add(new AddEnumValueOperation(target.Id, targetValue));
                continue;
            }

            if (!string.Equals(sourceValue.Value, targetValue.Value, StringComparison.Ordinal)
                || !string.Equals(sourceValue.Label, targetValue.Label, StringComparison.Ordinal)
                || !string.Equals(sourceValue.Description, targetValue.Description, StringComparison.Ordinal))
            {
                operations.Add(new ReplaceNamedTypeOperation(target));
                return;
            }
        }
    }

    static void DiffConstraints(ImmutableArray<ShapeConstraint> source, ImmutableArray<ShapeConstraint> target, List<GraphDeltaOperation> operations, GraphFieldPath path)
    {
        foreach (var sourceConstraint in source)
        {
            if (!target.Any(x => EqualityComparer<ShapeConstraint>.Default.Equals(x, sourceConstraint)))
                operations.Add(new RemoveFieldConstraintOperation(path, sourceConstraint));
        }

        foreach (var targetConstraint in target)
        {
            if (!source.Any(x => EqualityComparer<ShapeConstraint>.Default.Equals(x, targetConstraint)))
                operations.Add(new AddFieldConstraintOperation(path, targetConstraint));
        }
    }

    static void DiffGraphAnnotations(
        ImmutableDictionary<AnnotationKey, AnnotationValue> source,
        ImmutableDictionary<AnnotationKey, AnnotationValue> target,
        List<GraphDeltaOperation> operations
        )
    {
        foreach (var (sourceKey, _) in source)
        {
            if (!target.ContainsKey(sourceKey))
                operations.Add(new RemoveGraphAnnotationOperation(sourceKey));
        }

        foreach (var (targetKey, targetValue) in target)
        {
            if (!source.TryGetValue(targetKey, out var sourceValue) || sourceValue != targetValue)
                operations.Add(new SetGraphAnnotationOperation(targetKey, targetValue));
        }
    }

    static void DiffShapeAnnotations(
        ShapeId shapeId,
        ImmutableDictionary<AnnotationKey, AnnotationValue> source,
        ImmutableDictionary<AnnotationKey, AnnotationValue> target,
        List<GraphDeltaOperation> operations
        )
    {
        var comparableSource = source.Remove(new(ShapeAnnotationKeys.Role));
        var comparableTarget = target.Remove(new(ShapeAnnotationKeys.Role));

        foreach (var (sourceKey, _) in comparableSource)
        {
            if (!comparableTarget.ContainsKey(sourceKey))
                operations.Add(new RemoveShapeAnnotationOperation(shapeId, sourceKey));
        }

        foreach (var (targetKey, targetValue) in comparableTarget)
        {
            if (!comparableSource.TryGetValue(targetKey, out var sourceValue) || sourceValue != targetValue)
                operations.Add(new SetShapeAnnotationOperation(shapeId, targetKey, targetValue));
        }
    }

    static void DiffTypeAnnotations(
        TypeId typeId,
        ImmutableDictionary<AnnotationKey, AnnotationValue> source,
        ImmutableDictionary<AnnotationKey, AnnotationValue> target,
        List<GraphDeltaOperation> operations
        )
    {
        foreach (var (sourceKey, _) in source)
        {
            if (!target.ContainsKey(sourceKey))
                operations.Add(new RemoveTypeAnnotationOperation(typeId, sourceKey));
        }

        foreach (var (targetKey, targetValue) in target)
        {
            if (!source.TryGetValue(targetKey, out var sourceValue) || sourceValue != targetValue)
                operations.Add(new SetTypeAnnotationOperation(typeId, targetKey, targetValue));
        }
    }

    static void DiffAnnotations(
        ImmutableDictionary<AnnotationKey, AnnotationValue> source,
        ImmutableDictionary<AnnotationKey, AnnotationValue> target,
        List<GraphDeltaOperation> operations,
        GraphFieldPath path
        )
    {
        foreach (var (sourceKey, _) in source)
        {
            if (!target.ContainsKey(sourceKey))
                operations.Add(new RemoveFieldAnnotationOperation(path, sourceKey));
        }

        foreach (var (targetKey, targetValue) in target)
        {
            if (!source.TryGetValue(targetKey, out var sourceValue) || sourceValue != targetValue)
                operations.Add(new SetFieldAnnotationOperation(path, targetKey, targetValue));
        }
    }

    static bool SameConstraints(ImmutableArray<ShapeConstraint> source, ImmutableArray<ShapeConstraint> target)
    {
        if (source.Length != target.Length)
            return false;

        foreach (var constraint in source)
        {
            if (!target.Any(x => EqualityComparer<ShapeConstraint>.Default.Equals(x, constraint)))
                return false;
        }

        return true;
    }
}
