using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cohesive.Relations.Model;

namespace Cohesive.Relations.Mapping;

/// <summary>Compatibility builder for mapping legacy indexed Relations observations to CLR values.</summary>
/// <remarks>
/// Constructor selection, field conversion, missing-field behavior, and serializer semantics are delegated to the
/// core <see cref="ObservationMaterializer{T}"/>. This local-shape surface remains only until ARI-504 migrates
/// downstream consumers to graph-qualified semantic observations or physical occurrence readers.
/// </remarks>
/// <typeparam name="T">CLR target type.</typeparam>
public sealed class ObservationObjectMapperBuilder<T>
{
    static readonly GraphId CompatibilityGraphId = new("cohesive.relations/legacy-local-shape/v1");

    readonly ObservationLayout layout;
    readonly ShapeMappingContext context;
    readonly QualifiedShapeId compatibilityShape;
    readonly ObservationMaterializerBuilder<T> materializer;
    JsonSerializerOptions jsonOptions;
    ObservationObjectMissingFieldBehavior missingFieldBehavior;

    internal ObservationObjectMapperBuilder(ObservationLayout layout, ShapeMappingContext? context = null)
    {
        this.layout = Guard.RequireNotNull(layout);
        this.context = context ?? ShapeMappingContext.Default;
        compatibilityShape = new(CompatibilityGraphId, layout.Schema);
        materializer = ObservationMaterializer
            .For<T>(compatibilityShape)
            .WithImplicitFieldIdentityConvention(this.context.ResolveImplicitFieldIdentity);
        jsonOptions = this.context.ObservationObjectSerializerOptions;
        missingFieldBehavior = this.context.ObservationObjectMissingFieldBehavior;
    }

    /// <summary>
    /// Maps an observation field identity to a target property.
    /// </summary>
    public ObservationObjectMapperBuilder<T> Map<TValue>(
        string fieldIdentity,
        Expression<Func<T, TValue>> target
        ) => Map(fieldIdentity, target, convert: null);

    /// <summary>
    /// Maps an observation field identity to a target property.
    /// </summary>
    public ObservationObjectMapperBuilder<T> Map<TValue>(
        string fieldIdentity,
        Expression<Func<T, TValue>> target,
        Func<ObservationValue, TValue>? convert
        )
    {
        materializer.Map(fieldIdentity, target, convert);
        return this;
    }

    /// <summary>
    /// Maps all readable instance properties using <see cref="JsonPropertyNameAttribute"/> values as field identities.
    /// </summary>
    public ObservationObjectMapperBuilder<T> MapAllFromJsonPropertyName(bool requireAttribute = false, Func<PropertyInfo, string>? fallback = null)
    {
        materializer.MapAll(property =>
            ResolveFieldIdentityFromJsonPropertyName(property, requireAttribute, fallback));
        return this;
    }

    /// <summary>
    /// Sets serializer options used by default JSON conversions.
    /// </summary>
    public ObservationObjectMapperBuilder<T> WithSerializerOptions(JsonSerializerOptions options)
    {
        jsonOptions = Guard.RequireNotNull(options);
        return this;
    }

    /// <summary>
    /// Sets behavior used when a mapped field is absent from the layout or a specific observation.
    /// </summary>
    public ObservationObjectMapperBuilder<T> WithMissingFieldBehavior(ObservationObjectMissingFieldBehavior behavior)
    {
        if (!Enum.IsDefined(behavior))
            throw new ArgumentOutOfRangeException(nameof(behavior), behavior, "Unknown missing-field behavior.");

        missingFieldBehavior = behavior;
        return this;
    }

    /// <summary>
    /// Builds a compiled mapper.
    /// </summary>
    /// <exception cref="InvalidOperationException"></exception>
    public ObservationObjectMapper<T> Build()
    {
        var compiled = materializer
            .WithSerializerOptions(jsonOptions)
            .WithMissingFieldBehavior(ToCoreMissingFieldBehavior(missingFieldBehavior))
            .Compile();
        return new(
            layout,
            observation => compiled.Materialize(
                new LegacyIndexedObservationReader(observation, compatibilityShape)));
    }

    static ObservationMissingFieldBehavior ToCoreMissingFieldBehavior(
        ObservationObjectMissingFieldBehavior behavior) =>
        behavior switch
        {
            ObservationObjectMissingFieldBehavior.Throw => ObservationMissingFieldBehavior.Throw,
            ObservationObjectMissingFieldBehavior.UseDefaultForOptionalMembers =>
                ObservationMissingFieldBehavior.UseDefaultForOptionalMembers,
            ObservationObjectMissingFieldBehavior.UseDefaultForAllMembers =>
                ObservationMissingFieldBehavior.UseDefaultForAllMembers,
            _ => throw new ArgumentOutOfRangeException(nameof(behavior), behavior, "Unknown missing-field behavior.")
        };

    sealed class LegacyIndexedObservationReader(
        Observation observation,
        QualifiedShapeId shapeId) : IObservationFieldReader
    {
        public QualifiedShapeId ShapeId { get; } = shapeId;

        public bool TryGetField(string fieldIdentity, out ObservationValue field) =>
            observation.TryGetField(fieldIdentity, out field);
    }

    static string ResolveFieldIdentityFromJsonPropertyName(PropertyInfo property, bool requireAttribute, Func<PropertyInfo, string>? fallback)
    {
        var attribute = property.GetCustomAttribute<JsonPropertyNameAttribute>(inherit: true);
        if (attribute is not null)
            return attribute.Name;
        if (requireAttribute)
            throw new InvalidOperationException($"Property '{property.Name}' on '{typeof(T).Name}' is missing JsonPropertyNameAttribute.");
        return fallback?.Invoke(property) ?? property.Name;
    }

}
