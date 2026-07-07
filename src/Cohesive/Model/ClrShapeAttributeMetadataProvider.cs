using System.Reflection;

namespace Cohesive.Model;

/// <summary>
/// Reads built-in CLR shape metadata attributes.
/// </summary>
sealed class ClrShapeAttributeMetadataProvider : IClrShapeMetadataProvider
{
    public static ClrShapeAttributeMetadataProvider Instance { get; } = new();

    ClrShapeAttributeMetadataProvider()
    {
    }

    public ClrShapeMetadata GetMetadata(ClrShapeMetadataContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.Target switch
        {
            ClrShapeMetadataTarget.Shape => GetShapeMetadata(context.AttributeProvider),
            ClrShapeMetadataTarget.Type => GetTypeMetadata(context.AttributeProvider),
            _ => ClrShapeMetadata.Empty
        };
    }

    static ClrShapeMetadata GetShapeMetadata(ICustomAttributeProvider attributeProvider)
    {
        var attribute = GetAttribute<ShapeDefinitionAttribute>(attributeProvider);
        if (attribute is null)
            return ClrShapeMetadata.Empty;

        return new()
        {
            ShapeId = new ShapeId(attribute.Id),
            ShapeRole = attribute.HasRole ? attribute.Role : null
        };
    }

    static ClrShapeMetadata GetTypeMetadata(ICustomAttributeProvider attributeProvider)
    {
        var attribute = GetAttribute<ShapeTypeAttribute>(attributeProvider);
        if (attribute is null)
            return ClrShapeMetadata.Empty;

        return new()
        {
            TypeId = new TypeId(attribute.Id)
        };
    }

    static T? GetAttribute<T>(ICustomAttributeProvider attributeProvider) where T : Attribute
    {
        var attributes = attributeProvider.GetCustomAttributes(typeof(T), inherit: true);
        return attributes.OfType<T>().FirstOrDefault();
    }
}
