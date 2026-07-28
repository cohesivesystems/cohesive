using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cohesive.Model.Serialization;
using Cohesive.Transitions.Model;

namespace Cohesive.Transitions.Authoring;

/// <summary>
/// Base type for declarative entity definitions.
/// </summary>
/// <typeparam name="TEntity">The derived entity type.</typeparam>
public abstract class Entity<TEntity>(string? entityName = null) 
    : Entity(entityName: entityName ?? typeof(TEntity).Name) where TEntity : Entity, new()
{
    /// <summary>
    /// The singleton instance of the entity type.
    /// </summary>
    public static readonly TEntity Instance = new();
    
    /// <summary>
    /// Gets the <see cref="EntityDefinition"/> for this entity type.
    /// </summary>
    /// <returns></returns>
    public static EntityDefinition Define() => Entity.Define<TEntity>();

    /// <summary>
    /// Creates an <see cref="EntitySnapshot{TEntity}"/> for this entity given the entity state.
    /// </summary>
    /// <param name="state">The entity state to bind to the entity definition.</param>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException"></exception>
    public EntitySnapshot<TEntity> Snapshot(EntityState state) => new(
        entity: this as TEntity ?? throw new InvalidOperationException($"Entity instance '{GetType().Name}' does not match typed entity parameter '{typeof(TEntity).Name}'."),
        state: state 
        );
    
    /// <summary>
    /// Creates an <see cref="EntitySnapshot{TEntity}"/> for this entity given the entity id, state and version.
    /// </summary>
    /// <param name="entityId">The entity id.</param>
    /// <param name="stateObject">The object containing the state data for the entity.</param>
    /// <param name="version">The entity version.</param>
    /// <returns></returns>
    /// <exception cref="ArgumentException"></exception>
    public EntitySnapshot<TEntity> CreateSnapshot(string entityId, object? stateObject = null, long version = 0)
    {
        if (string.IsNullOrWhiteSpace(entityId))
            throw new ArgumentException("Entity ID cannot be null or whitespace", nameof(entityId));
        return Snapshot(CreateState(entityId, stateObject, version));
    }
    
    /// <summary>Adds an invariant to the entity definition.</summary>
    protected void Invariant(string name, Expression<Func<TEntity, bool>> predicate, string? message = null) =>
        base.Invariant(name, predicate, message);
    
    /// <summary>
    /// Defines a field computed in terms of other fields.
    /// </summary>
    /// <param name="name">The name of the field.</param>
    /// <param name="compute">The expression that computes the field value.</param>
    /// <param name="configure">Optional configuration for the field.</param>
    /// <typeparam name="T">The type of the field.</typeparam>
    /// <returns>The configured field.</returns>
    protected Field<T> ComputedField<T>(string name, Expression<Func<TEntity, T>> compute, Action<FieldBuilder>? configure = null) =>
        base.ComputedField(name, compute, configure);

    /// <summary>Defines a transition for the entity.</summary>
    protected Transition<TEntity, TInput> Transition<TInput>(string name, Action<TransitionExpressionBuilder<TEntity, TInput>> configure) =>
        base.Transition(name, configure);

    /// <summary>Produces one canonical Transition execution-definition document for this entity shape.</summary>
    /// <typeparam name="TInput">Typed invocation input.</typeparam>
    /// <typeparam name="TOutcome">Typed Transition outcome.</typeparam>
    /// <param name="metadata">Stable identity, revision, root-body identity, and provenance.</param>
    /// <param name="configure">Finite builder callback that is evaluated once and is not retained.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for source attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for source attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for source attribution.</param>
    /// <returns>A typed handle containing only the canonical document and its validation result.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="metadata"/> or <paramref name="configure"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="TransitionExpressionTranslationException">
    /// An authored expression is outside the portable Transition subset.
    /// </exception>
    /// <exception cref="SemanticRuleViolationException">
    /// No entity field has been declared before the Transition is authored.
    /// </exception>
    /// <exception cref="ArgumentException">Authored identity, shape, or canonical structure is invalid.</exception>
    /// <exception cref="InvalidOperationException">Builder structure or canonical JSON state is contradictory.</exception>
    /// <exception cref="NotSupportedException">An authored constant or canonical value cannot be represented portably.</exception>
    /// <exception cref="System.Text.Json.JsonException">
    /// Canonical content cannot be encoded by the strict execution serializer.
    /// </exception>
    protected Transition<TEntity, TInput, TOutcome> Transition<TInput, TOutcome>(
        TransitionAuthoringMetadata metadata,
        Action<TransitionBuilder<TEntity, TInput, TOutcome>> configure,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "") =>
        base.Transition<TEntity, TInput, TOutcome>(
            metadata,
            configure,
            sourceFile,
            sourceLine,
            sourceMember);
}

