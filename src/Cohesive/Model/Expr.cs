using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Cohesive.Model;

/// <summary>
/// Shared semantic expression IR used by relations and transitions.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$expr")]
[JsonDerivedType(typeof(BindingExpr), "binding")]
[JsonDerivedType(typeof(FieldExpr), "field")]
[JsonDerivedType(typeof(CurrentItemExpr), "currentItem")]
[JsonDerivedType(typeof(ParameterExpr), "parameter")]
[JsonDerivedType(typeof(ConstantExpr), "constant")]
[JsonDerivedType(typeof(UnaryExpr), "unary")]
[JsonDerivedType(typeof(BinaryExpr), "binary")]
[JsonDerivedType(typeof(ConditionalExpr), "conditional")]
[JsonDerivedType(typeof(CallExpr), "function")]
[JsonDerivedType(typeof(FieldRefExpr), "typedFieldRef")]
[JsonDerivedType(typeof(LiteralExpr), "literal")]
[JsonDerivedType(typeof(AggregateExpr), "aggregate")]
public abstract record Expr
{
    /// <summary>
    /// Expression that references the complete value of a specific semantic binding.
    /// </summary>
    /// <param name="binding">Value binding to reference.</param>
    /// <returns>A whole-binding expression.</returns>
    public static Expr BoundValue(ValueBindingId binding) => new BindingExpr(binding);

    /// <summary>
    /// Expression that references a source field/path.
    /// </summary>
    /// <param name="path">Field path within the current value binding.</param>
    /// <returns>An unqualified field expression.</returns>
    public static Expr Field(FieldPath path) => new FieldExpr(path);

    /// <summary>
    /// Expression that references a field/path from a specific value binding.
    /// </summary>
    /// <param name="binding">Value binding containing the field.</param>
    /// <param name="path">Field path within the bound value.</param>
    /// <returns>A binding-qualified field expression.</returns>
    public static Expr Field(ValueBindingId binding, FieldPath path) => new FieldExpr(path, binding);

    /// <summary>
    /// Expression that references a source field/path.
    /// </summary>
    /// <param name="path">Dotted field path within the current value binding.</param>
    /// <returns>An unqualified field expression.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="path"/> is empty, consists only of white-space characters, or contains no field segments.
    /// </exception>
    public static Expr Field(string path) => Field(FieldPath.Parse(path));

    /// <summary>
    /// Expression that references a field/path from a specific value binding.
    /// </summary>
    /// <param name="binding">Value binding containing the field.</param>
    /// <param name="path">Dotted field path within the bound value.</param>
    /// <returns>A binding-qualified field expression.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="path"/> is empty, consists only of white-space characters, or contains no field segments.
    /// </exception>
    public static Expr Field(ValueBindingId binding, string path) => Field(binding, FieldPath.Parse(path));

    /// <summary>
    /// Expression that references the current item while iterating a collection.
    /// </summary>
    public static Expr CurrentItem() => new CurrentItemExpr();

    /// <summary>
    /// Expression that references a named parameter in the current evaluation context.
    /// </summary>
    public static Expr Param(string name) => new ParameterExpr(Guard.RequireNotNullOrWhiteSpace(name));

    /// <summary>
    /// Constant value expression.
    /// </summary>
    public static Expr Const(ObservationValue value) => new ConstantExpr(value);

    /// <summary>
    /// Constant value expression.
    /// </summary>
    public static Expr Const(string value) => new ConstantExpr(ObservationValue.FromString(value));

    /// <summary>Creates a constant expression.</summary>
    public static Expr Const(int value) => new ConstantExpr(ObservationValue.FromInt64(value));

    /// <summary>Creates a constant expression.</summary>
    public static Expr Const(long value) => new ConstantExpr(ObservationValue.FromInt64(value));

    /// <summary>Creates a constant expression.</summary>
    public static Expr Const(decimal value) => new ConstantExpr(ObservationValue.FromDecimal(value));

    /// <summary>Creates a constant expression.</summary>
    public static Expr Const(double value) => new ConstantExpr(ObservationValue.FromDouble(value));

    /// <summary>Creates a constant expression.</summary>
    public static Expr Const(bool value) => new ConstantExpr(ObservationValue.FromBool(value));

