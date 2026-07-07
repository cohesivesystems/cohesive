using System.Reflection;

namespace Cohesive.Model;

/// <summary>
/// Identifies the CLR-derived shape artifact currently being described.
/// </summary>
public enum ClrShapeMetadataTarget
{
    /// <summary>
    /// Metadata for a root <see cref="Shape"/>.
    /// </summary>
    Shape = 0,

    /// <summary>
    /// Metadata for a named <see cref="TypeDefinition"/>.
    /// </summary>
    Type = 1,

    /// <summary>
    /// Metadata for a <see cref="FieldDefinition"/> or <see cref="StructuralField"/>.
    /// </summary>
    Field = 2
}

/// <summary>
/// Reflection context supplied to CLR shape metadata providers.
/// </summary>
public sealed record ClrShapeMetadataContext
{
    ClrShapeMetadataContext(
        ClrShapeMetadataTarget target,
        Type clrType,
        ICustomAttributeProvider attributeProvider,
        Type? declaringType,
        PropertyInfo? property
        )
    {
        Target = target;
        ClrType = Guard.RequireNotNull(clrType);
        AttributeProvider = Guard.RequireNotNull(attributeProvider);
        DeclaringType = declaringType;
        Property = property;
    }

    /// <summary>
    /// Shape artifact currently being built.
    /// </summary>
    public ClrShapeMetadataTarget Target { get; }

    /// <summary>
    /// CLR type associated with the current artifact. For fields this is the property type.
    /// </summary>
    public Type ClrType { get; }

    /// <summary>
    /// CLR type that declares <see cref="Property"/> when <see cref="Target"/> is <see cref="ClrShapeMetadataTarget.Field"/>.
    /// </summary>
    public Type? DeclaringType { get; }

    /// <summary>
    /// CLR property associated with the current field artifact.
    /// </summary>
    public PropertyInfo? Property { get; }

    /// <summary>
    /// Reflection surface from which custom attributes can be read.
    /// </summary>
    public ICustomAttributeProvider AttributeProvider { get; }

    /// <summary>
    /// Creates metadata context for a root shape.
    /// </summary>
    public static ClrShapeMetadataContext ForShape(Type clrType)
    {
        ArgumentNullException.ThrowIfNull(clrType);
        return new(
            target: ClrShapeMetadataTarget.Shape,
            clrType: clrType,
            attributeProvider: clrType,
            declaringType: null,
            property: null);
    }

    /// <summary>
    /// Creates metadata context for a named type definition.
    /// </summary>
    public static ClrShapeMetadataContext ForType(Type clrType)
    {
        ArgumentNullException.ThrowIfNull(clrType);
        return new(
            target: ClrShapeMetadataTarget.Type,
            clrType: clrType,
            attributeProvider: clrType,
            declaringType: null,
            property: null);
    }

    /// <summary>
    /// Creates metadata context for a CLR property-backed shape field.
    /// </summary>
    public static ClrShapeMetadataContext ForField(PropertyInfo property)
    {
        ArgumentNullException.ThrowIfNull(property);
        return new(
            target: ClrShapeMetadataTarget.Field,
            clrType: property.PropertyType,
            attributeProvider: property,
            declaringType: property.DeclaringType,
            property: property);
    }
}