/// <summary>
/// Base type for declarative entity definitions.
/// </summary>
public abstract class Entity
{
    sealed record ContinuationBinding(Type InputType, Func<EntityState, object?, TransitionResult> Run);

    sealed record SharedTransitionBinding(TransitionDefinition Definition, Type InputType);

    // ReSharper disable once ClassNeverInstantiated.Local
    sealed record EmptyComputedFieldParameters;

    sealed class SharedEntityModel(
        EntityDefinition definition,
        DeclarativeEntityRuntime runtime,
        IReadOnlyDictionary<string, FieldDefinition> fieldByName,
        IReadOnlyDictionary<string, SharedTransitionBinding> transitionByName,
        IReadOnlySet<string> invariantNames
        )
    {
        public EntityDefinition Definition { get; } = definition;

        public DeclarativeEntityRuntime Runtime { get; } = runtime;

        public IReadOnlyDictionary<string, FieldDefinition> FieldByName { get; } = fieldByName;

        public IReadOnlyDictionary<string, SharedTransitionBinding> TransitionByName { get; } = transitionByName;

        public IReadOnlySet<string> InvariantNames { get; } = invariantNames;

        public static SharedEntityModel Create(EntityDefinition definition, IReadOnlyDictionary<string, Type> transitionInputTypeByName)
        {
            ArgumentNullException.ThrowIfNull(definition);
            ArgumentNullException.ThrowIfNull(transitionInputTypeByName);

            var runtime = new DeclarativeEntityRuntime(definition);
            var fieldByName = definition.Fields.ToDictionary(x => x.Name.Value, StringComparer.Ordinal);
            Dictionary<string, SharedTransitionBinding> transitionByName = new(StringComparer.Ordinal);
            foreach (var transition in definition.Transitions)
            {
                if (!transitionInputTypeByName.TryGetValue(transition.Name, out var inputType))
                {
                    throw new SemanticRuleViolationException(
                        $"Entity type '{definition.Name.Value}' is missing input type metadata for transition '{transition.Name}'.");
                }

                transitionByName[transition.Name] = new(transition, inputType);
            }

            return new(
                definition,
                runtime,
                fieldByName,
                transitionByName,
                definition.Invariants.Select(x => x.Name).ToHashSet(StringComparer.Ordinal)
                );
        }
    }

    static readonly ConcurrentDictionary<Type, SharedEntityModel> SharedModelByEntityType = [];
    static readonly IClrTypeRefMapper ClrTypeRefMapper = new DefaultClrTypeRefMapper();

