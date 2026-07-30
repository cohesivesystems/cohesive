using System.Text.Json;
using Cohesive.Transitions.Model;

namespace Cohesive.Transitions.Authoring;

interface IAuthoredField
{
    string Name { get; }

    FieldDefinition Definition { get; }

    bool HasDefaultValue { get; }

    ObservationValue DefaultValue { get; }

    void ValidateState(EntityState state, JsonSerializerOptions options);
}

/// <summary>
/// Typed field definition owned by an entity definition.
/// </summary>
public class Field<T> : IAuthoredField
{
    readonly Func<T, bool>? constraint;
    readonly bool hasDefaultValue;
    readonly T defaultValue;

    /// <summary>
    /// Creates a field definition with an optional default value and CLR constraint.
    /// </summary>
    internal Field(Entity entity, FieldDefinition definition, bool hasDefaultValue, T defaultValue, Func<T, bool>? constraint)
    {
        Entity = Guard.RequireNotNull(entity);
        Definition = Guard.RequireNotNull(definition);
        this.hasDefaultValue = hasDefaultValue;
        this.defaultValue = defaultValue;
        this.constraint = constraint;
        EnsureDefinitionCompatibility();

        if (hasDefaultValue)
        {
            EnsureDefinitionValueRules(defaultValue, "default");
            EnsureConstraint(defaultValue);
        }
    }

    /// <summary>
    /// Entity definition that owns this field.
    /// </summary>
    public Entity Entity { get; }

    /// <summary>
    /// Semantic field definition backing this typed field.
    /// </summary>
    public FieldDefinition Definition { get; }

    /// <summary>
    /// Declared field name.
    /// </summary>
    public string Name => Definition.Name.Value;

    /// <summary>
    /// Expression-only placeholder used by the typed C# authoring DSL for collection counts.
    /// </summary>
    public int Count => throw CreatePlaceholderAccessException(fieldName: Name, memberName: nameof(Count));

    /// <summary>
    /// Expression-only placeholder used by the typed C# authoring DSL for membership checks.
    /// </summary>
    public bool IsOneOf(params T[] values) =>
        throw CreatePlaceholderAccessException(fieldName: Name, memberName: nameof(IsOneOf));

    /// <summary>
    /// Expression-only placeholder used by the typed C# authoring DSL for negated membership checks.
    /// </summary>
    public bool IsNotOneOf(params T[] values) =>
        throw CreatePlaceholderAccessException(fieldName: Name, memberName: nameof(IsNotOneOf));

    /// <summary>
    /// Converts a field definition to its semantic value inside expression-based DSL authoring.
    /// </summary>
    public static implicit operator T(Field<T> field) =>
        throw CreatePlaceholderAccessException(fieldName: field?.Name, memberName: "implicit conversion");

    /// <summary>
    /// True when this field definition carries a default value for new states.
    /// </summary>
    public bool HasDefaultValue => hasDefaultValue;
    
    /// <summary>
    /// Reads the typed field value from a state snapshot.
    /// </summary>
    public T Get(EntityState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (!state.TryGet(Definition, out var observed) || observed.Kind is ObservationValueKind.Null or ObservationValueKind.Undefined)
        {
            if (Definition.Presence == FieldPresence.Required)
            {
                throw new SemanticRuleViolationException(
                    $"Required field '{Name}' is missing from entity '{state.EntityId.Value}'.");
            }

            return default!;
        }

        var value = observed.Deserialize<T>(Entity.JsonOptions);
        if (value is null && Definition.Presence == FieldPresence.Required)
        {
            throw new SemanticRuleViolationException(
                $"Required field '{Name}' on entity '{state.EntityId.Value}' could not be materialized as '{typeof(T).Name}'.");
        }

        return value!;
    }

    /// <summary>
    /// Attempts to read the typed field value from a state snapshot.
    /// </summary>
    public bool TryGet(EntityState state, out T value)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (!state.TryGet(Definition, out var observed) || observed.Kind is ObservationValueKind.Null or ObservationValueKind.Undefined)
        {
            value = default!;
            return false;
        }

