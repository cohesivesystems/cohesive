using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json.Serialization;
using Cohesive.Model.Serialization;

namespace Cohesive.Model.Expressions;

/// <summary>
/// Stable identity for one semantic location at which an <see cref="Expr"/> is evaluated.
/// </summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct ExprSiteId
{
    /// <summary>Creates an expression-site identifier.</summary>
    /// <param name="value">Stable, non-empty identifier.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white space.</exception>
    [JsonConstructor]
    public ExprSiteId(string value)
    {
        Value = Guard.RequireNotNullOrWhiteSpace(value);
    }

    /// <summary>Raw stable identifier.</summary>
    public string Value { get; }

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>
/// Stable identity for an expression operation or ambient semantic capability.
/// </summary>
public readonly record struct ExprCapabilityId
{
    /// <summary>Creates an expression capability identifier.</summary>
    /// <param name="value">Stable, non-empty identifier.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white space.</exception>
    [JsonConstructor]
    public ExprCapabilityId(string value)
    {
        Value = Guard.RequireNotNullOrWhiteSpace(value);
    }

    /// <summary>Raw stable identifier.</summary>
    public string Value { get; }

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>Whether a scoped binding is present for every evaluation row.</summary>
public enum ExprBindingAvailability
{
    /// <summary>The binding is present whenever the expression is evaluated.</summary>
    AlwaysPresent = 0,

    /// <summary>The binding is visible but may be absent for some evaluations.</summary>
    MayBeAbsent = 1
}

/// <summary>Coarse result category used when a complete portable type is not available.</summary>
public enum ExprResultCategory
{
    /// <summary>No category constraint is declared or known.</summary>
    Any = 0,

    /// <summary>A Boolean result.</summary>
    Boolean = 1,

    /// <summary>A non-Boolean scalar result.</summary>
    Scalar = 2,

    /// <summary>A collection result.</summary>
    Collection = 3,

    /// <summary>An object or shaped-value result.</summary>
    Object = 4,

    /// <summary>A numeric scalar result, including integral values.</summary>
    Numeric = 5,

    /// <summary>An integral numeric scalar result.</summary>
    Integer = 6,

    /// <summary>A text scalar result.</summary>
    Text = 7,

    /// <summary>A collection or object result whose elements or fields can be counted.</summary>
    Countable = 8,

    /// <summary>A scalar result with portable ordering semantics.</summary>
    Comparable = 9,

    /// <summary>A date, date-time, or instant scalar result.</summary>
    Temporal = 10
}

/// <summary>Context dependencies that an expression may derive from its site.</summary>
[Flags]
public enum ExprDependencyKind
{
    /// <summary>No contextual dependencies are allowed or required.</summary>
    None = 0,

    /// <summary>Named value-binding or field-path access.</summary>
    Binding = 1 << 0,

    /// <summary>Declared invocation-parameter access.</summary>
    Parameter = 1 << 1,

    /// <summary>Current-item access inside an explicitly scoped iteration.</summary>
    CurrentItem = 1 << 2,

    /// <summary>Ambient context such as entity identity, root key, or source-set access.</summary>
    Ambient = 1 << 3,

    /// <summary>All currently defined dependency kinds.</summary>
    All = Binding | Parameter | CurrentItem | Ambient
}

/// <summary>Origin of a capability required by an expression.</summary>
public enum ExprCapabilityRequirementKind
{
    /// <summary>An operator, expression node, or function required by the expression.</summary>
    Operation = 0,

    /// <summary>Ambient semantic data that the expression site must provide.</summary>
    Ambient = 1
}

/// <summary>
/// Portable value information known or expected at an expression boundary.
/// </summary>
public sealed record ExprValueContract
{
    /// <summary>Creates a portable expression value contract.</summary>
    /// <param name="type">Known element or single-value type, or <see langword="null"/> when unknown.</param>
    /// <param name="shape">Known graph-qualified shape, or <see langword="null"/> for unshaped or unresolved values.</param>
    /// <param name="cardinality">Whether the value is single or many-valued.</param>
    /// <param name="presence">Whether the value is required to be present.</param>
    /// <param name="nullability">Whether an explicitly present value may be null.</param>
    /// <param name="shapeDefinition">
    /// Optional in-memory shape snapshot used to resolve field contracts without persisting derived scope.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="cardinality"/>, <paramref name="presence"/>, or <paramref name="nullability"/> is unsupported.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="shape"/> is a default or incomplete qualified identity,
    /// <paramref name="shape"/> and <paramref name="shapeDefinition"/> identify different local shapes, or
    /// <paramref name="shapeDefinition"/> contains a field with invalid identity or value-contract metadata.
    /// </exception>
    public ExprValueContract(
        TypeRef? type = null,
        QualifiedShapeId? shape = null,
        FieldCardinality cardinality = FieldCardinality.Single,
        FieldPresence presence = FieldPresence.Required,
        FieldNullability nullability = FieldNullability.NonNullable,
        Shape? shapeDefinition = null)
    {
        if (!Enum.IsDefined(cardinality))
            throw new ArgumentOutOfRangeException(nameof(cardinality), cardinality, "Unsupported value cardinality.");
        if (!Enum.IsDefined(presence))
            throw new ArgumentOutOfRangeException(nameof(presence), presence, "Unsupported value presence.");
        if (!Enum.IsDefined(nullability))
            throw new ArgumentOutOfRangeException(nameof(nullability), nullability, "Unsupported value nullability.");
        if (shape is { } candidateIdentity
            && (string.IsNullOrWhiteSpace(candidateIdentity.GraphId.Value)
                || string.IsNullOrWhiteSpace(candidateIdentity.ShapeId.Value)))
        {
            throw new ArgumentException(
                "A known expression shape requires non-empty graph and shape identifiers.",
                nameof(shape));
        }
        if (shapeDefinition is not null)
            ValidateShapeDefinition(shapeDefinition, nameof(shapeDefinition));
        if (shape is { } identity
            && shapeDefinition is { } definition
            && identity.ShapeId != definition.Id)
        {
            throw new ArgumentException(
                $"Qualified shape identity '{identity}' does not identify shape '{definition.Id.Value}'.",
                nameof(shapeDefinition));
        }

        Type = type;
        Shape = shape;
        Cardinality = cardinality;
        Presence = presence;
        Nullability = nullability;
        ShapeDefinition = shapeDefinition;
    }

    /// <summary>Known element or single-value type.</summary>
    public TypeRef? Type { get; }

    /// <summary>Known graph-qualified shape.</summary>
    public QualifiedShapeId? Shape { get; }

    /// <summary>Whether the value is single or many-valued.</summary>
    public FieldCardinality Cardinality { get; }

    /// <summary>Whether the value is required to be present.</summary>
    public FieldPresence Presence { get; }

    /// <summary>Whether an explicitly present value may be null.</summary>
    public FieldNullability Nullability { get; }

    /// <summary>Optional in-memory shape snapshot used for precise field-contract resolution.</summary>
    [JsonIgnore]
    public Shape? ShapeDefinition { get; }

    /// <summary>
    /// Gets the expression-level type, wrapping the element type in <see cref="ArrayTypeRef"/> for a many-valued contract.
    /// </summary>
    /// <returns>The expression-level type, or <see langword="null"/> when the type is unknown.</returns>
    public TypeRef? GetEffectiveType() => Type is null
        ? null
        : Cardinality == FieldCardinality.Many
            ? new ArrayTypeRef(Type)
            : Type;

    /// <summary>Gets the most specific coarse result category known for this value contract.</summary>
    /// <returns>The portable result category, or <see cref="ExprResultCategory.Any"/> when unknown.</returns>
    public ExprResultCategory GetResultCategory() => ExprResultCategorySemantics.Classify(this);

    /// <summary>Tests whether a portable constant satisfies this value contract.</summary>
    /// <param name="value">Constant value to test.</param>
    /// <returns>
    /// <see langword="true"/> when presence, nullability, and every locally resolvable type constraint are satisfied;
    /// otherwise <see langword="false"/>.
    /// </returns>
    public bool IsSatisfiedByConstant(ObservationValue value)
    {
        if (value.Kind == ObservationValueKind.Undefined)
            return Presence == FieldPresence.Optional;
        if (value.Kind == ObservationValueKind.Null)
            return Nullability == FieldNullability.Nullable;
        return GetEffectiveType() is not { } type
            || ExprValueContractSemantics.Evaluate(type, value) != ExprConstantCompatibility.Incompatible;
    }

    /// <summary>Creates a value contract from a semantic field definition.</summary>
    /// <param name="field">Field whose type and value guarantees are copied.</param>
    /// <returns>A value contract preserving the field type, cardinality, presence, and nullability.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="field"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="field"/> has no semantic type.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="field"/> has an unsupported cardinality, presence, or nullability value.
    /// </exception>
    public static ExprValueContract FromField(FieldDefinition field)
    {
        ArgumentNullException.ThrowIfNull(field);
        if (field.Type is null)
            throw new ArgumentException("A field value contract requires a semantic type.", nameof(field));
        return new(field.Type, cardinality: field.Cardinality, presence: field.Presence, nullability: field.Nullability);
    }

    /// <summary>Creates an object-value contract from a semantic shape.</summary>
    /// <param name="shape">Shape whose fields form the object type.</param>
    /// <param name="qualifiedShape">Optional graph-qualified identity for <paramref name="shape"/>.</param>
    /// <returns>An object-value contract derived from the shape.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="shape"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="qualifiedShape"/> identifies a different local shape than <paramref name="shape"/>, or
    /// <paramref name="shape"/> contains a field with invalid identity or value-contract metadata.
    /// </exception>
    public static ExprValueContract FromShape(Shape shape, QualifiedShapeId? qualifiedShape = null)
    {
        ArgumentNullException.ThrowIfNull(shape);
        ValidateShapeDefinition(shape, nameof(shape));
        if (qualifiedShape is { } identity && identity.ShapeId != shape.Id)
        {
            throw new ArgumentException(
                $"Qualified shape identity '{identity}' does not identify shape '{shape.Id.Value}'.",
                nameof(qualifiedShape));
        }

        var fields = shape.Fields.IsDefault
            ? ImmutableArray<FieldDefinition>.Empty
            : shape.Fields;
        if (fields.IsDefaultOrEmpty)
            return new(shape: qualifiedShape, shapeDefinition: shape);

        return new(
            type: new ObjectTypeRef(
            [
                .. fields.Select(static field => new ObjectFieldTypeDef(
                    field.Name.Value,
                    field.Cardinality == FieldCardinality.Many
                        ? new ArrayTypeRef(field.Type)
                        : field.Type,
                    field.Presence))
            ]),
            shape: qualifiedShape,
            shapeDefinition: shape);
    }

    static void ValidateShapeDefinition(Shape shape, string parameterName)
    {
        foreach (var field in shape.Fields)
        {
            if (field.Type is null)
            {
                throw new ArgumentException(
                    $"Shape '{shape.Id.Value}' field '{field.Name.Value}' has no semantic type.",
                    parameterName);
            }
            if (!Enum.IsDefined(field.Cardinality))
            {
                throw new ArgumentException(
                    $"Shape '{shape.Id.Value}' field '{field.Name.Value}' has unsupported cardinality '{((int)field.Cardinality).ToString(CultureInfo.InvariantCulture)}'.",
                    parameterName);
            }
            if (!Enum.IsDefined(field.Presence))
            {
                throw new ArgumentException(
                    $"Shape '{shape.Id.Value}' field '{field.Name.Value}' has unsupported presence '{((int)field.Presence).ToString(CultureInfo.InvariantCulture)}'.",
                    parameterName);
            }
            if (!Enum.IsDefined(field.Nullability))
            {
                throw new ArgumentException(
                    $"Shape '{shape.Id.Value}' field '{field.Name.Value}' has unsupported nullability '{((int)field.Nullability).ToString(CultureInfo.InvariantCulture)}'.",
                    parameterName);
            }
        }
    }
}

/// <summary>One named value binding visible at an expression site.</summary>
public sealed record ExprScopeBinding
{
    /// <summary>Creates a scoped value binding.</summary>
    /// <param name="id">Stable value-binding identifier.</param>
    /// <param name="value">Known semantic value contract.</param>
    /// <param name="availability">Whether the binding may be absent during evaluation.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="id"/> is the default, empty identifier.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="availability"/> is unsupported.</exception>
    public ExprScopeBinding(
        ValueBindingId id,
        ExprValueContract value,
        ExprBindingAvailability availability = ExprBindingAvailability.AlwaysPresent)
    {
        if (string.IsNullOrWhiteSpace(id.Value))
            throw new ArgumentException("A scope binding must have a non-empty identifier.", nameof(id));
        if (!Enum.IsDefined(availability))
            throw new ArgumentOutOfRangeException(nameof(availability), availability, "Unsupported binding availability.");

        Id = id;
        Value = Guard.RequireNotNull(value);
        Availability = availability;
    }

    /// <summary>Stable binding identifier.</summary>
    public ValueBindingId Id { get; }

    /// <summary>Known semantic value contract.</summary>
    public ExprValueContract Value { get; }

    /// <summary>Whether the binding may be absent during evaluation.</summary>
    public ExprBindingAvailability Availability { get; }
}

/// <summary>One declared parameter visible at an expression site.</summary>
public sealed record ExprScopeParameter
{
    /// <summary>
    /// Creates a scoped expression parameter whose invocation and evaluated-value presence are the same.
    /// </summary>
    /// <param name="name">Stable parameter name referenced by <see cref="ParameterExpr"/>.</param>
    /// <param name="type">Portable parameter type.</param>
    /// <param name="presence">
    /// Whether an invocation must supply the parameter and whether its evaluated value is always present.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> or <paramref name="type"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty or white space.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="presence"/> is unsupported.</exception>
    public ExprScopeParameter(
        string name,
        TypeRef type,
        FieldPresence presence = FieldPresence.Required)
    {
        if (!Enum.IsDefined(presence))
            throw new ArgumentOutOfRangeException(nameof(presence), presence, "Unsupported parameter presence.");

        Name = Guard.RequireNotNullOrWhiteSpace(name);
        Value = new(Guard.RequireNotNull(type), presence: presence);
        InvocationPresence = presence;
    }

    /// <summary>
    /// Creates a scoped expression parameter with distinct invocation and evaluated-value contracts.
    /// </summary>
    /// <param name="name">Stable parameter name referenced by <see cref="ParameterExpr"/>.</param>
    /// <param name="value">Contract of the value observed while evaluating the expression.</param>
    /// <param name="invocationPresence">
    /// Whether an invocation must explicitly supply the parameter. This may be optional even when
    /// <paramref name="value"/> is always present because a declaration supplies a default.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> or <paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="name"/> is empty or white space, or <paramref name="value"/> has no semantic type.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="invocationPresence"/> is unsupported.</exception>
    public ExprScopeParameter(
        string name,
        ExprValueContract value,
        FieldPresence invocationPresence)
    {
        if (!Enum.IsDefined(invocationPresence))
        {
            throw new ArgumentOutOfRangeException(
                nameof(invocationPresence),
                invocationPresence,
                "Unsupported invocation presence.");
        }

        Name = Guard.RequireNotNullOrWhiteSpace(name);
        Value = Guard.RequireNotNull(value);
        if (Value.GetEffectiveType() is null)
            throw new ArgumentException("A scoped parameter value must have a semantic type.", nameof(value));
        InvocationPresence = invocationPresence;
    }

    /// <summary>Stable parameter name.</summary>
    public string Name { get; }

    /// <summary>Contract of the value observed while evaluating the expression.</summary>
    public ExprValueContract Value { get; }

    /// <summary>Whether an invocation must explicitly supply the parameter.</summary>
    public FieldPresence InvocationPresence { get; }
}

/// <summary>
/// Immutable environment made available by one expression site.
/// </summary>
public sealed class ExprScope
{
    readonly ImmutableDictionary<ValueBindingId, ExprScopeBinding> bindingsById;
    readonly ImmutableDictionary<string, ExprScopeParameter> parametersByName;
    readonly ImmutableHashSet<ExprCapabilityId> ambientCapabilitySet;

    /// <summary>An empty expression scope.</summary>
    public static ExprScope Empty { get; } = new();

    /// <summary>Creates an immutable expression scope.</summary>
    /// <param name="bindings">Named value bindings visible at the site.</param>
    /// <param name="implicitBinding">Binding used for unqualified field references, or <see langword="null"/>.</param>
    /// <param name="parameters">Declared parameters visible at the site.</param>
    /// <param name="currentItem">Current-item value contract, or <see langword="null"/> when current-item access is unavailable.</param>
    /// <param name="ambientCapabilities">Ambient semantic capabilities supplied by the site.</param>
    /// <exception cref="ArgumentException">
    /// A binding or parameter identifier is duplicated, an entry is <see langword="null"/>, or
    /// <paramref name="implicitBinding"/> is empty or not present in <paramref name="bindings"/>, or an
    /// ambient capability is the default, empty identifier.
    /// </exception>
    public ExprScope(
        IEnumerable<ExprScopeBinding>? bindings = null,
        ValueBindingId? implicitBinding = null,
        IEnumerable<ExprScopeParameter>? parameters = null,
        ExprValueContract? currentItem = null,
        IEnumerable<ExprCapabilityId>? ambientCapabilities = null)
    {
        var normalizedBindings = NormalizeBindings(bindings);
        var normalizedParameters = NormalizeParameters(parameters);
        var normalizedCapabilities = NormalizeCapabilities(ambientCapabilities);

        bindingsById = normalizedBindings.ToImmutableDictionary(static binding => binding.Id);
        parametersByName = normalizedParameters.ToImmutableDictionary(
            static parameter => parameter.Name,
            StringComparer.Ordinal);
        ambientCapabilitySet = normalizedCapabilities.ToImmutableHashSet();

        if (implicitBinding is { } binding && string.IsNullOrWhiteSpace(binding.Value))
            throw new ArgumentException("An implicit binding must have a non-empty identifier.", nameof(implicitBinding));
        if (implicitBinding is { } visibleBinding && !bindingsById.ContainsKey(visibleBinding))
        {
            throw new ArgumentException(
                $"Implicit binding '{visibleBinding.Value}' is not visible in the scope.",
                nameof(implicitBinding));
        }

        Bindings = normalizedBindings;
        ImplicitBinding = implicitBinding;
        Parameters = normalizedParameters;
        CurrentItem = currentItem;
        AmbientCapabilities = normalizedCapabilities;
    }

    /// <summary>Named value bindings sorted by ordinal identifier.</summary>
    public ImmutableArray<ExprScopeBinding> Bindings { get; }

    /// <summary>Binding used for unqualified field references.</summary>
    public ValueBindingId? ImplicitBinding { get; }

    /// <summary>Declared parameters sorted by ordinal name.</summary>
    public ImmutableArray<ExprScopeParameter> Parameters { get; }

    /// <summary>Current-item contract, or <see langword="null"/> when current-item access is unavailable.</summary>
    public ExprValueContract? CurrentItem { get; }

    /// <summary>Ambient capabilities sorted by ordinal identifier.</summary>
    public ImmutableArray<ExprCapabilityId> AmbientCapabilities { get; }

    /// <summary>Looks up a visible value binding.</summary>
    /// <param name="id">Binding identifier to resolve.</param>
    /// <param name="binding">Resolved binding when visible.</param>
    /// <returns><see langword="true"/> when the binding is visible; otherwise <see langword="false"/>.</returns>
    public bool TryGetBinding(ValueBindingId id, out ExprScopeBinding binding) =>
        bindingsById.TryGetValue(id, out binding!);

    /// <summary>Looks up a declared parameter.</summary>
    /// <param name="name">Parameter name to resolve.</param>
    /// <param name="parameter">Resolved declaration when visible.</param>
    /// <returns><see langword="true"/> when the parameter is declared; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <see langword="null"/>.</exception>
    public bool TryGetParameter(string name, out ExprScopeParameter parameter)
    {
        ArgumentNullException.ThrowIfNull(name);
        return parametersByName.TryGetValue(name, out parameter!);
    }

    /// <summary>Tests whether the site supplies an ambient capability.</summary>
    /// <param name="capability">Capability to test.</param>
    /// <returns><see langword="true"/> when supplied; otherwise <see langword="false"/>.</returns>
    public bool HasAmbientCapability(ExprCapabilityId capability) => ambientCapabilitySet.Contains(capability);

    /// <summary>Returns a scope that makes a current item available while preserving the remaining environment.</summary>
    /// <param name="currentItem">Current-item value contract, or an unknown contract when omitted.</param>
    /// <returns>A new scope with explicit current-item access.</returns>
    public ExprScope WithCurrentItem(ExprValueContract? currentItem = null) => new(
        Bindings,
        ImplicitBinding,
        Parameters,
        currentItem ?? new ExprValueContract(),
        AmbientCapabilities);

    static ImmutableArray<ExprScopeBinding> NormalizeBindings(IEnumerable<ExprScopeBinding>? bindings)
    {
        var array = bindings is null ? [] : bindings.ToImmutableArray();
        if (array.Any(static binding => binding is null))
            throw new ArgumentException("Expression scope bindings cannot contain null entries.", nameof(bindings));
        var duplicate = array
            .GroupBy(static binding => binding.Id)
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicate is not null)
            throw new ArgumentException($"Expression scope contains duplicate binding '{duplicate.Key.Value}'.", nameof(bindings));
        return [.. array.OrderBy(static binding => binding.Id.Value, StringComparer.Ordinal)];
    }

    static ImmutableArray<ExprScopeParameter> NormalizeParameters(IEnumerable<ExprScopeParameter>? parameters)
    {
        var array = parameters is null ? [] : parameters.ToImmutableArray();
        if (array.Any(static parameter => parameter is null))
            throw new ArgumentException("Expression scope parameters cannot contain null entries.", nameof(parameters));
        var duplicate = array
            .GroupBy(static parameter => parameter.Name, StringComparer.Ordinal)
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicate is not null)
            throw new ArgumentException($"Expression scope contains duplicate parameter '{duplicate.Key}'.", nameof(parameters));
        return [.. array.OrderBy(static parameter => parameter.Name, StringComparer.Ordinal)];
    }

    static ImmutableArray<ExprCapabilityId> NormalizeCapabilities(IEnumerable<ExprCapabilityId>? capabilities)
    {
        if (capabilities is null)
            return [];

        var array = capabilities.ToImmutableArray();
        if (array.Any(static capability => string.IsNullOrWhiteSpace(capability.Value)))
            throw new ArgumentException("Ambient capabilities must have non-empty identifiers.", nameof(capabilities));
        return [.. array.Distinct().OrderBy(static capability => capability.Value, StringComparer.Ordinal)];
    }
}

