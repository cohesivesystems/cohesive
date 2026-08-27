using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cohesive.Model;

/// <summary>Controls how observation materialization handles absent semantic fields.</summary>
public enum ObservationMissingFieldBehavior
{
    /// <summary>
    /// An absent mapped field causes materialization to fail unless its selected constructor parameter declares an
    /// explicit default value.
    /// </summary>
    Throw = 0,

    /// <summary>
    /// An absent field uses the target member's default value only for a nullable or reference type, or when a
    /// constructor parameter declares an explicit default value.
    /// </summary>
    UseDefaultForOptionalMembers = 1,

    /// <summary>Every absent mapped field uses the target member's default value.</summary>
    UseDefaultForAllMembers = 2
}

/// <summary>Reusable compiled interpretation from an identity-free observation to a CLR value.</summary>
/// <typeparam name="T">CLR target type.</typeparam>
/// <remarks>
/// A materializer is immutable and thread-safe. Its qualified shape identity, field mappings, converters,
/// serializer contract, and missing-field policy are fixed when it is compiled.
/// </remarks>
public sealed class ObservationMaterializer<T>
{
    readonly Func<IObservationFieldReader, T> materialize;

    internal ObservationMaterializer(QualifiedShapeId shapeId, Func<IObservationFieldReader, T> materialize)
    {
        ShapeId = shapeId;
        this.materialize = Guard.RequireNotNull(materialize);
    }

    /// <summary>Gets the exact graph-qualified shape accepted by this materializer.</summary>
    public QualifiedShapeId ShapeId { get; }

    /// <summary>Materializes a CLR value from an observation governed by <see cref="ShapeId"/>.</summary>
    /// <param name="observation">Identity-free observation to interpret.</param>
    /// <returns>The materialized CLR value.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="observation"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// The observation has another qualified shape, a required mapped field is absent, conversion fails, or a
    /// converter returns null for a non-nullable target member.
    /// </exception>
    public T Materialize(Observation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        return Materialize((IObservationFieldReader)observation);
    }

    /// <summary>Materializes a CLR value directly from a physical reader governed by <see cref="ShapeId"/>.</summary>
    /// <param name="reader">Validated physical interpretation of identity-free observation fields.</param>
    /// <returns>The materialized CLR value.</returns>
    /// <remarks>
    /// The reader is trusted to preserve validated core observation semantics. This method verifies exact qualified
    /// shape identity but does not revalidate the complete value tree.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="reader"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// The reader has another qualified shape, a required mapped field is absent, conversion fails, or a converter
    /// returns null for a non-nullable target member.
    /// </exception>
    public T Materialize(IObservationFieldReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (reader.ShapeId != ShapeId)
        {
            throw new InvalidOperationException(
                $"Materializer shape '{ShapeId}' does not match observation shape '{reader.ShapeId}'.");
        }

        return materialize(reader);
    }
}

/// <summary>Creates compiled observation-to-CLR materializers.</summary>
public static class ObservationMaterializer
{
    /// <summary>Creates a configurable materializer builder for an exact graph-scoped shape.</summary>
    /// <typeparam name="T">CLR target type.</typeparam>
    /// <param name="shape">Exact graph and shape accepted by the compiled materializer.</param>
    /// <returns>A builder using deterministic CLR property-name conventions.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="shape"/> is default.</exception>
    public static ObservationMaterializerBuilder<T> For<T>(GraphShapeId shape)
    {
        ArgumentNullException.ThrowIfNull(shape.Graph);
        return new(shape.QualifiedId);
    }

    /// <summary>Creates a configurable materializer builder from an already resolved qualified shape identity.</summary>
    /// <typeparam name="T">CLR target type.</typeparam>
    /// <param name="shapeId">Exact graph-qualified shape accepted by the compiled materializer.</param>
    /// <returns>A builder using deterministic CLR property-name conventions.</returns>
    /// <remarks>
    /// Use this overload when a compiler or physical interpreter has already resolved and validated shape evidence.
    /// The materializer binds that identity but does not require or retain a mutable graph object.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="shapeId"/> is default.</exception>
    public static ObservationMaterializerBuilder<T> For<T>(QualifiedShapeId shapeId)
    {
        if (string.IsNullOrWhiteSpace(shapeId.GraphId.Value) || string.IsNullOrWhiteSpace(shapeId.ShapeId.Value))
            throw new ArgumentException("A materializer requires a graph-qualified shape identity.", nameof(shapeId));

        return new(shapeId);
    }

