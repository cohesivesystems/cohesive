using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Cohesive.Model;

/// <summary>
/// Shared semantic expression IR used by relations and transitions.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$expr")]
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
    /// Expression that references a source field/path.
    /// </summary>
    /// <param name="path"></param>
    /// <returns></returns>
    public static Expr Field(FieldPath path) => new FieldExpr(path);
    
    /// <summary>
    /// Expression that references a source field/path.
    /// </summary>
    public static Expr Field(string path) => Field(FieldPath.Parse(path));

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

    public static Expr Const(int value) => new ConstantExpr(ObservationValue.FromInt64(value));

    public static Expr Const(long value) => new ConstantExpr(ObservationValue.FromInt64(value));

    public static Expr Const(decimal value) => new ConstantExpr(ObservationValue.FromDouble((double)value));

    public static Expr Const(double value) => new ConstantExpr(ObservationValue.FromDouble(value));

    public static Expr Const(bool value) => new ConstantExpr(ObservationValue.FromBool(value));

    public static Expr Const(Guid value) => new ConstantExpr(ObservationValue.FromString(value.ToString()));

    public static Expr Const(DateTimeOffset value) => new ConstantExpr(ObservationValue.FromString(value.ToString("O")));

    public static Expr Null() => new ConstantExpr(ObservationValue.Null);

    public static Expr Eq(Expr left, Expr right) => new BinaryExpr(BinaryOperator.Eq, left, right);

    public static Expr Ne(Expr left, Expr right) => new BinaryExpr(BinaryOperator.Ne, left, right);

    public static Expr Gt(Expr left, Expr right) => new BinaryExpr(BinaryOperator.Gt, left, right);

    public static Expr Ge(Expr left, Expr right) => new BinaryExpr(BinaryOperator.Ge, left, right);

    public static Expr Lt(Expr left, Expr right) => new BinaryExpr(BinaryOperator.Lt, left, right);

    public static Expr Le(Expr left, Expr right) => new BinaryExpr(BinaryOperator.Le, left, right);

    public static Expr And(Expr left, Expr right) => new BinaryExpr(BinaryOperator.And, left, right);

    public static Expr Or(Expr left, Expr right) => new BinaryExpr(BinaryOperator.Or, left, right);

    public static Expr Add(Expr left, Expr right) => new BinaryExpr(BinaryOperator.Add, left, right);

    public static Expr Sub(Expr left, Expr right) => new BinaryExpr(BinaryOperator.Sub, left, right);

    public static Expr Mul(Expr left, Expr right) => new BinaryExpr(BinaryOperator.Mul, left, right);

    public static Expr Div(Expr left, Expr right) => new BinaryExpr(BinaryOperator.Div, left, right);

    public static Expr Not(Expr operand) => new UnaryExpr(UnaryOperator.Not, operand);

    public static Expr If(Expr test, Expr ifTrue, Expr ifFalse) => new ConditionalExpr(test, ifTrue, ifFalse);

    /// <summary>
    /// Creates a function-call expression.
    /// </summary>
    public static Expr Call(string function, params Expr[] arguments) => new CallExpr(function, [.. arguments]);

    public static Expr Join(Expr leftKey, Expr rightKey, Expr rightCollection) =>
        Call(function: ExprFunctionNames.Join, leftKey, rightKey, rightCollection);

    public static Expr GroupItems(Expr source, Expr keySelector) =>
        Call(function: ExprFunctionNames.GroupBy, source, keySelector);

    public static Expr Aggregate(string aggregate, Expr source, params Expr[] args) =>
        Call(function: aggregate, [source, .. args]);

    public static Expr RelatedField(Expr schema, Expr entityOrKey, Expr fieldName) =>
        Call(function: ExprFunctionNames.RelatedField, schema, entityOrKey, fieldName);
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
    Count = 0,
    Sum = 1,
    Min = 2,
    Max = 3,
    Any = 4,
    All = 5
}

/// <summary>
/// Aggregate expression node.
/// </summary>
public sealed record AggregateExpr : Expr
{
    /// <summary>
    /// Creates an aggregate expression.
    /// </summary>
    public AggregateExpr(
        AggregateOperator op,
        Expr source,
        TypeRef returnType,
        ImmutableArray<Expr> groupBy = default
        )
    {
        Operator = op;
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
/// Expression that references a source field/path.
/// </summary>
public sealed record FieldExpr(FieldPath Path) : Expr;

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
    Not = 0
}

/// <summary>
/// Supported binary operators.
/// </summary>
public enum BinaryOperator
{
    Eq = 0,
    Ne = 1,
    Gt = 2,
    Ge = 3,
    Lt = 4,
    Le = 5,
    And = 6,
    Or = 7,
    Add = 8,
    Sub = 9,
    Mul = 10,
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