/// <summary>Expected result and dependency contract declared by an expression site.</summary>
public sealed record ExprExpectation
{
    /// <summary>An unconstrained expression expectation.</summary>
    public static ExprExpectation Any { get; } = new();

    /// <summary>A required, non-null Boolean expression expectation.</summary>
    public static ExprExpectation Boolean { get; } = new(
        ExprResultCategory.Boolean,
        new ExprValueContract(new ScalarTypeRef(ScalarTypeKind.Bool)));

    /// <summary>Creates an expression expectation.</summary>
    /// <param name="category">
    /// Expected coarse result category. A constrained category without <paramref name="value"/> requires a present,
    /// non-null value of that category.
    /// </param>
    /// <param name="value">Expected portable value contract, or <see langword="null"/> when unknown.</param>
    /// <param name="allowedDependencies">Context dependency kinds permitted at the site.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="category"/> or <paramref name="allowedDependencies"/> contains an unsupported value.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="value"/> has a known category that contradicts <paramref name="category"/>.
    /// </exception>
    public ExprExpectation(
        ExprResultCategory category = ExprResultCategory.Any,
        ExprValueContract? value = null,
        ExprDependencyKind allowedDependencies = ExprDependencyKind.All)
    {
        if (!Enum.IsDefined(category))
            throw new ArgumentOutOfRangeException(nameof(category), category, "Unsupported result category.");
        if ((allowedDependencies & ~ExprDependencyKind.All) != 0)
            throw new ArgumentOutOfRangeException(nameof(allowedDependencies), allowedDependencies, "Unsupported dependency kind.");
        if (value is not null
            && !ExprResultCategorySemantics.Satisfies(value.GetResultCategory(), category))
        {
            throw new ArgumentException(
                "The expected value contract does not satisfy the declared result category.",
                nameof(value));
        }

        Category = category;
        Value = value;
        AllowedDependencies = allowedDependencies;
    }