    /// <summary>
    /// Gets the cached default materializer for a target CLR type and an observation's exact qualified shape.
    /// </summary>
    /// <typeparam name="T">CLR target type.</typeparam>
    /// <param name="observation">Observation providing qualified shape evidence.</param>
    /// <returns>
    /// A process-wide reusable materializer using CLR property names, web JSON conversion with string enums, and
    /// optional-member defaults.
    /// </returns>
    /// <remarks>
    /// Cache identity includes <typeparamref name="T"/> through the generic cache and the complete
    /// <see cref="QualifiedShapeId"/> through its key. It never depends on a local shape id or a physical layout.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="observation"/> is <see langword="null"/>.</exception>
    public static ObservationMaterializer<T> GetDefault<T>(Observation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        return DefaultObservationMaterializerCache<T>.Get(observation.ShapeId);
    }

    /// <summary>
    /// Gets the cached default materializer for a target CLR type and a physical observation reader's exact shape.
    /// </summary>
    /// <typeparam name="T">CLR target type.</typeparam>
    /// <param name="reader">Physical reader providing qualified shape evidence.</param>
    /// <returns>
    /// A process-wide reusable materializer using CLR property names, web JSON conversion with string enums, and
    /// optional-member defaults.
    /// </returns>
    /// <remarks>
    /// The reader is an execution interpretation; this operation does not make it a semantic observation authority.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="reader"/> is <see langword="null"/>.</exception>
    public static ObservationMaterializer<T> GetDefault<T>(IObservationFieldReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return DefaultObservationMaterializerCache<T>.Get(reader.ShapeId);
    }
}

/// <summary>Builds an immutable compiled observation-to-CLR materializer.</summary>
/// <typeparam name="T">CLR target type.</typeparam>
/// <remarks>
/// This builder is mutable and is intended for single-threaded authoring before <see cref="Compile"/>. The framework
/// default serializer contract compiles canonical scalar, array, and immutable constructor-object reads directly.
/// Customized or unsupported JSON contracts retain the serializer compatibility path.
/// </remarks>
public sealed class ObservationMaterializerBuilder<T>
{
    readonly QualifiedShapeId shapeId;
    readonly List<PropertyMapping> mappings = [];
    JsonSerializerOptions serializerOptions = ObservationMaterializerDefaults.SerializerOptions;
    ObservationMissingFieldBehavior missingFieldBehavior =
        ObservationMissingFieldBehavior.UseDefaultForOptionalMembers;
    ClrShapeGraphBuildResult? clrShapeMetadata;
    Func<PropertyInfo, string> implicitFieldIdentity = static property => property.Name;

    internal ObservationMaterializerBuilder(QualifiedShapeId shapeId) => this.shapeId = shapeId;

    /// <summary>Maps a semantic field identity to a target CLR property.</summary>
    /// <typeparam name="TValue">Target property type.</typeparam>
    /// <param name="fieldIdentity">Canonical top-level semantic field identity.</param>
    /// <param name="target">Direct target property selector, such as <c>value =&gt; value.Name</c>.</param>
    /// <returns>This builder for continued configuration.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="fieldIdentity"/> or <paramref name="target"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="fieldIdentity"/> is empty.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="target"/> is not a direct property access.</exception>
    public ObservationMaterializerBuilder<T> Map<TValue>(
        string fieldIdentity,
        Expression<Func<T, TValue>> target) =>
        Map(fieldIdentity, target, convert: null);

