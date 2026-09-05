using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection;
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
    /// <typeparam name="T">Ordinary CLR object type.</typeparam>
    /// <returns>The cached canonical entity definition using the default CLR projection.</returns>
    /// <exception cref="InvalidOperationException">The type has no readable fields or has ambiguous semantic names.</exception>
    public static EntityDefinition For<T>() where T : notnull => For(typeof(T));

    /// <summary>
    /// Returns a cached semantic entity definition for a CLR object type with no transitions.
    /// </summary>
    /// <param name="clrType">Ordinary CLR object type.</param>
    /// <returns>The cached canonical entity definition using the default CLR projection.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="clrType"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The type has no readable fields or has ambiguous semantic names.</exception>
    public static EntityDefinition For(Type clrType)
    {
        ArgumentNullException.ThrowIfNull(clrType);
        var effectiveType = Nullable.GetUnderlyingType(clrType) ?? clrType;
        return DefinitionsByClrType.GetOrAdd(effectiveType, static type => Build(type, new(type.Name), ClrTypeRefMapper));
    }

    /// <summary>Builds a definition with explicit stable identity and CLR value contracts.</summary>
    /// <typeparam name="T">Ordinary CLR object type.</typeparam>
    /// <param name="name">Stable semantic entity name, independent of the CLR type name.</param>
    /// <param name="typeRefMapper">Resolved type projection; defaults to the framework projection.</param>
    /// <returns>A materialized definition retaining no mapper or authoring callback.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty.</exception>
    /// <exception cref="InvalidOperationException">The type has no readable fields or has ambiguous semantic names.</exception>
    /// <remarks>Configured definitions are not cached by CLR type; callers own and reuse the resolved revision.</remarks>
    public static EntityDefinition For<T>(EntityTypeName name, IClrTypeRefMapper? typeRefMapper = null)
        where T : notnull => Build(typeof(T), name, typeRefMapper ?? ClrTypeRefMapper);

    static EntityDefinition Build(Type clrType, EntityTypeName name, IClrTypeRefMapper typeRefMapper)
    {
        var properties = ShapeTypeInspector.GetReadableProperties(clrType);
        if (properties.Length == 0)
        {
            throw new InvalidOperationException(
                $"CLR type '{clrType.Name}' does not expose any readable public instance properties and cannot be represented as an entity definition.");
        }

        var nullabilityContext = new NullabilityInfoContext();
        var fields = ImmutableArray.CreateBuilder<FieldDefinition>(properties.Length);
        for (var index = 0; index < properties.Length; index++)
            fields.Add(BuildFieldDefinition(properties[index], nullabilityContext, typeRefMapper));
        fields.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.Name.Value, right.Name.Value));
        for (var index = 1; index < fields.Count; index++)
        {
            if (fields[index - 1].Name == fields[index].Name)
                throw new InvalidOperationException($"CLR type '{clrType.Name}' maps multiple properties to semantic field '{fields[index].Name}'.");
        }
        return new(
            name: name,
            fields: fields.MoveToImmutable()
            );
    }

    static FieldDefinition BuildFieldDefinition(PropertyInfo property, NullabilityInfoContext nullabilityContext, IClrTypeRefMapper typeRefMapper)
    {
        ArgumentNullException.ThrowIfNull(property);
        ArgumentNullException.ThrowIfNull(nullabilityContext);

        var nullability = nullabilityContext.Create(property);
        var mappedType = typeRefMapper.Map(property.PropertyType, nullability);
        var cardinality = FieldCardinality.Single;
        if (mappedType is ArrayTypeRef arrayType)
        {
            cardinality = FieldCardinality.Many;
            mappedType = arrayType.ElementType;
        }

        var isOptional = IsOptional(property.PropertyType, nullability);
        return new(
            name: new(DefaultClrTypeRefMapper.GetSerializedMemberName(property)),
            type: mappedType,
            cardinality: cardinality,
            presence: isOptional ? FieldPresence.Optional : FieldPresence.Required,
            nullability: isOptional ? FieldNullability.Nullable : FieldNullability.NonNullable
            );
    }

    static bool IsOptional(Type clrType, NullabilityInfo? nullability)
    {
        if (Nullable.GetUnderlyingType(clrType) is not null)
            return true;

        return !clrType.IsValueType && nullability?.ReadState == NullabilityState.Nullable;
    }
}