    /// <summary>
    /// Expected coarse result category; when constrained without <see cref="Value"/>, it also requires a present,
    /// non-null value.
    /// </summary>
    public ExprResultCategory Category { get; }

    /// <summary>Expected portable value contract.</summary>
    public ExprValueContract? Value { get; }

    /// <summary>Context dependency kinds permitted at the site.</summary>
    public ExprDependencyKind AllowedDependencies { get; }
}

/// <summary>
/// Immutable declaration of expression operations allowed by a language surface or supported by an interpretation target.
/// </summary>
public sealed class ExprCapabilityProfile
{
    readonly ImmutableHashSet<ExprCapabilityId> supportedCapabilitySet;

    /// <summary>An analysis/interpretation profile that allows no operations.</summary>
    public static ExprCapabilityProfile None { get; } = new();

    /// <summary>Creates an expression capability profile.</summary>
    /// <param name="supportedCapabilities">Operation capabilities supported or allowed by the selected profile.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="supportedCapabilities"/> contains a default, empty identifier.
    /// </exception>
    public ExprCapabilityProfile(IEnumerable<ExprCapabilityId>? supportedCapabilities = null)
    {
        var capabilities = supportedCapabilities is null
            ? []
            : supportedCapabilities.ToImmutableArray();
        if (capabilities.Any(static capability => string.IsNullOrWhiteSpace(capability.Value)))
            throw new ArgumentException("Supported capabilities must have non-empty identifiers.", nameof(supportedCapabilities));

        SupportedCapabilities = [.. capabilities.Distinct().OrderBy(static capability => capability.Value, StringComparer.Ordinal)];
        supportedCapabilitySet = SupportedCapabilities.ToImmutableHashSet();
    }

