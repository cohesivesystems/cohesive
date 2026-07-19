using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Cohesive.Model;
using Microsoft.Azure.Cosmos;

namespace Cohesive.Adapters.Cosmos;

/// <summary>Sort direction emitted by the standalone Cosmos SQL construction layer.</summary>
public enum CosmosSqlSortDirection
{
    /// <summary>Sorts values from lower to higher.</summary>
    Ascending = 0,

    /// <summary>Sorts values from higher to lower.</summary>
    Descending = 1
}

/// <summary>Binary operators supported by <see cref="CosmosSqlExpression.Binary"/>.</summary>
public enum CosmosSqlBinaryOperator
{
    /// <summary>Equality.</summary>
    Equal = 0,

    /// <summary>Inequality.</summary>
    NotEqual = 1,

    /// <summary>Greater-than comparison.</summary>
    GreaterThan = 2,

    /// <summary>Greater-than-or-equal comparison.</summary>
    GreaterThanOrEqual = 3,

    /// <summary>Less-than comparison.</summary>
    LessThan = 4,

    /// <summary>Less-than-or-equal comparison.</summary>
    LessThanOrEqual = 5,

    /// <summary>Boolean conjunction.</summary>
    And = 6,

    /// <summary>Boolean disjunction.</summary>
    Or = 7,

    /// <summary>Numeric addition.</summary>
    Add = 8,

    /// <summary>Numeric subtraction.</summary>
    Subtract = 9,

    /// <summary>Numeric multiplication.</summary>
    Multiply = 10,

    /// <summary>Numeric division.</summary>
    Divide = 11
}

/// <summary>Unary operators supported by <see cref="CosmosSqlExpression.Unary"/>.</summary>
public enum CosmosSqlUnaryOperator
{
    /// <summary>Boolean negation.</summary>
    Not = 0
}

/// <summary>Cosmos SQL scalar functions exposed by the safe construction layer.</summary>
public enum CosmosSqlFunction
{
    /// <summary>Tests whether a property is defined.</summary>
    IsDefined = 0,

    /// <summary>Tests whether a value is JSON null.</summary>
    IsNull = 1,

    /// <summary>Tests whether an array contains a value.</summary>
    ArrayContains = 2,

    /// <summary>Tests whether a string starts with another string.</summary>
    StartsWith = 3,

    /// <summary>Tests whether a string ends with another string.</summary>
    EndsWith = 4,

    /// <summary>Tests whether a string contains another string.</summary>
    Contains = 5,

    /// <summary>Compares strings using Cosmos SQL string-comparison semantics.</summary>
    StringEquals = 6,

    /// <summary>Converts a string to lower case.</summary>
    Lower = 7,

    /// <summary>Converts a string to upper case.</summary>
    Upper = 8,

    /// <summary>Tests whether a value is a JSON object.</summary>
    IsObject = 9
}

/// <summary>Aggregate functions exposed by the safe Cosmos SQL construction layer.</summary>
public enum CosmosSqlAggregateFunction
{
    /// <summary>Counts input rows or defined expression values.</summary>
    Count = 0,

    /// <summary>Sums numeric values.</summary>
    Sum = 1,

    /// <summary>Returns the minimum value.</summary>
    Minimum = 2,

    /// <summary>Returns the maximum value.</summary>
    Maximum = 3,

    /// <summary>Returns the arithmetic average.</summary>
    Average = 4
}

/// <summary>Source from which a command-template parameter receives its value.</summary>
public enum CosmosSqlParameterBindingKind
{
    /// <summary>The parameter value was captured while the query was constructed.</summary>
    Constant = 0,

    /// <summary>The parameter value must be supplied when the template is bound.</summary>
    Runtime = 1
}

/// <summary>One concrete, ordered Cosmos SQL parameter.</summary>
public sealed record CosmosSqlParameter
{
    /// <summary>Creates a concrete Cosmos SQL parameter.</summary>
    /// <param name="name">SQL parameter name, including its leading <c>@</c>.</param>
    /// <param name="value">Value to normalize into a deterministic Cosmos SDK-compatible representation.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is not a valid Cosmos SQL parameter name.</exception>
    /// <exception cref="NotSupportedException">
    /// <paramref name="value"/> cannot be represented as a deterministic Cosmos SQL parameter.
    /// </exception>
    public CosmosSqlParameter(string name, object? value)
    {
        Name = CosmosSqlNames.RequireParameterName(name, nameof(name));
        Value = CosmosSqlParameterValues.Normalize(value);
    }

    /// <summary>SQL parameter name, including its leading <c>@</c>.</summary>
    public string Name { get; }

    /// <summary>Recursively normalized immutable value supplied to the Cosmos SDK.</summary>
    public object? Value { get; }
}

/// <summary>One deterministic parameter slot in a Cosmos SQL command template.</summary>
public sealed record CosmosSqlParameterSlot
{
    internal CosmosSqlParameterSlot(
        string name,
        CosmosSqlParameterBindingKind kind,
        string? binding,
        object? constantValue)
    {
        Name = CosmosSqlNames.RequireParameterName(name, nameof(name));
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported Cosmos SQL parameter binding kind.");
        if (kind == CosmosSqlParameterBindingKind.Runtime && string.IsNullOrWhiteSpace(binding))
            throw new ArgumentException("A runtime parameter slot requires a binding name.", nameof(binding));
        if (kind == CosmosSqlParameterBindingKind.Constant && binding is not null)
            throw new ArgumentException("A constant parameter slot cannot declare a runtime binding.", nameof(binding));

        Kind = kind;
        Binding = binding;
        ConstantValue = kind == CosmosSqlParameterBindingKind.Constant
            ? CosmosSqlParameterValues.Normalize(constantValue)
            : null;
    }

    /// <summary>SQL parameter name, including its leading <c>@</c>.</summary>
    public string Name { get; }

    /// <summary>Source from which the parameter receives its value.</summary>
    public CosmosSqlParameterBindingKind Kind { get; }

    /// <summary>Runtime binding name, or <see langword="null"/> for a captured constant.</summary>
    public string? Binding { get; }

    /// <summary>Captured constant value, or <see langword="null"/> for a runtime slot or JSON null.</summary>
    public object? ConstantValue { get; }
}

/// <summary>
/// Immutable Cosmos SQL text and ordered parameter slots that can be bound repeatedly without rebuilding the query.
/// </summary>
public sealed class CosmosSqlCommandTemplate
{
    internal CosmosSqlCommandTemplate(string text, ImmutableArray<CosmosSqlParameterSlot> parameters)
    {
        Text = Guard.RequireNotNullOrWhiteSpace(text);
        Parameters = parameters.IsDefault ? [] : parameters;
        if (Parameters.Any(static parameter => parameter is null))
            throw new ArgumentException("Cosmos SQL parameter slots cannot contain null entries.", nameof(parameters));
        if (Parameters.GroupBy(static parameter => parameter.Name, StringComparer.Ordinal).Any(static group => group.Count() > 1))
            throw new ArgumentException("Cosmos SQL parameter slots cannot repeat a parameter name.", nameof(parameters));
        if (Parameters.Where(static parameter => parameter.Kind == CosmosSqlParameterBindingKind.Runtime)
            .GroupBy(static parameter => parameter.Binding, StringComparer.Ordinal)
            .Any(static group => group.Count() > 1))
        {
            throw new ArgumentException("A runtime binding must map to exactly one Cosmos SQL parameter slot.", nameof(parameters));
        }
    }

    /// <summary>Normalized Cosmos SQL text.</summary>
    public string Text { get; }

    /// <summary>Parameter slots in first-use SQL order.</summary>
    public ImmutableArray<CosmosSqlParameterSlot> Parameters { get; }

