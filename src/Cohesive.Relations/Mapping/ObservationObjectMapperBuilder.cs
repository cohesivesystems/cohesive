using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cohesive.Relations.Model;

namespace Cohesive.Relations.Mapping;

/// <summary>
/// Builder for observed-shape-to-object mapping.
/// </summary>
public sealed class ObservationObjectMapperBuilder<T>
{
    readonly ObservationLayout layout;
    readonly ShapeMappingContext context;
    readonly List<PropertyMapping> mappings = [];
    JsonSerializerOptions jsonOptions;
    ObservationObjectMissingFieldBehavior missingFieldBehavior;

    internal ObservationObjectMapperBuilder(ObservationLayout layout, ShapeMappingContext? context = null)
    {
        this.layout = Guard.RequireNotNull(layout);
        this.context = context ?? ShapeMappingContext.Default;
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
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldIdentity);
        ArgumentNullException.ThrowIfNull(target);
        var property = ResolveTargetProperty(target);
        mappings.Add(new(property, fieldIdentity, convert));
        return this;
    }

    /// <summary>
    /// Maps all readable instance properties using <see cref="JsonPropertyNameAttribute"/> values as field identities.
    /// </summary>
    public ObservationObjectMapperBuilder<T> MapAllFromJsonPropertyName(bool requireAttribute = false, Func<PropertyInfo, string>? fallback = null)
    {
        foreach (var property in GetReadableProperties())
            mappings.Add(new(property, ResolveFieldIdentityFromJsonPropertyName(property, requireAttribute, fallback), null));
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
        missingFieldBehavior = behavior;
        return this;
    }

    /// <summary>
    /// Builds a compiled mapper.
    /// </summary>
    /// <exception cref="InvalidOperationException"></exception>
    public ObservationObjectMapper<T> Build()
    {
        var effectiveMappings = BuildEffectiveMappings(GetReadableProperties());
        if (effectiveMappings.Count == 0)
            throw new InvalidOperationException($"Mapper for '{typeof(T).Name}' must define at least one field mapping.");

        var duplicateMappingByProp = effectiveMappings.TryGetDuplicateByKey(x => x.Property.Name, StringComparer.OrdinalIgnoreCase);
        if (duplicateMappingByProp is not null)
            throw new InvalidOperationException($"Mapper for '{typeof(T).Name}' contains duplicate property mapping '{duplicateMappingByProp.Property.Name}'.");

        var duplicateMappingByField = effectiveMappings.TryGetDuplicateByKey(x => x.FieldIdentity, StringComparer.Ordinal);
        if (duplicateMappingByField is not null)
            throw new InvalidOperationException($"Mapper for '{typeof(T).Name}' contains duplicate field mapping '{duplicateMappingByField.FieldIdentity}'.");

        var byProperty = effectiveMappings.ToDictionary(x => x.Property.Name, StringComparer.OrdinalIgnoreCase);
        var constructor = SelectConstructor(byProperty);
        var mapDelegate = CompileMap(constructor, byProperty, effectiveMappings);
        
        return new(layout, mapDelegate);
    }

    IReadOnlyList<PropertyMapping> BuildEffectiveMappings(IReadOnlyList<PropertyInfo> readableProperties)
    {
        Dictionary<string, PropertyMapping> explicitByProperty = new(StringComparer.OrdinalIgnoreCase);
        foreach (var mapping in mappings)
            explicitByProperty[mapping.Property.Name] = mapping;

        List<PropertyMapping> effective = [];
        HashSet<string> knownProperties = new(StringComparer.OrdinalIgnoreCase);

        foreach (var property in readableProperties)
        {
            knownProperties.Add(property.Name);
            if (explicitByProperty.TryGetValue(property.Name, out var mapping))
            {
                effective.Add(mapping);
                continue;
            }

            effective.Add(new(property, context.ResolveImplicitFieldIdentity(property), null));
        }

        foreach (var mapping in explicitByProperty.Values.Where(x => !knownProperties.Contains(x.Property.Name)))
        {
            effective.Add(mapping);
        }

        return effective;
    }

    static ConstructorInfo? SelectConstructor(IReadOnlyDictionary<string, PropertyMapping> byProperty)
    {
        var constructors = typeof(T).GetConstructors(BindingFlags.Public | BindingFlags.Instance);
        var candidates = constructors
            .Where(ctor => ctor.GetParameters().All(p => byProperty.ContainsKey(p.Name ?? string.Empty)))
            .OrderByDescending(ctor => ctor.GetParameters().Length)
            .ToArray();

        if (candidates.Length == 0)
        {
            if (typeof(T).IsValueType || typeof(T).GetConstructor(Type.EmptyTypes) is not null)
                return null;

            throw new InvalidOperationException($"No public constructor on '{typeof(T).Name}' can be satisfied by mapped properties.");
        }

        var maxArity = candidates[0].GetParameters().Length;
        var sameArity = candidates.Where(x => x.GetParameters().Length == maxArity).ToArray();
        if (sameArity.Length > 1)
            throw new InvalidOperationException($"Multiple constructors on '{typeof(T).Name}' are ambiguous for mapped properties.");

        return sameArity[0];
    }

    Func<Observation, T> CompileMap(ConstructorInfo? constructor, IReadOnlyDictionary<string, PropertyMapping> byProperty, IReadOnlyList<PropertyMapping> effectiveMappings)
    {
        var observationParameter = Expression.Parameter(typeof(Observation), "observation");
        var serializerOptionsConstant = Expression.Constant(jsonOptions);
        var readMethod = typeof(ObservationObjectMapperBuilder<T>).GetMethod(nameof(ReadMappedValue), BindingFlags.Static | BindingFlags.NonPublic)
                         ?? throw new InvalidOperationException("ReadMappedValue method not found.");

        var constructorParameters = constructor?.GetParameters() ?? [];
        var constructorArguments = constructorParameters
            .Select(parameter =>
            {
                var mapping = byProperty[parameter.Name ?? string.Empty];
                return BuildReadExpression(
                    observationParameter,
                    mapping,
                    parameter.ParameterType,
                    serializerOptionsConstant,
                    readMethod,
                    ResolveMissingFieldResolution(parameter.ParameterType, parameter.HasDefaultValue, parameter.HasDefaultValue ? parameter.DefaultValue : null)
                    );
            })
            .ToArray();

        var body = constructor is null ? Expression.New(typeof(T)) : Expression.New(constructor, constructorArguments);

        var constructorPropertyNames = constructorParameters
            .Select(x => x.Name ?? string.Empty)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        
        var additionalBindings = effectiveMappings
            .Where(mapping => !constructorPropertyNames.Contains(mapping.Property.Name))
            .Select(mapping =>
            {
                if (mapping.Property.SetMethod is null)
                    throw new InvalidOperationException($"Property '{mapping.Property.Name}' on '{typeof(T).Name}' is not settable and is not in selected constructor.");

                var propertyValue = BuildReadExpression(
                    observationParameter,
                    mapping,
                    mapping.Property.PropertyType,
                    serializerOptionsConstant,
                    readMethod,
                    ResolveMissingFieldResolution(mapping.Property.PropertyType)
                    );
                
                return Expression.Bind(mapping.Property, propertyValue);
            })
            .ToArray();

        if (additionalBindings.Length > 0)
            return Expression.Lambda<Func<Observation, T>>(Expression.MemberInit(body, additionalBindings), observationParameter).Compile();

        return Expression.Lambda<Func<Observation, T>>(body, observationParameter).Compile();
    }

    MethodCallExpression BuildReadExpression(
        ParameterExpression observationParameter,
        PropertyMapping mapping,
        Type targetType,
        ConstantExpression serializerOptionsConstant,
        MethodInfo readMethod,
        MissingFieldResolution missingFieldResolution
        )
    {
        var hasOrdinal = layout.TryGetOrdinal(mapping.FieldIdentity, out var ordinal);
        var converterConstant = Expression.Constant(mapping.Converter, typeof(Delegate));
        var genericRead = readMethod.MakeGenericMethod(targetType);
        return Expression.Call(
            genericRead,
            observationParameter,
            Expression.Constant(ordinal),
            Expression.Constant(hasOrdinal),
            converterConstant,
            serializerOptionsConstant,
            Expression.Constant(mapping.FieldIdentity),
            Expression.Constant(missingFieldResolution)
            );
    }

    static TValue ReadMappedValue<TValue>(
        Observation observation,
        int ordinal,
        bool hasOrdinal,
        Delegate? converter,
        JsonSerializerOptions options,
        string fieldName,
        MissingFieldResolution missingFieldResolution)
    {
        if (!hasOrdinal || !observation.TryGetField(ordinal, out var observed))
            return ResolveMissingValue<TValue>(observation, fieldName, missingFieldResolution);

        if (converter is Func<ObservationValue, TValue> typedConverter)
            return typedConverter(observed);

        if (converter is not null)
            throw new InvalidOperationException($"Field '{fieldName}' uses unsupported converter type '{converter.GetType().Name}' for target '{typeof(TValue).Name}'.");

        var value = observed.Deserialize<TValue>(options);
        if (value is null && default(TValue) is not null)
            throw new InvalidOperationException($"Field '{fieldName}' deserialized to null for non-nullable target type '{typeof(TValue).Name}'.");
        
        return value!;
    }

    MissingFieldResolution ResolveMissingFieldResolution(Type targetType, bool hasExplicitDefaultValue = false, object? explicitDefaultValue = null)
    {
        if (hasExplicitDefaultValue)
            return new(MissingFieldResolutionKind.UseExplicitDefaultValue, explicitDefaultValue);

        return missingFieldBehavior switch
        {
            ObservationObjectMissingFieldBehavior.Throw => MissingFieldResolution.Throw,
            ObservationObjectMissingFieldBehavior.UseDefaultForAllMembers => MissingFieldResolution.UseTypeDefault,
            ObservationObjectMissingFieldBehavior.UseDefaultForOptionalMembers when CanUseTypeDefault(targetType) => MissingFieldResolution.UseTypeDefault,
            _ => MissingFieldResolution.Throw
        };
    }

    static bool CanUseTypeDefault(Type targetType) =>
        !targetType.IsValueType || Nullable.GetUnderlyingType(targetType) is not null;

    static TValue ResolveMissingValue<TValue>(Observation observation, string fieldName, MissingFieldResolution missingFieldResolution)
    {
        return missingFieldResolution.Kind switch
        {
            MissingFieldResolutionKind.UseTypeDefault => default!,
            MissingFieldResolutionKind.UseExplicitDefaultValue => missingFieldResolution.ExplicitDefaultValue is TValue typed
                ? typed
                : (TValue)missingFieldResolution.ExplicitDefaultValue!,
            _ => throw new InvalidOperationException($"Observation '{observation.ShapeId}:{observation.Id}' is missing required field '{fieldName}'.")
        };
    }

    static PropertyInfo ResolveTargetProperty<TValue>(Expression<Func<T, TValue>> target)
    {
        var body = target.Body;
        if (body is UnaryExpression unary && (unary.NodeType == ExpressionType.Convert || unary.NodeType == ExpressionType.ConvertChecked))
        {
            body = unary.Operand;
        }

        if (body is not MemberExpression member
            || member.Member is not PropertyInfo property
            || member.Expression is not ParameterExpression parameter
            || !ReferenceEquals(parameter, target.Parameters[0]))
        {
            throw new InvalidOperationException("Target selector must be a direct property access like 'x => x.Property'.");
        }

        return property;
    }

    IReadOnlyList<PropertyInfo> GetReadableProperties() => context.GetReadableProperties(typeof(T));

    static string ResolveFieldIdentityFromJsonPropertyName(PropertyInfo property, bool requireAttribute, Func<PropertyInfo, string>? fallback)
    {
        var attribute = property.GetCustomAttribute<JsonPropertyNameAttribute>(inherit: true);
        if (attribute is not null)
            return attribute.Name;
        if (requireAttribute)
            throw new InvalidOperationException($"Property '{property.Name}' on '{typeof(T).Name}' is missing JsonPropertyNameAttribute.");
        return fallback?.Invoke(property) ?? property.Name;
    }

    sealed record PropertyMapping(PropertyInfo Property, string FieldIdentity, Delegate? Converter);

    readonly record struct MissingFieldResolution(MissingFieldResolutionKind Kind, object? ExplicitDefaultValue = null)
    {
        public static MissingFieldResolution Throw => new(MissingFieldResolutionKind.Throw);

        public static MissingFieldResolution UseTypeDefault => new(MissingFieldResolutionKind.UseTypeDefault);
    }

    enum MissingFieldResolutionKind
    {
        Throw = 0,
        UseTypeDefault = 1,
        UseExplicitDefaultValue = 2
    }
}
