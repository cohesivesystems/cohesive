namespace Cohesive.Model;

/// <summary>
/// Standard shape role annotation values.
/// </summary>
public static class ShapeRoles
{
    /// <summary>A shape that represents an entity.</summary>
    public const string Entity = "entity";
    
    /// <summary>A shape that represents a value object.</summary>
    public const string ValueObject = "valueObject";
    
    /// <summary>A shape that represents a DTO.</summary>
    public const string Dto = "dto";
    
    /// <summary>A shape that represents a contract.</summary>
    public const string Contract = "contract";
    
    /// <summary>A shape that represents a projection.</summary>
    public const string Projection = "projection";
    
    /// <summary>A shape that represents a transport message.</summary>
    public const string Transport = "transport";
}

/// <summary>
/// Standard annotation keys for shape metadata.
/// </summary>
public static class ShapeAnnotationKeys
{
    /// <summary>
    /// Coarse shape role such as transport, dto, entity, or projection.
    /// </summary>
    public const string Role = "shape.role";

    /// <summary>
    /// Stable logical entity type represented by an entity shape.
    /// </summary>
    public const string EntityType = "shape.entityType";
}
