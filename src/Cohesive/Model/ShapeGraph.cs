using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace Cohesive.Model;

/// <summary>
/// Immutable container of semantic shapes and named types.
/// </summary>
public sealed class ShapeGraph
{
    readonly ImmutableDictionary<ShapeId, Shape> shapesById;
    readonly ImmutableDictionary<TypeId, TypeDefinition> namedTypesById;

    /// <summary>
    /// Creates an immutable shape graph.
    /// </summary>
    [JsonConstructor]
    public ShapeGraph(
        GraphId id,
        ImmutableArray<Shape> shapes,
        ImmutableArray<TypeDefinition> namedTypes = default,
        ImmutableArray<GraphDiagnostic> diagnostics = default,
        ImmutableDictionary<AnnotationKey, AnnotationValue>? annotations = null
        )
    {
        Id = id;
        Shapes = shapes.IsDefault ? [] : shapes;
        NamedTypes = namedTypes.IsDefault ? [] : namedTypes;
        Annotations = AnnotationMap.Normalize(annotations);

        List<GraphDiagnostic> allDiagnostics = [];
        allDiagnostics.AddRange(diagnostics.IsDefault ? [] : diagnostics);

        shapesById = BuildShapeLookups(Shapes, allDiagnostics);
        namedTypesById = BuildTypeLookups(NamedTypes, allDiagnostics);
        ValidateTypeReferences(Shapes, NamedTypes, allDiagnostics);

        Diagnostics = [.. allDiagnostics];
    }

    /// <summary>
    /// Stable graph build id.
    /// </summary>
    public GraphId Id { get; }

    /// <summary>
    /// Shapes contained in this graph.
    /// </summary>
    public ImmutableArray<Shape> Shapes { get; }

    /// <summary>
    /// Named types contained in this graph.
    /// </summary>
    public ImmutableArray<TypeDefinition> NamedTypes { get; }

    /// <summary>
    /// Optional graph-level metadata annotations.
    /// </summary>
    public ImmutableDictionary<AnnotationKey, AnnotationValue> Annotations { get; }

    /// <summary>
    /// Graph diagnostics emitted during compilation.
    /// </summary>
    public ImmutableArray<GraphDiagnostic> Diagnostics { get; }

    /// <summary>
    /// Returns true if the graph has one or more error diagnostics.
    /// </summary>
    public bool HasErrors => Diagnostics.Any(x => x.Severity == DiagnosticSeverity.Error);

    /// <summary>
    /// Looks up a shape by id.
    /// </summary>
    public bool TryGetShape(ShapeId id, [MaybeNullWhen(false)] out Shape definition) => 
        shapesById.TryGetValue(id, out definition!);
    
    /// <summary>
    /// Looks up a shape by id.
    /// </summary>
    public Shape? TryGetShape(ShapeId id) => TryGetShape(id, out var definition) ? definition : null;

    /// <summary>
    /// Looks up a graph-qualified shape and rejects identities belonging to another graph.
    /// </summary>
    /// <param name="id">Graph-qualified shape identifier.</param>
    /// <param name="definition">Resolved shape when the graph and local shape identifiers match.</param>
    /// <returns><see langword="true"/> when this graph contains the qualified shape; otherwise <see langword="false"/>.</returns>
    public bool TryGetShape(
        QualifiedShapeId id,
        [MaybeNullWhen(false)] out Shape definition)
    {
        if (id.GraphId != Id)
        {
            definition = null!;
            return false;
        }

        return TryGetShape(id.ShapeId, out definition);
    }

    /// <summary>
    /// Looks up a graph-qualified shape and returns <see langword="null"/> when it belongs to
    /// another graph or is not present.
    /// </summary>
    /// <param name="id">Graph-qualified shape identifier.</param>
    /// <returns>The resolved shape, or <see langword="null"/>.</returns>
    public Shape? TryGetShape(QualifiedShapeId id) =>
        TryGetShape(id, out var definition) ? definition : null;

    /// <summary>
    /// Gets a shape by id, throwing if the shape is not present.
    /// </summary>
    /// <exception cref="KeyNotFoundException">The shape was not found.</exception>
    public Shape GetShape(ShapeId id)
    {
        if (TryGetShape(id, out var definition))
            return definition;

        throw new KeyNotFoundException($"Shape graph '{Id.Value}' does not contain shape '{id.Value}'.");
    }

    /// <summary>
    /// Gets a graph-qualified shape, throwing when it belongs to another graph or is absent.
    /// </summary>
    /// <param name="id">Graph-qualified shape identifier.</param>
    /// <returns>The resolved shape.</returns>
    /// <exception cref="KeyNotFoundException">This graph does not contain the qualified shape.</exception>
    public Shape GetShape(QualifiedShapeId id)
    {
        if (TryGetShape(id, out var definition))
            return definition;

        throw new KeyNotFoundException($"Shape graph '{Id.Value}' does not contain qualified shape '{id}'.");
    }

    /// <summary>Qualifies a local shape identifier with this graph's stable identity.</summary>
    /// <param name="shapeId">Local shape identifier.</param>
    /// <returns>A graph-qualified shape identifier.</returns>
    /// <exception cref="KeyNotFoundException">This graph does not contain <paramref name="shapeId"/>.</exception>
    public QualifiedShapeId Qualify(ShapeId shapeId)
    {
        _ = GetShape(shapeId);
        return new(Id, shapeId);
    }

    /// <summary>
    /// Looks up a named type by id.
    /// </summary>
    public bool TryGetType(TypeId id, out TypeDefinition definition) => 
        namedTypesById.TryGetValue(id, out definition!);