        value = observed.Deserialize<T>(Entity.JsonOptions)!;
        return value is not null;
    }

    /// <summary>
    /// Returns true when the state snapshot contains a materializable value for this field.
    /// </summary>
    public bool HasValue(EntityState state) => TryGet(state, out _);

    /// <summary>
    /// Reads the field value or returns the supplied fallback when the field is absent.
    /// </summary>
    public T GetOrDefault(EntityState state, T defaultValue = default!) =>
        TryGet(state, out var value) ? value : defaultValue;

    /// <summary>
    /// Reads the field value and throws when no materializable value is present.
    /// </summary>
    public T Require(EntityState state, string? message = null)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (TryGet(state, out var value))
            return value;

        throw new SemanticRuleViolationException(
            message
            ?? $"Field '{Name}' is required in entity '{state.EntityId.Value}' but no value is present.");
    }

    ObservationValue IAuthoredField.DefaultValue => ObservationValue.FromObject(defaultValue);

    void IAuthoredField.ValidateState(EntityState state, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (!state.TryGet(Definition, out var observed) || observed.Kind is ObservationValueKind.Null or ObservationValueKind.Undefined)
        {
            if (Definition.Presence is FieldPresence.Required)
                throw new SemanticRuleViolationException($"Required field '{Name}' is missing from entity '{state.EntityId.Value}'.");

            return;
        }

        var value = observed.Deserialize<T>(options);
        if (value is null)
        {
            if (Definition.Presence is FieldPresence.Required)
                throw new SemanticRuleViolationException($"Required field '{Name}' on entity '{state.EntityId.Value}' could not be materialized as '{typeof(T).Name}'.");

            return;
        }

        EnsureDefinitionValueRules(value, "validation");
        EnsureConstraint(value);
    }

    void EnsureConstraint(T value)
    {
        if (constraint is not null && !constraint(value))
        {
            throw new SemanticRuleViolationException(
                $"Field '{Name}' rejected a value using its CLR constraint.");
        }
    }

    void EnsureDefinitionCompatibility()
    {
        if (!IsDefinitionCompatibleWithType(typeof(T), Definition))
        {
            throw new SemanticRuleViolationException($"Field '{Name}' has CLR type '{typeof(T).Name}' incompatible with definition type '{DescribeType(Definition)}'.");
        }
    }

    void EnsureDefinitionValueRules(T value, string operation)
    {
        if (IsNullValue(value) && Definition.Presence is FieldPresence.Required)
        {
            throw new SemanticRuleViolationException($"Field '{Name}' is required and cannot be null during {operation}.");
        }
    }

    static bool IsNullValue(T value) => value is null;

    static InvalidOperationException CreatePlaceholderAccessException(string? fieldName, string memberName)
    {
        var prefix = string.IsNullOrWhiteSpace(fieldName)
            ? "A field definition"
            : $"Field '{fieldName}'";
        return new InvalidOperationException(
            $"{prefix} is a definition, not runtime state. '{memberName}' is only valid inside authoring expressions or by reading from an EntityState.");
    }

    static string DescribeType(FieldDefinition definition)
    {
        var cardinality = definition.Cardinality == FieldCardinality.Many ? "Many" : "Single";
        return $"{cardinality}<{DescribeTypeRef(definition.Type)}>";
    }

    static string DescribeTypeRef(TypeRef type) => type switch
    {
        ScalarTypeRef scalar => scalar.Kind.ToString(),
        OpaqueRuntimeTypeRef opaque => $"Opaque({opaque.RuntimeType})",
        JsonTypeRef json => $"Json({json.Kind})",
        EnumTypeRef enumType => $"Enum({enumType.Name})",
        EntityReferenceTypeRef entityReference => $"EntityRef({entityReference.Entity.Value})",
        ArrayTypeRef array => $"Array({DescribeTypeRef(array.ElementType)})",
        ObjectTypeRef => "Object",
        QuantityTypeRef quantity => $"Quantity({quantity.Quantity},{quantity.BaseKind})",
        _ => type.GetType().Name
    };

    static bool IsDefinitionCompatibleWithType(Type declaredType, FieldDefinition definition)
    {
        if (definition.Cardinality == FieldCardinality.Many)
        {
            if (!TryGetEnumerableElementType(declaredType, out var elementType))
                return false;

            return IsTypeRefCompatible(elementType, definition.Type);
        }

        return IsTypeRefCompatible(declaredType, definition.Type);
    }

    static bool IsTypeRefCompatible(Type type, TypeRef typeRef)
    {
        var t = Nullable.GetUnderlyingType(type) ?? type;

        switch (typeRef)
        {
            case OpaqueRuntimeTypeRef:
                return true;

            case JsonTypeRef:
                return IsJsonClrTypeCompatible(t);

            case ScalarTypeRef scalar:
                return DefaultClrTypeRefMapper.TryMapScalarTypeKind(t, out var mappedKind)
                       && mappedKind == scalar.Kind;

            case EnumTypeRef enumType:
                if (t == typeof(string))
                    return true;

                if (!t.IsEnum)
                    return false;

                var enumNames = Enum.GetNames(t);
                return enumType.Members.All(member => enumNames.Contains(member, StringComparer.Ordinal))
                       && enumNames.All(name => enumType.Members.Contains(name, StringComparer.Ordinal));

            case EntityReferenceTypeRef:
                return t == typeof(string) || t == typeof(Guid);

            case ArrayTypeRef arrayType:
                return TryGetEnumerableElementType(t, out var elementType)
                       && IsTypeRefCompatible(elementType, arrayType.ElementType);

            case ObjectTypeRef:
                return !t.IsPrimitive
                       && t != typeof(string)
                       && t != typeof(decimal)
                       && t != typeof(Guid)
                       && t != typeof(DateTimeOffset)
                       && t != typeof(DateTime)
                       && !t.IsEnum;

            case QuantityTypeRef quantityType:
                if (!TryGetStructuredQuantityRepresentationType(t, out var representationType))
                    return false;

                return MatchesQuantityName(t, quantityType.Quantity)
                       && IsScalarClrTypeCompatible(representationType, quantityType.BaseKind);
        }

        return false;
    }

    static bool IsJsonClrTypeCompatible(Type clrType) =>
        clrType == typeof(System.Text.Json.JsonElement)
        || clrType == typeof(System.Text.Json.JsonDocument)
        || clrType == typeof(AnnotationValue)
        || typeof(System.Text.Json.Nodes.JsonNode).IsAssignableFrom(clrType);

    static bool TryGetStructuredQuantityRepresentationType(Type type, out Type representationType)
    {
        var structuredQuantityInterface = type.GetInterfaces()
            .FirstOrDefault(x =>
                x.IsGenericType
                && x.GetGenericTypeDefinition() == typeof(IStructuredQuantity<,,>)
                && x.GetGenericArguments()[0] == type);

        if (structuredQuantityInterface is null)
        {
            representationType = null!;
            return false;
        }

        representationType = structuredQuantityInterface.GetGenericArguments()[2];
        return true;
    }

    static bool MatchesQuantityName(Type type, string quantityName) =>
        string.Equals(type.Name, quantityName, StringComparison.Ordinal)
        || string.Equals(type.FullName, quantityName, StringComparison.Ordinal);

    static bool IsScalarClrTypeCompatible(Type clrType, ScalarTypeKind scalarKind)
        => DefaultClrTypeRefMapper.TryMapScalarTypeKind(clrType, out var mappedKind)
           && mappedKind == scalarKind;

    static bool TryGetEnumerableElementType(Type type, out Type elementType)
    {
        if (type.IsArray)
        {
            elementType = type.GetElementType()!;
            return true;
        }

        if (type.IsGenericType)
        {
            var genericDefinition = type.GetGenericTypeDefinition();
            if (genericDefinition == typeof(IEnumerable<>)
                || genericDefinition == typeof(IReadOnlyList<>)
                || genericDefinition == typeof(IReadOnlyCollection<>)
                || genericDefinition == typeof(IList<>)
                || genericDefinition == typeof(List<>)
                || genericDefinition == typeof(ICollection<>))
            {
                elementType = type.GetGenericArguments()[0];
                return true;
            }
        }

        var enumerableInterface = type.GetInterfaces()
            .FirstOrDefault(x => x.IsGenericType && x.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        if (enumerableInterface is not null)
        {
            elementType = enumerableInterface.GetGenericArguments()[0];
            return true;
        }

        elementType = null!;
        return false;
    }
}