    /// <summary>Binds runtime parameter values and creates a concrete Cosmos SQL statement.</summary>
    /// <param name="runtimeParameters">
    /// Values keyed by runtime binding name. Omit or pass <see langword="null"/> when the template has no runtime slots.
    /// </param>
    /// <returns>A concrete immutable statement suitable for conversion to <see cref="QueryDefinition"/>.</returns>
    /// <exception cref="ArgumentException">
    /// A required runtime binding is missing, an unknown runtime binding is supplied, or a value cannot be represented
    /// as a Cosmos SQL parameter.
    /// </exception>
    public CosmosSqlStatement Bind(IReadOnlyDictionary<string, object?>? runtimeParameters = null)
    {
        runtimeParameters ??= EmptyRuntimeParameters.Instance;
        var expectedBindings = Parameters
            .Where(static parameter => parameter.Kind == CosmosSqlParameterBindingKind.Runtime)
            .Select(static parameter => parameter.Binding!)
            .ToHashSet(StringComparer.Ordinal);
        var unknown = runtimeParameters.Keys
            .Where(binding => !expectedBindings.Contains(binding))
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (unknown.Length != 0)
        {
            throw new ArgumentException(
                $"Unknown Cosmos SQL runtime parameter binding(s): {string.Join(", ", unknown)}.",
                nameof(runtimeParameters));
        }

        ImmutableArray<CosmosSqlParameter>.Builder bound = ImmutableArray.CreateBuilder<CosmosSqlParameter>(Parameters.Length);
        foreach (var slot in Parameters)
        {
            object? value;
            if (slot.Kind == CosmosSqlParameterBindingKind.Constant)
            {
                value = slot.ConstantValue;
            }
            else if (!runtimeParameters.TryGetValue(slot.Binding!, out value))
            {
                throw new ArgumentException(
                    $"Runtime parameter binding '{slot.Binding}' is required by Cosmos SQL parameter '{slot.Name}'.",
                    nameof(runtimeParameters));
            }
            try
            {
                bound.Add(new(slot.Name, value));
            }
            catch (NotSupportedException exception)
            {
                throw new ArgumentException(
                    $"Runtime parameter binding '{slot.Binding}' cannot be represented by Cosmos SQL.",
                    nameof(runtimeParameters),
                    exception);
            }
        }

        return new(Text, bound.MoveToImmutable());
    }

    sealed class EmptyRuntimeParameters : Dictionary<string, object?>
    {
        public static EmptyRuntimeParameters Instance { get; } = new();

        EmptyRuntimeParameters()
            : base(StringComparer.Ordinal)
        {
        }
    }
}

/// <summary>Immutable parameterized Cosmos SQL statement.</summary>
public sealed class CosmosSqlStatement
{
    internal CosmosSqlStatement(string text, ImmutableArray<CosmosSqlParameter> parameters)
    {
        Text = Guard.RequireNotNullOrWhiteSpace(text);
        Parameters = parameters.IsDefault ? [] : parameters;
        if (Parameters.Any(static parameter => parameter is null))
            throw new ArgumentException("Cosmos SQL parameters cannot contain null entries.", nameof(parameters));
        if (Parameters.GroupBy(static parameter => parameter.Name, StringComparer.Ordinal).Any(static group => group.Count() > 1))
            throw new ArgumentException("Cosmos SQL parameters cannot repeat a parameter name.", nameof(parameters));
    }

    /// <summary>Normalized Cosmos SQL text.</summary>
    public string Text { get; }

    /// <summary>Concrete parameters in first-use SQL order.</summary>
    public ImmutableArray<CosmosSqlParameter> Parameters { get; }

    /// <summary>Creates the corresponding Cosmos SDK query definition.</summary>
    /// <returns>A new <see cref="QueryDefinition"/> containing this statement's text and parameters.</returns>
    public QueryDefinition ToQueryDefinition()
    {
        var query = new QueryDefinition(Text);
        foreach (var parameter in Parameters)
            query = query.WithParameter(parameter.Name, parameter.Value);
        return query;
    }
}

/// <summary>One property in a Cosmos SQL object-construction expression.</summary>
public sealed record CosmosSqlObjectProperty
{
    /// <summary>Creates an object-construction property.</summary>
    /// <param name="name">JSON property name emitted as an escaped string literal.</param>
    /// <param name="value">Expression producing the property value.</param>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> or <paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty or consists only of white-space characters.</exception>
    public CosmosSqlObjectProperty(string name, CosmosSqlExpression value)
    {
        Name = Guard.RequireNotNullOrWhiteSpace(name);
        Value = Guard.RequireNotNull(value);
    }

    /// <summary>JSON property name.</summary>
    public string Name { get; }

    /// <summary>Expression producing the property value.</summary>
    public CosmosSqlExpression Value { get; }
}

/// <summary>
/// Closed, safely rendered Cosmos SQL expression tree used by <see cref="CosmosSqlBuilder"/>.
/// </summary>
public abstract record CosmosSqlExpression
{
    /// <summary>Initializes the base state for a closed Cosmos SQL expression implementation.</summary>
    private protected CosmosSqlExpression()
    {
    }

    /// <summary>References a validated SQL alias.</summary>
    /// <param name="alias">Root or collection-item alias.</param>
    /// <returns>An expression referencing <paramref name="alias"/>.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="alias"/> is not a simple identifier or is a reserved Cosmos SQL word.
    /// </exception>
    public static CosmosSqlExpression Alias(string alias) => new AliasExpression(
        CosmosSqlNames.RequireIdentifier(alias, nameof(alias)));

    /// <summary>References a property path below a validated SQL alias.</summary>
    /// <param name="alias">Root or collection-item alias.</param>
    /// <param name="path">Property-only path to render through escaped bracket access.</param>
    /// <returns>A safely escaped property expression.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="alias"/> is invalid or reserved, or <paramref name="path"/> is empty or contains an element segment.
    /// </exception>
    public static CosmosSqlExpression Property(string alias, FieldPath path) => new PropertyExpression(
        CosmosSqlNames.RequireIdentifier(alias, nameof(alias)),
        CosmosSqlNames.RequirePropertyPath(path, nameof(path)));

    /// <summary>References a property path below another expression.</summary>
    /// <param name="target">Expression producing an object.</param>
    /// <param name="path">Property-only path to render through escaped bracket access.</param>
    /// <returns>A safely escaped nested property expression.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="target"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty or contains an element segment.</exception>
    public static CosmosSqlExpression Property(CosmosSqlExpression target, FieldPath path) => new NestedPropertyExpression(
        Guard.RequireNotNull(target),
        CosmosSqlNames.RequirePropertyPath(path, nameof(path)));

    /// <summary>Captures a concrete value as a safely parameterized expression.</summary>
    /// <param name="value">Value captured by the command template.</param>
    /// <returns>A parameter expression whose SQL name is allocated deterministically.</returns>
    /// <exception cref="NotSupportedException"><paramref name="value"/> cannot be represented as a Cosmos SQL parameter.</exception>
    public static CosmosSqlExpression Parameter(object? value) => new ConstantParameterExpression(
        CosmosSqlParameterValues.Normalize(value));

    /// <summary>Declares a parameter whose value is supplied when the command template is bound.</summary>
    /// <param name="binding">Stable runtime binding name; it is not emitted into SQL.</param>
    /// <returns>A runtime parameter expression whose SQL name is allocated deterministically.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="binding"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="binding"/> is empty or consists only of white-space characters.</exception>
    public static CosmosSqlExpression RuntimeParameter(string binding) => new RuntimeParameterExpression(
        Guard.RequireNotNullOrWhiteSpace(binding));

