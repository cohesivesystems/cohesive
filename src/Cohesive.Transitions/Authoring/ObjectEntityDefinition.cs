using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json.Serialization;
using Cohesive.Transitions.Model;

namespace Cohesive.Transitions.Authoring;

/// <summary>
/// Builds cached definition-only entity models for CLR object shapes.
/// </summary>
public static class ObjectEntityDefinition
{
    static readonly ConcurrentDictionary<Type, EntityDefinition> DefinitionsByClrType = [];
    static readonly IClrTypeRefMapper ClrTypeRefMapper = new DefaultClrTypeRefMapper();

    /// <summary>
    /// Returns a cached semantic entity definition for a CLR object type with no transitions.
    /// </summary>
    public static EntityDefinition For<T>() where T : notnull => For(typeof(T));

    /// <summary>
    /// Returns a cached semantic entity definition for a CLR object type with no transitions.
    /// </summary>
    public static EntityDefinition For(Type clrType)
    {
        ArgumentNullException.ThrowIfNull(clrType);
        var effectiveType = Nullable.GetUnderlyingType(clrType) ?? clrType;
        return DefinitionsByClrType.GetOrAdd(effectiveType, static type => Build(type));
    }

    static EntityDefinition Build(Type clrType)
    {
        var properties = ShapeTypeInspector.GetReadableProperties(clrType);
        if (properties.Length == 0)
        {
            throw new InvalidOperationException(
                $"CLR type '{clrType.Name}' does not expose any readable public instance properties and cannot be represented as an entity definition.");
        }

        var nullabilityContext = new NullabilityInfoContext();
        var fields = ImmutableArray.CreateRange(properties.Select(property => BuildFieldDefinition(property, nullabilityContext)));
        return new(
            name: new(clrType.Name),
            fields: fields
            );
    }

    static FieldDefinition BuildFieldDefinition(PropertyInfo property, NullabilityInfoContext nullabilityContext)
    {
        ArgumentNullException.ThrowIfNull(property);
        ArgumentNullException.ThrowIfNull(nullabilityContext);

        var nullability = nullabilityContext.Create(property);
        var mappedType = ClrTypeRefMapper.Map(property.PropertyType, nullability);
        var cardinality = FieldCardinality.Single;
        if (mappedType is ArrayTypeRef arrayType)
        {
            cardinality = FieldCardinality.Many;
            mappedType = arrayType.ElementType;
        }

        var isOptional = IsOptional(property.PropertyType, nullability);
        return new(
            name: new(ResolveFieldName(property)),
            type: mappedType,
            cardinality: cardinality,
            presence: isOptional ? FieldPresence.Optional : FieldPresence.Required,
            nullability: isOptional ? FieldNullability.Nullable : FieldNullability.NonNullable
            );
    }

    static string ResolveFieldName(PropertyInfo property) =>
        property.GetCustomAttribute<JsonPropertyNameAttribute>(inherit: true)?.Name
        ?? property.Name;

    static bool IsOptional(Type clrType, NullabilityInfo? nullability)
    {
        if (Nullable.GetUnderlyingType(clrType) is not null)
            return true;

        return !clrType.IsValueType && nullability?.ReadState == NullabilityState.Nullable;
    }
}