    /// <summary>
    /// Gets a named type by id, throwing if the type is not present.
    /// </summary>
    /// <exception cref="KeyNotFoundException">The given type was not found.</exception>
    public TypeDefinition GetType(TypeId id)
    {
        if (TryGetType(id, out var definition))
            return definition;

        throw new KeyNotFoundException($"Shape graph '{Id.Value}' does not contain type '{id.Value}'.");
    }

    /// <summary>
    /// Gets a structural type by id, throwing if the type is absent or not structural.
    /// </summary>
    /// <exception cref="KeyNotFoundException">The given type was not found.</exception>
    /// <exception cref="InvalidOperationException">The given type was not a structural type.</exception>
    public TypeDefinition.Structural GetStructuralType(TypeId id)
    {
        var definition = GetType(id);
        return definition as TypeDefinition.Structural ?? throw new InvalidOperationException($"Type '{id.Value}' is '{definition.GetType().Name}', not structural.");
    }

    static ImmutableDictionary<ShapeId, Shape> BuildShapeLookups(ImmutableArray<Shape> definitions, List<GraphDiagnostic> diagnostics)
    {
        var byId = ImmutableDictionary.CreateBuilder<ShapeId, Shape>();
        foreach (var definition in definitions)
        {
            if (!byId.TryAdd(definition.Id, definition))
            {
                diagnostics.Add(new(
                    id: new("shape.duplicateId"),
                    severity: DiagnosticSeverity.Error,
                    message: $"Duplicate shape id '{definition.Id.Value}'.",
                    shapeId: definition.Id
                    )
                );
                continue;
            }
        }
        return byId.ToImmutable();
    }

    static ImmutableDictionary<TypeId, TypeDefinition> BuildTypeLookups(ImmutableArray<TypeDefinition> definitions, List<GraphDiagnostic> diagnostics)
    {
        var byId = ImmutableDictionary.CreateBuilder<TypeId, TypeDefinition>();

        foreach (var definition in definitions)
        {
            if (!byId.TryAdd(definition.Id, definition))
            {
                diagnostics.Add(new(
                    id: new("type.duplicateId"),
                    severity: DiagnosticSeverity.Error,
                    message: $"Duplicate type id '{definition.Id.Value}'.",
                    typeId: definition.Id
                    )
                );
            }
        }

        return [.. byId];
    }

    static void ValidateTypeReferences(ImmutableArray<Shape> shapes, ImmutableArray<TypeDefinition> namedTypes, List<GraphDiagnostic> diagnostics)
    {
        HashSet<TypeId> namedTypeIds = [.. namedTypes.Select(x => x.Id)];
        foreach (var shape in shapes)
        {
            foreach (var field in shape.Fields)
            {
                foreach (var referencedType in EnumerateNamedTypeReferences(field.Type))
                {
                    if (namedTypeIds.Contains(referencedType))
                        continue;

                    diagnostics.Add(new(
                        id: new("type.ref.missing"),
                        severity: DiagnosticSeverity.Error,
                        message: $"Shape '{shape.Id.Value}' field '{field.Name.Value}' references missing type '{referencedType.Value}'.",
                        shapeId: shape.Id,
                        fieldIdentity: field.Name.Value,
                        typeId: referencedType
                    ));
                }
            }
        }

        foreach (var namedType in namedTypes)
            ValidateNamedTypeReferences(namedType, namedTypeIds, diagnostics);
    }

    static void ValidateNamedTypeReferences(TypeDefinition namedType, HashSet<TypeId> namedTypeIds, List<GraphDiagnostic> diagnostics)
    {
        switch (namedType)
        {
            case TypeDefinition.Structural structural:
                foreach (var field in structural.Fields)
                {
                    foreach (var referencedType in EnumerateNamedTypeReferences(field.Type))
                    {
                        if (namedTypeIds.Contains(referencedType))
                            continue;

                        diagnostics.Add(new(
                            id: new("type.ref.missing"),
                            severity: DiagnosticSeverity.Error,
                            message: $"Named type '{structural.Id.Value}' field '{field.Name.Value}' references missing type '{referencedType.Value}'.",
                            fieldIdentity: field.Name.Value,
                            typeId: structural.Id
                        ));
                    }
                }
                break;

            case TypeDefinition.Union union:
                foreach (var unionCase in union.Cases)
                {
                    foreach (var referencedType in EnumerateNamedTypeReferences(unionCase.Type))
                    {
                        if (namedTypeIds.Contains(referencedType))
                            continue;

                        diagnostics.Add(new(
                            id: new("type.ref.missing"),
                            severity: DiagnosticSeverity.Error,
                            message: $"Union type '{union.Id.Value}' case '{unionCase.Name}' references missing type '{referencedType.Value}'.",
                            fieldIdentity: unionCase.Name,
                            typeId: union.Id
                        ));
                    }
                }
                break;

            case TypeDefinition.Enum:
                break;
        }
    }

    static IEnumerable<TypeId> EnumerateNamedTypeReferences(TypeRef type)
    {
        switch (type)
        {
            case NamedTypeRef named:
                yield return named.TypeId;
                yield break;

            case ArrayTypeRef array:
                foreach (var nested in EnumerateNamedTypeReferences(array.ElementType))
                    yield return nested;
                yield break;

            case ObjectTypeRef obj:
                foreach (var field in obj.Fields)
                {
                    foreach (var nested in EnumerateNamedTypeReferences(field.Type))
                        yield return nested;
                }
                yield break;

            case ScalarTypeRef:
            case EnumTypeRef:
            case EntityReferenceTypeRef:
            case QuantityTypeRef:
            case OpaqueRuntimeTypeRef:
            case JsonTypeRef:
                yield break;
        }

        throw new InvalidOperationException($"Unsupported type reference '{type.GetType().Name}'.");
    }
}