    /// <summary>Creates a unary expression.</summary>
    /// <param name="operator">Unary operation to emit.</param>
    /// <param name="operand">Operand expression.</param>
    /// <returns>A parenthesized unary expression.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="operand"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="operator"/> is unsupported.</exception>
    public static CosmosSqlExpression Unary(CosmosSqlUnaryOperator @operator, CosmosSqlExpression operand)
    {
        if (!Enum.IsDefined(@operator))
            throw new ArgumentOutOfRangeException(nameof(@operator), @operator, "Unsupported Cosmos SQL unary operator.");
        return new UnaryExpression(@operator, Guard.RequireNotNull(operand));
    }

    /// <summary>Creates a binary expression.</summary>
    /// <param name="operator">Binary operation to emit.</param>
    /// <param name="left">Left operand.</param>
    /// <param name="right">Right operand.</param>
    /// <returns>A parenthesized binary expression.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="left"/> or <paramref name="right"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="operator"/> is unsupported.</exception>
    public static CosmosSqlExpression Binary(
        CosmosSqlBinaryOperator @operator,
        CosmosSqlExpression left,
        CosmosSqlExpression right)
    {
        if (!Enum.IsDefined(@operator))
            throw new ArgumentOutOfRangeException(nameof(@operator), @operator, "Unsupported Cosmos SQL binary operator.");
        return new BinaryExpression(@operator, Guard.RequireNotNull(left), Guard.RequireNotNull(right));
    }

    /// <summary>Creates a conditional <c>IIF</c> expression.</summary>
    /// <param name="test">Boolean condition.</param>
    /// <param name="ifTrue">Value returned when <paramref name="test"/> is true.</param>
    /// <param name="ifFalse">Value returned when <paramref name="test"/> is false.</param>
    /// <returns>A Cosmos SQL conditional expression.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="test"/>, <paramref name="ifTrue"/>, or <paramref name="ifFalse"/> is <see langword="null"/>.
    /// </exception>
    public static CosmosSqlExpression Conditional(
        CosmosSqlExpression test,
        CosmosSqlExpression ifTrue,
        CosmosSqlExpression ifFalse) =>
        new ConditionalExpression(
            Guard.RequireNotNull(test),
            Guard.RequireNotNull(ifTrue),
            Guard.RequireNotNull(ifFalse));

    /// <summary>Creates a correlated existential expression over one in-document collection.</summary>
    /// <param name="collection">Expression producing the collection enumerated by the correlated subquery.</param>
    /// <param name="predicate">
    /// Factory invoked exactly once with an expression bound to the current collection item. The supplied item
    /// expression is valid only within the returned predicate.
    /// </param>
    /// <returns>
    /// An <c>EXISTS</c> expression whose collection-item alias is allocated deterministically when the containing
    /// statement is rendered.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="collection"/> or <paramref name="predicate"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="predicate"/> returns <see langword="null"/>.</exception>
    /// <exception cref="Exception">
    /// <paramref name="predicate"/> throws; the delegate's exception is propagated unchanged.
    /// </exception>
    public static CosmosSqlExpression CollectionExists(
        CosmosSqlExpression collection,
        Func<CosmosSqlExpression, CosmosSqlExpression> predicate)
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(predicate);

        CollectionItemExpression item = new();
        var itemPredicate = predicate(item);
        if (itemPredicate is null)
        {
            throw new ArgumentException(
                "A Cosmos SQL collection existential predicate cannot be null.",
                nameof(predicate));
        }