    /// <summary>Allowed or supported operation capabilities sorted by ordinal identifier.</summary>
    public ImmutableArray<ExprCapabilityId> SupportedCapabilities { get; }

    /// <summary>Tests whether the selected profile allows or supports an operation capability.</summary>
    /// <param name="capability">Capability to test.</param>
    /// <returns><see langword="true"/> when supported; otherwise <see langword="false"/>.</returns>
    public bool Supports(ExprCapabilityId capability) => supportedCapabilitySet.Contains(capability);
}

/// <summary>Semantic root against which a required field path is evaluated.</summary>
public enum ExprFieldRootKind
{
    /// <summary>The path is evaluated against a named value binding.</summary>
    Binding = 0,

    /// <summary>The path is evaluated against the current item of a scoped expression.</summary>
    CurrentItem = 1,

    /// <summary>The authored field path could not be associated with an available root.</summary>
    Unresolved = 2
}

/// <summary>One field-path access derived from an expression.</summary>
public readonly record struct ExprFieldRequirement
{
    /// <summary>Creates a field-path requirement with an explicit semantic root.</summary>
    /// <param name="path">Complete authored field path.</param>
    /// <param name="root">Semantic value against which the path is evaluated.</param>
    /// <param name="binding">Named binding for <see cref="ExprFieldRootKind.Binding"/>; otherwise <see langword="null"/>.</param>
    /// <param name="wasUnqualified">Whether the authored expression omitted an explicit binding.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="path"/> is invalid, <paramref name="binding"/> is missing or empty for a binding root,
    /// <paramref name="binding"/> is supplied for another root, or a current-item path does not begin with
    /// <see cref="ExprFieldRoots.CurrentItem"/>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="root"/> is unsupported.</exception>
    public ExprFieldRequirement(
        FieldPath path,
        ExprFieldRootKind root,
        ValueBindingId? binding = null,
        bool wasUnqualified = false)
    {
        if (!Enum.IsDefined(root))
            throw new ArgumentOutOfRangeException(nameof(root), root, "Unsupported field root kind.");
        if (!IsValidPath(path))
            throw new ArgumentException("A field requirement must contain a valid path.", nameof(path));
        if (root == ExprFieldRootKind.Binding
            && (binding is not { } named || string.IsNullOrWhiteSpace(named.Value)))
        {
            throw new ArgumentException("A binding-rooted field requirement must name a binding.", nameof(binding));
        }
        if (root != ExprFieldRootKind.Binding && binding is not null)
            throw new ArgumentException("Only binding-rooted field requirements may name a binding.", nameof(binding));
        if (root == ExprFieldRootKind.CurrentItem && !IsCurrentItemPath(path))
        {
            throw new ArgumentException(
                $"A current-item field path must begin with '{ExprFieldRoots.CurrentItem}'.",
                nameof(path));
        }

        Path = path;
        Root = root;
        Binding = binding;
        WasUnqualified = wasUnqualified;
    }

    /// <summary>Complete authored field path.</summary>
    public FieldPath Path { get; }

    /// <summary>Semantic value against which the path is evaluated.</summary>
    public ExprFieldRootKind Root { get; }

    /// <summary>Named binding for a binding-rooted field; otherwise <see langword="null"/>.</summary>
    public ValueBindingId? Binding { get; }

    /// <summary>Whether the authored expression omitted an explicit binding.</summary>
    public bool WasUnqualified { get; }

    internal static bool IsValidPath(FieldPath path) =>
        !path.Segments.IsDefaultOrEmpty
        && path.Segments.All(static segment => segment.Kind switch
        {
            SegmentKind.Field => !string.IsNullOrWhiteSpace(segment.Segment),
            SegmentKind.Element => segment.Segment is null,
            _ => false
        });

    internal static bool IsCurrentItemPath(FieldPath path) =>
        IsValidPath(path)
        && path.Segments[0] is
        {
            Kind: SegmentKind.Field,
            Segment: ExprFieldRoots.CurrentItem
        };
}

