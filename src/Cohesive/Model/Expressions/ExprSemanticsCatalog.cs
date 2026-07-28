using System.Collections.Immutable;
using System.Globalization;

namespace Cohesive.Model.Expressions;

/// <summary>Stable identity for a semantic expression function.</summary>
public readonly record struct ExprFunctionId
{
    /// <summary>Creates a semantic function identifier.</summary>
    /// <param name="value">Stable function name used by <see cref="CallExpr.Function"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white space.</exception>
    public ExprFunctionId(string value)
    {
        Value = Guard.RequireNotNullOrWhiteSpace(value);
    }

    /// <summary>Stable function name.</summary>
    public string Value { get; }

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>Declarative argument-count contract for a semantic function.</summary>
public readonly record struct ExprFunctionArity
{
    /// <summary>Creates a function arity contract.</summary>
    /// <param name="minimum">Inclusive lower bound for accepted argument counts.</param>
    /// <param name="maximum">Inclusive upper bound for accepted argument counts, or <see langword="null"/> when unbounded.</param>
    /// <param name="multiple">Required argument-count multiple, such as two for key/value pairs.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="minimum"/> is negative, <paramref name="maximum"/> is less than
    /// <paramref name="minimum"/>, <paramref name="multiple"/> is not positive, or a bounded
    /// range contains no count divisible by <paramref name="multiple"/>.
    /// </exception>
    public ExprFunctionArity(int minimum, int? maximum = null, int multiple = 1)
    {
        if (minimum < 0)
            throw new ArgumentOutOfRangeException(nameof(minimum), minimum, "Minimum arity cannot be negative.");
        if (maximum is { } maximumValue && maximumValue < minimum)
            throw new ArgumentOutOfRangeException(nameof(maximum), maximum, "Maximum arity cannot be less than minimum arity.");
        if (multiple <= 0)
            throw new ArgumentOutOfRangeException(nameof(multiple), multiple, "Arity multiple must be positive.");
        if (maximum is { } boundedMaximum
            && SmallestAcceptedCount(minimum, multiple) > boundedMaximum)
        {
            throw new ArgumentOutOfRangeException(
                nameof(multiple),
                multiple,
                "The bounded arity range contains no argument count accepted by the required multiple.");
        }

        Minimum = minimum;
        Maximum = maximum;
        Multiple = multiple;
    }

    static long SmallestAcceptedCount(int minimum, int multiple)
    {
        var remainder = minimum % multiple;
        return remainder == 0 ? minimum : (long)minimum + multiple - remainder;
    }

    /// <summary>Inclusive lower bound for accepted argument counts.</summary>
    public int Minimum { get; }

    /// <summary>Inclusive upper bound for accepted argument counts, or <see langword="null"/> when unbounded.</summary>
    public int? Maximum { get; }

    /// <summary>Required argument-count multiple.</summary>
    public int Multiple { get; }

    /// <summary>Tests whether an argument count satisfies this contract.</summary>
    /// <param name="count">Argument count to test.</param>
    /// <returns><see langword="true"/> when accepted; otherwise <see langword="false"/>.</returns>
    /// <exception cref="InvalidOperationException">This is a default, uninitialized arity value.</exception>
    public bool Accepts(int count)
    {
        EnsureInitialized();
        return count >= Minimum
            && (Maximum is null || count <= Maximum.Value)
            && count % Multiple == 0;
    }

    /// <summary>Describes the accepted argument counts for diagnostics.</summary>
    /// <returns>A culture-independent human-readable arity description.</returns>
    /// <exception cref="InvalidOperationException">This is a default, uninitialized arity value.</exception>
    public string Describe()
    {
        EnsureInitialized();
        if (Minimum == Maximum)
            return Minimum.ToString(CultureInfo.InvariantCulture);

        var range = Maximum is null
            ? $"at least {Minimum.ToString(CultureInfo.InvariantCulture)}"
            : $"{Minimum.ToString(CultureInfo.InvariantCulture)} to {Maximum.Value.ToString(CultureInfo.InvariantCulture)}";
        return Multiple == 1
            ? range
            : $"{range}, in multiples of {Multiple.ToString(CultureInfo.InvariantCulture)}";
    }

    void EnsureInitialized()
    {
        if (Multiple <= 0)
            throw new InvalidOperationException("A default expression-function arity has no valid contract.");
    }
}

/// <summary>How a function's known result contract is derived.</summary>
public enum ExprFunctionResultRule
{
    /// <summary>Use a portable type declared by the call, then the function's fixed contract when available.</summary>
    DeclaredOrFixed = 0,

    /// <summary>Use the function's fixed result category and contract.</summary>
    Fixed = 1,

    /// <summary>Use the first argument's known result contract.</summary>
    FirstArgument = 2,

    /// <summary>Return a collection whose element contract is derived from a scoped selector when known.</summary>
    CollectionOfSelector = 3
}

/// <summary>One function argument evaluated with an explicit current-item scope.</summary>
public readonly record struct ExprScopedFunctionArgument
{
    /// <summary>Creates scoped function-argument semantics.</summary>
    /// <param name="argumentIndex">Zero-based argument evaluated in the current-item scope.</param>
    /// <param name="sourceArgumentIndex">Zero-based collection argument that supplies current items.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="argumentIndex"/> or <paramref name="sourceArgumentIndex"/> is negative.
    /// </exception>
    public ExprScopedFunctionArgument(int argumentIndex, int sourceArgumentIndex)
    {
        if (argumentIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(argumentIndex), argumentIndex, "Argument index cannot be negative.");
        if (sourceArgumentIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(sourceArgumentIndex), sourceArgumentIndex, "Source argument index cannot be negative.");

        ArgumentIndex = argumentIndex;
        SourceArgumentIndex = sourceArgumentIndex;
    }

    /// <summary>Zero-based scoped argument index.</summary>
    public int ArgumentIndex { get; }

    /// <summary>Zero-based collection-source argument index.</summary>
    public int SourceArgumentIndex { get; }
}

/// <summary>Inspectable semantic definition for one expression function.</summary>
public sealed class ExprFunctionDefinition
{
    /// <summary>Creates a semantic function definition.</summary>
    /// <param name="id">Stable function identity.</param>
    /// <param name="arity">Accepted argument counts.</param>
    /// <param name="argumentCategories">Expected categories for fixed-position arguments.</param>
    /// <param name="variadicCategory">Expected category for remaining variadic arguments.</param>
    /// <param name="resultCategory">Known coarse result category.</param>
    /// <param name="resultRule">Rule used to derive the known result contract.</param>
    /// <param name="fixedResult">Fixed portable result contract when known.</param>
    /// <param name="scopedArguments">Arguments evaluated with current-item scope.</param>
    /// <param name="ambientCapabilities">Ambient context required by the function.</param>
    /// <param name="repeatingArgumentCategories">
    /// Optional repeating category pattern applied after fixed-position arguments.
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="id"/> or an ambient capability is empty, the arity is the default invalid value,
    /// an argument category is unsupported; scoped argument metadata is duplicated, circular, outside a bounded
    /// arity contract, or sourced from a non-collection argument; fixed-result metadata is missing, ignored, or
    /// contradicts the declared result category; or result-rule metadata is internally inconsistent.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="variadicCategory"/>, <paramref name="resultCategory"/>, or
    /// <paramref name="resultRule"/> is unsupported.
    /// </exception>
    public ExprFunctionDefinition(
        ExprFunctionId id,
        ExprFunctionArity arity,
        ImmutableArray<ExprResultCategory> argumentCategories = default,
        ExprResultCategory variadicCategory = ExprResultCategory.Any,
        ExprResultCategory resultCategory = ExprResultCategory.Any,
        ExprFunctionResultRule resultRule = ExprFunctionResultRule.DeclaredOrFixed,
        ValueContract? fixedResult = null,
        ImmutableArray<ExprScopedFunctionArgument> scopedArguments = default,
        ImmutableArray<ExprCapabilityId> ambientCapabilities = default,
        ImmutableArray<ExprResultCategory> repeatingArgumentCategories = default)
    {
        if (string.IsNullOrWhiteSpace(id.Value))
            throw new ArgumentException("A function definition must have a non-empty identifier.", nameof(id));
        if (arity.Multiple <= 0)
            throw new ArgumentException("A function definition must have a valid arity contract.", nameof(arity));
        if (!argumentCategories.IsDefault
            && argumentCategories.Any(static category => !Enum.IsDefined(category)))
        {
            throw new ArgumentException("Function argument categories contain an unsupported value.", nameof(argumentCategories));
        }
        if (!repeatingArgumentCategories.IsDefault
            && repeatingArgumentCategories.Any(static category => !Enum.IsDefined(category)))
        {
            throw new ArgumentException(
                "Function repeating argument categories contain an unsupported value.",
                nameof(repeatingArgumentCategories));
        }
        if (!Enum.IsDefined(variadicCategory))
            throw new ArgumentOutOfRangeException(nameof(variadicCategory), variadicCategory, "Unsupported argument category.");
        if (!Enum.IsDefined(resultCategory))
            throw new ArgumentOutOfRangeException(nameof(resultCategory), resultCategory, "Unsupported result category.");
        if (!Enum.IsDefined(resultRule))
            throw new ArgumentOutOfRangeException(nameof(resultRule), resultRule, "Unsupported function result rule.");

        Id = id;
        Arity = arity;
        ArgumentCategories = argumentCategories.IsDefault ? [] : argumentCategories;
        VariadicCategory = variadicCategory;
        RepeatingArgumentCategories = repeatingArgumentCategories.IsDefault ? [] : repeatingArgumentCategories;
        ResultCategory = resultCategory;
        ResultRule = resultRule;
        FixedResult = fixedResult;
        ScopedArguments = scopedArguments.IsDefault
            ? []
            : [.. scopedArguments.OrderBy(static argument => argument.ArgumentIndex)];
        AmbientCapabilities = ambientCapabilities.IsDefault
            ? []
            : [.. ambientCapabilities.Distinct().OrderBy(static capability => capability.Value, StringComparer.Ordinal)];

        if (ResultRule == ExprFunctionResultRule.Fixed && FixedResult is null)
            throw new ArgumentException("A fixed-result function must declare a fixed result contract.", nameof(fixedResult));
        if (FixedResult is not null
            && !ExprResultCategorySemantics.Satisfies(
                ExprResultCategorySemantics.Classify(FixedResult),
                ResultCategory))
        {
            throw new ArgumentException(
                "The fixed result contract does not satisfy the declared function result category.",
                nameof(fixedResult));
        }
        if (ResultRule == ExprFunctionResultRule.FirstArgument)
        {
            if (Arity.Minimum < 1)
            {
                throw new ArgumentException(
                    "A first-argument result rule requires at least one argument.",
                    nameof(arity));
            }
            if (FixedResult is not null)
            {
                throw new ArgumentException(
                    "A first-argument result rule cannot declare an ignored fixed result.",
                    nameof(fixedResult));
            }

            var firstCategory = GetArgumentCategory(0);
            if (ResultCategory != ExprResultCategory.Any
                && (firstCategory == ExprResultCategory.Any
                    || !ExprResultCategorySemantics.Satisfies(firstCategory, ResultCategory)))
            {
                throw new ArgumentException(
                    "A first-argument result category must accept the declared first-argument category.",
                    nameof(resultCategory));
            }
        }
        if (ResultRule == ExprFunctionResultRule.CollectionOfSelector)
        {
            if (ResultCategory != ExprResultCategory.Collection)
            {
                throw new ArgumentException(
                    "A collection-of-selector result rule must declare a collection result category.",
                    nameof(resultCategory));
            }
            if (FixedResult is not null)
            {
                throw new ArgumentException(
                    "A collection-of-selector result rule cannot declare an ignored fixed result.",
                    nameof(fixedResult));
            }
            if (ScopedArguments.Length != 1)
            {
                throw new ArgumentException(
                    "A collection-of-selector result rule must declare exactly one scoped selector argument.",
                    nameof(scopedArguments));
            }
        }

        if (AmbientCapabilities.Any(static capability => string.IsNullOrWhiteSpace(capability.Value)))
            throw new ArgumentException("Function ambient capabilities must have non-empty identifiers.", nameof(ambientCapabilities));

        var duplicate = ScopedArguments
            .GroupBy(static argument => argument.ArgumentIndex)
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicate is not null)
            throw new ArgumentException($"Function '{id.Value}' scopes argument {duplicate.Key} more than once.", nameof(scopedArguments));
        var scopedIndexes = ScopedArguments.Select(static argument => argument.ArgumentIndex).ToHashSet();
        if (ScopedArguments.Any(argument => argument.ArgumentIndex == argument.SourceArgumentIndex
            || scopedIndexes.Contains(argument.SourceArgumentIndex)))
        {
            throw new ArgumentException(
                $"Function '{id.Value}' must source each scoped argument from an unscoped collection argument.",
                nameof(scopedArguments));
        }
        if (ScopedArguments.Any(argument =>
                argument.SourceArgumentIndex > argument.ArgumentIndex
                && HasAcceptedCount(
                    argument.ArgumentIndex + 1,
                    argument.SourceArgumentIndex)))
        {
            throw new ArgumentException(
                $"Function '{id.Value}' accepts an arity that supplies a scoped argument without its source argument.",
                nameof(scopedArguments));
        }
        if (Arity.Maximum is { } maximum
            && ScopedArguments.Any(argument => argument.ArgumentIndex >= maximum || argument.SourceArgumentIndex >= maximum))
        {
            throw new ArgumentException($"Function '{id.Value}' has scoped argument metadata outside its maximum arity.", nameof(scopedArguments));
        }
        if (ScopedArguments.Any(argument =>
                GetArgumentCategory(argument.SourceArgumentIndex) != ExprResultCategory.Collection))
        {
            throw new ArgumentException(
                $"Function '{id.Value}' must declare every scoped argument source as a collection.",
                nameof(scopedArguments));
        }

        bool HasAcceptedCount(int minimum, int maximum)
        {
            var lower = Math.Max(Arity.Minimum, minimum);
            var upper = Math.Min(Arity.Maximum ?? maximum, maximum);
            if (lower > upper)
                return false;

            var remainder = lower % Arity.Multiple;
            var first = remainder == 0
                ? (long)lower
                : (long)lower + Arity.Multiple - remainder;
            return first <= upper;
        }
    }

    /// <summary>Stable function identity.</summary>
    public ExprFunctionId Id { get; }

    /// <summary>Accepted argument counts.</summary>
    public ExprFunctionArity Arity { get; }

    /// <summary>Expected categories for fixed-position arguments.</summary>
    public ImmutableArray<ExprResultCategory> ArgumentCategories { get; }

    /// <summary>Expected category for remaining variadic arguments.</summary>
    public ExprResultCategory VariadicCategory { get; }

    /// <summary>Repeating category pattern applied after fixed-position arguments.</summary>
    public ImmutableArray<ExprResultCategory> RepeatingArgumentCategories { get; }

    /// <summary>Known coarse result category.</summary>
    public ExprResultCategory ResultCategory { get; }

    /// <summary>Rule used to derive the known result contract.</summary>
    public ExprFunctionResultRule ResultRule { get; }

    /// <summary>Fixed portable result contract when known.</summary>
    public ValueContract? FixedResult { get; }

    /// <summary>Arguments evaluated with current-item scope.</summary>
    public ImmutableArray<ExprScopedFunctionArgument> ScopedArguments { get; }

    /// <summary>Ambient context required by the function.</summary>
    public ImmutableArray<ExprCapabilityId> AmbientCapabilities { get; }

    /// <summary>Stable operation capability required to execute this function.</summary>
    public ExprCapabilityId OperationCapability => ExprCapabilities.ForFunction(Id.Value);

    /// <summary>Gets the expected category for a zero-based argument.</summary>
    /// <param name="index">Zero-based argument index.</param>
    /// <returns>
    /// The fixed-position category when declared, the repeating pattern category when configured, or the variadic category.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is negative.</exception>
    public ExprResultCategory GetArgumentCategory(int index)
    {
        if (index < 0)
            throw new ArgumentOutOfRangeException(nameof(index), index, "Argument index cannot be negative.");
        if (index >= 0 && index < ArgumentCategories.Length)
            return ArgumentCategories[index];
        if (index >= ArgumentCategories.Length && !RepeatingArgumentCategories.IsDefaultOrEmpty)
        {
            return RepeatingArgumentCategories[
                (index - ArgumentCategories.Length) % RepeatingArgumentCategories.Length];
        }

        return VariadicCategory;
    }
}

