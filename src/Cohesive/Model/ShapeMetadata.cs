using Cohesive.Model;

namespace Cohesive.Model;

/// <summary>
/// Navigation cardinality for shape relationships.
/// </summary>
public enum NavigationCardinality
{
    One = 0,
    Many = 1
}

/// <summary>
/// Navigation edge from a source shape to a target shape through a field.
/// </summary>
public sealed record ShapeNavigation
{
    public ShapeNavigation(string viaField, ShapeId target, NavigationCardinality cardinality)
    {
        ViaField = Guard.RequireNotNullOrWhiteSpace(viaField);
        Target = target;
        Cardinality = cardinality;
    }

    public string ViaField { get; init; }

    public ShapeId Target { get; init; }

    public NavigationCardinality Cardinality { get; init; }

}

/// <summary>
/// Shape metadata used by compiler and planners.
/// </summary>
public sealed record ShapeDescriptor
{
    /// <summary>
    /// Creates a shape descriptor.
    /// </summary>
    public ShapeDescriptor(
        ShapeId id,
        EntityTypeName entityType,
        IReadOnlyList<string> fields,
        IReadOnlyList<ShapeNavigation>? navigations = null)
    {
        Id = id;
        EntityType = entityType;
        Fields = Guard.RequireNotNull(fields).ToArray();
        Navigations = navigations ?? [];
        Definition = new Shape(
            id: id,
            fields: [.. Fields.Select(x => new FieldDefinition(
                name: new FieldName(x),
                type: new OpaqueRuntimeTypeRef("unknown"),
                presence: FieldPresence.Optional,
                nullability: FieldNullability.Nullable))],
            role: ShapeRoles.Entity);
    }

    /// <summary>
    /// Creates a shape descriptor from a canonical shape definition.
    /// </summary>
    public ShapeDescriptor(
        Shape definition,
        EntityTypeName entityType,
        IReadOnlyList<ShapeNavigation>? navigations = null)
    {
        Definition = Guard.RequireNotNull(definition);
        Id = definition.Id;
        EntityType = entityType;
        Fields = [.. definition.Fields.Select(x => x.Name.Value)];
        Navigations = navigations ?? [];
    }

    /// <summary>
    /// Shape id.
    /// </summary>
    public ShapeId Id { get; init; }

    /// <summary>
    /// Backing entity name.
    /// </summary>
    public EntityTypeName EntityType { get; init; }

    /// <summary>
    /// Stable field identities in this shape.
    /// </summary>
    public IReadOnlyList<string> Fields { get; init; }

    /// <summary>
    /// Outbound navigation definitions.
    /// </summary>
    public IReadOnlyList<ShapeNavigation> Navigations { get; init; }

    /// <summary>
    /// Canonical shape definition.
    /// </summary>
    public Shape Definition { get; init; }
}

/// <summary>
/// Registry of shape descriptors.
/// </summary>
public sealed class ShapeCatalog
{
    readonly Dictionary<ShapeId, ShapeDescriptor> shapes = [];

    /// <summary>
    /// Registers a shape descriptor.
    /// </summary>
    public ShapeCatalog Register(ShapeDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        shapes[descriptor.Id] = descriptor;
        return this;
    }

    /// <summary>
    /// Registers a canonical shape definition.
    /// </summary>
    public ShapeCatalog Register(
        Shape definition,
        EntityTypeName entityType,
        IReadOnlyList<ShapeNavigation>? navigations = null)
    {
        return Register(new ShapeDescriptor(definition, entityType, navigations));
    }

    /// <summary>
    /// Builds an immutable shape graph from registered shape descriptors.
    /// </summary>
    public ShapeGraph BuildGraph(
        GraphId? graphId = null,
        IReadOnlyList<TypeDefinition>? namedTypes = null,
        IReadOnlyList<GraphDiagnostic>? diagnostics = null)
    {
        var id = graphId ?? GraphId.New();
        return new ShapeGraph(
            id: id,
            shapes: [.. shapes.Values.Select(x => x.Definition)],
            namedTypes: namedTypes is null ? [] : [.. namedTypes],
            diagnostics: diagnostics is null ? [] : [.. diagnostics]);
    }
}
