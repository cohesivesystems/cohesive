using System.Collections.Immutable;

namespace Cohesive.Model;

/// <summary>
/// Applies graph, overlay, and version deltas to immutable shape graphs.
/// </summary>
public static class ShapeGraphDeltaApplicator
{
    /// <summary>
    /// Applies a generic graph delta.
    /// </summary>
    public static ShapeGraph Apply(ShapeGraph graph, GraphDelta delta, GraphId? resultGraphId = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(delta);
        return ApplyOperations(
            graph: graph,
            operations: delta.Operations,
            resultGraphId: resultGraphId ?? delta.TargetGraphId ?? CreateDerivedGraphId(graph.Id, delta.Id)
            );
    }

    /// <summary>
    /// Applies a party/profile overlay.
    /// </summary>
    public static ShapeGraph Overlay(ShapeGraph graph, OverlayDelta delta, GraphId? resultGraphId = null)
    {
        ArgumentNullException.ThrowIfNull(delta);
        return ApplyOperations(
            graph: graph,
            operations: delta.Operations,
            resultGraphId: resultGraphId ?? CreateDerivedGraphId(graph.Id, delta.Id)
            );
    }

    /// <summary>
    /// Applies a standard version evolution delta.
    /// </summary>
    public static ShapeGraph Evolve(ShapeGraph graph, VersionDelta delta, GraphId? resultGraphId = null)
    {
        ArgumentNullException.ThrowIfNull(delta);
        return ApplyOperations(
            graph: graph,
            operations: delta.Operations,
            resultGraphId: resultGraphId ?? delta.TargetGraphId ?? CreateDerivedGraphId(graph.Id, delta.Id)
            );
    }

    static ShapeGraph ApplyOperations(ShapeGraph graph, ImmutableArray<GraphDeltaOperation> operations, GraphId resultGraphId)
    {
        ArgumentNullException.ThrowIfNull(graph);

        var shapes = graph.Shapes.ToList();
        var namedTypes = graph.NamedTypes.ToList();
        var graphAnnotations = graph.Annotations;

        foreach (var operation in operations.IsDefault ? [] : operations)
            ApplyOperation(shapes, namedTypes, ref graphAnnotations, operation);

        return new(
            id: resultGraphId,
            shapes: [.. shapes],
            namedTypes: [.. namedTypes],
            annotations: graphAnnotations
            );
    }