/// <summary>Inspectable unary-operator semantics.</summary>
/// <param name="Operator">Canonical unary operator.</param>
/// <param name="OperandCategory">Expected operand category.</param>
/// <param name="ResultCategory">Known result category.</param>
/// <param name="FixedResult">Known portable result contract.</param>
public sealed record ExprUnaryOperatorDefinition(
    UnaryOperator Operator,
    ExprResultCategory OperandCategory,
    ExprResultCategory ResultCategory,
    ValueContract? FixedResult = null)
{
    /// <summary>Stable operation capability required by this operator.</summary>
    public ExprCapabilityId OperationCapability => ExprCapabilities.ForUnary(Operator);
}

/// <summary>Inspectable binary-operator semantics.</summary>
/// <param name="Operator">Canonical binary operator.</param>
/// <param name="LeftCategory">Expected left-operand category.</param>
/// <param name="RightCategory">Expected right-operand category.</param>
/// <param name="ResultCategory">Known result category.</param>
/// <param name="FixedResult">Known portable result contract.</param>
public sealed record ExprBinaryOperatorDefinition(
    BinaryOperator Operator,
    ExprResultCategory LeftCategory,
    ExprResultCategory RightCategory,
    ExprResultCategory ResultCategory,
    ValueContract? FixedResult = null)
{
    /// <summary>Stable operation capability required by this operator.</summary>
    public ExprCapabilityId OperationCapability => ExprCapabilities.ForBinary(Operator);
}

