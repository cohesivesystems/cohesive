using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cohesive.Relations.Model;

namespace Cohesive.Relations.Mapping;

/// <summary>
/// Shared mapper configuration and reflection cache access across multiple CLR shapes.
/// </summary>
public sealed class ShapeMappingContext
{
    static readonly Lazy<ShapeMappingContext> Shared = new(static () => new ShapeMappingContext());
    readonly ConcurrentDictionary<ObjectMapperCacheKey, object> objectObservationMappers = [];
    readonly ConcurrentDictionary<ObservedMapperCacheKey, object> observationObjectMappers = [];

    /// <summary>
    /// Global default context used by static mapper factories.
    /// </summary>
    public static ShapeMappingContext Default => Shared.Value;

    /// <summary>
    /// True to use <see cref="JsonPropertyNameAttribute"/> when deriving implicit field identities.
    /// </summary>
    public bool UseJsonPropertyNameAttributesForFieldIdentity
    {
        get;
        set
        {
            if (field == value)
                return;
            field = value;
            ClearMapperCaches();
        }
    } = true;

    /// <summary>
    /// True to require <see cref="JsonPropertyNameAttribute"/> when deriving implicit field identities.
    /// </summary>
    public bool RequireJsonPropertyNameAttributeForFieldIdentity
    {
        get;
        set
        {
            if (field == value)
                return;
            field = value;
            ClearMapperCaches();
        }
    }

    /// <summary>
    /// Optional fallback resolver used when no <see cref="JsonPropertyNameAttribute"/> exists.
    /// </summary>
    public Func<PropertyInfo, string>? ResolveFieldIdentityFallback
    {
        get;
        set
        {
            if (ReferenceEquals(field, value))
                return;
            field = value;
            ClearMapperCaches();
        }
    }

    /// <summary>
    /// Default serializer options for object-to-observed-shape mapping.
    /// </summary>
    public JsonSerializerOptions ObjectObservationSerializerOptions
    {
        get;
        set
        {
            if (ReferenceEquals(field, value))
                return;
            field = Guard.RequireNotNull(value);
            ClearMapperCaches();
        }
    } = CreateDefaultSerializerOptions();

    /// <summary>
    /// Default serializer options for observed-shape-to-object mapping.
    /// </summary>
    public JsonSerializerOptions ObservationObjectSerializerOptions
    {
        get;
        set
        {
            if (ReferenceEquals(field, value))
                return;
            field = Guard.RequireNotNull(value);
            ClearMapperCaches();
        }
    } = CreateDefaultSerializerOptions();

    /// <summary>
    /// Default behavior used when an observation-to-object mapper encounters a missing field.
    /// </summary>
    public ObservationObjectMissingFieldBehavior ObservationObjectMissingFieldBehavior
    {
        get;
        set
        {
            if (field == value)
                return;
            field = value;
            ClearMapperCaches();
        }
    } = ObservationObjectMissingFieldBehavior.UseDefaultForOptionalMembers;

    /// <summary>
    /// Default metadata conventions for object-to-observed-shape mapping.
    /// </summary>
    public ObjectObservationMetadataConventionOptions ObjectObservationMetadataConventions
    {
        get;
        set
        {
            if (ReferenceEquals(field, value))
                return;
            field = Guard.RequireNotNull(value);
            ClearMapperCaches();
        }
    } = new();

    /// <summary>
    /// Maps a CLR object to an observed shape using type-name schema conventions.
    /// </summary>
    public Observation Map<T>(T source, ObjectObservationMetadata? metadata = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        return Map(source, schemaId: new ShapeId(typeof(T).Name), metadata);
    }

    /// <summary>
    /// Maps a CLR object to an observed shape using the supplied schema id.
    /// </summary>
    public Observation Map<T>(T source, ShapeId schemaId, ObjectObservationMetadata? metadata = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        return GetObjectObservationMapper<T>(schemaId).Map(source, metadata);
    }

    /// <summary>
    /// Maps an observed shape to a CLR object using layout conventions.
    /// </summary>
    public T Map<T>(Observation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        return GetObservationObjectMapper<T>(observation.Layout).Map(observation);
    }

    /// <summary>
    /// Maps an observed shape to a CLR object using an explicitly configured mapper.
    /// </summary>
    public T Map<T>(Observation observation, Action<ObservationObjectMapperBuilder<T>> configure)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(configure);

        var builder = ForObservationObject<T>(observation.Layout);
        configure(builder);
        return builder.Build().Map(observation);
    }

    /// <summary>
    /// Builds an object-to-observed-shape mapper configured by this context.
    /// </summary>
    public ObjectObservationMapperBuilder<T> ForObjectObservation<T>(ShapeId schemaId)
        => new(schemaId, this);

    /// <summary>
    /// Builds an object-to-observed-shape mapper using <typeparamref name="T"/> name as schema.
    /// </summary>
    public ObjectObservationMapperBuilder<T> ForObjectObservation<T>()
        => ForObjectObservation<T>(new(typeof(T).Name));

    static JsonSerializerOptions CreateDefaultSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    /// <summary>
    /// Builds an observed-shape-to-object mapper configured by this context.
    /// </summary>
    public ObservationObjectMapperBuilder<T> ForObservationObject<T>(ObservationLayout layout)
        => new(layout, this);

    /// <summary>
    /// Gets readable public instance properties for a CLR shape using a shared type cache.
    /// </summary>
    public PropertyInfo[] GetReadableProperties(Type type) => ShapeTypeInspector.GetReadableProperties(type);

    /// <summary>
    /// Clears all cached compiled mappers for this context.
    /// </summary>
    public void ClearMapperCaches()
    {
        objectObservationMappers.Clear();
        observationObjectMappers.Clear();
    }

    internal string ResolveImplicitFieldIdentity(PropertyInfo property)
    {
        ArgumentNullException.ThrowIfNull(property);

        if (UseJsonPropertyNameAttributesForFieldIdentity)
        {
            var attribute = property.GetCustomAttribute<JsonPropertyNameAttribute>(inherit: true);
            if (attribute is not null)
                return attribute.Name;
        }

        if (RequireJsonPropertyNameAttributeForFieldIdentity)
            throw new InvalidOperationException($"Property '{property.Name}' on '{property.DeclaringType?.Name ?? "Unknown"}' is missing JsonPropertyNameAttribute.");

        return ResolveFieldIdentityFallback?.Invoke(property) ?? property.Name;
    }

    IObjectObservationMapper<T> GetObjectObservationMapper<T>(ShapeId schemaId)
    {
        var key = new ObjectMapperCacheKey(ObjectType: typeof(T), SchemaId: schemaId.Value);
        return (IObjectObservationMapper<T>)objectObservationMappers.GetOrAdd(key, _ => ForObjectObservation<T>(schemaId).Build());
    }

    IObservationObjectMapper<T> GetObservationObjectMapper<T>(ObservationLayout layout)
    {
        var key = new ObservedMapperCacheKey(
            ObjectType: typeof(T),
            SchemaId: layout.Schema.Value,
            FieldSignature: BuildFieldSignature(layout));
        return (IObservationObjectMapper<T>)observationObjectMappers.GetOrAdd(
            key,
            _ => ForObservationObject<T>(layout).Build());
    }

    static string BuildFieldSignature(ObservationLayout layout)
        => string.Join('\u001f', layout.FieldNames);

    readonly record struct ObjectMapperCacheKey(Type ObjectType, string SchemaId);

    readonly record struct ObservedMapperCacheKey(Type ObjectType, string SchemaId, string FieldSignature);
}