    static void ApplyOperation(
        List<Shape> shapes,
        List<TypeDefinition> namedTypes,
        ref ImmutableDictionary<AnnotationKey, AnnotationValue> graphAnnotations,
        GraphDeltaOperation operation)
    {
        switch (operation)
        {
            case AddShapeOperation add:
                Insert(shapes, add.Shape, add.Ordinal);
                return;

            case RemoveShapeOperation remove:
                shapes.RemoveAt(FindShapeIndex(shapes, remove.ShapeId));
                return;

            case ReplaceShapeOperation replace:
                shapes[FindShapeIndex(shapes, replace.Shape.Id)] = replace.Shape;
                return;

            case SetGraphAnnotationOperation set:
                graphAnnotations = graphAnnotations.SetItem(set.Key, set.Value);
                return;

            case RemoveGraphAnnotationOperation remove:
                graphAnnotations = graphAnnotations.Remove(remove.Key);
                return;

            case AddNamedTypeOperation add:
                Insert(namedTypes, add.Type, add.Ordinal);
                return;

            case RemoveNamedTypeOperation remove:
                namedTypes.RemoveAt(FindTypeIndex(namedTypes, remove.TypeId));
                return;

            case ReplaceNamedTypeOperation replace:
                namedTypes[FindTypeIndex(namedTypes, replace.Type.Id)] = replace.Type;
                return;

            case AddShapeFieldOperation add:
                UpdateShape(shapes, add.ShapeId, shape => AddField(shape, add.Field, add.Ordinal));
                return;

            case RemoveShapeFieldOperation remove:
                UpdateShape(shapes, remove.ShapeId, shape => RemoveField(shape, remove.FieldName));
                return;

            case ReplaceShapeFieldOperation replace:
                UpdateShape(shapes, replace.ShapeId, shape => ReplaceField(shape, replace.Field));
                return;

            case AddTypeFieldOperation add:
                UpdateStructuralType(namedTypes, add.TypeId, type => AddField(type, add.Field, add.Ordinal));
                return;

            case RemoveTypeFieldOperation remove:
                UpdateStructuralType(namedTypes, remove.TypeId, type => RemoveField(type, remove.FieldName));
                return;

            case ReplaceTypeFieldOperation replace:
                UpdateStructuralType(namedTypes, replace.TypeId, type => ReplaceField(type, replace.Field));
                return;

            case SetFieldTypeOperation set:
                UpdateField(shapes, namedTypes, set.Target, field => Recreate(field with { Type = set.Type }), field => Recreate(field with { Type = set.Type }));
                return;

            case SetFieldPresenceOperation set:
                UpdateField(shapes, namedTypes, set.Target, field => Recreate(field with { Presence = set.Presence }), field => Recreate(field with { Presence = set.Presence }));
                return;

            case SetFieldCardinalityOperation set:
                UpdateField(shapes, namedTypes, set.Target, field => Recreate(field with { Cardinality = set.Cardinality }), field => Recreate(field with { Cardinality = set.Cardinality }));
                return;

            case SetFieldNullabilityOperation set:
                UpdateField(shapes, namedTypes, set.Target, field => Recreate(field with { Nullability = set.Nullability }), field => Recreate(field with { Nullability = set.Nullability }));
                return;

            case AddFieldConstraintOperation add:
                UpdateField(shapes, namedTypes, add.Target, field => Recreate(field with { Constraints = AddConstraint(field.Constraints, add.Constraint) }), field => Recreate(field with { Constraints = AddConstraint(field.Constraints, add.Constraint) }));
                return;

            case RemoveFieldConstraintOperation remove:
                UpdateField(shapes, namedTypes, remove.Target, field => Recreate(field with { Constraints = RemoveConstraint(field.Constraints, remove.Constraint) }), field => Recreate(field with { Constraints = RemoveConstraint(field.Constraints, remove.Constraint) }));
                return;

            case SetShapeAnnotationOperation set:
                UpdateShape(shapes, set.ShapeId, shape => shape with { Annotations = shape.Annotations.SetItem(set.Key, set.Value) });
                return;

            case RemoveShapeAnnotationOperation remove:
                UpdateShape(shapes, remove.ShapeId, shape => shape with { Annotations = shape.Annotations.Remove(remove.Key) });
                return;

            case SetTypeAnnotationOperation set:
                UpdateNamedType(namedTypes, set.TypeId, type => SetTypeAnnotation(type, set.Key, set.Value));
                return;

            case RemoveTypeAnnotationOperation remove:
                UpdateNamedType(namedTypes, remove.TypeId, type => RemoveTypeAnnotation(type, remove.Key));
                return;

            case SetFieldAnnotationOperation set:
                UpdateField(shapes, namedTypes, set.Target, field => Recreate(field with { Annotations = field.Annotations.SetItem(set.Key, set.Value) }), field => Recreate(field with { Annotations = field.Annotations.SetItem(set.Key, set.Value) }));
                return;

            case RemoveFieldAnnotationOperation remove:
                UpdateField(shapes, namedTypes, remove.Target, field => Recreate(field with { Annotations = field.Annotations.Remove(remove.Key) }), field => Recreate(field with { Annotations = field.Annotations.Remove(remove.Key) }));
                return;

            case RestrictAllowedValuesOperation restrict:
                UpdateField(shapes, namedTypes, restrict.Target, field => Recreate(field with { Constraints = MergeAllowedValues(field.Constraints, restrict.Values, restrict: true) }), field => Recreate(field with { Constraints = MergeAllowedValues(field.Constraints, restrict.Values, restrict: true) }));
                return;

            case ExtendAllowedValuesOperation extend:
                UpdateField(shapes, namedTypes, extend.Target, field => Recreate(field with { Constraints = MergeAllowedValues(field.Constraints, extend.Values, restrict: false) }), field => Recreate(field with { Constraints = MergeAllowedValues(field.Constraints, extend.Values, restrict: false) }));
                return;

            case AddEnumValueOperation add:
                UpdateEnumType(namedTypes, add.TypeId, type => AddEnumValue(type, add.Value));
                return;

            case RemoveEnumValueOperation remove:
                UpdateEnumType(namedTypes, remove.TypeId, type => RemoveEnumValue(type, remove.ValueName));
                return;

            default:
                throw new InvalidOperationException($"Unsupported graph delta operation '{operation.GetType().Name}'.");
        }
    }

    static GraphId CreateDerivedGraphId(GraphId baseGraphId, string deltaId) =>
        new($"{baseGraphId.Value}+{deltaId}");

    static void Insert<T>(List<T> values, T value, int? ordinal)
    {
        if (ordinal is null || ordinal < 0 || ordinal > values.Count)
        {
            values.Add(value);
            return;
        }

        values.Insert(ordinal.Value, value);
    }