/// <summary>Inspectable aggregate-operator semantics for <see cref="AggregateExpr"/>.</summary>
/// <param name="Operator">Canonical aggregate operator.</param>
/// <param name="SourceCategory">Expected source category.</param>
/// <param name="ResultCategory">Known result category when not refined by the node's declared return type.</param>
/// <param name="FixedResult">Known portable result contract that declared return-type metadata must match.</param>
public sealed record ExprAggregateOperatorDefinition(
    AggregateOperator Operator,
    ExprResultCategory SourceCategory,
    ExprResultCategory ResultCategory,
    ValueContract? FixedResult = null)
{
    /// <summary>Stable operation capability required by this aggregate.</summary>
    public ExprCapabilityId OperationCapability => ExprCapabilities.ForAggregate(Operator);
}

/// <summary>
/// Immutable, data-only catalog describing canonical expression functions and operators.
/// </summary>
public sealed class ExprSemanticsCatalog
{
    readonly ImmutableDictionary<UnaryOperator, ExprUnaryOperatorDefinition> unaryByOperator;
    readonly ImmutableDictionary<BinaryOperator, ExprBinaryOperatorDefinition> binaryByOperator;
    readonly ImmutableDictionary<AggregateOperator, ExprAggregateOperatorDefinition> aggregateByOperator;
    readonly ImmutableDictionary<string, ExprFunctionDefinition> functionsById;

