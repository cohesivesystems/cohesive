namespace Cohesive.Model;

/// <summary>
/// Standard shape role annotation values.
/// </summary>
public static class ShapeRoles
{
    public const string Entity = "entity";
    public const string ValueObject = "valueObject";
    public const string Dto = "dto";
    public const string Contract = "contract";
    public const string Projection = "projection";
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
}