        return new CollectionExistsExpression(collection, item, itemPredicate);
    }

    /// <summary>Creates a call to an allow-listed Cosmos SQL scalar function.</summary>
    /// <param name="function">Function to emit.</param>
    /// <param name="argument">The function argument.</param>
    /// <returns>A scalar function-call expression.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="argument"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The function does not accept one argument.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="function"/> is unsupported.</exception>
    public static CosmosSqlExpression Function(
        CosmosSqlFunction function,
        CosmosSqlExpression argument)
    {
        ArgumentNullException.ThrowIfNull(argument);
        ImmutableArray<CosmosSqlExpression> arguments = [argument];
        ValidateFunction(function, arguments.AsSpan(), nameof(argument));
        return new FunctionExpression(function, arguments);
    }

    /// <summary>Creates a call to an allow-listed Cosmos SQL scalar function.</summary>
    /// <param name="function">Function to emit.</param>
    /// <param name="firstArgument">The first function argument.</param>
    /// <param name="secondArgument">The second function argument.</param>
    /// <returns>A scalar function-call expression.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="firstArgument"/> or <paramref name="secondArgument"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">The function does not accept two arguments.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="function"/> is unsupported.</exception>
    public static CosmosSqlExpression Function(
        CosmosSqlFunction function,
        CosmosSqlExpression firstArgument,
        CosmosSqlExpression secondArgument)
    {
        ArgumentNullException.ThrowIfNull(firstArgument);
        ArgumentNullException.ThrowIfNull(secondArgument);
        ImmutableArray<CosmosSqlExpression> arguments = [firstArgument, secondArgument];
        ValidateFunction(function, arguments.AsSpan(), nameof(firstArgument));
        return new FunctionExpression(function, arguments);
    }

    /// <summary>Creates a call to an allow-listed Cosmos SQL scalar function.</summary>
    /// <param name="function">Function to emit.</param>
    /// <param name="firstArgument">The first function argument.</param>
    /// <param name="secondArgument">The second function argument.</param>
    /// <param name="thirdArgument">The third function argument.</param>
    /// <returns>A scalar function-call expression.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="firstArgument"/>, <paramref name="secondArgument"/>, or <paramref name="thirdArgument"/>
    /// is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">The function does not accept three arguments.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="function"/> is unsupported.</exception>
    public static CosmosSqlExpression Function(
        CosmosSqlFunction function,
        CosmosSqlExpression firstArgument,
        CosmosSqlExpression secondArgument,
        CosmosSqlExpression thirdArgument)
    {
        ArgumentNullException.ThrowIfNull(firstArgument);
        ArgumentNullException.ThrowIfNull(secondArgument);
        ArgumentNullException.ThrowIfNull(thirdArgument);
        ImmutableArray<CosmosSqlExpression> arguments = [firstArgument, secondArgument, thirdArgument];
        ValidateFunction(function, arguments.AsSpan(), nameof(firstArgument));
        return new FunctionExpression(function, arguments);
    }

    /// <summary>Creates a call to an allow-listed Cosmos SQL scalar function.</summary>
    /// <param name="function">Function to emit.</param>
    /// <param name="arguments">Immutable arguments in semantic call order; their storage is retained without copying.</param>
    /// <returns>A scalar function-call expression.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="arguments"/> is default, contains a <see langword="null"/> entry, or has invalid function arity.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="function"/> is unsupported.</exception>
    public static CosmosSqlExpression FunctionFromImmutable(
        CosmosSqlFunction function,
        ImmutableArray<CosmosSqlExpression> arguments)
    {
        if (arguments.IsDefault)
            throw new ArgumentException("Cosmos SQL function arguments cannot be default.", nameof(arguments));
        ValidateFunction(function, arguments.AsSpan(), nameof(arguments));
        return new FunctionExpression(function, arguments);
    }

    /// <summary>Creates a call to an allow-listed Cosmos SQL scalar function.</summary>
    /// <param name="function">Function to emit.</param>
    /// <param name="arguments">
    /// Mutable arguments in semantic call order. The array is defensively copied before this method returns.
    /// </param>
    /// <returns>A scalar function-call expression.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="arguments"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An argument is <see langword="null"/> or the function arity is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="function"/> is unsupported.</exception>
    public static CosmosSqlExpression FunctionFromMutable(
        CosmosSqlFunction function,
        CosmosSqlExpression[] arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ValidateFunction(function, arguments.AsSpan(), nameof(arguments));
        return new FunctionExpression(function, [.. arguments]);
    }

    /// <summary>Creates a Cosmos SQL aggregate expression.</summary>
    /// <param name="function">Aggregate function to emit.</param>
    /// <param name="value">Value expression, or <see langword="null"/> only for row count.</param>
    /// <returns>An aggregate expression.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="value"/> is <see langword="null"/> for a function other than <see cref="CosmosSqlAggregateFunction.Count"/>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="function"/> is unsupported.</exception>
    public static CosmosSqlExpression Aggregate(
        CosmosSqlAggregateFunction function,
        CosmosSqlExpression? value = null)
    {
        if (!Enum.IsDefined(function))
            throw new ArgumentOutOfRangeException(nameof(function), function, "Unsupported Cosmos SQL aggregate function.");
        if (function != CosmosSqlAggregateFunction.Count && value is null)
            throw new ArgumentException("Only COUNT may omit its value expression.", nameof(value));
        return new AggregateExpression(function, value);
    }

    /// <summary>Creates an object expression with safely escaped JSON property names.</summary>
    /// <param name="property">The single object property.</param>
    /// <returns>A Cosmos SQL object-construction expression.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="property"/> is <see langword="null"/>.</exception>
    public static CosmosSqlExpression Object(CosmosSqlObjectProperty property)
    {
        ArgumentNullException.ThrowIfNull(property);
        return new ObjectExpression([property]);
    }

    /// <summary>Creates an object expression with safely escaped JSON property names.</summary>
    /// <param name="firstProperty">The first object property.</param>
    /// <param name="secondProperty">The second object property.</param>
    /// <returns>A Cosmos SQL object-construction expression.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="firstProperty"/> or <paramref name="secondProperty"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">The properties repeat a property name.</exception>
    public static CosmosSqlExpression Object(
        CosmosSqlObjectProperty firstProperty,
        CosmosSqlObjectProperty secondProperty)
    {
        ArgumentNullException.ThrowIfNull(firstProperty);
        ArgumentNullException.ThrowIfNull(secondProperty);
        if (string.Equals(firstProperty.Name, secondProperty.Name, StringComparison.Ordinal))
            throw DuplicateObjectProperty(nameof(secondProperty));
        ImmutableArray<CosmosSqlObjectProperty> properties = [firstProperty, secondProperty];
        return new ObjectExpression(properties);
    }

    /// <summary>Creates an object expression with safely escaped JSON property names.</summary>
    /// <param name="firstProperty">The first object property.</param>
    /// <param name="secondProperty">The second object property.</param>
    /// <param name="thirdProperty">The third object property.</param>
    /// <returns>A Cosmos SQL object-construction expression.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="firstProperty"/>, <paramref name="secondProperty"/>, or <paramref name="thirdProperty"/>
    /// is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">The properties repeat a property name.</exception>
    public static CosmosSqlExpression Object(
        CosmosSqlObjectProperty firstProperty,
        CosmosSqlObjectProperty secondProperty,
        CosmosSqlObjectProperty thirdProperty)
    {
        ArgumentNullException.ThrowIfNull(firstProperty);
        ArgumentNullException.ThrowIfNull(secondProperty);
        ArgumentNullException.ThrowIfNull(thirdProperty);
        if (string.Equals(firstProperty.Name, secondProperty.Name, StringComparison.Ordinal))
            throw DuplicateObjectProperty(nameof(secondProperty));
        if (string.Equals(firstProperty.Name, thirdProperty.Name, StringComparison.Ordinal)
            || string.Equals(secondProperty.Name, thirdProperty.Name, StringComparison.Ordinal))
        {
            throw DuplicateObjectProperty(nameof(thirdProperty));
        }
        ImmutableArray<CosmosSqlObjectProperty> properties = [firstProperty, secondProperty, thirdProperty];
        return new ObjectExpression(properties);
    }

    /// <summary>Creates an object expression with safely escaped JSON property names.</summary>
    /// <param name="properties">Immutable object properties in emitted order; their storage is retained without copying.</param>
    /// <returns>A Cosmos SQL object-construction expression.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="properties"/> is default or empty, contains a <see langword="null"/> entry, or repeats a property name.
    /// </exception>
    public static CosmosSqlExpression ObjectFromImmutable(ImmutableArray<CosmosSqlObjectProperty> properties)
    {
        if (properties.IsDefault)
            throw new ArgumentException("Cosmos SQL object properties cannot be default.", nameof(properties));
        ValidateObjectProperties(properties.AsSpan(), nameof(properties));
        return new ObjectExpression(properties);
    }

    /// <summary>Creates an object expression with safely escaped JSON property names.</summary>
    /// <param name="properties">
    /// Mutable object properties in emitted order. The array is defensively copied before this method returns.
    /// </param>
    /// <returns>A Cosmos SQL object-construction expression.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="properties"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="properties"/> is empty, contains a <see langword="null"/> entry, or repeats a property name.
    /// </exception>
    public static CosmosSqlExpression ObjectFromMutable(CosmosSqlObjectProperty[] properties)
    {
        ArgumentNullException.ThrowIfNull(properties);
        ValidateObjectProperties(properties.AsSpan(), nameof(properties));
        return new ObjectExpression([.. properties]);
    }

    static void ValidateFunction(
        CosmosSqlFunction function,
        ReadOnlySpan<CosmosSqlExpression> arguments,
        string argumentsParameterName)
    {
        if (!Enum.IsDefined(function))
            throw new ArgumentOutOfRangeException(nameof(function), function, "Unsupported Cosmos SQL scalar function.");
        foreach (var argument in arguments)
        {
            if (argument is null)
                throw new ArgumentException(
                    "Cosmos SQL function arguments cannot contain null entries.",
                    argumentsParameterName);
        }
        CosmosSqlFunctions.ValidateArity(function, arguments.Length, nameof(function));
    }

    static void ValidateObjectProperties(
        ReadOnlySpan<CosmosSqlObjectProperty> properties,
        string parameterName)
    {
        if (properties.IsEmpty)
            throw new ArgumentException("A Cosmos SQL object expression requires at least one property.", parameterName);
        for (var index = 0; index < properties.Length; index++)
        {
            var property = properties[index];
            if (property is null)
                throw new ArgumentException("Cosmos SQL object properties cannot contain null entries.", parameterName);
            for (var priorIndex = 0; priorIndex < index; priorIndex++)
            {
                if (string.Equals(properties[priorIndex].Name, property.Name, StringComparison.Ordinal))
                    throw DuplicateObjectProperty(parameterName);
            }
        }
    }

    static ArgumentException DuplicateObjectProperty(string parameterName) => new(
        "A Cosmos SQL object expression cannot repeat a property name.",
        parameterName);

    internal abstract void WriteTo(CosmosSqlRenderContext context, StringBuilder builder);

    sealed record AliasExpression(string AliasName) : CosmosSqlExpression
    {
        internal override void WriteTo(CosmosSqlRenderContext context, StringBuilder builder) => builder.Append(AliasName);
    }

    sealed record PropertyExpression(string AliasName, FieldPath Path) : CosmosSqlExpression
    {
        internal override void WriteTo(CosmosSqlRenderContext context, StringBuilder builder)
        {
            builder.Append(AliasName);
            CosmosSqlNames.AppendPropertyPath(builder, Path);
        }
    }

    sealed record NestedPropertyExpression(CosmosSqlExpression Target, FieldPath Path) : CosmosSqlExpression
    {
        internal override void WriteTo(CosmosSqlRenderContext context, StringBuilder builder)
        {
            Target.WriteTo(context, builder);
            CosmosSqlNames.AppendPropertyPath(builder, Path);
        }
    }

    sealed record ConstantParameterExpression(object? Value) : CosmosSqlExpression
    {
        internal override void WriteTo(CosmosSqlRenderContext context, StringBuilder builder) =>
            builder.Append(context.AddConstant(Value));
    }

    sealed record RuntimeParameterExpression(string Binding) : CosmosSqlExpression
    {
        internal override void WriteTo(CosmosSqlRenderContext context, StringBuilder builder) =>
            builder.Append(context.AddRuntime(Binding));
    }

    sealed record UnaryExpression(CosmosSqlUnaryOperator Operator, CosmosSqlExpression Operand) : CosmosSqlExpression
    {
        internal override void WriteTo(CosmosSqlRenderContext context, StringBuilder builder)
        {
            builder.Append("(NOT ");
            Operand.WriteTo(context, builder);
            builder.Append(')');
        }
    }

    sealed record BinaryExpression(
        CosmosSqlBinaryOperator Operator,
        CosmosSqlExpression Left,
        CosmosSqlExpression Right) : CosmosSqlExpression
    {
        internal override void WriteTo(CosmosSqlRenderContext context, StringBuilder builder)
        {
            builder.Append('(');
            Left.WriteTo(context, builder);
            builder.Append(' ').Append(CosmosSqlOperators.Text(Operator)).Append(' ');
            Right.WriteTo(context, builder);
            builder.Append(')');
        }
    }

    sealed record ConditionalExpression(
        CosmosSqlExpression Test,
        CosmosSqlExpression IfTrue,
        CosmosSqlExpression IfFalse) : CosmosSqlExpression
    {
        internal override void WriteTo(CosmosSqlRenderContext context, StringBuilder builder)
        {
            builder.Append("IIF(");
            Test.WriteTo(context, builder);
            builder.Append(", ");
            IfTrue.WriteTo(context, builder);
            builder.Append(", ");
            IfFalse.WriteTo(context, builder);
            builder.Append(')');
        }
    }

    sealed record CollectionItemExpression : CosmosSqlExpression
    {
        internal override void WriteTo(CosmosSqlRenderContext context, StringBuilder builder) =>
            builder.Append(context.RequireCollectionItemAlias(this));
    }

    sealed record CollectionExistsExpression(
        CosmosSqlExpression Collection,
        CollectionItemExpression Item,
        CosmosSqlExpression Predicate) : CosmosSqlExpression
    {
        internal override void WriteTo(CosmosSqlRenderContext context, StringBuilder builder)
        {
            context.EnterCollectionItem(Item);
            try
            {
                builder.Append("EXISTS (SELECT VALUE ");
                Item.WriteTo(context, builder);
                builder.Append(" FROM ");
                Item.WriteTo(context, builder);
                builder.Append(" IN ");
                Collection.WriteTo(context, builder);
                builder.Append(" WHERE ");
                Predicate.WriteTo(context, builder);
                builder.Append(')');
            }
            finally
            {
                context.ExitCollectionItem(Item);
            }
        }
    }

    sealed record FunctionExpression(
        CosmosSqlFunction FunctionKind,
        ImmutableArray<CosmosSqlExpression> Arguments) : CosmosSqlExpression
    {
        internal override void WriteTo(CosmosSqlRenderContext context, StringBuilder builder)
        {
            builder.Append(CosmosSqlFunctions.Name(FunctionKind)).Append('(');
            for (var index = 0; index < Arguments.Length; index++)
            {
                if (index != 0)
                    builder.Append(", ");
                Arguments[index].WriteTo(context, builder);
            }
            builder.Append(')');
        }
    }

    sealed record AggregateExpression(
        CosmosSqlAggregateFunction FunctionKind,
        CosmosSqlExpression? Value) : CosmosSqlExpression
    {
        internal override void WriteTo(CosmosSqlRenderContext context, StringBuilder builder)
        {
            builder.Append(CosmosSqlFunctions.Name(FunctionKind)).Append('(');
            if (Value is null)
                builder.Append('1');
            else
                Value.WriteTo(context, builder);
            builder.Append(')');
        }
    }

    sealed record ObjectExpression(ImmutableArray<CosmosSqlObjectProperty> Properties) : CosmosSqlExpression
    {
        internal override void WriteTo(CosmosSqlRenderContext context, StringBuilder builder)
        {
            builder.Append("{ ");
            for (var index = 0; index < Properties.Length; index++)
            {
                if (index != 0)
                    builder.Append(", ");
                builder.Append(JsonSerializer.Serialize(Properties[index].Name)).Append(": ");
                Properties[index].Value.WriteTo(context, builder);
            }
            builder.Append(" }");
        }
    }
}