    /// <summary>Canonical built-in expression semantics.</summary>
    public static ExprSemanticsCatalog Default { get; } = CreateDefault();

    /// <summary>Creates an immutable expression semantics catalog.</summary>
    /// <param name="unaryOperators">Unary-operator definitions.</param>
    /// <param name="binaryOperators">Binary-operator definitions.</param>
    /// <param name="aggregateOperators">Aggregate-operator definitions.</param>
    /// <param name="functions">Function definitions.</param>
    /// <exception cref="ArgumentException">
    /// A collection contains null, duplicate, or unsupported semantic definitions.
    /// </exception>
    public ExprSemanticsCatalog(
        IEnumerable<ExprUnaryOperatorDefinition>? unaryOperators = null,
        IEnumerable<ExprBinaryOperatorDefinition>? binaryOperators = null,
        IEnumerable<ExprAggregateOperatorDefinition>? aggregateOperators = null,
        IEnumerable<ExprFunctionDefinition>? functions = null)
    {
        UnaryOperators = Normalize(
            unaryOperators,
            static definition => (int)definition.Operator,
            nameof(unaryOperators));
        BinaryOperators = Normalize(
            binaryOperators,
            static definition => (int)definition.Operator,
            nameof(binaryOperators));
        AggregateOperators = Normalize(
            aggregateOperators,
            static definition => (int)definition.Operator,
            nameof(aggregateOperators));
        Functions = Normalize(
            functions,
            static definition => definition.Id.Value,
            nameof(functions),
            StringComparer.Ordinal);

        ValidateOperatorDefinitions(UnaryOperators, BinaryOperators, AggregateOperators);

        unaryByOperator = UnaryOperators.ToImmutableDictionary(static definition => definition.Operator);
        binaryByOperator = BinaryOperators.ToImmutableDictionary(static definition => definition.Operator);
        aggregateByOperator = AggregateOperators.ToImmutableDictionary(static definition => definition.Operator);
        functionsById = Functions.ToImmutableDictionary(
            static definition => definition.Id.Value,
            StringComparer.Ordinal);

        ExprCapabilityId[] capabilities =
        [
            ExprCapabilities.Binding,
            ExprCapabilities.Field,
            ExprCapabilities.NestedFieldPath,
            ExprCapabilities.Parameter,
            ExprCapabilities.Constant,
            ExprCapabilities.TypedField,
            ExprCapabilities.TypedLiteral,
            ExprCapabilities.Conditional,
            ExprCapabilities.CurrentItem,
            .. UnaryOperators.Select(static definition => definition.OperationCapability),
            .. BinaryOperators.Select(static definition => definition.OperationCapability),
            .. AggregateOperators.Select(static definition => definition.OperationCapability),
            .. Functions.Select(static definition => definition.OperationCapability)
        ];
        Capabilities = [.. capabilities.Distinct().OrderBy(static capability => capability.Value, StringComparer.Ordinal)];
    }