    /// <summary>Creates a constant expression.</summary>
    public static Expr Const(Guid value) => new ConstantExpr(ObservationValue.FromString(value.ToString()));

    /// <summary>Creates a constant expression.</summary>
    public static Expr Const(DateTimeOffset value) => new ConstantExpr(ObservationValue.FromString(value.ToString("O")));

    /// <summary>Creates a null constant expression.</summary>
    public static Expr Null() => new ConstantExpr(ObservationValue.Null);

    /// <summary>Creates an equality expression.</summary>
    public static Expr Eq(Expr left, Expr right) => new BinaryExpr(BinaryOperator.Eq, left, right);

    /// <summary>Creates an inequality expression.</summary>
    public static Expr Ne(Expr left, Expr right) => new BinaryExpr(BinaryOperator.Ne, left, right);

    /// <summary>Creates a greater-than expression.</summary>
    public static Expr Gt(Expr left, Expr right) => new BinaryExpr(BinaryOperator.Gt, left, right);

    /// <summary>Creates a greater-than-or-equal expression.</summary>
    public static Expr Ge(Expr left, Expr right) => new BinaryExpr(BinaryOperator.Ge, left, right);

    /// <summary>Creates a less-than expression.</summary>
    public static Expr Lt(Expr left, Expr right) => new BinaryExpr(BinaryOperator.Lt, left, right);

    /// <summary>Creates a less-than-or-equal expression.</summary>
    public static Expr Le(Expr left, Expr right) => new BinaryExpr(BinaryOperator.Le, left, right);

    /// <summary>Creates a logical-and expression.</summary>
    public static Expr And(Expr left, Expr right) => new BinaryExpr(BinaryOperator.And, left, right);

    /// <summary>Creates a logical-or expression.</summary>
    public static Expr Or(Expr left, Expr right) => new BinaryExpr(BinaryOperator.Or, left, right);

    /// <summary>Creates an addition expression.</summary>
    public static Expr Add(Expr left, Expr right) => new BinaryExpr(BinaryOperator.Add, left, right);

    /// <summary>Creates a subtraction expression.</summary>
    public static Expr Sub(Expr left, Expr right) => new BinaryExpr(BinaryOperator.Sub, left, right);

    /// <summary>Creates a multiplication expression.</summary>
    public static Expr Mul(Expr left, Expr right) => new BinaryExpr(BinaryOperator.Mul, left, right);

    /// <summary>Creates a division expression.</summary>
    public static Expr Div(Expr left, Expr right) => new BinaryExpr(BinaryOperator.Div, left, right);

    /// <summary>Creates a logical-negation expression.</summary>
    public static Expr Not(Expr operand) => new UnaryExpr(UnaryOperator.Not, operand);

    /// <summary>
    /// Creates an ordinal, case-sensitive text-suffix predicate.
    /// </summary>
    /// <param name="value">Text value whose suffix is tested.</param>
    /// <param name="suffix">Text suffix required at the end of <paramref name="value"/>.</param>
    /// <returns>A Boolean expression that is true when <paramref name="value"/> ends with <paramref name="suffix"/>.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="value"/> or <paramref name="suffix"/> is <see langword="null"/>.
    /// </exception>
    public static Expr EndsWith(Expr value, Expr suffix) =>
        Call(
            ExprFunctionNames.EndsWith,
            Guard.RequireNotNull(value),
            Guard.RequireNotNull(suffix));

    /// <summary>Creates an ordinal, case-sensitive text-prefix predicate.</summary>
    /// <param name="value">Text value whose prefix is tested.</param>
    /// <param name="prefix">Text prefix required at the start of <paramref name="value"/>.</param>
    /// <returns>A Boolean expression that is true when <paramref name="value"/> starts with <paramref name="prefix"/>.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="value"/> or <paramref name="prefix"/> is <see langword="null"/>.
    /// </exception>
    public static Expr StartsWith(Expr value, Expr prefix) =>
        Call(
            ExprFunctionNames.StartsWith,
            Guard.RequireNotNull(value),
            Guard.RequireNotNull(prefix));