/// <summary>Fluent, deterministic builder for one safe Cosmos SQL query.</summary>
public sealed class CosmosSqlBuilder
{
    readonly string rootAlias;
    readonly List<SelectItem> select = [];
    readonly List<JoinItem> joins = [];
    readonly List<CosmosSqlExpression> predicates = [];
    readonly List<CosmosSqlExpression> groupings = [];
    readonly List<OrderItem> orderings = [];
    CosmosSqlExpression? selectValue;
    bool distinct;
    int? offset;
    int? limit;

    /// <summary>Creates a query builder rooted at one document alias.</summary>
    /// <param name="rootAlias">Simple identifier emitted after <c>FROM</c>.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="rootAlias"/> is not a simple identifier or is a reserved Cosmos SQL word.
    /// </exception>
    public CosmosSqlBuilder(string rootAlias = "c") =>
        this.rootAlias = CosmosSqlNames.RequireIdentifier(rootAlias, nameof(rootAlias));

    /// <summary>Root document alias emitted by the builder.</summary>
    public string RootAlias => rootAlias;

    /// <summary>Adds a projected expression with a safe result alias.</summary>
    /// <param name="expression">Expression to project.</param>
    /// <param name="alias">Simple result alias.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="expression"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="alias"/> is invalid, reserved, or repeated.</exception>
    /// <exception cref="InvalidOperationException"><see cref="SelectValue"/> was already used to configure value selection.</exception>
    public CosmosSqlBuilder Select(CosmosSqlExpression expression, string alias)
    {
        if (selectValue is not null)
            throw new InvalidOperationException("A query cannot combine SELECT VALUE with aliased projections.");
        var normalizedAlias = CosmosSqlNames.RequireIdentifier(alias, nameof(alias));
        if (select.Any(item => string.Equals(item.Alias, normalizedAlias, StringComparison.Ordinal)))
            throw new ArgumentException($"Cosmos SQL projection alias '{normalizedAlias}' is already defined.", nameof(alias));
        select.Add(new(Guard.RequireNotNull(expression), normalizedAlias));
        return this;
    }

    /// <summary>Configures scalar or object <c>SELECT VALUE</c> projection.</summary>
    /// <param name="expression">Expression forming each result value.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="expression"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">A projection or value selection was already configured.</exception>
    public CosmosSqlBuilder SelectValue(CosmosSqlExpression expression)
    {
        if (selectValue is not null || select.Count != 0)
            throw new InvalidOperationException("A Cosmos SQL query can configure its selection only once.");
        selectValue = Guard.RequireNotNull(expression);
        return this;
    }

    /// <summary>Enables <c>SELECT DISTINCT</c> semantics.</summary>
    /// <returns>This builder.</returns>
    public CosmosSqlBuilder Distinct()
    {
        distinct = true;
        return this;
    }