    /// <summary>Unary definitions sorted by enum value.</summary>
    public ImmutableArray<ExprUnaryOperatorDefinition> UnaryOperators { get; }

    /// <summary>Binary definitions sorted by enum value.</summary>
    public ImmutableArray<ExprBinaryOperatorDefinition> BinaryOperators { get; }

    /// <summary>Aggregate definitions sorted by enum value.</summary>
    public ImmutableArray<ExprAggregateOperatorDefinition> AggregateOperators { get; }

    /// <summary>Function definitions sorted by ordinal function identifier.</summary>
    public ImmutableArray<ExprFunctionDefinition> Functions { get; }

    /// <summary>All operation capabilities described by this catalog, sorted ordinally.</summary>
    public ImmutableArray<ExprCapabilityId> Capabilities { get; }

    /// <summary>Looks up unary-operator semantics.</summary>
    /// <param name="operator">Operator to resolve.</param>
    /// <param name="definition">Resolved semantics when found.</param>
    /// <returns><see langword="true"/> when defined; otherwise <see langword="false"/>.</returns>
    public bool TryGetUnary(UnaryOperator @operator, out ExprUnaryOperatorDefinition definition) =>
        unaryByOperator.TryGetValue(@operator, out definition!);

    /// <summary>Looks up binary-operator semantics.</summary>
    /// <param name="operator">Operator to resolve.</param>
    /// <param name="definition">Resolved semantics when found.</param>
    /// <returns><see langword="true"/> when defined; otherwise <see langword="false"/>.</returns>
    public bool TryGetBinary(BinaryOperator @operator, out ExprBinaryOperatorDefinition definition) =>
        binaryByOperator.TryGetValue(@operator, out definition!);

    /// <summary>Looks up aggregate-operator semantics.</summary>
    /// <param name="operator">Operator to resolve.</param>
    /// <param name="definition">Resolved semantics when found.</param>
    /// <returns><see langword="true"/> when defined; otherwise <see langword="false"/>.</returns>
    public bool TryGetAggregate(AggregateOperator @operator, out ExprAggregateOperatorDefinition definition) =>
        aggregateByOperator.TryGetValue(@operator, out definition!);

    /// <summary>Looks up function semantics by canonical call name.</summary>
    /// <param name="function">Function name to resolve.</param>
    /// <param name="definition">Resolved semantics when found.</param>
    /// <returns><see langword="true"/> when defined; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="function"/> is <see langword="null"/>.</exception>
    public bool TryGetFunction(string function, out ExprFunctionDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(function);
        return functionsById.TryGetValue(function, out definition!);
    }

    /// <summary>Creates a capability profile supporting every operation described by this catalog.</summary>
    /// <param name="additionalCapabilities">Additional operation capabilities supported by the target.</param>
    /// <returns>A deterministic target capability profile.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="additionalCapabilities"/> contains a default, empty identifier.
    /// </exception>
    public ExprCapabilityProfile CreateCapabilityProfile(
        IEnumerable<ExprCapabilityId>? additionalCapabilities = null) =>
        new(additionalCapabilities is null ? Capabilities : Capabilities.Concat(additionalCapabilities));

