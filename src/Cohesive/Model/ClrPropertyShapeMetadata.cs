using System.Reflection;

namespace Cohesive.Model;

/// <summary>
/// Cached CLR property metadata used for shape inference.
/// </summary>
public readonly record struct ClrPropertyShapeMetadata
{
    /// <summary>
    /// Creates property metadata.
    /// </summary>
    public ClrPropertyShapeMetadata(PropertyInfo property, bool isOptional)
    {
        Property = Guard.RequireNotNull(property);
        IsOptional = isOptional;
    }

    /// <summary>
    /// Backing CLR property.
    /// </summary>
    public PropertyInfo Property { get; }

    /// <summary>
    /// Property name.
    /// </summary>
    public string Name => Property.Name;

    /// <summary>
    /// Property type.
    /// </summary>
    public Type PropertyType => Property.PropertyType;

    /// <summary>
    /// True when nullability metadata marks this property as optional.
    /// </summary>
    public bool IsOptional { get; }
}