    /// <summary>Adds an in-document collection expansion using Cosmos SQL <c>JOIN ... IN</c>.</summary>
    /// <param name="alias">Simple alias introduced for each collection item.</param>
    /// <param name="collection">Expression producing the array to expand.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="collection"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="alias"/> is invalid, reserved, or collides with another alias.
    /// </exception>
    public CosmosSqlBuilder JoinCollection(string alias, CosmosSqlExpression collection)
    {
        var normalizedAlias = CosmosSqlNames.RequireIdentifier(alias, nameof(alias));
        if (string.Equals(rootAlias, normalizedAlias, StringComparison.Ordinal)
            || joins.Any(item => string.Equals(item.Alias, normalizedAlias, StringComparison.Ordinal)))
        {
            throw new ArgumentException($"Cosmos SQL alias '{normalizedAlias}' is already defined.", nameof(alias));
        }
        joins.Add(new(normalizedAlias, Guard.RequireNotNull(collection)));
        return this;
    }

    /// <summary>Adds a predicate; multiple predicates are conjoined in declaration order.</summary>
    /// <param name="predicate">Boolean predicate expression.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="predicate"/> is <see langword="null"/>.</exception>
    public CosmosSqlBuilder Where(CosmosSqlExpression predicate)
    {
        predicates.Add(Guard.RequireNotNull(predicate));
        return this;
    }

    /// <summary>Adds one grouping expression.</summary>
    /// <param name="expression">Grouping key expression.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="expression"/> is <see langword="null"/>.</exception>
    public CosmosSqlBuilder GroupBy(CosmosSqlExpression expression)
    {
        groupings.Add(Guard.RequireNotNull(expression));
        return this;
    }

    /// <summary>Adds one ordering expression.</summary>
    /// <param name="expression">Ordering key expression.</param>
    /// <param name="direction">Sort direction.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="expression"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="direction"/> is unsupported.</exception>
    public CosmosSqlBuilder OrderBy(
        CosmosSqlExpression expression,
        CosmosSqlSortDirection direction = CosmosSqlSortDirection.Ascending)
    {
        if (!Enum.IsDefined(direction))
            throw new ArgumentOutOfRangeException(nameof(direction), direction, "Unsupported Cosmos SQL sort direction.");
        orderings.Add(new(Guard.RequireNotNull(expression), direction));
        return this;
    }

    /// <summary>Configures offset and limit paging.</summary>
    /// <param name="offset">Number of ordered rows to skip.</param>
    /// <param name="limit">Maximum number of rows to return.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="offset"/> is negative or <paramref name="limit"/> is not positive.</exception>
    public CosmosSqlBuilder OffsetLimit(int offset, int limit)
    {
        if (offset < 0)
            throw new ArgumentOutOfRangeException(nameof(offset), offset, "Cosmos SQL offset must be non-negative.");
        if (limit <= 0)
            throw new ArgumentOutOfRangeException(nameof(limit), limit, "Cosmos SQL limit must be positive.");
        this.offset = offset;
        this.limit = limit;
        return this;
    }

    /// <summary>Builds a reusable command template without binding runtime parameters.</summary>
    /// <returns>Immutable normalized SQL and its deterministic parameter slots.</returns>
    /// <exception cref="InvalidOperationException">
    /// No projection was configured, only one of offset and limit is configured, or the query combines
    /// <c>GROUP BY</c> with <c>ORDER BY</c>, or a collection-item expression escaped its defining existential scope.
    /// </exception>
    public CosmosSqlCommandTemplate BuildTemplate()
    {
        if (selectValue is null && select.Count == 0)
            throw new InvalidOperationException("A Cosmos SQL query requires at least one projected expression.");
        if (limit is null != (offset is null))
            throw new InvalidOperationException("Cosmos SQL OFFSET and LIMIT must be configured together.");
        if (groupings.Count != 0 && orderings.Count != 0)
        {
            throw new InvalidOperationException(
                "Cosmos SQL does not support GROUP BY and ORDER BY in the same query.");
        }

        CosmosSqlRenderContext context = new();
        context.ReserveAlias(rootAlias);
        foreach (var join in joins)
            context.ReserveAlias(join.Alias);
        StringBuilder builder = new("SELECT ");
        if (distinct)
            builder.Append("DISTINCT ");
        if (selectValue is not null)
        {
            builder.Append("VALUE ");
            selectValue.WriteTo(context, builder);
        }
        else
        {
            for (var index = 0; index < select.Count; index++)
            {
                if (index != 0)
                    builder.Append(", ");
                select[index].Expression.WriteTo(context, builder);
                builder.Append(" AS ").Append(select[index].Alias);
            }
        }

        builder.Append(" FROM ").Append(rootAlias);
        foreach (var join in joins)
        {
            builder.Append(" JOIN ").Append(join.Alias).Append(" IN ");
            join.Collection.WriteTo(context, builder);
        }

        if (predicates.Count != 0)
        {
            builder.Append(" WHERE ");
            for (var index = 0; index < predicates.Count; index++)
            {
                if (index != 0)
                    builder.Append(" AND ");
                predicates[index].WriteTo(context, builder);
            }
        }

        if (groupings.Count != 0)
        {
            builder.Append(" GROUP BY ");
            WriteExpressionList(groupings, context, builder);
        }

        if (orderings.Count != 0)
        {
            builder.Append(" ORDER BY ");
            for (var index = 0; index < orderings.Count; index++)
            {
                if (index != 0)
                    builder.Append(", ");
                orderings[index].Expression.WriteTo(context, builder);
                builder.Append(orderings[index].Direction == CosmosSqlSortDirection.Descending ? " DESC" : " ASC");
            }
        }

        if (offset is { } pageOffset && limit is { } pageLimit)
        {
            builder
                .Append(" OFFSET ")
                .Append(pageOffset.ToString(CultureInfo.InvariantCulture))
                .Append(" LIMIT ")
                .Append(pageLimit.ToString(CultureInfo.InvariantCulture));
        }

        return new(builder.ToString(), context.Parameters);
    }

    /// <summary>Builds a concrete statement when every parameter value is already captured.</summary>
    /// <returns>An immutable Cosmos SQL statement.</returns>
    /// <exception cref="ArgumentException">The query contains a runtime parameter binding that cannot be bound without a value.</exception>
    /// <exception cref="InvalidOperationException">
    /// No projection was configured, only one of offset and limit is configured, or the query combines
    /// <c>GROUP BY</c> with <c>ORDER BY</c>, or a collection-item expression escaped its defining existential scope.
    /// </exception>
    public CosmosSqlStatement Build() => BuildTemplate().Bind();

    static void WriteExpressionList(
        IReadOnlyList<CosmosSqlExpression> expressions,
        CosmosSqlRenderContext context,
        StringBuilder builder)
    {
        for (var index = 0; index < expressions.Count; index++)
        {
            if (index != 0)
                builder.Append(", ");
            expressions[index].WriteTo(context, builder);
        }
    }

    sealed record SelectItem(CosmosSqlExpression Expression, string Alias);
    sealed record JoinItem(string Alias, CosmosSqlExpression Collection);
    sealed record OrderItem(CosmosSqlExpression Expression, CosmosSqlSortDirection Direction);
}

sealed class CosmosSqlRenderContext
{
    readonly ImmutableArray<CosmosSqlParameterSlot>.Builder parameters = ImmutableArray.CreateBuilder<CosmosSqlParameterSlot>();
    readonly Dictionary<string, string> runtimeNames = new(StringComparer.Ordinal);
    readonly HashSet<string> usedAliases = new(StringComparer.Ordinal);
    readonly Dictionary<CosmosSqlExpression, string> activeCollectionItemAliases = new(
        ReferenceEqualityComparer.Instance);
    int nextCollectionAlias;

    public ImmutableArray<CosmosSqlParameterSlot> Parameters => parameters.ToImmutable();