    static ExprSemanticsCatalog CreateDefault()
    {
        var boolean = new ValueContract(new ScalarTypeRef(ScalarTypeKind.Bool));
        var decimalNumber = new ValueContract(
            new ScalarTypeRef(ScalarTypeKind.Decimal),
            presence: FieldPresence.Optional);
        var int64 = new ValueContract(new ScalarTypeRef(ScalarTypeKind.Int64));
        var @string = new ValueContract(new ScalarTypeRef(ScalarTypeKind.String));

        return new(
            unaryOperators:
            [
                new(UnaryOperator.Not, ExprResultCategory.Boolean, ExprResultCategory.Boolean, boolean)
            ],
            binaryOperators:
            [
                new(BinaryOperator.Eq, ExprResultCategory.Any, ExprResultCategory.Any, ExprResultCategory.Boolean, boolean),
                new(BinaryOperator.Ne, ExprResultCategory.Any, ExprResultCategory.Any, ExprResultCategory.Boolean, boolean),
                new(BinaryOperator.Gt, ExprResultCategory.Comparable, ExprResultCategory.Comparable, ExprResultCategory.Boolean, boolean),
                new(BinaryOperator.Ge, ExprResultCategory.Comparable, ExprResultCategory.Comparable, ExprResultCategory.Boolean, boolean),
                new(BinaryOperator.Lt, ExprResultCategory.Comparable, ExprResultCategory.Comparable, ExprResultCategory.Boolean, boolean),
                new(BinaryOperator.Le, ExprResultCategory.Comparable, ExprResultCategory.Comparable, ExprResultCategory.Boolean, boolean),
                new(BinaryOperator.And, ExprResultCategory.Boolean, ExprResultCategory.Boolean, ExprResultCategory.Boolean, boolean),
                new(BinaryOperator.Or, ExprResultCategory.Boolean, ExprResultCategory.Boolean, ExprResultCategory.Boolean, boolean),
                new(BinaryOperator.Add, ExprResultCategory.Numeric, ExprResultCategory.Numeric, ExprResultCategory.Numeric),
                new(BinaryOperator.Sub, ExprResultCategory.Numeric, ExprResultCategory.Numeric, ExprResultCategory.Numeric),
                new(BinaryOperator.Mul, ExprResultCategory.Numeric, ExprResultCategory.Numeric, ExprResultCategory.Numeric),
                new(BinaryOperator.Div, ExprResultCategory.Numeric, ExprResultCategory.Numeric, ExprResultCategory.Numeric)
            ],
            aggregateOperators:
            [
                new(AggregateOperator.Count, ExprResultCategory.Collection, ExprResultCategory.Integer),
                new(AggregateOperator.Sum, ExprResultCategory.Collection, ExprResultCategory.Numeric),
                new(AggregateOperator.Min, ExprResultCategory.Collection, ExprResultCategory.Scalar),
                new(AggregateOperator.Max, ExprResultCategory.Collection, ExprResultCategory.Scalar),
                new(AggregateOperator.Any, ExprResultCategory.Collection, ExprResultCategory.Boolean),
                new(AggregateOperator.All, ExprResultCategory.Collection, ExprResultCategory.Boolean),
                new(
                    AggregateOperator.Average,
                    ExprResultCategory.Collection,
                    ExprResultCategory.Numeric,
                    decimalNumber)
            ],
            functions:
            [
                Function(ExprFunctionNames.All, 1, 2, [ExprResultCategory.Collection, ExprResultCategory.Boolean], ExprResultCategory.Any, ExprResultCategory.Boolean, ExprFunctionResultRule.Fixed, boolean, [new(1, 0)]),
                Function(ExprFunctionNames.Any, 1, 2, [ExprResultCategory.Collection, ExprResultCategory.Boolean], ExprResultCategory.Any, ExprResultCategory.Boolean, ExprFunctionResultRule.Fixed, boolean, [new(1, 0)]),
                Function(ExprFunctionNames.Append, 2, 2, [ExprResultCategory.Collection, ExprResultCategory.Any], resultRule: ExprFunctionResultRule.FirstArgument, resultCategory: ExprResultCategory.Collection),
                Function(ExprFunctionNames.AppendRange, 2, 2, [ExprResultCategory.Collection, ExprResultCategory.Collection], resultRule: ExprFunctionResultRule.FirstArgument, resultCategory: ExprResultCategory.Collection),
                Function(
                    id: ExprFunctionNames.Avg,
                    minimum: 1,
                    maximum: 2,
                    argumentCategories: [ExprResultCategory.Collection, ExprResultCategory.Numeric],
                    variadicCategory: ExprResultCategory.Any,
                    resultCategory: ExprResultCategory.Numeric,
                    resultRule: ExprFunctionResultRule.Fixed,
                    fixedResult: decimalNumber,
                    scoped: [new(1, 0)]),
                Function(ExprFunctionNames.Concat, 1, null, argumentCategories: [], variadicCategory: ExprResultCategory.Text, resultCategory: ExprResultCategory.Text, resultRule: ExprFunctionResultRule.Fixed, fixedResult: @string),
                Function(ExprFunctionNames.Contains, 2, 2, [ExprResultCategory.Collection, ExprResultCategory.Any], resultCategory: ExprResultCategory.Boolean, resultRule: ExprFunctionResultRule.Fixed, fixedResult: boolean),
                Function(ExprFunctionNames.Count, 1, 1, [ExprResultCategory.Countable], resultCategory: ExprResultCategory.Integer, resultRule: ExprFunctionResultRule.Fixed, fixedResult: int64),
                Function(ExprFunctionNames.EntityId, 0, 0, resultCategory: ExprResultCategory.Text, resultRule: ExprFunctionResultRule.Fixed, fixedResult: @string, ambient: [ExprCapabilities.EntityIdentity]),
                Function(ExprFunctionNames.EndsWith, 2, 2, [ExprResultCategory.Text, ExprResultCategory.Text], resultCategory: ExprResultCategory.Boolean, resultRule: ExprFunctionResultRule.Fixed, fixedResult: boolean),
                Function(ExprFunctionNames.StartsWith, 2, 2, [ExprResultCategory.Text, ExprResultCategory.Text], resultCategory: ExprResultCategory.Boolean, resultRule: ExprFunctionResultRule.Fixed, fixedResult: boolean),
                Function(ExprFunctionNames.TextContains, 2, 2, [ExprResultCategory.Text, ExprResultCategory.Text], resultCategory: ExprResultCategory.Boolean, resultRule: ExprFunctionResultRule.Fixed, fixedResult: boolean),
                Function(ExprFunctionNames.GroupBy, 2, 2, [ExprResultCategory.Collection, ExprResultCategory.Any], resultCategory: ExprResultCategory.Object, scoped: [new(1, 0)]),
                Function(ExprFunctionNames.GroupByRows, 2, 2, [ExprResultCategory.Collection, ExprResultCategory.Any], resultCategory: ExprResultCategory.Collection, scoped: [new(1, 0)]),
                Function(ExprFunctionNames.InsertAt, 3, 3, [ExprResultCategory.Collection, ExprResultCategory.Integer, ExprResultCategory.Any], resultCategory: ExprResultCategory.Collection, resultRule: ExprFunctionResultRule.FirstArgument),
                Function(ExprFunctionNames.InsertRangeAt, 3, 3, [ExprResultCategory.Collection, ExprResultCategory.Integer, ExprResultCategory.Collection], resultCategory: ExprResultCategory.Collection, resultRule: ExprFunctionResultRule.FirstArgument),
                Function(ExprFunctionNames.Join, 3, 3, [ExprResultCategory.Any, ExprResultCategory.Any, ExprResultCategory.Collection], resultCategory: ExprResultCategory.Collection, scoped: [new(1, 2)]),
                Function(ExprFunctionNames.Key, 0, 0, resultCategory: ExprResultCategory.Text, resultRule: ExprFunctionResultRule.Fixed, fixedResult: @string, ambient: [ExprCapabilities.RootKey]),
                Function(ExprFunctionNames.Max, 1, 2, [ExprResultCategory.Collection, ExprResultCategory.Comparable], ExprResultCategory.Any, ExprResultCategory.Scalar, scoped: [new(1, 0)]),
                Function(ExprFunctionNames.Min, 1, 2, [ExprResultCategory.Collection, ExprResultCategory.Comparable], ExprResultCategory.Any, ExprResultCategory.Scalar, scoped: [new(1, 0)]),
                Function(ExprFunctionNames.Object, 0, null, argumentCategories: [], variadicCategory: ExprResultCategory.Any, resultCategory: ExprResultCategory.Object, multiple: 2, repeating: [ExprResultCategory.Text, ExprResultCategory.Any]),
                Function(ExprFunctionNames.Select, 2, 2, [ExprResultCategory.Collection, ExprResultCategory.Any], resultCategory: ExprResultCategory.Collection, resultRule: ExprFunctionResultRule.CollectionOfSelector, scoped: [new(1, 0)]),
                Function(ExprFunctionNames.SourceRows, 0, 0, resultCategory: ExprResultCategory.Collection, ambient: [ExprCapabilities.SourceSet]),
                Function(ExprFunctionNames.Sum, 1, 2, [ExprResultCategory.Collection, ExprResultCategory.Numeric], ExprResultCategory.Any, ExprResultCategory.Numeric, scoped: [new(1, 0)])
            ]);

        static ExprFunctionDefinition Function(
            string id,
            int minimum,
            int? maximum,
            ImmutableArray<ExprResultCategory> argumentCategories = default,
            ExprResultCategory variadicCategory = ExprResultCategory.Any,
            ExprResultCategory resultCategory = ExprResultCategory.Any,
            ExprFunctionResultRule resultRule = ExprFunctionResultRule.DeclaredOrFixed,
            ValueContract? fixedResult = null,
            ImmutableArray<ExprScopedFunctionArgument> scoped = default,
            ImmutableArray<ExprCapabilityId> ambient = default,
            int multiple = 1,
            ImmutableArray<ExprResultCategory> repeating = default) =>
            new(
                new(id),
                new(minimum, maximum, multiple),
                argumentCategories,
                variadicCategory,
                resultCategory,
                resultRule,
                fixedResult,
                scoped,
                ambient,
                repeating);
    }