    /// <summary>Creates an ordinal, case-sensitive text-substring predicate.</summary>
    /// <param name="value">Text value searched for the substring.</param>
    /// <param name="substring">Text substring searched for in <paramref name="value"/>.</param>
    /// <returns>A Boolean expression that is true when <paramref name="value"/> contains <paramref name="substring"/>.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="value"/> or <paramref name="substring"/> is <see langword="null"/>.
    /// </exception>
    public static Expr TextContains(Expr value, Expr substring) =>
        Call(
            ExprFunctionNames.TextContains,
            Guard.RequireNotNull(value),
            Guard.RequireNotNull(substring));

    /// <summary>
    /// Creates a collection-membership predicate using canonical value equality.
    /// </summary>
    /// <param name="collection">Collection whose elements are searched.</param>
    /// <param name="value">Value compared with each collection element.</param>
    /// <returns>A Boolean expression that is true when <paramref name="collection"/> contains <paramref name="value"/>.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="collection"/> or <paramref name="value"/> is <see langword="null"/>.
    /// </exception>
    public static Expr Contains(Expr collection, Expr value) =>
        Call(
            ExprFunctionNames.Contains,
            Guard.RequireNotNull(collection),
            Guard.RequireNotNull(value));

    /// <summary>
    /// Creates a scoped collection existential predicate.
    /// </summary>
    /// <remarks>
    /// <paramref name="predicate"/> is evaluated once for each element of <paramref name="collection"/> in an
    /// isolated current-item scope. Use <see cref="CurrentItem()"/> for the complete element or a field path rooted
    /// at <see cref="Expressions.ExprFieldRoots.CurrentItem"/> for an element field. All predicate reads in one
    /// evaluation are correlated to the same collection element. Canonical execution requires the collection to
    /// produce a present, non-null array and the predicate to produce a present, non-null Boolean for every evaluated
    /// element. A missing, null, or non-array collection and a missing, null, or non-Boolean predicate result are
    /// evaluation failures; they are not coerced to an empty collection or <see langword="false"/>.
    /// </remarks>
    /// <param name="collection">Collection whose elements are tested.</param>
    /// <param name="predicate">Required Boolean predicate evaluated against each current element.</param>
    /// <returns>
    /// A Boolean expression that is true when the predicate is true for at least one element; an empty collection
    /// produces false.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="collection"/> or <paramref name="predicate"/> is <see langword="null"/>.
    /// </exception>
    public static Expr Any(Expr collection, Expr predicate) =>
        Call(
            ExprFunctionNames.Any,
            Guard.RequireNotNull(collection),
            Guard.RequireNotNull(predicate));

    /// <summary>Creates a conditional expression.</summary>
    public static Expr If(Expr test, Expr ifTrue, Expr ifFalse) => new ConditionalExpr(test, ifTrue, ifFalse);

    /// <summary>
    /// Creates a function-call expression.
    /// </summary>
    public static Expr Call(string function, params Expr[] arguments) => new CallExpr(function, [.. arguments]);

    /// <summary>Creates a join expression.</summary>
    public static Expr Join(Expr leftKey, Expr rightKey, Expr rightCollection) =>
        Call(function: ExprFunctionNames.Join, leftKey, rightKey, rightCollection);

    /// <summary>Creates a grouping expression.</summary>
    public static Expr GroupItems(Expr source, Expr keySelector) =>
        Call(function: ExprFunctionNames.GroupBy, source, keySelector);

    /// <summary>Creates an aggregate expression.</summary>
    public static Expr Aggregate(string aggregate, Expr source, params Expr[] args) =>
        Call(function: aggregate, [source, .. args]);
}

/// <summary>
/// Field reference expression with an explicit resulting type.
/// </summary>
public sealed record FieldRefExpr : Expr
{
    /// <summary>
    /// Creates a field reference expression.
    /// </summary>
    public FieldRefExpr(FieldPath path, TypeRef type)
    {
        Path = path;
        Type = Guard.RequireNotNull(type);
    }

    /// <summary>
    /// Referenced field path.
    /// </summary>
    public FieldPath Path { get; init; }

    /// <summary>
    /// Resulting field type.
    /// </summary>
    public TypeRef Type { get; init; }
}