    static int FindShapeIndex(IReadOnlyList<Shape> shapes, ShapeId shapeId)
    {
        for (var i = 0; i < shapes.Count; i++)
        {
            if (shapes[i].Id == shapeId)
                return i;
        }

        throw new KeyNotFoundException($"Shape '{shapeId.Value}' was not found.");
    }

    static int FindTypeIndex(IReadOnlyList<TypeDefinition> types, TypeId typeId)
    {
        for (var i = 0; i < types.Count; i++)
        {
            if (types[i].Id == typeId)
                return i;
        }

        throw new KeyNotFoundException($"Named type '{typeId.Value}' was not found.");
    }

    static void UpdateShape(List<Shape> shapes, ShapeId shapeId, Func<Shape, Shape> update)
    {
        var index = FindShapeIndex(shapes, shapeId);
        shapes[index] = update(shapes[index]);
    }

    static void UpdateStructuralType(List<TypeDefinition> namedTypes, TypeId typeId, Func<TypeDefinition.Structural, TypeDefinition.Structural> update)
    {
        var index = FindTypeIndex(namedTypes, typeId);
        if (namedTypes[index] is not TypeDefinition.Structural structural)
            throw new InvalidOperationException($"Named type '{typeId.Value}' is not structural.");

        namedTypes[index] = update(structural);
    }

    static void UpdateEnumType(List<TypeDefinition> namedTypes, TypeId typeId, Func<TypeDefinition.Enum, TypeDefinition.Enum> update)
    {
        var index = FindTypeIndex(namedTypes, typeId);
        if (namedTypes[index] is not TypeDefinition.Enum enumType)
            throw new InvalidOperationException($"Named type '{typeId.Value}' is not an enum.");

        namedTypes[index] = update(enumType);
    }

    static void UpdateNamedType(List<TypeDefinition> namedTypes, TypeId typeId, Func<TypeDefinition, TypeDefinition> update)
    {
        var index = FindTypeIndex(namedTypes, typeId);
        namedTypes[index] = update(namedTypes[index]);
    }

    static Shape AddField(Shape shape, FieldDefinition field, int? ordinal)
    {
        var fields = shape.Fields.ToList();
        Insert(fields, field, ordinal);
        return Recreate(shape, [.. fields]);
    }

    static Shape RemoveField(Shape shape, FieldName fieldName)
    {
        var index = FindFieldIndex(shape.Fields, fieldName.Value);
        var fields = shape.Fields.RemoveAt(index);
        return Recreate(shape, fields);
    }

    static Shape ReplaceField(Shape shape, FieldDefinition field)
    {
        var index = FindFieldIndex(shape.Fields, field.Name.Value);
        var fields = shape.Fields.SetItem(index, field);
        return Recreate(shape, fields);
    }

    static TypeDefinition.Structural AddField(TypeDefinition.Structural type, StructuralField field, int? ordinal)
    {
        var fields = type.Fields.ToList();
        Insert(fields, field, ordinal);
        return Recreate(type, [.. fields]);
    }

    static TypeDefinition.Structural RemoveField(TypeDefinition.Structural type, FieldName fieldName)
    {
        var index = FindFieldIndex(type.Fields, fieldName.Value);
        var fields = type.Fields.RemoveAt(index);
        return Recreate(type, fields);
    }

    static TypeDefinition.Structural ReplaceField(TypeDefinition.Structural type, StructuralField field)
    {
        var index = FindFieldIndex(type.Fields, field.Name.Value);
        var fields = type.Fields.SetItem(index, field);
        return Recreate(type, fields);
    }

    static int FindFieldIndex(IReadOnlyList<FieldDefinition> fields, string fieldName)
    {
        for (var i = 0; i < fields.Count; i++)
        {
            if (fields[i].MatchesName(fieldName))
                return i;
        }

        throw new KeyNotFoundException($"Shape field '{fieldName}' was not found.");
    }

    static int FindFieldIndex(IReadOnlyList<StructuralField> fields, string fieldName)
    {
        for (var i = 0; i < fields.Count; i++)
        {
            if (fields[i].MatchesIdentity(fieldName))
                return i;
        }

        throw new KeyNotFoundException($"Structural field '{fieldName}' was not found.");
    }