    static ImmutableArray<T> Normalize<T, TKey>(
        IEnumerable<T>? definitions,
        Func<T, TKey> keySelector,
        string parameterName,
        IComparer<TKey>? comparer = null)
        where T : class
        where TKey : notnull
    {
        var array = definitions is null ? [] : definitions.ToImmutableArray();
        if (array.Any(static definition => definition is null))
            throw new ArgumentException("Semantic definitions cannot contain null entries.", parameterName);
        var duplicate = array.GroupBy(keySelector).FirstOrDefault(static group => group.Count() > 1);
        if (duplicate is not null)
            throw new ArgumentException($"Semantic definition '{duplicate.Key}' is declared more than once.", parameterName);

        comparer ??= Comparer<TKey>.Default;
        return [.. array.OrderBy(keySelector, comparer)];
    }

    static void ValidateOperatorDefinitions(
        ImmutableArray<ExprUnaryOperatorDefinition> unary,
        ImmutableArray<ExprBinaryOperatorDefinition> binary,
        ImmutableArray<ExprAggregateOperatorDefinition> aggregate)
    {
        if (unary.Any(static definition =>
                !Enum.IsDefined(definition.Operator)
                || !Enum.IsDefined(definition.OperandCategory)
                || !Enum.IsDefined(definition.ResultCategory)
                || definition.FixedResult is not null
                    && !ExprResultCategorySemantics.Satisfies(
                        ExprResultCategorySemantics.Classify(definition.FixedResult),
                        definition.ResultCategory)))
        {
            throw new ArgumentException("Unary operator definitions contain an unsupported value.", nameof(unary));
        }

        if (binary.Any(static definition =>
                !Enum.IsDefined(definition.Operator)
                || !Enum.IsDefined(definition.LeftCategory)
                || !Enum.IsDefined(definition.RightCategory)
                || !Enum.IsDefined(definition.ResultCategory)
                || definition.FixedResult is not null
                    && !ExprResultCategorySemantics.Satisfies(
                        ExprResultCategorySemantics.Classify(definition.FixedResult),
                        definition.ResultCategory)))
        {
            throw new ArgumentException("Binary operator definitions contain an unsupported value.", nameof(binary));
        }

        if (aggregate.Any(static definition =>
                !Enum.IsDefined(definition.Operator)
                || !Enum.IsDefined(definition.SourceCategory)
                || !Enum.IsDefined(definition.ResultCategory)
                || definition.FixedResult is not null
                    && !ExprResultCategorySemantics.Satisfies(
                        ExprResultCategorySemantics.Classify(definition.FixedResult),
                        definition.ResultCategory)))
        {
            throw new ArgumentException("Aggregate operator definitions contain an unsupported value.", nameof(aggregate));
        }
    }
}