/// <summary>One operation or ambient capability required by an expression.</summary>
/// <param name="Capability">Stable required capability identifier.</param>
/// <param name="Kind">Whether the requirement belongs to the selected interpretation profile or expression site.</param>
public readonly record struct ExprCapabilityRequirement(
    ExprCapabilityId Capability,
    ExprCapabilityRequirementKind Kind);

/// <summary>One capability use at a precise expression-tree location.</summary>
/// <param name="Requirement">Capability and whether it belongs to the selected profile or ambient scope.</param>
/// <param name="ExpressionPath">Culture-independent path to the expression node that requires the capability.</param>
/// <param name="IsSatisfied">Whether the selected profile or scope satisfies the capability at this use.</param>
public readonly record struct ExprCapabilityUse(
    ExprCapabilityRequirement Requirement,
    string ExpressionPath,
    bool IsSatisfied);

/// <summary>Immutable context requirements derived from one or more expressions.</summary>
public sealed class ExprRequirements
{
    /// <summary>An empty requirement set.</summary>
    public static ExprRequirements Empty { get; } = new();

    /// <summary>Creates an immutable requirement set.</summary>
    /// <param name="fields">Field-path requirements.</param>
    /// <param name="bindings">Named binding requirements.</param>
    /// <param name="parameters">Parameter-name requirements.</param>
    /// <param name="requiresCurrentItem">Whether current-item access is required.</param>
    /// <param name="capabilities">Operation and ambient capability requirements.</param>
    /// <exception cref="ArgumentException">
    /// A field path, binding id, parameter name, capability id, or capability kind is invalid.
    /// </exception>
    public ExprRequirements(
        IEnumerable<ExprFieldRequirement>? fields = null,
        IEnumerable<ValueBindingId>? bindings = null,
        IEnumerable<string>? parameters = null,
        bool requiresCurrentItem = false,
        IEnumerable<ExprCapabilityRequirement>? capabilities = null)
    {
        var normalizedFields = fields is null ? [] : fields.ToImmutableArray();
        var normalizedBindings = bindings is null ? [] : bindings.ToImmutableArray();
        var normalizedParameters = parameters is null ? [] : parameters.ToImmutableArray();
        var normalizedCapabilities = capabilities is null ? [] : capabilities.ToImmutableArray();

        if (normalizedFields.Any(static field =>
                !Enum.IsDefined(field.Root)
                || !ExprFieldRequirement.IsValidPath(field.Path)
                || field.Root == ExprFieldRootKind.Binding
                    && (field.Binding is not { } binding || string.IsNullOrWhiteSpace(binding.Value))
                || field.Root != ExprFieldRootKind.Binding && field.Binding is not null
                || field.Root == ExprFieldRootKind.CurrentItem
                    && !ExprFieldRequirement.IsCurrentItemPath(field.Path)))
        {
            throw new ArgumentException(
                "Field requirements must contain valid paths and consistent semantic roots.",
                nameof(fields));
        }
        if (normalizedBindings.Any(static binding => string.IsNullOrWhiteSpace(binding.Value)))
            throw new ArgumentException("Binding requirements must have non-empty identifiers.", nameof(bindings));
        if (normalizedParameters.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Parameter requirements must have non-empty names.", nameof(parameters));
        if (normalizedCapabilities.Any(static capability =>
                string.IsNullOrWhiteSpace(capability.Capability.Value)
                || !Enum.IsDefined(capability.Kind)))
        {
            throw new ArgumentException(
                "Capability requirements must have non-empty identifiers and supported kinds.",
                nameof(capabilities));
        }

        Fields =
            [
                .. normalizedFields.GroupBy(static field => (
                        field.Root,
                        Binding: field.Binding?.Value,
                        Path: FieldPathSortKey(field.Path),
                        field.WasUnqualified))
                    .Select(static group => group.First())
                    .OrderBy(static field => (int)field.Root)
                    .ThenBy(static field => field.Binding?.Value, StringComparer.Ordinal)
                    .ThenBy(static field => FieldPathSortKey(field.Path), StringComparer.Ordinal)
                    .ThenBy(static field => field.WasUnqualified)
            ];
        Bindings = [.. normalizedBindings.Distinct().OrderBy(static binding => binding.Value, StringComparer.Ordinal)];
        Parameters = [.. normalizedParameters.Distinct(StringComparer.Ordinal).OrderBy(static parameter => parameter, StringComparer.Ordinal)];
        RequiresCurrentItem = requiresCurrentItem
            || Fields.Any(static field => field.Root == ExprFieldRootKind.CurrentItem);
        Capabilities =
            [
                .. normalizedCapabilities.Distinct()
                    .OrderBy(static capability => (int)capability.Kind)
                    .ThenBy(static capability => capability.Capability.Value, StringComparer.Ordinal)
            ];

        var dependencies = ExprDependencyKind.None;
        if (Fields.Any(static field => field.Root != ExprFieldRootKind.CurrentItem)
            || !Bindings.IsDefaultOrEmpty)
            dependencies |= ExprDependencyKind.Binding;
        if (!Parameters.IsDefaultOrEmpty)
            dependencies |= ExprDependencyKind.Parameter;
        if (RequiresCurrentItem)
            dependencies |= ExprDependencyKind.CurrentItem;
        if (Capabilities.Any(static capability => capability.Kind == ExprCapabilityRequirementKind.Ambient))
            dependencies |= ExprDependencyKind.Ambient;
        Dependencies = dependencies;
    }

    static string FieldPathSortKey(FieldPath path)
    {
        if (path.Segments.IsDefaultOrEmpty)
            return string.Empty;

        return string.Join(
            '\u001f',
            path.Segments.Select(static segment =>
            {
                var value = segment.Segment;
                return $"{((int)segment.Kind).ToString(System.Globalization.CultureInfo.InvariantCulture)}:"
                    + $"{(value?.Length ?? -1).ToString(System.Globalization.CultureInfo.InvariantCulture)}:{value}";
            }));
    }

    /// <summary>Field-path requirements sorted by binding and path.</summary>
    public ImmutableArray<ExprFieldRequirement> Fields { get; }

    /// <summary>Required named bindings sorted by ordinal identifier.</summary>
    public ImmutableArray<ValueBindingId> Bindings { get; }

    /// <summary>Required parameter names sorted ordinally.</summary>
    public ImmutableArray<string> Parameters { get; }

    /// <summary>Whether current-item access is required.</summary>
    public bool RequiresCurrentItem { get; }

    /// <summary>Required capabilities sorted by kind and ordinal identifier.</summary>
    public ImmutableArray<ExprCapabilityRequirement> Capabilities { get; }

    /// <summary>Combined contextual dependency kinds derived from the requirements.</summary>
    public ExprDependencyKind Dependencies { get; }

    /// <summary>Combines requirement sets without depending on declaration order.</summary>
    /// <param name="requirements">Requirement sets to combine.</param>
    /// <returns>The deterministic union of all supplied requirements.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="requirements"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="requirements"/> contains a <see langword="null"/> entry.</exception>
    public static ExprRequirements Combine(IEnumerable<ExprRequirements> requirements)
    {
        ArgumentNullException.ThrowIfNull(requirements);
        var items = requirements.ToImmutableArray();
        if (items.Any(static item => item is null))
            throw new ArgumentException("Requirement sets cannot contain null entries.", nameof(requirements));
        return new(
            items.SelectMany(static item => item.Fields),
            items.SelectMany(static item => item.Bindings),
            items.SelectMany(static item => item.Parameters),
            items.Any(static item => item.RequiresCurrentItem),
            items.SelectMany(static item => item.Capabilities));
    }
}

/// <summary>Expression together with its semantic site, available scope, and expected result.</summary>
public sealed class ExprSite
{
    /// <summary>Creates an expression site.</summary>
    /// <param name="id">Stable semantic site identifier.</param>
    /// <param name="expression">Canonical expression evaluated at the site.</param>
    /// <param name="scope">Available bindings, parameters, current item, and ambient capabilities.</param>
    /// <param name="expectation">Expected result and allowed dependencies; defaults to unconstrained.</param>
    /// <param name="capabilityProfile">
    /// Selected language allowance or interpretation-target support; defaults to all built-in capabilities.
    /// </param>
    /// <param name="diagnosticLocation">Optional document location used by structured diagnostics.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="expression"/> or <paramref name="scope"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="id"/> is the default, empty identifier.</exception>
    public ExprSite(
        ExprSiteId id,
        Expr expression,
        ExprScope scope,
        ExprExpectation? expectation = null,
        ExprCapabilityProfile? capabilityProfile = null,
        string? diagnosticLocation = null)
    {
        if (string.IsNullOrWhiteSpace(id.Value))
            throw new ArgumentException("An expression site must have a non-empty identifier.", nameof(id));

        Id = id;
        Expression = Guard.RequireNotNull(expression);
        Scope = Guard.RequireNotNull(scope);
        Expectation = expectation ?? ExprExpectation.Any;
        CapabilityProfile = capabilityProfile ?? ExprSemanticsCatalog.Default.CreateCapabilityProfile();
        DiagnosticLocation = string.IsNullOrWhiteSpace(diagnosticLocation) ? id.Value : diagnosticLocation;
    }