/// <summary>
/// Typed literal expression.
/// </summary>
public sealed record LiteralExpr : Expr
{
    /// <summary>
    /// Creates a literal expression.
    /// </summary>
    public LiteralExpr(TypeRef type, ObservationValue value)
    {
        Type = type;
        Value = value;
    }

    /// <summary>
    /// Literal type.
    /// </summary>
    public TypeRef Type { get; init; }

    /// <summary>
    /// Literal value.
    /// </summary>
    public ObservationValue Value { get; init; }
}

/// <summary>
/// Aggregate operators supported by relation expressions.
/// </summary>
public enum AggregateOperator
{
    /// <summary>Represents the count option.</summary>
    Count = 0,
    /// <summary>Represents the sum option.</summary>
    Sum = 1,
    /// <summary>Represents the min option.</summary>
    Min = 2,
    /// <summary>Represents the max option.</summary>
    Max = 3,
    /// <summary>Represents the any option.</summary>
    Any = 4,
    /// <summary>Represents the all option.</summary>
    All = 5,
    /// <summary>Represents the arithmetic average option in the canonical decimal result domain.</summary>
    Average = 6
}

/// <summary>
/// Aggregate expression node.
/// </summary>
public sealed record AggregateExpr : Expr
{
    /// <summary>
    /// Creates an aggregate expression.
    /// </summary>
    /// <param name="operator">Aggregate operation.</param>
    /// <param name="source">Collection expression supplying aggregate inputs.</param>
    /// <param name="returnType">Declared aggregate result type.</param>
    /// <param name="groupBy">Optional grouping expressions.</param>
    public AggregateExpr(
        AggregateOperator @operator,
        Expr source,
        TypeRef returnType,
        ImmutableArray<Expr> groupBy = default
        )
    {
        Operator = @operator;
        Source = Guard.RequireNotNull(source);
        ReturnType = Guard.RequireNotNull(returnType);
        GroupBy = groupBy.IsDefault ? [] : groupBy;
    }

    /// <summary>
    /// Aggregate operator.
    /// </summary>
    public AggregateOperator Operator { get; init; }

    /// <summary>
    /// Aggregate source expression.
    /// </summary>
    public Expr Source { get; init; }

    /// <summary>
    /// Grouping expressions.
    /// </summary>
    public ImmutableArray<Expr> GroupBy { get; init; }

    /// <summary>
    /// Aggregate result type.
    /// </summary>
    public TypeRef ReturnType { get; init; }

    /// <summary>
    /// Compares aggregate expressions using value semantics for grouping expressions.
    /// </summary>
    public bool Equals(AggregateExpr? other)
    {
        if (ReferenceEquals(this, other))
            return true;
        if (other is null)
            return false;

        return Operator == other.Operator
               && Source == other.Source
               && ReturnType == other.ReturnType
               && GroupBy.SequenceEqual(other.GroupBy);
    }

    /// <summary>
    /// Computes a hash code aligned with <see cref="Equals(AggregateExpr?)"/>.
    /// </summary>
    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add((int)Operator);
        hash.Add(Source);
        hash.Add(ReturnType);
        foreach (var groupBy in GroupBy)
            hash.Add(groupBy);
        return hash.ToHashCode();
    }
}

/// <summary>
/// Pure conditional expression.
/// </summary>
public sealed record ConditionalExpr : Expr
{
    /// <summary>
    /// Creates a conditional expression.
    /// </summary>
    [JsonConstructor]
    public ConditionalExpr(Expr test, Expr ifTrue, Expr ifFalse, TypeRef? returnType = null)
    {
        Test = Guard.RequireNotNull(test);
        IfTrue = Guard.RequireNotNull(ifTrue);
        IfFalse = Guard.RequireNotNull(ifFalse);
        ReturnType = returnType ?? new OpaqueRuntimeTypeRef("unknown");
    }

    /// <summary>
    /// Test expression.
    /// </summary>
    public Expr Test { get; init; }

    /// <summary>
    /// Value expression when <see cref="Test"/> is true.
    /// </summary>
    public Expr IfTrue { get; init; }

    /// <summary>
    /// Value expression when <see cref="Test"/> is false.
    /// </summary>
    public Expr IfFalse { get; init; }