    /// <summary>Maps and converts a semantic field value to a target CLR property.</summary>
    /// <typeparam name="TValue">Target property type.</typeparam>
    /// <param name="fieldIdentity">Canonical top-level semantic field identity.</param>
    /// <param name="target">Direct target property selector, such as <c>value =&gt; value.Name</c>.</param>
    /// <param name="convert">
    /// Optional field converter. When omitted, the immutable observation value is interpreted using the effective
    /// serializer contract. The framework default may use an equivalent direct conversion for canonical values.
    /// </param>
    /// <returns>This builder for continued configuration.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="fieldIdentity"/> or <paramref name="target"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="fieldIdentity"/> is empty.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="target"/> is not a direct property access.</exception>
    public ObservationMaterializerBuilder<T> Map<TValue>(
        string fieldIdentity,
        Expression<Func<T, TValue>> target,
        Func<ObservationValue, TValue>? convert)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldIdentity);
        ArgumentNullException.ThrowIfNull(target);
        mappings.Add(new(ResolveTargetProperty(target), fieldIdentity, convert));
        return this;
    }

    /// <summary>Maps every readable target property using a caller-supplied semantic field-identity convention.</summary>
    /// <param name="resolveFieldIdentity">Resolves one non-empty canonical field identity per readable property.</param>
    /// <returns>This builder for continued configuration.</returns>
    /// <remarks>
    /// These mappings are explicit and therefore take precedence over CLR shape metadata or the implicit convention.
    /// Use <see cref="WithImplicitFieldIdentityConvention"/> when only unmapped properties should use a convention.
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="resolveFieldIdentity"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">A resolved field identity is empty or white-space.</exception>
    public ObservationMaterializerBuilder<T> MapAll(Func<PropertyInfo, string> resolveFieldIdentity)
    {
        ArgumentNullException.ThrowIfNull(resolveFieldIdentity);
        foreach (var property in ObservationMaterializerTypeCache<T>.ReadableProperties)
        {
            var fieldIdentity = resolveFieldIdentity(property);
            ArgumentException.ThrowIfNullOrWhiteSpace(fieldIdentity);
            mappings.Add(new(property, fieldIdentity, Converter: null));
        }

        return this;
    }

    /// <summary>Sets the field-identity convention used for readable properties without explicit mappings.</summary>
    /// <param name="resolveFieldIdentity">Resolves one non-empty canonical field identity per readable property.</param>
    /// <returns>This builder for continued configuration.</returns>
    /// <remarks>Effective CLR shape metadata, when supplied, takes precedence over this fallback convention.</remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="resolveFieldIdentity"/> is <see langword="null"/>.
    /// </exception>
    public ObservationMaterializerBuilder<T> WithImplicitFieldIdentityConvention(
        Func<PropertyInfo, string> resolveFieldIdentity)
    {
        implicitFieldIdentity = Guard.RequireNotNull(resolveFieldIdentity);
        return this;
    }

    /// <summary>
    /// Uses effective CLR field identities from the immutable result produced by <see cref="ClrShapeGraphBuilder"/>.
    /// </summary>
    /// <param name="metadata">
    /// Immutable graph-build result whose root mapping for <typeparamref name="T"/> must identify this
    /// materializer's exact graph and shape.
    /// </param>
    /// <returns>This builder for continued configuration.</returns>
    /// <remarks>Explicit mappings configured with <see cref="Map{TValue}(string, Expression{Func{T, TValue}})"/> take precedence.</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="metadata"/> is <see langword="null"/>.</exception>
    public ObservationMaterializerBuilder<T> WithClrShapeMetadata(ClrShapeGraphBuildResult metadata)
    {
        clrShapeMetadata = Guard.RequireNotNull(metadata);
        return this;
    }

    /// <summary>Sets serializer options used by default field conversions.</summary>
    /// <param name="options">Serializer contract to snapshot when <see cref="Compile"/> is called.</param>
    /// <returns>This builder for continued configuration.</returns>
    /// <remarks>Compilation clones and freezes the options; later caller mutation cannot change the materializer.</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    public ObservationMaterializerBuilder<T> WithSerializerOptions(JsonSerializerOptions options)
    {
        serializerOptions = Guard.RequireNotNull(options);
        return this;
    }

    /// <summary>Sets the policy used when a mapped semantic field is absent.</summary>
    /// <param name="behavior">Missing-field policy fixed into the compiled materializer.</param>
    /// <returns>This builder for continued configuration.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="behavior"/> is not defined.</exception>
    public ObservationMaterializerBuilder<T> WithMissingFieldBehavior(ObservationMissingFieldBehavior behavior)
    {
        if (!Enum.IsDefined(behavior))
            throw new ArgumentOutOfRangeException(nameof(behavior), behavior, "Unknown missing-field behavior.");

        missingFieldBehavior = behavior;
        return this;
    }

    /// <summary>Compiles the configured mappings into an immutable reusable materializer.</summary>
    /// <returns>A thread-safe materializer bound to the exact qualified shape.</returns>
    /// <exception cref="InvalidOperationException">
    /// Mappings are empty, duplicated, cannot satisfy a constructor, leave a property unsettable, or supplied CLR
    /// metadata does not identify this target type and exact qualified shape.
    /// </exception>
    public ObservationMaterializer<T> Compile()
    {
        var effectiveMappings = BuildEffectiveMappings(ObservationMaterializerTypeCache<T>.ReadableProperties);
        if (effectiveMappings.Count == 0)
            throw new InvalidOperationException($"Materializer for '{typeof(T).Name}' must define at least one field mapping.");

        var duplicateProperty = effectiveMappings.TryGetDuplicateByKey(
            static mapping => mapping.Property.Name,
            StringComparer.OrdinalIgnoreCase);
        if (duplicateProperty is not null)
        {
            throw new InvalidOperationException(
                $"Materializer for '{typeof(T).Name}' contains duplicate property mapping '{duplicateProperty.Property.Name}'.");
        }

        var duplicateField = effectiveMappings.TryGetDuplicateByKey(
            static mapping => mapping.FieldIdentity,
            StringComparer.Ordinal);
        if (duplicateField is not null)
        {
            throw new InvalidOperationException(
                $"Materializer for '{typeof(T).Name}' contains duplicate field mapping '{duplicateField.FieldIdentity}'.");
        }

        var byProperty = effectiveMappings.ToDictionary(
            static mapping => mapping.Property.Name,
            StringComparer.OrdinalIgnoreCase);
        var constructor = SelectConstructor(byProperty);
        var useDefaultValueConverters = ReferenceEquals(
            serializerOptions,
            ObservationMaterializerDefaults.SerializerOptions);
        var frozenSerializerOptions = ObservationMaterializerDefaults.Snapshot(serializerOptions);
        var compiled = CompileMaterializer(
            constructor,
            byProperty,
            effectiveMappings,
            frozenSerializerOptions,
            useDefaultValueConverters);
        return new(shapeId, compiled);
    }

    IReadOnlyList<PropertyMapping> BuildEffectiveMappings(IReadOnlyList<PropertyInfo> readableProperties)
    {
        ValidateClrShapeMetadata();

        Dictionary<string, PropertyMapping> explicitByProperty = new(StringComparer.OrdinalIgnoreCase);
        foreach (var mapping in mappings)
        {
            if (!explicitByProperty.TryAdd(mapping.Property.Name, mapping))
            {
                throw new InvalidOperationException(
                    $"Materializer for '{typeof(T).Name}' contains duplicate property mapping '{mapping.Property.Name}'.");
            }
        }

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

            effective.Add(new(property, ResolveImplicitFieldIdentity(property), Converter: null));
        }

        foreach (var mapping in explicitByProperty.Values.Where(mapping => !knownProperties.Contains(mapping.Property.Name)))
            effective.Add(mapping);

        return effective;
    }

    void ValidateClrShapeMetadata()
    {
        if (clrShapeMetadata is null)
            return;

        if (clrShapeMetadata.Graph.Id != shapeId.GraphId)
        {
            throw new InvalidOperationException(
                $"CLR shape metadata graph '{clrShapeMetadata.Graph.Id.Value}' does not match materializer graph '{shapeId.GraphId.Value}'.");
        }

        if (!clrShapeMetadata.ShapeIds.TryGetValue(typeof(T), out var metadataShapeId))
        {
            throw new InvalidOperationException(
                $"CLR shape metadata does not contain root target type '{typeof(T).FullName}'.");
        }

        if (metadataShapeId != shapeId.ShapeId)
        {
            throw new InvalidOperationException(
                $"CLR shape metadata maps target type '{typeof(T).FullName}' to shape '{metadataShapeId.Value}', not materializer shape '{shapeId.ShapeId.Value}'.");
        }
    }

    string ResolveImplicitFieldIdentity(PropertyInfo property)
    {
        if (clrShapeMetadata is null)
        {
            var fieldIdentity = implicitFieldIdentity(property);
            if (string.IsNullOrWhiteSpace(fieldIdentity))
            {
                throw new InvalidOperationException(
                    $"Field-identity convention returned an empty identity for property '{property.Name}' on '{typeof(T).Name}'.");
            }

            return fieldIdentity;
        }

        var path = clrShapeMetadata.ResolveMemberPath(typeof(T), [property]);
        return path.Segments[0].Segment!;
    }

    static ConstructorInfo? SelectConstructor(IReadOnlyDictionary<string, PropertyMapping> byProperty)
    {
        var candidates = ObservationMaterializerTypeCache<T>.PublicConstructors
            .Where(constructor => constructor.GetParameters().All(
                parameter => byProperty.ContainsKey(parameter.Name ?? string.Empty)))
            .OrderByDescending(static constructor => constructor.GetParameters().Length)
            .ToArray();

        if (candidates.Length == 0)
        {
            if (typeof(T).IsValueType || ObservationMaterializerTypeCache<T>.DefaultConstructor is not null)
                return null;

            throw new InvalidOperationException(
                $"No public constructor on '{typeof(T).Name}' can be satisfied by mapped properties.");
        }

        var maximumArity = candidates[0].GetParameters().Length;
        var maximumArityCandidates = candidates
            .Where(constructor => constructor.GetParameters().Length == maximumArity)
            .ToArray();
        if (maximumArityCandidates.Length > 1)
        {
            throw new InvalidOperationException(
                $"Multiple constructors on '{typeof(T).Name}' are ambiguous for mapped properties.");
        }

        return maximumArityCandidates[0];
    }

    Func<IObservationFieldReader, T> CompileMaterializer(
        ConstructorInfo? constructor,
        IReadOnlyDictionary<string, PropertyMapping> byProperty,
        IReadOnlyList<PropertyMapping> effectiveMappings,
        JsonSerializerOptions frozenSerializerOptions,
        bool useDefaultValueConverters)
    {
        var observation = Expression.Parameter(typeof(IObservationFieldReader), "observation");
        var options = Expression.Constant(frozenSerializerOptions);
        var constructorParameters = constructor?.GetParameters() ?? [];
        var constructorArguments = constructorParameters
            .Select(parameter =>
            {
                var mapping = byProperty[parameter.Name ?? string.Empty];
                return BuildReadExpression(
                    observation,
                    mapping,
                    parameter.ParameterType,
                    options,
                    useDefaultValueConverters,
                    ResolveMissingField(
                        parameter.ParameterType,
                        parameter.HasDefaultValue,
                        parameter.HasDefaultValue ? parameter.DefaultValue : null));
            })
            .ToArray();

        var creation = constructor is null
            ? Expression.New(typeof(T))
            : Expression.New(constructor, constructorArguments);
        var constructorProperties = constructorParameters
            .Select(static parameter => parameter.Name ?? string.Empty)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var additionalBindings = effectiveMappings
            .Where(mapping => !constructorProperties.Contains(mapping.Property.Name))
            .Select(mapping =>
            {
                if (mapping.Property.SetMethod is null)
                {
                    throw new InvalidOperationException(
                        $"Property '{mapping.Property.Name}' on '{typeof(T).Name}' is not settable and is not in the selected constructor.");
                }

                return Expression.Bind(
                    mapping.Property,
                    BuildReadExpression(
                        observation,
                        mapping,
                        mapping.Property.PropertyType,
                        options,
                        useDefaultValueConverters,
                        ResolveMissingField(mapping.Property.PropertyType)));
            })
            .ToArray();

        Expression body = additionalBindings.Length == 0
            ? creation
            : Expression.MemberInit(creation, additionalBindings);
        return Expression.Lambda<Func<IObservationFieldReader, T>>(body, observation).Compile();
    }

    MethodCallExpression BuildReadExpression(
        ParameterExpression observation,
        PropertyMapping mapping,
        Type targetType,
        ConstantExpression serializerOptions,
        bool useDefaultValueConverters,
        MissingFieldResolution missingFieldResolution)
    {
        var converter = mapping.Converter;
        if (converter is null && useDefaultValueConverters)
        {
            converter = DefaultObservationValueConverterCache.Get(targetType);
        }

        return Expression.Call(
            ObservationMaterializerTypeCache<T>.ReadMappedValueMethod.MakeGenericMethod(targetType),
            observation,
            Expression.Constant(converter, typeof(Delegate)),
            serializerOptions,
            Expression.Constant(mapping.FieldIdentity),
            Expression.Constant(missingFieldResolution));
    }

    static TValue ReadMappedValue<TValue>(
        IObservationFieldReader observation,
        Delegate? converter,
        JsonSerializerOptions serializerOptions,
        string fieldIdentity,
        MissingFieldResolution missingFieldResolution)
    {
        if (!observation.TryGetField(fieldIdentity, out var observed))
            return ResolveMissingValue<TValue>(observation, fieldIdentity, missingFieldResolution);

        if (converter is Func<ObservationValue, TValue> typedConverter)
            return EnsureNonNull(typedConverter(observed), fieldIdentity);

        if (converter is not null)
        {
            throw new InvalidOperationException(
                $"Field '{fieldIdentity}' uses unsupported converter type '{converter.GetType().Name}' for target '{typeof(TValue).Name}'.");
        }

        return EnsureNonNull(observed.Deserialize<TValue>(serializerOptions), fieldIdentity);
    }

    static TValue EnsureNonNull<TValue>(TValue? value, string fieldIdentity)
    {
        if (value is null && default(TValue) is not null)
        {
            throw new InvalidOperationException(
                $"Field '{fieldIdentity}' converted to null for non-nullable target type '{typeof(TValue).Name}'.");
        }

        return value!;
    }

    MissingFieldResolution ResolveMissingField(
        Type targetType,
        bool hasExplicitDefault = false,
        object? explicitDefault = null)
    {
        if (hasExplicitDefault)
            return new(MissingFieldResolutionKind.UseExplicitDefaultValue, explicitDefault);

        return missingFieldBehavior switch
        {
            ObservationMissingFieldBehavior.Throw => MissingFieldResolution.Throw,
            ObservationMissingFieldBehavior.UseDefaultForAllMembers => MissingFieldResolution.UseTypeDefault,
            ObservationMissingFieldBehavior.UseDefaultForOptionalMembers when CanUseTypeDefault(targetType) =>
                MissingFieldResolution.UseTypeDefault,
            _ => MissingFieldResolution.Throw
        };
    }

    static bool CanUseTypeDefault(Type targetType) =>
        !targetType.IsValueType || Nullable.GetUnderlyingType(targetType) is not null;

    static TValue ResolveMissingValue<TValue>(
        IObservationFieldReader observation,
        string fieldIdentity,
        MissingFieldResolution resolution) =>
        resolution.Kind switch
        {
            MissingFieldResolutionKind.UseTypeDefault => default!,
            MissingFieldResolutionKind.UseExplicitDefaultValue when resolution.ExplicitDefaultValue is TValue value => value,
            MissingFieldResolutionKind.UseExplicitDefaultValue => (TValue)resolution.ExplicitDefaultValue!,
            _ => throw new InvalidOperationException(
                $"Observation '{observation.ShapeId}' is missing required field '{fieldIdentity}'.")
        };

    static PropertyInfo ResolveTargetProperty<TValue>(Expression<Func<T, TValue>> target)
    {
        Expression body = target.Body;
        if (body is UnaryExpression
            {
                NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked
            } unary)
        {
            body = unary.Operand;
        }

        if (body is not MemberExpression
            {
                Member: PropertyInfo property,
                Expression: ParameterExpression parameter
            }
            || !ReferenceEquals(parameter, target.Parameters[0]))
        {
            throw new InvalidOperationException(
                "Target selector must be a direct property access like 'value => value.Property'.");
        }

        return property;
    }

    sealed record PropertyMapping(PropertyInfo Property, string FieldIdentity, Delegate? Converter);

    readonly record struct MissingFieldResolution(
        MissingFieldResolutionKind Kind,
        object? ExplicitDefaultValue = null)
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

