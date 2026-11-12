using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Cohesive.Model;

namespace Cohesive.Api;

// Preparation-only metadata; serializer metadata is frozen and cached by System.Text.Json.
// Reader acceptance is independent of the semantic field presence / writer contract.
internal sealed class ApiJsonWireMetadata(JsonSerializerOptions options) : IClrShapeMetadataProvider
{
    internal const string ReaderRequired = "api.json.readerRequired/v1";

    public ClrShapeMetadata GetMetadata(ClrShapeMetadataContext context)
    {
        if (context.Target != ClrShapeMetadataTarget.Field || context.Property is not { } property)
            return ClrShapeMetadata.Empty;
        var info = options.GetTypeInfo(property.DeclaringType!);
        if (info.Kind != JsonTypeInfoKind.Object) return ClrShapeMetadata.Empty;
        var json = info.Properties.FirstOrDefault(p => p.AttributeProvider is PropertyInfo member && member == property);
        if (json is null) return ClrShapeMetadata.Empty;
        var required = json.IsRequired || (options.RespectRequiredConstructorParameters
            && json.AssociatedParameter is { HasDefaultValue: false, IsMemberInitializer: false });
        return new() { Annotations = AnnotationMap.Create(ReaderRequired, required) };
    }
}