    /// <summary>
    /// Result type.
    /// </summary>
    public TypeRef ReturnType { get; init; }
}

/// <summary>
/// Expression that references the complete value of a named semantic binding.
/// </summary>
/// <param name="Binding">Value binding whose complete value is returned.</param>
public sealed record BindingExpr(ValueBindingId Binding) : Expr;

/// <summary>
/// Expression that references a source field/path, optionally qualified by a value binding.
/// </summary>
/// <param name="Path">Field path within the bound value.</param>
/// <param name="Binding">Optional binding that disambiguates the source value.</param>
public sealed record FieldExpr(
    FieldPath Path,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ValueBindingId? Binding = null
    ) : Expr;

/// <summary>
/// Expression that references the current item while iterating a collection.
/// </summary>
public sealed record CurrentItemExpr() : Expr;

/// <summary>
/// Expression that references a named parameter in the current evaluation context.
/// </summary>
public sealed record ParameterExpr(string Parameter) : Expr;

/// <summary>
/// Constant JSON value expression.
/// </summary>
public sealed record ConstantExpr(ObservationValue Value) : Expr;

/// <summary>
/// Supported unary operators.
/// </summary>
public enum UnaryOperator
{
    /// <summary>Represents the not option.</summary>
    Not = 0
}

/// <summary>
/// Supported binary operators.
/// </summary>
public enum BinaryOperator
{
    /// <summary>Represents the eq option.</summary>
    Eq = 0,
    /// <summary>Represents the ne option.</summary>
    Ne = 1,
    /// <summary>Represents the gt option.</summary>
    Gt = 2,
    /// <summary>Represents the ge option.</summary>
    Ge = 3,
    /// <summary>Represents the lt option.</summary>
    Lt = 4,
    /// <summary>Represents the le option.</summary>
    Le = 5,
    /// <summary>Represents the and option.</summary>
    And = 6,
    /// <summary>Represents the or option.</summary>
    Or = 7,
    /// <summary>Represents the add option.</summary>
    Add = 8,
    /// <summary>Represents the sub option.</summary>
    Sub = 9,
    /// <summary>Represents the mul option.</summary>
    Mul = 10,
    /// <summary>Represents the div option.</summary>
    Div = 11
}

/// <summary>
/// Unary expression node.
/// </summary>
public sealed record UnaryExpr(UnaryOperator Operator, Expr Operand) : Expr;

/// <summary>
/// Binary expression node.
/// </summary>
public sealed record BinaryExpr(BinaryOperator Operator, Expr Left, Expr Right) : Expr;

/// <summary>
/// Pure function-call expression.
/// </summary>
public sealed record CallExpr : Expr
{
    /// <summary>
    /// Creates a function-call expression.
    /// </summary>
    [JsonConstructor]
    public CallExpr(string function, ImmutableArray<Expr> arguments, TypeRef? returnType = null)
    {
        Function = Guard.RequireNotNullOrWhiteSpace(function);
        Arguments = arguments.IsDefault ? [] : arguments;
        ReturnType = returnType ?? new OpaqueRuntimeTypeRef("unknown");
    }

    /// <summary>
    /// Function identifier.
    /// </summary>
    public string Function { get; init; }

    /// <summary>
    /// Function arguments.
    /// </summary>
    public ImmutableArray<Expr> Arguments { get; init; }

    /// <summary>
    /// Expression return type.
    /// </summary>
    public TypeRef ReturnType { get; init; }

    /// <summary>
    /// Compares call expressions using value semantics for arguments.
    /// </summary>
    public bool Equals(CallExpr? other)
    {
        if (ReferenceEquals(this, other))
            return true;
        if (other is null)
            return false;

        return Function == other.Function
               && ReturnType == other.ReturnType
               && Arguments.SequenceEqual(other.Arguments);
    }

    /// <summary>
    /// Computes a hash code aligned with <see cref="Equals(CallExpr?)"/>.
    /// </summary>
    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(Function, StringComparer.Ordinal);
        hash.Add(ReturnType);
        foreach (var argument in Arguments)
            hash.Add(argument);
        return hash.ToHashCode();
    }
}