    /// <summary>Stable semantic site identifier.</summary>
    public ExprSiteId Id { get; }

    /// <summary>Canonical expression evaluated at this site.</summary>
    public Expr Expression { get; }

    /// <summary>Available semantic scope.</summary>
    public ExprScope Scope { get; }

    /// <summary>Expected result and allowed dependencies.</summary>
    public ExprExpectation Expectation { get; }

    /// <summary>Language allowance or interpretation-target support used during analysis.</summary>
    public ExprCapabilityProfile CapabilityProfile { get; }

    /// <summary>Document location used by structured diagnostics.</summary>
    public string DiagnosticLocation { get; }
}

/// <summary>Immutable result of analyzing a canonical expression at one semantic site.</summary>
public sealed class ExprAnalysisResult
{
    /// <summary>Creates an expression analysis result.</summary>
    /// <param name="site">Analyzed expression site.</param>
    /// <param name="semantics">Exact immutable function/operator semantics used by the analysis.</param>
    /// <param name="resultCategory">Known coarse result category.</param>
    /// <param name="knownResult">Known portable result contract, or <see langword="null"/>.</param>
    /// <param name="requirements">Requirements derived from the expression.</param>
    /// <param name="capabilityUses">Capability uses with expression-tree provenance.</param>
    /// <param name="validation">Structured analysis diagnostics.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="site"/>, <paramref name="semantics"/>, <paramref name="requirements"/>,
    /// <paramref name="capabilityUses"/>, or <paramref name="validation"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="knownResult"/> contradicts <paramref name="resultCategory"/>, or
    /// <paramref name="capabilityUses"/> contains an invalid capability, kind, or expression path.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="resultCategory"/> is unsupported.</exception>
    public ExprAnalysisResult(
        ExprSite site,
        ExprSemanticsCatalog semantics,
        ExprResultCategory resultCategory,
        ExprValueContract? knownResult,
        ExprRequirements requirements,
        IEnumerable<ExprCapabilityUse> capabilityUses,
        DocumentValidationResult validation)
    {
        if (!Enum.IsDefined(resultCategory))
            throw new ArgumentOutOfRangeException(nameof(resultCategory), resultCategory, "Unsupported result category.");
        if (knownResult is not null
            && !ExprResultCategorySemantics.Satisfies(knownResult.GetResultCategory(), resultCategory))
        {
            throw new ArgumentException(
                "The known result contract does not satisfy the declared result category.",
                nameof(knownResult));
        }

        Site = Guard.RequireNotNull(site);
        Semantics = Guard.RequireNotNull(semantics);
        ResultCategory = resultCategory;
        KnownResult = knownResult;
        Requirements = Guard.RequireNotNull(requirements);
        ArgumentNullException.ThrowIfNull(capabilityUses);
        var normalizedCapabilityUses = capabilityUses.ToImmutableArray();
        if (normalizedCapabilityUses.Any(static use =>
                string.IsNullOrWhiteSpace(use.Requirement.Capability.Value)
                || !Enum.IsDefined(use.Requirement.Kind)
                || string.IsNullOrWhiteSpace(use.ExpressionPath)))
        {
            throw new ArgumentException(
                "Capability uses must contain valid capabilities, kinds, and expression paths.",
                nameof(capabilityUses));
        }
        CapabilityUses =
        [
            .. normalizedCapabilityUses.Distinct()
                .OrderBy(static use => use.ExpressionPath, StringComparer.Ordinal)
                .ThenBy(static use => (int)use.Requirement.Kind)
                .ThenBy(static use => use.Requirement.Capability.Value, StringComparer.Ordinal)
                .ThenBy(static use => use.IsSatisfied)
        ];
        Validation = Guard.RequireNotNull(validation);
    }