/// <summary>Stable capability identifiers for canonical expression operations and ambient context.</summary>
public static class ExprCapabilities
{
    /// <summary>Whole-value binding access.</summary>
    public static ExprCapabilityId Binding { get; } = new("expr.node.binding");

    /// <summary>Field-reference evaluation.</summary>
    public static ExprCapabilityId Field { get; } = new("expr.node.field");

    /// <summary>Navigation through a field path containing more than one segment.</summary>
    public static ExprCapabilityId NestedFieldPath { get; } = new("expr.node.field.nestedPath");

    /// <summary>Named-parameter access.</summary>
    public static ExprCapabilityId Parameter { get; } = new("expr.node.parameter");

    /// <summary>Untyped constant evaluation.</summary>
    public static ExprCapabilityId Constant { get; } = new("expr.node.constant");

    /// <summary>Field-reference evaluation with declared type metadata.</summary>
    public static ExprCapabilityId TypedField { get; } = new("expr.node.typedField");

    /// <summary>Literal evaluation with declared type metadata.</summary>
    public static ExprCapabilityId TypedLiteral { get; } = new("expr.node.typedLiteral");

    /// <summary>Conditional-expression evaluation.</summary>
    public static ExprCapabilityId Conditional { get; } = new("expr.node.conditional");

    /// <summary>Explicit current-item access.</summary>
    public static ExprCapabilityId CurrentItem { get; } = new("expr.node.currentItem");

    /// <summary>Ambient access to the current logical entity identity.</summary>
    public static ExprCapabilityId EntityIdentity { get; } = new("expr.ambient.entityIdentity");

    /// <summary>Ambient access to the current root observation key.</summary>
    public static ExprCapabilityId RootKey { get; } = new("expr.ambient.rootKey");

    /// <summary>Ambient access to the current source row set.</summary>
    public static ExprCapabilityId SourceSet { get; } = new("expr.ambient.sourceSet");

    /// <summary>Creates the operation capability for a function call.</summary>
    /// <param name="function">Stable function name.</param>
    /// <returns>The stable function-operation capability.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="function"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="function"/> is empty or white space.</exception>
    public static ExprCapabilityId ForFunction(string function) =>
        new($"expr.function.{Guard.RequireNotNullOrWhiteSpace(function)}");

    /// <summary>Creates the operation capability for a unary operator.</summary>
    /// <param name="operator">Unary operator.</param>
    /// <returns>The stable unary-operation capability.</returns>
    public static ExprCapabilityId ForUnary(UnaryOperator @operator) =>
        new($"expr.operator.unary.{UnaryName(@operator)}");

    /// <summary>Creates the operation capability for a binary operator.</summary>
    /// <param name="operator">Binary operator.</param>
    /// <returns>The stable binary-operation capability.</returns>
    public static ExprCapabilityId ForBinary(BinaryOperator @operator) =>
        new($"expr.operator.binary.{BinaryName(@operator)}");

    /// <summary>Creates the operation capability for an aggregate operator.</summary>
    /// <param name="operator">Aggregate operator.</param>
    /// <returns>The stable aggregate-operation capability.</returns>
    public static ExprCapabilityId ForAggregate(AggregateOperator @operator) =>
        new($"expr.operator.aggregate.{AggregateName(@operator)}");

    static string UnaryName(UnaryOperator @operator) => @operator switch
    {
        UnaryOperator.Not => "not",
        _ => $"unknown.{((int)@operator).ToString(CultureInfo.InvariantCulture)}"
    };

    static string BinaryName(BinaryOperator @operator) => @operator switch
    {
        BinaryOperator.Eq => "eq",
        BinaryOperator.Ne => "ne",
        BinaryOperator.Gt => "gt",
        BinaryOperator.Ge => "ge",
        BinaryOperator.Lt => "lt",
        BinaryOperator.Le => "le",
        BinaryOperator.And => "and",
        BinaryOperator.Or => "or",
        BinaryOperator.Add => "add",
        BinaryOperator.Sub => "sub",
        BinaryOperator.Mul => "mul",
        BinaryOperator.Div => "div",
        _ => $"unknown.{((int)@operator).ToString(CultureInfo.InvariantCulture)}"
    };

    static string AggregateName(AggregateOperator @operator) => @operator switch
    {
        AggregateOperator.Count => "count",
        AggregateOperator.Sum => "sum",
        AggregateOperator.Min => "min",
        AggregateOperator.Max => "max",
        AggregateOperator.Any => "any",
        AggregateOperator.All => "all",
        AggregateOperator.Average => "average",
        _ => $"unknown.{((int)@operator).ToString(CultureInfo.InvariantCulture)}"
    };
}