    static void UpdateField(
        List<Shape> shapes,
        List<TypeDefinition> namedTypes,
        GraphFieldPath target,
        Func<FieldDefinition, FieldDefinition> updateShapeField,
        Func<StructuralField, StructuralField> updateTypeField)
    {
        var location = ResolveFieldLocation(shapes, namedTypes, target);
        switch (location.Kind)
        {
            case FieldLocationKind.Shape:
            {
                var shape = shapes[location.ContainerIndex];
                var fields = shape.Fields.SetItem(location.FieldIndex, updateShapeField(shape.Fields[location.FieldIndex]));
                shapes[location.ContainerIndex] = Recreate(shape, fields);
                return;
            }

            case FieldLocationKind.Type:
            {
                var type = (TypeDefinition.Structural)namedTypes[location.ContainerIndex];
                var fields = type.Fields.SetItem(location.FieldIndex, updateTypeField(type.Fields[location.FieldIndex]));
                namedTypes[location.ContainerIndex] = Recreate(type, fields);
                return;
            }

            default:
                throw new InvalidOperationException($"Unsupported field location '{location.Kind}'.");
        }
    }

    static FieldLocation ResolveFieldLocation(IReadOnlyList<Shape> shapes, IReadOnlyList<TypeDefinition> namedTypes, GraphFieldPath target)
    {
        var fieldIdentities = GetFieldIdentities(target.Path);
        if (fieldIdentities.Length == 0)
            throw new InvalidOperationException($"Graph field target '{target}' does not contain a field segment.");

        if (target.ShapeId is { } shapeId)
        {
            var shapeIndex = FindShapeIndex(shapes, shapeId);
            var shape = shapes[shapeIndex];
            var shapeFieldIndex = FindFieldIndex(shape.Fields, fieldIdentities[0]);
            if (fieldIdentities.Length == 1)
                return new(FieldLocationKind.Shape, shapeIndex, shapeFieldIndex);

            var typeId = ResolveStructuralTypeId(shape.Fields[shapeFieldIndex].Type, namedTypes, target);
            return ResolveTypeFieldLocation(namedTypes, typeId, fieldIdentities, startIndex: 1, target);
        }

        if (target.TypeId is { } rootTypeId)
            return ResolveTypeFieldLocation(namedTypes, rootTypeId, fieldIdentities, startIndex: 0, target);

        throw new InvalidOperationException($"Graph field target '{target}' is missing an anchor.");
    }

    static FieldLocation ResolveTypeFieldLocation(IReadOnlyList<TypeDefinition> namedTypes, TypeId rootTypeId, IReadOnlyList<string> fieldIdentities, int startIndex, GraphFieldPath target)
    {
        var typeId = rootTypeId;
        for (var pathIndex = startIndex; pathIndex < fieldIdentities.Count; pathIndex++)
        {
            var typeIndex = FindTypeIndex(namedTypes, typeId);
            if (namedTypes[typeIndex] is not TypeDefinition.Structural structural)
                throw new InvalidOperationException($"Named type '{typeId.Value}' is not structural while resolving '{target}'.");

            var fieldIndex = FindFieldIndex(structural.Fields, fieldIdentities[pathIndex]);
            if (pathIndex == fieldIdentities.Count - 1)
                return new(FieldLocationKind.Type, typeIndex, fieldIndex);

            typeId = ResolveStructuralTypeId(structural.Fields[fieldIndex].Type, namedTypes, target);
        }

        throw new InvalidOperationException($"Unable to resolve graph field target '{target}'.");
    }

    static TypeId ResolveStructuralTypeId(TypeRef type, IReadOnlyList<TypeDefinition> namedTypes, GraphFieldPath target)
    {
        var current = type;
        while (current is ArrayTypeRef array)
            current = array.ElementType;

        if (current is not NamedTypeRef named)
            throw new InvalidOperationException($"Field target '{target}' traverses a non-named structural type '{current.GetType().Name}'.");

        var index = FindTypeIndex(namedTypes, named.TypeId);
        if (namedTypes[index] is not TypeDefinition.Structural)
            throw new InvalidOperationException($"Field target '{target}' traverses non-structural named type '{named.TypeId.Value}'.");

        return named.TypeId;
    }

    static ImmutableArray<string> GetFieldIdentities(FieldPath path)
    {
        return
        [
            .. path.Segments
                .Where(x => x.Kind == SegmentKind.Field)
                .Select(x => x.Segment!)
        ];
    }

    static ImmutableArray<ShapeConstraint> AddConstraint(ImmutableArray<ShapeConstraint> constraints, ShapeConstraint constraint)
    {
        return constraints.Any(x => EqualityComparer<ShapeConstraint>.Default.Equals(x, constraint))
            ? constraints
            : constraints.Add(constraint);
    }

    static ImmutableArray<ShapeConstraint> RemoveConstraint(ImmutableArray<ShapeConstraint> constraints, ShapeConstraint constraint) => 
        [.. constraints.Where(x => !EqualityComparer<ShapeConstraint>.Default.Equals(x, constraint))];