    readonly Dictionary<string, IAuthoredField> fields = new(StringComparer.Ordinal);
    readonly Dictionary<string, ContinuationBinding> continuationByTransitionName = new(StringComparer.Ordinal);
    readonly HashSet<string> fieldIdentities = new(StringComparer.Ordinal);
    readonly HashSet<string> invariantNames = new(StringComparer.Ordinal);
    readonly HashSet<string> transitionNames = new(StringComparer.Ordinal);
    readonly List<FieldDefinition> fieldDefinitions = [];
    readonly List<InvariantDefinition> invariantDefinitions = [];
    readonly List<TransitionDefinition> transitionDefinitions = [];
    readonly Dictionary<string, Type> transitionInputTypeByName = new(StringComparer.Ordinal);
    readonly TransitionExpressionCompiler transitionCompiler = new();
    readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web);
    ImmutableDictionary<AnnotationKey, AnnotationValue>.Builder? annotations;
    readonly EntityTypeName entityName;
    readonly Type entityClrType;
    SharedEntityModel? sharedModel;
    bool initialized;

    /// <summary>
    /// Creates a definition-only entity authoring surface.
    /// </summary>
    protected Entity(string? entityName = null)
    {
        entityClrType = GetType();
        this.entityName = new(entityName ?? entityClrType.Name);
        // TODO: drop or abstract the JsonSerializationOptions
        jsonOptions.Converters.Add(new StructuredQuantityJsonConverterFactory());
        jsonOptions.Converters.Add(new AttributeAwareJsonStringEnumConverterFactory());
        if (SharedModelByEntityType.TryGetValue(entityClrType, out var cachedModel))
            sharedModel = cachedModel;
    }
    
    /// <summary>
    /// Returns the compiled definition for a parameterless entity authoring type.
    /// </summary>
    protected static EntityDefinition Define<TEntity>()
        where TEntity : Entity, new() => new TEntity().Definition;

    internal JsonSerializerOptions JsonOptions => jsonOptions;

    /// <summary>
    /// Compiled semantic definition shared by this CLR entity type.
    /// </summary>
    public EntityDefinition Definition
    {
        get
        {
            EnsureDefinitionInitialized();
            return sharedModel.Definition;
        }
    }

    /// <summary>
    /// Declares an annotation on the entity shape while the definition is still being authored.
    /// </summary>
    protected void Annotate(string key, AnnotationValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        EnsureDefinitionIsMutable("annotations");
        annotations ??= ImmutableDictionary.CreateBuilder<AnnotationKey, AnnotationValue>();
        annotations[new(key)] = value;
    }

    /// <summary>
    /// Declares an annotation on the entity shape from a CLR value or object graph.
    /// </summary>
    protected void Annotate<TValue>(string key, TValue value) =>
        Annotate(key, AnnotationValue.FromObject(value));

    /// <summary>
    /// Declares multiple annotations on the entity shape from CLR values or object graphs.
    /// </summary>
    protected void Annotate(params (string Key, object? Value)[] annotations)
    {
        ArgumentNullException.ThrowIfNull(annotations);

        foreach (var (key, value) in annotations)
            Annotate(key, AnnotationValue.FromObject(value));
    }

    /// <summary>
    /// Declares a mutable field by inferring its semantic type from the CLR field type.
    /// </summary>
    protected Field<T> MutableField<T>(string name, params (string Key, object? Value)[] annotations) =>
        Field<T>(
            name,
            configure: field => ApplyFieldAnnotations(field, annotations));

    /// <summary>
    /// Declares a mutable field by inferring its semantic type from the CLR field type and applying builder configuration.
    /// </summary>
    protected Field<T> MutableField<T>(string name, Action<FieldBuilder> configure) =>
        Field<T>(name, configure: configure);

    /// <summary>
    /// Declares a mutable field by inferring its semantic type from the CLR field type.
    /// </summary>
    protected Field<T> MutableField<T>(
        string name,
        (string Key, object? Value) annotation,
        params (string Key, object? Value)[] additionalAnnotations) =>
        Field<T>(
            name,
            configure: field => ApplyFieldAnnotations(field, [annotation, .. additionalAnnotations]));

    /// <summary>
    /// Declares a write-once field by inferring its semantic type from the CLR field type.
    /// </summary>
    /// <param name="name">The name of the field.</param>
    /// <param name="annotations">Optional annotations.</param>
    protected Field<T> WriteOnceField<T>(string name, params (string Key, object? Value)[] annotations) =>
        Field<T>(
            name,
            configure: field =>
            {
                field.WriteOnce();
                ApplyFieldAnnotations(field, annotations);
            });

    /// <summary>
    /// Declares a write-once field by inferring its semantic type from the CLR field type.
    /// </summary>
    /// <param name="name">The name of the field.</param>
    /// <param name="annotation">An annotation.</param>
    /// <param name="additionalAnnotations">Optional additional annotations.</param>
    protected Field<T> WriteOnceField<T>(
        string name,
        (string Key, object? Value) annotation,
        params (string Key, object? Value)[] additionalAnnotations) =>
        Field<T>(
            name,
            configure: field =>
            {
                field.WriteOnce();
                ApplyFieldAnnotations(field, [annotation, .. additionalAnnotations]);
            });
    
    /// <summary>
    /// Declares a field owned by this entity definition.
    /// </summary>
    protected Field<T> Field<T>(FieldDefinition definition) =>
        FieldCore<T>(definition, hasDefaultValue: false, defaultValue: default!, constraint: null);

    /// <summary>
    /// Declares a field owned by this entity definition with a CLR-only constraint.
    /// </summary>
    protected Field<T> Field<T>(FieldDefinition definition, Func<T, bool> constraint) =>
        FieldCore(definition, hasDefaultValue: false, defaultValue: default!, constraint);

    /// <summary>
    /// Declares a field owned by this entity definition with a default value for new states.
    /// </summary>
    protected Field<T> Field<T>(FieldDefinition definition, T initialValue, Func<T, bool>? constraint = null) =>
        FieldCore(definition, hasDefaultValue: true, initialValue, constraint);

    /// <summary>
    /// Declares a field by inferring its semantic type from the CLR field type.
    /// </summary>
    protected Field<T> Field<T>(string name, Action<FieldBuilder>? configure = null) =>
        FieldCore<T>(
            InferFieldDefinition<T>(name, configure),
            hasDefaultValue: false,
            defaultValue: default!,
            constraint: null
            );

    /// <summary>
    /// Declares a computed field authored as a restricted typed expression.
    /// </summary>
    protected Field<T> ComputedField<TEntity, T>(
        string name,
        Expression<Func<TEntity, T>> compute,
        Action<FieldBuilder>? configure = null
        )
        where TEntity : Entity =>
        FieldCore<T>(
            InferComputedFieldDefinition(name, compute, configure),
            hasDefaultValue: false,
            defaultValue: default!,
            constraint: null
        );

    /// <summary>
    /// Declares a field by inferring its semantic type from the CLR field type and applying a CLR-only constraint.
    /// </summary>
    protected Field<T> Field<T>(string name, Func<T, bool> constraint, Action<FieldBuilder>? configure = null) =>
        FieldCore(
            InferFieldDefinition<T>(name, configure),
            hasDefaultValue: false,
            defaultValue: default!,
            constraint
            );

    /// <summary>
    /// Declares a field by inferring its semantic type from the CLR field type and assigning a default value.
    /// </summary>
    protected Field<T> Field<T>(
        string name,
        T initialValue,
        Action<FieldBuilder>? configure = null,
        Func<T, bool>? constraint = null
        ) =>
        FieldCore(
            InferFieldDefinition<T>(name, configure),
            hasDefaultValue: true,
            defaultValue: initialValue,
            constraint: constraint
            );

    Field<T> FieldCore<T>(FieldDefinition definition, bool hasDefaultValue, T defaultValue, Func<T, bool>? constraint)
    {
        ArgumentNullException.ThrowIfNull(definition);
        EnsureDefinitionIsMutable("fields");

        var effectiveDefinition = ResolveFieldDefinition(definition);
        var name = effectiveDefinition.Name.Value;
        if (fields.ContainsKey(name))
            throw new SemanticRuleViolationException($"Entity type '{entityName.Value}' already defines a field named '{name}'.");

        RegisterFieldIdentities(effectiveDefinition);

        var field = new Field<T>(this, effectiveDefinition, hasDefaultValue, defaultValue, constraint);
        fields.Add(name, field);
        if (sharedModel is null)
            fieldDefinitions.Add(effectiveDefinition);
        return field;
    }

    static void ApplyFieldAnnotations(FieldBuilder field, (string Key, object? Value)[] annotations)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(annotations);

        foreach (var (key, value) in annotations)
            field.Annotation(key, AnnotationValue.FromObject(value));
    }

    FieldDefinition InferFieldDefinition<T>(string name, Action<FieldBuilder>? configure)
    {
        var builder = CreateFieldBuilder<T>(name);
        configure?.Invoke(builder);
        return builder.Build();
    }

    FieldDefinition InferComputedFieldDefinition<TEntity, T>(
        string name,
        Expression<Func<TEntity, T>> compute,
        Action<FieldBuilder>? configure
        )
        where TEntity : Entity
    {
        ArgumentNullException.ThrowIfNull(compute);

        var builder = CreateFieldBuilder<T>(name);
        configure?.Invoke(builder);

        var provisionalFieldDefinition = builder.Build();
        var translationDefinition = sharedModel?.Definition
            ?? BuildDefinitionSnapshot([.. fieldDefinitions, provisionalFieldDefinition]);
        var translator = new TransitionExpressionBuilder<TEntity, EmptyComputedFieldParameters>.ExpressionTranslator(
            entityDefinition: translationDefinition,
            parameterNames: new HashSet<string>(StringComparer.Ordinal));

        builder.Computed(translator.Translate(compute));
        return builder.Build();
    }

    FieldBuilder CreateFieldBuilder<T>(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var (clrType, nullability) = ResolveFieldClrTypeAndNullability<T>(name);
        var mappedType = ClrTypeRefMapper.Map(clrType, nullability);
        var cardinality = FieldCardinality.Single;
        if (mappedType is ArrayTypeRef arrayType)
        {
            cardinality = FieldCardinality.Many;
            mappedType = arrayType.ElementType;
        }

        var builder = new FieldBuilder(name: new(name), type: mappedType, runtimeType: clrType);
        if (cardinality is FieldCardinality.Many)
            builder.Many();

        if (IsOptionalField(clrType, nullability))
            builder.Optional();

        return builder;
    }

    void RegisterFieldIdentities(FieldDefinition definition) => RegisterFieldIdentity(definition.Name.Value, definition);

    void RegisterFieldIdentity(string identity, FieldDefinition definition)
    {
        if (!fieldIdentities.Add(identity))
        {
            throw new SemanticRuleViolationException(
                $"Entity type '{entityName.Value}' already defines ambiguous field identity '{identity}' for field '{definition.Name.Value}'.");
        }
    }

    (Type ClrType, NullabilityInfo? Nullability) ResolveFieldClrTypeAndNullability<T>(string name)
    {
        var property = entityClrType.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        if (property is null || !property.PropertyType.IsGenericType || property.PropertyType.GetGenericTypeDefinition() != typeof(Field<>))
            return (typeof(T), null);

        var fieldType = property.PropertyType.GetGenericArguments()[0];
        if (fieldType != typeof(T))
            throw new SemanticRuleViolationException($"Entity type '{entityName.Value}' declares field property '{name}' with CLR type '{fieldType.FullName}', but the inferred field was bound as '{typeof(T).FullName}'.");

        var nullability = new NullabilityInfoContext().Create(property);
        var fieldNullability = nullability.GenericTypeArguments.Length > 0
            ? nullability.GenericTypeArguments[0]
            : null;
        
        return (fieldType, fieldNullability);
    }

    static bool IsOptionalField(Type clrType, NullabilityInfo? nullability)
    {
        if (Nullable.GetUnderlyingType(clrType) is not null)
            return true;

        return !clrType.IsValueType && nullability?.ReadState == NullabilityState.Nullable;
    }

    /// <summary>
    /// Declares an invariant that must hold for every valid entity state.
    /// </summary>
    protected void Invariant<TEntity>(string name, Expression<Func<TEntity, bool>> predicate, string? message = null) where TEntity : Entity
    {
        EnsureDefinitionIsMutable("invariants");
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(predicate);

        if (this is not TEntity)
            throw new SemanticRuleViolationException($"Invariant '{name}' is authored for entity type '{typeof(TEntity).Name}' but actual type is '{GetType().Name}'.");

        if (!invariantNames.Add(name))
            throw new SemanticRuleViolationException($"Entity type '{entityName.Value}' already defines invariant '{name}'.");

        if (sharedModel is null)
        {
            var provisional = BuildProvisionalDefinition();
            invariantDefinitions.Add(
                InvariantExpressionDsl.Compile(
                    provisional,
                    name,
                    predicate,
                    message,
                    transitionCompiler)
                );
            return;
        }

        if (!sharedModel.InvariantNames.Contains(name))
            throw new SemanticRuleViolationException($"Entity type '{entityName.Value}' does not declare invariant '{name}' in the cached definition.");
    }

    /// <summary>
    /// Declares a transition authored as restricted C# expressions.
    /// </summary>
    protected Transition<TEntity, TInput> Transition<TEntity, TInput>(string name, Action<TransitionExpressionBuilder<TEntity, TInput>> configure) where TEntity : Entity
    {
        ArgumentNullException.ThrowIfNull(configure);
        EnsureDefinitionIsMutable("transitions");

        if (this is not TEntity typedEntity)
            throw new SemanticRuleViolationException($"Transition '{name}' is authored for entity type '{typeof(TEntity).Name}' but actual type is '{GetType().Name}'.");

        if (!transitionNames.Add(name))
            throw new SemanticRuleViolationException($"Entity type '{entityName.Value}' already defines transition '{name}'.");

        var inputType = typeof(TInput);
        TransitionDefinition transitionDefinition;
        if (sharedModel is null)
        {
            var provisional = BuildProvisionalDefinition();
            transitionDefinition = transitionCompiler.Compile(provisional, name, configure);
            transitionDefinitions.Add(transitionDefinition);
        }
        else
        {
            if (!sharedModel.TransitionByName.TryGetValue(name, out var binding))
                throw new SemanticRuleViolationException($"Entity type '{entityName.Value}' does not declare transition '{name}' in the cached definition.");

            if (binding.InputType != inputType)
                throw new SemanticRuleViolationException($"Transition '{name}' on entity type '{entityName.Value}' expects input type '{binding.InputType.FullName}' but was bound as '{inputType.FullName}'.");

            transitionDefinition = binding.Definition;
        }

        transitionInputTypeByName[transitionDefinition.Name] = inputType;
        continuationByTransitionName[transitionDefinition.Name] = new(
            inputType,
            (state, rawInput) =>
            {
                if (rawInput is null)
                {
                    if (inputType.IsValueType && Nullable.GetUnderlyingType(inputType) is null)
                        throw new SemanticRuleViolationException($"Transition '{transitionDefinition.Name}' requires a non-null input value of type '{inputType.FullName}'.");

                    return ApplyTransition<TInput>(transitionDefinition.Name, state, default!);
                }

                if (rawInput is not TInput typedInput)
                    throw new SemanticRuleViolationException($"Transition '{transitionDefinition.Name}' expects input type '{inputType.FullName}' but received '{rawInput.GetType().FullName}'.");

                return ApplyTransition(transitionDefinition.Name, state, typedInput);
            });

        return new(
            typedEntity,
            transitionDefinition,
            (state, input) => ApplyTransition(transitionDefinition.Name, state, input)
            );
    }

    /// <summary>Produces one canonical Transition document against this entity's declared observation shape.</summary>
    /// <typeparam name="TEntity">Entity authoring type used by observation-field selectors.</typeparam>
    /// <typeparam name="TInput">Typed invocation input.</typeparam>
    /// <typeparam name="TOutcome">Typed Transition outcome.</typeparam>
    /// <param name="metadata">Stable identity, revision, root-body identity, and provenance.</param>
    /// <param name="configure">Finite builder callback that is evaluated once and is not retained.</param>
    /// <param name="sourceFile">Compiler-supplied source file used only for source attribution.</param>
    /// <param name="sourceLine">Compiler-supplied source line used only for source attribution.</param>
    /// <param name="sourceMember">Compiler-supplied source member used only for source attribution.</param>
    /// <returns>A typed handle containing only the canonical document and its validation result.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="metadata"/> or <paramref name="configure"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="SemanticRuleViolationException">
    /// <typeparamref name="TEntity"/> does not match this entity instance, or no field has been declared.
    /// </exception>
    /// <exception cref="TransitionExpressionTranslationException">
    /// An authored expression is outside the portable Transition subset.
    /// </exception>
    /// <exception cref="ArgumentException">Authored identity, shape, or canonical structure is invalid.</exception>
    /// <exception cref="InvalidOperationException">Builder structure or canonical JSON state is contradictory.</exception>
    /// <exception cref="NotSupportedException">An authored constant or canonical value cannot be represented portably.</exception>
    /// <exception cref="System.Text.Json.JsonException">
    /// Canonical content cannot be encoded by the strict execution serializer.
    /// </exception>
    protected Transition<TEntity, TInput, TOutcome> Transition<TEntity, TInput, TOutcome>(
        TransitionAuthoringMetadata metadata,
        Action<TransitionBuilder<TEntity, TInput, TOutcome>> configure,
        [CallerFilePath] string sourceFile = "",
        [CallerLineNumber] int sourceLine = 0,
        [CallerMemberName] string sourceMember = "")
        where TEntity : Entity
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(configure);
        if (this is not TEntity)
        {
            throw new SemanticRuleViolationException(
                $"Canonical Transition '{metadata.DefinitionId.Value}' is authored for entity type "
                + $"'{typeof(TEntity).Name}' but actual type is '{GetType().Name}'.");
        }

        var entityShape = sharedModel?.Definition.Shape ?? BuildProvisionalDefinition().Shape;
        return TransitionAuthoring.Create<TEntity, TInput, TOutcome>(
            entityShape,
            metadata,
            configure,
            sourceFile,
            sourceLine,
            sourceMember);
    }

    /// <summary>
    /// Creates a new state snapshot from CLR values and declared defaults.
    /// </summary>
    public EntityState CreateState(string entityId, object? stateObject = null, long version = 0)
    {
        // TODO: consider a type-safe way to initialize state from CLR values, perhaps using a entity-specific id type
        EnsureDefinitionInitialized();

        Dictionary<string, ObservationValue> values = new(StringComparer.Ordinal);
        ApplyDefaultValues(values);

        if (stateObject is not null)
        {
            var observed = ObservationValue.FromObject(stateObject);
            if (observed.Kind != ObservationValueKind.Object || observed.Fields is null)
                throw new SemanticRuleViolationException($"State for entity type '{entityName.Value}' must serialize to a JSON object.");

            foreach (var (name, value) in observed.Fields)
                values[name] = value;
        }

        var state = sharedModel.Definition.CreateState(entityId, values, version);
        state = sharedModel.Runtime.NormalizeState(entityId, state);
        ValidateAuthoredFieldRules(state);
        return state;
    }
    
    /// <summary>
    /// Creates a new state snapshot from observation values and declared defaults.
    /// </summary>
    public EntityState CreateState(string entityId, IReadOnlyDictionary<string, ObservationValue> values, long version = 0)
    {
        EnsureDefinitionInitialized();
        ArgumentNullException.ThrowIfNull(values);

        Dictionary<string, ObservationValue> effectiveValues = new(StringComparer.Ordinal);
        ApplyDefaultValues(effectiveValues);
        foreach (var (name, value) in values)
            effectiveValues[name] = value;

        var state = sharedModel.Definition.CreateState(entityId, effectiveValues, version);
        state = sharedModel.Runtime.NormalizeState(entityId, state);
        ValidateAuthoredFieldRules(state);
        return state;
    }

    /// <summary>
    /// Validates an entity state against declarative semantics and CLR-authored field rules.
    /// </summary>
    /// <exception cref="SemanticRuleViolationException"></exception>
    public void ValidateState(EntityState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        EnsureDefinitionInitialized();
        if (state.Observation.ShapeId != sharedModel.Definition.Shape.Id)
            throw new SemanticRuleViolationException($"State for entity '{state.EntityId.Value}' has shape '{state.Observation.ShapeId.Value}' but entity definition '{entityName.Value}' expects '{sharedModel.Definition.Shape.Id.Value}'.");
        sharedModel.Runtime.ValidateState(entityId: state.EntityId.Value, state: state);
        ValidateAuthoredFieldRules(state);
    }

    internal TransitionResult ApplyTransition<TInput>(string name, EntityState state, TInput input)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(state);

        EnsureDefinitionInitialized();
        ValidateState(state);

        var transitionInput = ToTransitionInput(input);
        var result = sharedModel.Runtime.Apply(
            entityId: state.EntityId.Value,
            state: state,
            version: state.Version,
            transitionName: name,
            input: transitionInput
            );
        ValidateAuthoredFieldRules(result.NewState);
        return BindEffectContinuations(result);
    }

    void ApplyDefaultValues(Dictionary<string, ObservationValue> values)
    {
        foreach (var field in fields.Values)
        {
            if (field.HasDefaultValue)
                values[field.Name] = field.DefaultValue;
        }
    }

    void ValidateAuthoredFieldRules(EntityState state)
    {
        foreach (var field in fields.Values)
            field.ValidateState(state, jsonOptions);
    }

    EntityDefinition BuildProvisionalDefinition()
    {
        if (fieldDefinitions.Count == 0)
            throw new SemanticRuleViolationException($"Entity type '{entityName.Value}' must declare at least one field before defining invariants or transitions.");

        return BuildDefinitionSnapshot(fieldDefinitions);
    }

    EntityDefinition BuildDefinitionSnapshot(IReadOnlyList<FieldDefinition> fields)
    {
        if (annotations is null)
        {
            return new(
                name: entityName,
                [.. fields],
                invariants: [.. invariantDefinitions],
                transitions: [.. transitionDefinitions]
                );
        }

        return new(
            name: entityName,
            shape: new Shape(
                id: new($"shape.entity.{entityName.Value}"),
                role: ShapeRoles.Entity,
                fields: [.. fields],
                annotations: annotations.ToImmutable()),
            invariants: [.. invariantDefinitions],
            transitions: [.. transitionDefinitions]
        );
    }

    [MemberNotNull(nameof(sharedModel))]
    void EnsureDefinitionInitialized()
    {
        if (initialized)
        {
            ArgumentNullException.ThrowIfNull(sharedModel);
            return;
        }

        if (sharedModel is null)
        {
            var provisionalDefinition = BuildProvisionalDefinition();
            var compiledModel = SharedEntityModel.Create(provisionalDefinition, transitionInputTypeByName);
            sharedModel = SharedModelByEntityType.GetOrAdd(entityClrType, compiledModel);
            if (!ReferenceEquals(sharedModel, compiledModel))
            {
                EnsureSharedDefinitionIsCompatible(
                    provisionalDefinition,
                    transitionInputTypeByName,
                    sharedModel);
            }
        }

        EnsureCurrentAuthoringMatchesSharedModel();
        initialized = true;
    }

    void EnsureDefinitionIsMutable(string construct)
    {
        if (initialized)
            throw new SemanticRuleViolationException($"Entity type '{entityName.Value}' cannot define {construct} after the definition is initialized.");
    }

    FieldDefinition ResolveFieldDefinition(FieldDefinition definition)
    {
        if (sharedModel is null)
            return definition;

        if (!sharedModel.FieldByName.TryGetValue(definition.Name.Value, out var cachedDefinition))
        {
            throw new SemanticRuleViolationException(
                $"Entity type '{entityName.Value}' does not declare a field named '{definition.Name.Value}' in the cached definition.");
        }

        if (!ReferenceEquals(definition, cachedDefinition)
            && !AreSemanticallyEquivalent(definition, cachedDefinition))
        {
            throw new SemanticRuleViolationException(
                $"Field '{definition.Name.Value}' on entity type '{entityName.Value}' does not match the cached definition.");
        }

        return cachedDefinition;
    }

    void EnsureCurrentAuthoringMatchesSharedModel()
    {
        var model = sharedModel ?? throw new InvalidOperationException("Shared entity model has not been initialized.");
        if (fields.Count != model.Definition.Fields.Length)
        {
            throw new SemanticRuleViolationException(
                $"Entity type '{entityName.Value}' must define {model.Definition.Fields.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)} field(s) before initialization.");
        }

        foreach (var field in fields.Values)
        {
            if (!model.FieldByName.TryGetValue(field.Name, out var cachedDefinition)
                || (!ReferenceEquals(field.Definition, cachedDefinition)
                    && !AreSemanticallyEquivalent(field.Definition, cachedDefinition)))
            {
                throw new SemanticRuleViolationException(
                    $"Field '{field.Name}' on entity type '{entityName.Value}' does not match the cached definition.");
            }
        }

        if (!invariantNames.SetEquals(model.InvariantNames))
        {
            throw new SemanticRuleViolationException(
                $"Entity type '{entityName.Value}' does not match the cached invariant set.");
        }

        var cachedTransitionNames = model.TransitionByName.Keys.ToHashSet(StringComparer.Ordinal);
        if (!transitionNames.SetEquals(cachedTransitionNames))
        {
            throw new SemanticRuleViolationException(
                $"Entity type '{entityName.Value}' does not match the cached transition set.");
        }

        foreach (var (transitionName, inputType) in transitionInputTypeByName)
        {
            if (!model.TransitionByName.TryGetValue(transitionName, out var binding)
                || binding.InputType != inputType)
            {
                throw new SemanticRuleViolationException(
                    $"Transition '{transitionName}' on entity type '{entityName.Value}' does not match the cached input type.");
            }
        }
    }

    static void EnsureSharedDefinitionIsCompatible(
        EntityDefinition provisionalDefinition,
        IReadOnlyDictionary<string, Type> provisionalTransitionInputTypes,
        SharedEntityModel sharedModel
        )
    {
        if (!AreSemanticallyEquivalent(provisionalDefinition, sharedModel.Definition))
        {
            throw new SemanticRuleViolationException($"Entity type '{provisionalDefinition.Name.Value}' produced a definition that does not match the cached CLR-type definition.");
        }

        if (provisionalTransitionInputTypes.Count != sharedModel.TransitionByName.Count)
        {
            throw new SemanticRuleViolationException($"Entity type '{provisionalDefinition.Name.Value}' produced transition input metadata that does not match the cached CLR-type definition.");
        }

        foreach (var (transitionName, inputType) in provisionalTransitionInputTypes)
        {
            if (!sharedModel.TransitionByName.TryGetValue(transitionName, out var binding)
                || binding.InputType != inputType)
            {
                throw new SemanticRuleViolationException(
                    $"Transition '{transitionName}' on entity type '{provisionalDefinition.Name.Value}' produced input metadata that does not match the cached CLR-type definition.");
            }
        }
    }

    ObservationValue ToTransitionInput<TInput>(TInput input) =>
        ObservationValue.FromClrPropertyBag(input, jsonOptions);

    TransitionResult BindEffectContinuations(TransitionResult result)
    {
        if (result.Effects.Count == 0)
            return result;

        List<EffectRequest>? boundEffects = null;
        for (var i = 0; i < result.Effects.Count; i++)
        {
            var effectRequest = result.Effects[i];
            var continuation = effectRequest.Continuation;
            if (continuation is null || continuation.HasDirectReference)
                continue;

            if (!continuationByTransitionName.TryGetValue(continuation.TransitionName, out var binding))
                continue;

            var baseState = result.NewState;
            boundEffects ??= [.. result.Effects];
            boundEffects[i] = effectRequest with
            {
                Continuation = continuation.Bind(
                    inputType: binding.InputType,
                    run: rawInput => binding.Run(baseState.Lineage.Current, rawInput),
                    snapshotTokenProjector: fieldNames =>
                        SnapshotTokenProjector.Compute(
                            ProjectSnapshot(baseState.Lineage.Current, fieldNames),
                            fieldNames))
            };
        }

        return boundEffects is null
            ? result
            : result with { Effects = boundEffects };
    }

    static IReadOnlyDictionary<string, ObservationValue> ProjectSnapshot(EntityState state, IReadOnlyList<string> fieldNames)
    {
        Dictionary<string, ObservationValue> snapshot = new(StringComparer.Ordinal);
        foreach (var fieldName in fieldNames)
        {
            if (state.Fields.TryGetValue(fieldName, out var value))
                snapshot[fieldName] = value;
        }

        return snapshot;
    }

    // TODO: abstract AreSemanticallyEquivalent JSON comparer
    static bool AreSemanticallyEquivalent(EntityDefinition left, EntityDefinition right)
    {
        return string.Equals(
            JsonSerializer.Serialize(left),
            JsonSerializer.Serialize(right),
            StringComparison.Ordinal);
    }
    
    static bool AreSemanticallyEquivalent(FieldDefinition left, FieldDefinition right)
    {
        return string.Equals(
            JsonSerializer.Serialize(left),
            JsonSerializer.Serialize(right),
            StringComparison.Ordinal);
    }

    sealed class AttributeAwareJsonStringEnumConverterFactory : JsonConverterFactory
    {
        readonly JsonStringEnumConverter fallback = new();

        public override bool CanConvert(Type typeToConvert)
        {
            var enumType = Nullable.GetUnderlyingType(typeToConvert) ?? typeToConvert;
            return enumType.IsEnum && enumType.GetCustomAttribute<JsonConverterAttribute>(inherit: true) is null;
        }

        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
            fallback.CreateConverter(typeToConvert, options);
    }
}