    public void ReserveAlias(string alias)
    {
        if (!usedAliases.Add(alias))
            throw new InvalidOperationException($"Cosmos SQL alias '{alias}' is already reserved.");
    }

    public void EnterCollectionItem(CosmosSqlExpression item)
    {
        if (activeCollectionItemAliases.ContainsKey(item))
            throw new InvalidOperationException("A Cosmos SQL collection item is already active in this render scope.");

        string alias;
        do
        {
            alias = $"e{nextCollectionAlias.ToString(CultureInfo.InvariantCulture)}";
            nextCollectionAlias++;
        }
        while (!usedAliases.Add(alias));

        activeCollectionItemAliases.Add(item, alias);
    }

    public string RequireCollectionItemAlias(CosmosSqlExpression item)
    {
        if (activeCollectionItemAliases.TryGetValue(item, out var alias))
            return alias;

        throw new InvalidOperationException(
            "A Cosmos SQL collection-item expression cannot be rendered outside its existential predicate scope.");
    }

    public void ExitCollectionItem(CosmosSqlExpression item)
    {
        if (!activeCollectionItemAliases.Remove(item))
            throw new InvalidOperationException("A Cosmos SQL collection item is not active in this render scope.");
    }

    public string AddConstant(object? value)
    {
        var name = NextName();
        parameters.Add(new(name, CosmosSqlParameterBindingKind.Constant, binding: null, value));
        return name;
    }

    public string AddRuntime(string binding)
    {
        if (runtimeNames.TryGetValue(binding, out var existing))
            return existing;
        var name = NextName();
        runtimeNames.Add(binding, name);
        parameters.Add(new(name, CosmosSqlParameterBindingKind.Runtime, binding, constantValue: null));
        return name;
    }

    string NextName() => $"@p{parameters.Count.ToString(CultureInfo.InvariantCulture)}";
}

static class CosmosSqlNames
{
    static readonly ImmutableHashSet<string> ReservedIdentifiers = ImmutableHashSet.CreateRange<string>(
        StringComparer.OrdinalIgnoreCase,
        [
            "AND",
            "AS",
            "ASC",
            "BETWEEN",
            "BY",
            "DESC",
            "DISTINCT",
            "EXISTS",
            "FALSE",
            "FROM",
            "GROUP",
            "IN",
            "JOIN",
            "LIKE",
            "LIMIT",
            "NOT",
            "NULL",
            "OFFSET",
            "OR",
            "ORDER",
            "SELECT",
            "TOP",
            "TRUE",
            "UNDEFINED",
            "VALUE",
            "WHERE"
        ]);

    public static string RequireIdentifier(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !IsIdentifierStart(value[0])
            || value.Skip(1).Any(static character => !IsIdentifierPart(character)))
        {
            throw new ArgumentException(
                "A Cosmos SQL identifier must start with a letter or underscore and contain only letters, digits, or underscores.",
                parameterName);
        }
        if (ReservedIdentifiers.Contains(value))
        {
            throw new ArgumentException(
                $"Cosmos SQL reserved word '{value}' cannot be used as an alias.",
                parameterName);
        }
        return value;
    }

    public static string RequireParameterName(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value[0] != '@'
            || value.Length == 1
            || !IsIdentifierStart(value[1])
            || value.Skip(2).Any(static character => !IsIdentifierPart(character)))
        {
            throw new ArgumentException(
                "A Cosmos SQL parameter name must begin with @ followed by a simple identifier.",
                parameterName);
        }
        return value;
    }

    public static FieldPath RequirePropertyPath(FieldPath path, string parameterName)
    {
        if (path.Segments.IsDefaultOrEmpty
            || path.Segments.Any(static segment =>
                segment.Kind != SegmentKind.Field || string.IsNullOrEmpty(segment.Segment)))
        {
            throw new ArgumentException(
                "A Cosmos SQL property path must contain one or more non-empty field segments and no element segments.",
                parameterName);
        }
        return path;
    }

    public static void AppendPropertyPath(StringBuilder builder, FieldPath path)
    {
        foreach (var segment in path.Segments)
            builder.Append('[').Append(JsonSerializer.Serialize(segment.Segment!)).Append(']');
    }

    static bool IsIdentifierStart(char value) => char.IsLetter(value) || value == '_';
    static bool IsIdentifierPart(char value) => char.IsLetterOrDigit(value) || value == '_';
}

static class CosmosSqlOperators
{
    public static string Text(CosmosSqlBinaryOperator @operator) => @operator switch
    {
        CosmosSqlBinaryOperator.Equal => "=",
        CosmosSqlBinaryOperator.NotEqual => "!=",
        CosmosSqlBinaryOperator.GreaterThan => ">",
        CosmosSqlBinaryOperator.GreaterThanOrEqual => ">=",
        CosmosSqlBinaryOperator.LessThan => "<",
        CosmosSqlBinaryOperator.LessThanOrEqual => "<=",
        CosmosSqlBinaryOperator.And => "AND",
        CosmosSqlBinaryOperator.Or => "OR",
        CosmosSqlBinaryOperator.Add => "+",
        CosmosSqlBinaryOperator.Subtract => "-",
        CosmosSqlBinaryOperator.Multiply => "*",
        CosmosSqlBinaryOperator.Divide => "/",
        _ => throw new ArgumentOutOfRangeException(nameof(@operator), @operator, "Unsupported Cosmos SQL binary operator.")
    };
}

static class CosmosSqlFunctions
{
    public static string Name(CosmosSqlFunction function) => function switch
    {
        CosmosSqlFunction.IsDefined => "IS_DEFINED",
        CosmosSqlFunction.IsNull => "IS_NULL",
        CosmosSqlFunction.IsObject => "IS_OBJECT",
        CosmosSqlFunction.ArrayContains => "ARRAY_CONTAINS",
        CosmosSqlFunction.StartsWith => "STARTSWITH",
        CosmosSqlFunction.EndsWith => "ENDSWITH",
        CosmosSqlFunction.Contains => "CONTAINS",
        CosmosSqlFunction.StringEquals => "STRINGEQUALS",
        CosmosSqlFunction.Lower => "LOWER",
        CosmosSqlFunction.Upper => "UPPER",
        _ => throw new ArgumentOutOfRangeException(nameof(function), function, "Unsupported Cosmos SQL scalar function.")
    };

    public static string Name(CosmosSqlAggregateFunction function) => function switch
    {
        CosmosSqlAggregateFunction.Count => "COUNT",
        CosmosSqlAggregateFunction.Sum => "SUM",
        CosmosSqlAggregateFunction.Minimum => "MIN",
        CosmosSqlAggregateFunction.Maximum => "MAX",
        CosmosSqlAggregateFunction.Average => "AVG",
        _ => throw new ArgumentOutOfRangeException(nameof(function), function, "Unsupported Cosmos SQL aggregate function.")
    };

    public static void ValidateArity(CosmosSqlFunction function, int count, string parameterName)
    {
        var valid = function switch
        {
            CosmosSqlFunction.IsDefined or CosmosSqlFunction.IsNull or CosmosSqlFunction.IsObject
                or CosmosSqlFunction.Lower or CosmosSqlFunction.Upper => count == 1,
            CosmosSqlFunction.ArrayContains => count is 2 or 3,
            CosmosSqlFunction.StartsWith or CosmosSqlFunction.EndsWith
                or CosmosSqlFunction.Contains or CosmosSqlFunction.StringEquals => count is 2 or 3,
            _ => false
        };
        if (!valid)
            throw new ArgumentException($"Cosmos SQL function '{function}' does not accept {count} argument(s).", parameterName);
    }
}