    static ImmutableArray<ShapeConstraint> MergeAllowedValues(ImmutableArray<ShapeConstraint> constraints, ImmutableArray<string> values, bool restrict)
    {
        var requested = NormalizeAllowedValues(values);
        var existingIndex = -1;
        AllowedValuesConstraint? existing = null;
        for (var i = 0; i < constraints.Length; i++)
        {
            if (constraints[i] is not AllowedValuesConstraint allowed)
                continue;

            existingIndex = i;
            existing = allowed;
            break;
        }

        if (existing is null)
            return constraints.Add(new AllowedValuesConstraint(requested));

        var merged = restrict
            ? existing.Values.Intersect(requested, StringComparer.Ordinal)
            : existing.Values.Union(requested, StringComparer.Ordinal);

        var normalized = NormalizeAllowedValues(merged);
        if (normalized.IsDefaultOrEmpty)
            throw new InvalidOperationException("Allowed-value restriction leaves no accepted values.");

        var replacement = existing with { Values = normalized };
        return constraints.SetItem(existingIndex, replacement);
    }

    static ImmutableArray<string> NormalizeAllowedValues(IEnumerable<string> values)
    {
        return
        [
            .. values
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.Ordinal)
                .OrderBy(x => x, StringComparer.Ordinal)
        ];
    }

    static TypeDefinition.Enum AddEnumValue(TypeDefinition.Enum type, EnumValue value)
    {
        if (type.Values.Any(x => string.Equals(x.Name, value.Name, StringComparison.Ordinal)))
            return type;

        return type with { Values = type.Values.Add(value) };
    }

    static TypeDefinition.Enum RemoveEnumValue(TypeDefinition.Enum type, string valueName)
    {
        var index = -1;
        for (var i = 0; i < type.Values.Length; i++)
        {
            if (string.Equals(type.Values[i].Name, valueName, StringComparison.Ordinal))
            {
                index = i;
                break;
            }
        }

        if (index < 0)
            return type;

        if (type.Values.Length == 1)
            throw new InvalidOperationException($"Cannot remove the last value from enum type '{type.Id.Value}'.");

        return type with { Values = type.Values.RemoveAt(index) };
    }

    static TypeDefinition SetTypeAnnotation(TypeDefinition type, AnnotationKey key, AnnotationValue value) => type switch
    {
        TypeDefinition.Structural structural => structural with { Annotations = structural.Annotations.SetItem(key, value) },
        TypeDefinition.Enum enumType => enumType with { Annotations = enumType.Annotations.SetItem(key, value) },
        TypeDefinition.Union union => union with { Annotations = union.Annotations.SetItem(key, value) },
        _ => throw new InvalidOperationException($"Unsupported named type '{type.GetType().Name}'.")
    };

    static TypeDefinition RemoveTypeAnnotation(TypeDefinition type, AnnotationKey key) => type switch
    {
        TypeDefinition.Structural structural => structural with { Annotations = structural.Annotations.Remove(key) },
        TypeDefinition.Enum enumType => enumType with { Annotations = enumType.Annotations.Remove(key) },
        TypeDefinition.Union union => union with { Annotations = union.Annotations.Remove(key) },
        _ => throw new InvalidOperationException($"Unsupported named type '{type.GetType().Name}'.")
    };

    static Shape Recreate(Shape shape, ImmutableArray<FieldDefinition> fields) =>
        new(
            id: shape.Id,
            fields: fields,
            constraints: shape.Constraints,
            annotations: shape.Annotations);

    static TypeDefinition.Structural Recreate(TypeDefinition.Structural type, ImmutableArray<StructuralField> fields) =>
        new(
            id: type.Id,
            fields: fields,
            constraints: type.Constraints,
            annotations: type.Annotations);

    static FieldDefinition Recreate(FieldDefinition field) =>
        new(
            name: field.Name,
            type: field.Type,
            cardinality: field.Cardinality,
            presence: field.Presence,
            nullability: field.Nullability,
            role: field.Role,
            mutability: field.Mutability,
            compute: field.Compute,
            constraints: field.Constraints,
            annotations: field.Annotations);

    static StructuralField Recreate(StructuralField field) =>
        new(
            name: field.Name,
            type: field.Type,
            cardinality: field.Cardinality,
            presence: field.Presence,
            nullability: field.Nullability,
            role: field.Role,
            constraints: field.Constraints,
            annotations: field.Annotations);

    readonly record struct FieldLocation(FieldLocationKind Kind, int ContainerIndex, int FieldIndex);

    enum FieldLocationKind
    {
        Shape = 0,
        Type = 1
    }
}
