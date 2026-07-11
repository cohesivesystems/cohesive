using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cohesive.Relations.Model;

namespace Cohesive.Relations.Mapping;

/// <summary>
/// Reflection-configured builder for object-to-observed-shape mapping.
/// </summary>
public sealed class ObjectObservationMapperBuilder<T>
{
    readonly ShapeId schema;
    readonly ShapeMappingContext context;
    readonly List<PropertyFieldMap> mappings = [];
    ObjectObservationMetadataConventionOptions metadataConventions;
    Func<T, string>? idExtractorOverride;
    Func<T, long>? versionExtractorOverride;

    internal ObjectObservationMapperBuilder(ShapeId schema, ShapeMappingContext? context = null)
    {
        this.schema = schema;
        this.context = context ?? ShapeMappingContext.Default;
        metadataConventions = this.context.ObjectObservationMetadataConventions;
    }

    /// <summary>
    /// Maps an object property to a field identity.
    /// </summary>
    public ObjectObservationMapperBuilder<T> Map(string propertyName, string fieldIdentity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(propertyName);
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldIdentity);
        mappings.Add(new(propertyName, fieldIdentity));
        return this;
    }

    /// <summary>
    /// Maps all readable public instance properties using field names as identities.
    /// </summary>
    public ObjectObservationMapperBuilder<T> MapAll() => 
        MapAllCore(resolveFieldIdentity: null);

    /// <summary>
    /// Maps all readable public instance properties using a field-identity resolver.
    /// </summary>
    public ObjectObservationMapperBuilder<T> MapAll(Func<PropertyInfo, string> resolveFieldIdentity)
    {
        ArgumentNullException.ThrowIfNull(resolveFieldIdentity);
        return MapAllCore(resolveFieldIdentity);
    }

    /// <summary>
    /// Maps all readable public instance properties using <see cref="JsonPropertyNameAttribute"/> values, the given override or the property name as field identities.
    /// </summary>
    /// <param name="requireAttribute">True to require every mapped property to define <see cref="JsonPropertyNameAttribute"/>; false to allow fallback resolution.</param>
    /// <param name="resolveFieldIdentity">Optional fallback resolver used when <see cref="JsonPropertyNameAttribute"/> is missing. If omitted, property name is used as the field identity.</param>
    /// <exception cref="InvalidOperationException"><see cref="JsonPropertyNameAttribute"/> is missing</exception>
    public ObjectObservationMapperBuilder<T> MapAllFromJsonPropertyName(bool requireAttribute = false, Func<PropertyInfo, string>? resolveFieldIdentity = null)
    {
        foreach (var property in GetReadableProperties())
            Map(propertyName: property.Name, ResolveFieldIdentityFromJsonPropertyName(property, requireAttribute, resolveFieldIdentity));
        return this;
    }

    /// <summary>
    /// Configures convention names used when auto-reading id/version metadata.
    /// </summary>
    public ObjectObservationMapperBuilder<T> WithMetadataConventions(ObjectObservationMetadataConventionOptions conventions)
    {
        metadataConventions = Guard.RequireNotNull(conventions);
        return this;
    }

    /// <summary>
    /// Overrides id extraction for metadata-aware mapping.
    /// </summary>
    public ObjectObservationMapperBuilder<T> WithId<TValue>(
        Expression<Func<T, TValue>> selector,
        Func<TValue, string>? convert = null)
    {
        ArgumentNullException.ThrowIfNull(selector);
        var getter = selector.Compile();
        idExtractorOverride = source =>
        {
            var value = getter(source);
            if (convert is not null)
                return Guard.RequireNotNullOrWhiteSpace(convert(value));
            return ConvertToRequiredString(value, nameof(idExtractorOverride));
        };
        return this;
    }

    /// <summary>
    /// Overrides version extraction for metadata-aware mapping.
    /// </summary>
    public ObjectObservationMapperBuilder<T> WithVersion<TValue>(
        Expression<Func<T, TValue>> selector,
        Func<TValue, long>? convert = null)
    {
        ArgumentNullException.ThrowIfNull(selector);
        var getter = selector.Compile();
        versionExtractorOverride = source =>
        {
            var value = getter(source);
            if (convert is not null)
                return convert(value);
            return ConvertToVersion(value, nameof(versionExtractorOverride));
        };
        return this;
    }

    /// <summary>
    /// Builds the mapper.
    /// </summary>
    /// <exception cref="InvalidOperationException">Must define at least one property mapping</exception>
    /// <exception cref="InvalidOperationException">Contains duplicate target field identity</exception>
    /// <exception cref="InvalidOperationException">Does not define mapped property</exception>
    public ObjectObservationMapper<T> Build()
    {
        var readableProperties = GetReadableProperties();
        var effectiveMappings = BuildEffectiveMappings(readableProperties);
        if (effectiveMappings.Count == 0)
            throw new InvalidOperationException($"Mapper for '{typeof(T).Name}' must define at least one property mapping.");
        
        var duplicateTargetField = effectiveMappings.TryGetDuplicateByKey(x => x.FieldIdentity);
        if (duplicateTargetField is not null)
            throw new InvalidOperationException($"Mapper for '{typeof(T).Name}' contains duplicate target field identity '{duplicateTargetField.FieldIdentity}'.");

        var properties = readableProperties.ToDictionary(x => x.Name, StringComparer.Ordinal);
        List<PropertyAccessor<T>> accessors = [];
        foreach (var mapping in effectiveMappings)
        {
            if (!properties.TryGetValue(mapping.PropertyName, out var property))
                throw new InvalidOperationException($"Type '{typeof(T).Name}' does not define mapped property '{mapping.PropertyName}'.");
            accessors.Add(new(FieldIdentity: mapping.FieldIdentity, Getter: CompileGetter(property)));
        }

        var layout = new ObservationLayout(schema, [.. accessors.Select(x => x.FieldIdentity)]);
        var metadata = ResolveMetadataAccessors(readableProperties);
        return new(layout, [.. accessors], metadata);
    }

    IReadOnlyList<PropertyFieldMap> BuildEffectiveMappings(IReadOnlyList<PropertyInfo> readableProperties)
    {
        Dictionary<string, PropertyFieldMap> explicitByProperty = new(StringComparer.Ordinal);
        foreach (var mapping in mappings)
            explicitByProperty[mapping.PropertyName] = mapping;

        List<PropertyFieldMap> effective = [];
        HashSet<string> knownProperties = new(StringComparer.Ordinal);

        foreach (var property in readableProperties)
        {
            knownProperties.Add(property.Name);
            if (explicitByProperty.TryGetValue(property.Name, out var mapping))
            {
                effective.Add(mapping);
                continue;
            }

            effective.Add(new(property.Name, context.ResolveImplicitFieldIdentity(property)));
        }

        foreach (var mapping in explicitByProperty.Values.Where(x => !knownProperties.Contains(x.PropertyName)))
        {
            effective.Add(mapping);
        }

        return effective;
    }

    ObjectObservationMapperBuilder<T> MapAllCore(Func<PropertyInfo, string>? resolveFieldIdentity)
    {
        foreach (var property in GetReadableProperties())
            Map(propertyName: property.Name, resolveFieldIdentity?.Invoke(property) ?? property.Name);
        return this;
    }

    ObjectObservationMetadataAccessors<T> ResolveMetadataAccessors(IReadOnlyList<PropertyInfo> properties)
    {
        var byName = properties.ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);
        var id = idExtractorOverride ?? ResolveIdExtractorByConvention(properties, byName);
        var version = versionExtractorOverride ?? ResolveVersionExtractorByConvention(properties, byName);
        return new(id, version);
    }

    Func<T, string>? ResolveIdExtractorByConvention(
        IReadOnlyList<PropertyInfo> properties,
        IReadOnlyDictionary<string, PropertyInfo> byName
        )
    {
        var property = ResolveByConvention(
            properties,
            byName,
            metadataConventions.IdPropertyNames,
            metadataConventions.IdJsonPropertyNames);
        if (property is null)
            return null;

        var getter = CompileGetter(property);
        return source => ConvertToRequiredString(getter(source), property.Name);
    }

    Func<T, long>? ResolveVersionExtractorByConvention(
        IReadOnlyList<PropertyInfo> properties,
        IReadOnlyDictionary<string, PropertyInfo> byName
        )
    {
        var property = ResolveByConvention(
            properties,
            byName,
            metadataConventions.VersionPropertyNames,
            metadataConventions.VersionJsonPropertyNames);
        if (property is null)
            return null;

        var getter = CompileGetter(property);
        return source => ConvertToVersion(getter(source), property.Name);
    }

    PropertyInfo? ResolveByConvention(
        IReadOnlyList<PropertyInfo> properties,
        IReadOnlyDictionary<string, PropertyInfo> byName,
        IReadOnlyList<string>? propertyNames,
        IReadOnlyList<string>? jsonPropertyNames
        )
    {
        if (propertyNames is not null)
        {
            foreach (var candidate in propertyNames)
            {
                if (string.IsNullOrWhiteSpace(candidate))
                    continue;
                if (byName.TryGetValue(candidate, out var property))
                    return property;
            }
        }

        if (!metadataConventions.UseJsonPropertyNameAttributes || jsonPropertyNames is null || jsonPropertyNames.Count == 0)
            return null;

        foreach (var property in properties)
        {
            var attribute = property.GetCustomAttribute<JsonPropertyNameAttribute>(inherit: true);
            if (attribute is null)
                continue;

            if (jsonPropertyNames.Any(candidate => string.Equals(candidate, attribute.Name, StringComparison.OrdinalIgnoreCase)))
                return property;
        }

        return null;
    }

    IReadOnlyList<PropertyInfo> GetReadableProperties() => context.GetReadableProperties(typeof(T));

    static string ResolveFieldIdentityFromJsonPropertyName(
        PropertyInfo property,
        bool requireAttribute,
        Func<PropertyInfo, string>? resolveFieldIdentity
        )
    {
        var attribute = property.GetCustomAttribute<JsonPropertyNameAttribute>(inherit: true);
        if (attribute is not null)
            return attribute.Name;
        if (requireAttribute)
            throw new InvalidOperationException($"Property '{property.Name}' on '{typeof(T).Name}' is missing JsonPropertyNameAttribute.");
        return resolveFieldIdentity?.Invoke(property) ?? property.Name;
    }

    static Func<T, object?> CompileGetter(PropertyInfo property)
    {
        var input = Expression.Parameter(typeof(T), "source");
        var access = Expression.Property(input, property);
        var convert = Expression.Convert(access, typeof(object));
        return Expression.Lambda<Func<T, object?>>(convert, input).Compile();
    }

    static string ConvertToRequiredString(object? value, string sourceName)
    {
        if (value is JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.String => Guard.RequireNotNullOrWhiteSpace(element.GetString()),
                JsonValueKind.Null => throw new InvalidOperationException($"Metadata value from '{sourceName}' cannot be null."),
                _ => Guard.RequireNotNullOrWhiteSpace(element.ToString())
            };
        }

        return value switch
        {
            null => throw new InvalidOperationException($"Metadata value from '{sourceName}' cannot be null."),
            string text => Guard.RequireNotNullOrWhiteSpace(text),
            _ => Guard.RequireNotNullOrWhiteSpace(Convert.ToString(value, CultureInfo.InvariantCulture))
        };
    }

    static long ConvertToVersion(object? value, string sourceName)
    {
        if (value is null)
            throw new InvalidOperationException($"Version value from '{sourceName}' cannot be null.");

        if (value is JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.Number when element.TryGetInt64(out var parsed) => parsed,
                JsonValueKind.String when long.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
                JsonValueKind.Null => throw new InvalidOperationException($"Version value from '{sourceName}' cannot be null."),
                _ => throw new InvalidOperationException($"Version value from '{sourceName}' must be numeric.")
            };
        }

        try
        {
            return Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException)
        {
            throw new InvalidOperationException($"Version value from '{sourceName}' must be numeric.", exception);
        }
    }

    sealed record PropertyFieldMap(string PropertyName, string FieldIdentity);
}