static class CosmosSqlParameterValues
{
    const int MaximumStructuredNesting = 64;

    public static object? Normalize(object? value) => new Normalizer().Normalize(value, nesting: 0);

    sealed class Normalizer
    {
        readonly HashSet<object> activeContainers = new(ReferenceEqualityComparer.Instance);

        public object? Normalize(object? value, int nesting) => value switch
        {
            null => null,
            ObservationValue observation => NormalizeObservation(observation, nesting),
            string text => text,
            bool flag => flag,
            byte number => (long)number,
            sbyte number => (long)number,
            short number => (long)number,
            ushort number => (long)number,
            int number => (long)number,
            uint number => (long)number,
            long number => number,
            float number when float.IsFinite(number) => (double)number,
            double number when double.IsFinite(number) => number,
            decimal number => number,
            DateTime dateTime when dateTime.Kind != DateTimeKind.Local =>
                dateTime.ToString("O", CultureInfo.InvariantCulture),
            DateTimeOffset dateTimeOffset => dateTimeOffset.ToString("O", CultureInfo.InvariantCulture),
            DateOnly date => date.ToString("O", CultureInfo.InvariantCulture),
            TimeOnly time => time.ToString("O", CultureInfo.InvariantCulture),
            TimeSpan duration => duration.ToString("c", CultureInfo.InvariantCulture),
            Guid guid => guid.ToString(),
            JsonElement element when element.ValueKind != JsonValueKind.Undefined => NormalizeJson(element, nesting),
            IReadOnlyDictionary<string, object?> dictionary => NormalizeDictionary(dictionary, nesting),
            Array array => NormalizeArray(array, nesting),
            IEnumerable<object?> sequence => NormalizeSequence(sequence, nesting),
            _ => throw new NotSupportedException(
                $"CLR value type '{value.GetType().FullName}' cannot be represented as a Cosmos SQL parameter.")
        };

        object? NormalizeObservation(ObservationValue value, int nesting) => value.Kind switch
        {
            ObservationValueKind.Null => null,
            ObservationValueKind.Int64 => value.Int64,
            ObservationValueKind.Double when double.IsFinite(value.Double) => value.Double,
            ObservationValueKind.Bool => value.Bool,
            ObservationValueKind.String or ObservationValueKind.DateTimeOffset
                or ObservationValueKind.DateOnly or ObservationValueKind.TimeOnly
                or ObservationValueKind.TimeSpan => value.String,
            ObservationValueKind.Object => NormalizeObservationObject(
                value.Fields ?? new Dictionary<string, ObservationValue>(StringComparer.Ordinal),
                nesting),
            ObservationValueKind.Array => NormalizeObservationArray(
                value.Array.IsDefault ? [] : value.Array,
                nesting),
            ObservationValueKind.Undefined => throw new NotSupportedException(
                "An undefined observation value cannot be bound as a Cosmos SQL parameter."),
            _ => throw new NotSupportedException(
                $"Observation value kind '{value.Kind}' cannot be represented as a Cosmos SQL parameter.")
        };

        object? NormalizeJson(JsonElement value, int nesting) => value.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number when value.TryGetInt64(out var integer) => integer,
            JsonValueKind.Number when value.TryGetDecimal(out var number) => number,
            JsonValueKind.Number when value.TryGetDouble(out var floating) && double.IsFinite(floating) => floating,
            JsonValueKind.Array => NormalizeJsonArray(value, nesting),
            JsonValueKind.Object => NormalizeJsonObject(value, nesting),
            _ => throw new NotSupportedException(
                $"JSON value kind '{value.ValueKind}' cannot be represented as a Cosmos SQL parameter.")
        };

        ImmutableSortedDictionary<string, object?> NormalizeDictionary(
            IReadOnlyDictionary<string, object?> values,
            int nesting)
        {
            Enter(values, nesting);
            try
            {
                var result = ImmutableSortedDictionary.CreateBuilder<string, object?>(StringComparer.Ordinal);
                foreach (var pair in values)
                    result.Add(pair.Key, Normalize(pair.Value, nesting + 1));
                return result.ToImmutable();
            }
            finally
            {
                activeContainers.Remove(values);
            }
        }

        ImmutableArray<object?> NormalizeSequence(IEnumerable<object?> values, int nesting)
        {
            Enter(values, nesting);
            try
            {
                ImmutableArray<object?>.Builder result = ImmutableArray.CreateBuilder<object?>();
                foreach (var value in values)
                    result.Add(Normalize(value, nesting + 1));
                return result.ToImmutable();
            }
            finally
            {
                activeContainers.Remove(values);
            }
        }

        ImmutableArray<object?> NormalizeArray(Array values, int nesting)
        {
            if (values.Rank != 1)
            {
                throw new NotSupportedException(
                    "Multidimensional CLR arrays cannot be represented as deterministic Cosmos SQL parameters.");
            }

            Enter(values, nesting);
            try
            {
                ImmutableArray<object?>.Builder result = ImmutableArray.CreateBuilder<object?>(values.Length);
                foreach (var value in values)
                    result.Add(Normalize(value, nesting + 1));
                return result.MoveToImmutable();
            }
            finally
            {
                activeContainers.Remove(values);
            }
        }

        ImmutableSortedDictionary<string, object?> NormalizeObservationObject(
            IReadOnlyDictionary<string, ObservationValue> values,
            int nesting)
        {
            Enter(values, nesting);
            try
            {
                var result = ImmutableSortedDictionary.CreateBuilder<string, object?>(StringComparer.Ordinal);
                foreach (var pair in values)
                    result.Add(pair.Key, NormalizeObservation(pair.Value, nesting + 1));
                return result.ToImmutable();
            }
            finally
            {
                activeContainers.Remove(values);
            }
        }

        ImmutableArray<object?> NormalizeObservationArray(
            ImmutableArray<ObservationValue> values,
            int nesting)
        {
            Enter(values, nesting);
            try
            {
                ImmutableArray<object?>.Builder result = ImmutableArray.CreateBuilder<object?>(values.Length);
                foreach (var value in values)
                    result.Add(NormalizeObservation(value, nesting + 1));
                return result.MoveToImmutable();
            }
            finally
            {
                activeContainers.Remove(values);
            }
        }

        ImmutableArray<object?> NormalizeJsonArray(JsonElement value, int nesting)
        {
            RequireSupportedNesting(nesting);
            ImmutableArray<object?>.Builder result = ImmutableArray.CreateBuilder<object?>(value.GetArrayLength());
            foreach (var item in value.EnumerateArray())
                result.Add(NormalizeJson(item, nesting + 1));
            return result.MoveToImmutable();
        }

        ImmutableSortedDictionary<string, object?> NormalizeJsonObject(JsonElement value, int nesting)
        {
            RequireSupportedNesting(nesting);
            var result = ImmutableSortedDictionary.CreateBuilder<string, object?>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!result.TryAdd(property.Name, NormalizeJson(property.Value, nesting + 1)))
                {
                    throw new NotSupportedException(
                        $"JSON object contains repeated property '{property.Name}', which has no deterministic Cosmos parameter interpretation.");
                }
            }
            return result.ToImmutable();
        }

        void Enter(object value, int nesting)
        {
            RequireSupportedNesting(nesting);
            if (!activeContainers.Add(value))
            {
                throw new NotSupportedException(
                    "Cyclic structured values cannot be represented as deterministic Cosmos SQL parameters.");
            }
        }

        static void RequireSupportedNesting(int nesting)
        {
            if (nesting >= MaximumStructuredNesting)
            {
                throw new NotSupportedException(
                    $"Structured Cosmos SQL parameter values cannot exceed {MaximumStructuredNesting} levels of nesting.");
            }
        }
    }
}