    /// <summary>Analyzed expression site.</summary>
    public ExprSite Site { get; }

    /// <summary>Exact immutable function/operator semantics used by the analysis.</summary>
    public ExprSemanticsCatalog Semantics { get; }

    /// <summary>Known coarse result category.</summary>
    public ExprResultCategory ResultCategory { get; }

    /// <summary>Known portable result contract.</summary>
    public ExprValueContract? KnownResult { get; }

    /// <summary>Requirements derived from the expression.</summary>
    public ExprRequirements Requirements { get; }

    /// <summary>Capability uses sorted by expression path, kind, and stable capability identity.</summary>
    public ImmutableArray<ExprCapabilityUse> CapabilityUses { get; }

    /// <summary>Structured analysis diagnostics.</summary>
    public DocumentValidationResult Validation { get; }

    /// <summary>Whether analysis produced no error diagnostics.</summary>
    public bool IsValid => Validation.IsValid;
}

/// <summary>Stable diagnostic codes emitted by shared expression analysis.</summary>
public static class ExprAnalysisDiagnosticCodes
{
    /// <summary>A required expression or child node is missing.</summary>
    public const string ExpressionMissing = "expr.expression.missing";

    /// <summary>A field path is empty or structurally invalid.</summary>
    public const string FieldPathInvalid = "expr.field.pathInvalid";