static class ObservationMaterializerDefaults
{
    public static JsonSerializerOptions SerializerOptions { get; } = CreateSerializerOptions();

    public static JsonSerializerOptions Snapshot(JsonSerializerOptions options)
    {
        JsonSerializerOptions snapshot = new(options);
        snapshot.MakeReadOnly(populateMissingResolver: true);
        return snapshot;
    }

    static JsonSerializerOptions CreateSerializerOptions()
    {
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}

static class ObservationMaterializerTypeCache<T>
{
    public static PropertyInfo[] ReadableProperties { get; } = ShapeTypeInspector.GetReadableProperties<T>();

    public static ConstructorInfo[] PublicConstructors { get; } =
        typeof(T).GetConstructors(BindingFlags.Public | BindingFlags.Instance);

    public static ConstructorInfo? DefaultConstructor { get; } = typeof(T).GetConstructor(Type.EmptyTypes);

    public static MethodInfo ReadMappedValueMethod { get; } =
        typeof(ObservationMaterializerBuilder<T>).GetMethod(
            "ReadMappedValue",
            BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Observation materializer read method was not found.");
}

static class DefaultObservationMaterializerCache<T>
{
    static readonly ConcurrentDictionary<QualifiedShapeId, ObservationMaterializer<T>> Materializers = [];

    public static ObservationMaterializer<T> Get(QualifiedShapeId shapeId) =>
        Materializers.GetOrAdd(
            shapeId,
            static currentShapeId => new ObservationMaterializerBuilder<T>(currentShapeId).Compile());
}