    /// <summary>A field path cannot be resolved against a known value type.</summary>
    public const string FieldPathUnknown = "expr.field.pathUnknown";

    /// <summary>An explicitly referenced binding is not visible.</summary>
    public const string BindingNotVisible = "expr.binding.notVisible";

    /// <summary>An explicitly referenced binding identifier is empty.</summary>
    public const string BindingInvalid = "expr.binding.invalid";

    /// <summary>An unqualified field has no explicit, unambiguous implicit binding.</summary>
    public const string ImplicitBindingUnavailable = "expr.binding.implicitUnavailable";

    /// <summary>A parameter identifier is empty.</summary>
    public const string ParameterInvalid = "expr.parameter.invalid";

    /// <summary>A referenced parameter is not declared by the scope.</summary>
    public const string ParameterNotDeclared = "expr.parameter.notDeclared";

    /// <summary>Current-item access is used outside an explicit current-item scope.</summary>
    public const string CurrentItemUnavailable = "expr.currentItem.unavailable";

    /// <summary>An operation is not allowed or supported by the selected capability profile.</summary>
    public const string CapabilityUnsupported = "expr.capability.unsupported";

    /// <summary>An ambient capability required by an expression is unavailable at the site.</summary>
    public const string AmbientCapabilityUnavailable = "expr.capability.ambientUnavailable";

    /// <summary>A function has no semantic definition in the selected catalog.</summary>
    public const string FunctionUnknown = "expr.function.unknown";

    /// <summary>A function call violates its declared arity.</summary>
    public const string FunctionArityInvalid = "expr.function.arityInvalid";

    /// <summary>An operator or aggregate enum value has no semantic definition.</summary>
    public const string OperationUnknown = "expr.operation.unknown";

    /// <summary>The expression depends on context forbidden by its site expectation.</summary>
    public const string DependencyNotAllowed = "expr.dependency.notAllowed";

    /// <summary>The known result category does not satisfy the site expectation.</summary>
    public const string ResultCategoryMismatch = "expr.result.categoryMismatch";

    /// <summary>The known portable result type does not satisfy the site expectation.</summary>
    public const string ResultTypeMismatch = "expr.result.typeMismatch";

    /// <summary>The analyzer encountered a future or unsupported expression node.</summary>
    public const string NodeUnsupported = "expr.node.unsupported";
}
