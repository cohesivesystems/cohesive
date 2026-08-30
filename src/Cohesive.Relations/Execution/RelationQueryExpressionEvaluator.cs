using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Cohesive.Model.Expressions;
using Cohesive.Relations.Diagnostics;

[assembly: InternalsVisibleTo("Cohesive.Relations.Tests")]
[assembly: InternalsVisibleTo("Cohesive.Relations.Benchmarks")]

namespace Cohesive.Relations.Execution;

enum RelationQueryExpressionEvaluationError
{
    UnsupportedExpression = 0,
    UnsupportedFieldPath = 1,
    UnsupportedFunction = 2,
    InvalidFunctionArity = 3,
    InvalidOperand = 4,
    NumericFailure = 5,
    EvaluationContextUnavailable = 6,
    RuntimeInputUnavailable = 7
}

sealed class RelationQueryExpressionEvaluationException(
    RelationQueryExpressionEvaluationError error,
    string message,
    Exception? innerException = null)
    : InvalidOperationException(message, innerException)
{
    public RelationQueryExpressionEvaluationError Error { get; } = error;
}

readonly record struct RelationQueryExpressionBinding
{
    public RelationQueryExpressionBinding(
        ObservationValue value,
        RelationQueryOccurrenceId? occurrence = null,
        string? observationIdentity = null)
        : this(
            isPresent: true,
            value,
            occurrence,
            observationIdentity)
    {
    }

    RelationQueryExpressionBinding(
        bool isPresent,
        ObservationValue value,
        RelationQueryOccurrenceId? occurrence,
        string? observationIdentity)
    {
        if (observationIdentity is not null)
            ArgumentException.ThrowIfNullOrWhiteSpace(observationIdentity);

        IsPresent = isPresent;
        Value = isPresent ? value : ObservationValue.Undefined;
        Occurrence = isPresent ? occurrence : null;
        ObservationIdentity = isPresent ? observationIdentity : null;
    }

    public static RelationQueryExpressionBinding Absent { get; } = new(
        isPresent: false,
        ObservationValue.Undefined,
        occurrence: null,
        observationIdentity: null);

    public bool IsPresent { get; }

    public ObservationValue Value { get; }

    public RelationQueryOccurrenceId? Occurrence { get; }

    public string? ObservationIdentity { get; }
}

/// <summary>
/// Resolves evaluation-scoped availability without allocating row-capturing delegates for every expression site.
/// </summary>
interface IRelationQueryExpressionRuntimeAvailability
{
    /// <summary>Determines whether one field can be read from the supplied runtime row.</summary>
    /// <param name="row">Runtime row being evaluated.</param>
    /// <param name="binding">Binding that owns the field.</param>
    /// <param name="path">Canonical field path.</param>
    /// <returns><see langword="true"/> when the field may be evaluated; otherwise, <see langword="false"/>.</returns>
    bool IsFieldAvailable(
        RelationQueryRuntimeRow row,
        ValueBindingId binding,
        FieldPath path);

    /// <summary>Determines whether one named parameter can be evaluated.</summary>
    /// <param name="parameter">Canonical parameter name.</param>
    /// <returns><see langword="true"/> when the parameter may be evaluated; otherwise, <see langword="false"/>.</returns>
    bool IsParameterAvailable(string parameter);

    /// <summary>Determines whether one ambient expression capability is available.</summary>
    /// <param name="capability">Canonical expression capability.</param>
    /// <returns><see langword="true"/> when the capability is available; otherwise, <see langword="false"/>.</returns>
    bool IsCapabilityAvailable(ExprCapabilityId capability);
}

/// <summary>Immutable stores and policies used during one expression evaluation.</summary>
readonly struct RelationQueryExpressionContext
{
    static readonly IReadOnlyDictionary<ValueBindingId, RelationQueryExpressionBinding> EmptyBindings =
        ImmutableDictionary<ValueBindingId, RelationQueryExpressionBinding>.Empty;
    static readonly IReadOnlyDictionary<string, ObservationValue> EmptyParameters =
        ImmutableDictionary.Create<string, ObservationValue>(StringComparer.Ordinal);
    static readonly IReadOnlyList<ObservationValue> EmptySourceRows = [];
    static readonly Func<ValueBindingId, FieldPath, bool> AllFieldsAvailable = static (_, _) => true;
    static readonly Func<string, bool> AllParametersAvailable = static _ => true;
    static readonly Func<ExprCapabilityId, bool> AllCapabilitiesAvailable = static _ => true;
    readonly IReadOnlyDictionary<ValueBindingId, RelationQueryExpressionBinding>? bindings;
    readonly IReadOnlyDictionary<string, ObservationValue>? parameters;
    readonly IReadOnlyList<ObservationValue>? sourceRows;
    readonly Func<ValueBindingId, FieldPath, bool>? isFieldAvailable;
    readonly Func<string, bool>? isParameterAvailable;
    readonly Func<ExprCapabilityId, bool>? isCapabilityAvailable;
    readonly IRelationQueryExpressionRuntimeAvailability? runtimeAvailability;
    readonly RelationQueryRuntimeRow? runtimeRow;

    public RelationQueryExpressionContext()
        : this(bindings: null)
    {
    }

    public RelationQueryExpressionContext(
        IReadOnlyDictionary<ValueBindingId, RelationQueryExpressionBinding>? bindings,
        ValueBindingId? implicitBinding = null,
        IReadOnlyDictionary<string, ObservationValue>? parameters = null,
        ObservationValue? currentItem = null,
        string? rootIdentity = null,
        IReadOnlyList<ObservationValue>? sourceRows = null,
        Func<ValueBindingId, FieldPath, bool>? isFieldAvailable = null,
        Func<string, bool>? isParameterAvailable = null,
        Func<ExprCapabilityId, bool>? isCapabilityAvailable = null)
    {
        if (implicitBinding is { } selectedImplicitBinding
            && string.IsNullOrWhiteSpace(selectedImplicitBinding.Value))
            throw new ArgumentException("An implicit binding must have a non-empty identity.", nameof(implicitBinding));
        if (rootIdentity is not null)
            ArgumentException.ThrowIfNullOrWhiteSpace(rootIdentity);

        Dictionary<ValueBindingId, RelationQueryExpressionBinding> bindingValues = [];
        if (bindings is not null)
        {
            foreach (var (binding, value) in bindings)
            {
                if (string.IsNullOrWhiteSpace(binding.Value))
                    throw new ArgumentException("Expression bindings must have non-empty identities.", nameof(bindings));
                bindingValues.Add(binding, value);
            }
        }

        Dictionary<string, ObservationValue> parameterValues = new(StringComparer.Ordinal);
        if (parameters is not null)
        {
            foreach (var (name, value) in parameters)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(name);
                parameterValues.Add(name, value);
            }
        }

        this.bindings = new ReadOnlyDictionary<ValueBindingId, RelationQueryExpressionBinding>(bindingValues);
        ImplicitBinding = implicitBinding;
        this.parameters = new ReadOnlyDictionary<string, ObservationValue>(parameterValues);
        CurrentItem = currentItem;
        RootIdentity = rootIdentity;
        this.sourceRows = sourceRows is null ? [] : [.. sourceRows];
        this.isFieldAvailable = isFieldAvailable ?? AllFieldsAvailable;
        this.isParameterAvailable = isParameterAvailable ?? AllParametersAvailable;
        this.isCapabilityAvailable = isCapabilityAvailable ?? AllCapabilitiesAvailable;
        runtimeAvailability = null;
        runtimeRow = null;
    }

    RelationQueryExpressionContext(
        IReadOnlyDictionary<ValueBindingId, RelationQueryExpressionBinding> bindings,
        IReadOnlyDictionary<string, ObservationValue> parameters,
        IReadOnlyList<ObservationValue> sourceRows,
        ValueBindingId? implicitBinding,
        ObservationValue? currentItem,
        string? rootIdentity,
        Func<ValueBindingId, FieldPath, bool> isFieldAvailable,
        Func<string, bool> isParameterAvailable,
        Func<ExprCapabilityId, bool> isCapabilityAvailable,
        IRelationQueryExpressionRuntimeAvailability? runtimeAvailability = null,
        RelationQueryRuntimeRow? runtimeRow = null)
    {
        this.bindings = bindings;
        ImplicitBinding = implicitBinding;
        this.parameters = parameters;
        CurrentItem = currentItem;
        RootIdentity = rootIdentity;
        this.sourceRows = sourceRows;
        this.isFieldAvailable = isFieldAvailable;
        this.isParameterAvailable = isParameterAvailable;
        this.isCapabilityAvailable = isCapabilityAvailable;
        this.runtimeAvailability = runtimeAvailability;
        this.runtimeRow = runtimeRow;
    }

    RelationQueryExpressionContext(
        in RelationQueryExpressionContext source,
        ObservationValue currentItem)
    {
        bindings = source.Bindings;
        ImplicitBinding = source.ImplicitBinding;
        parameters = source.Parameters;
        CurrentItem = currentItem;
        RootIdentity = source.RootIdentity;
        sourceRows = source.SourceRows;
        isFieldAvailable = source.isFieldAvailable ?? AllFieldsAvailable;
        isParameterAvailable = source.isParameterAvailable ?? AllParametersAvailable;
        isCapabilityAvailable = source.isCapabilityAvailable ?? AllCapabilitiesAvailable;
        runtimeAvailability = source.runtimeAvailability;
        runtimeRow = source.runtimeRow;
    }

    public IReadOnlyDictionary<ValueBindingId, RelationQueryExpressionBinding> Bindings =>
        bindings ?? EmptyBindings;

    public ValueBindingId? ImplicitBinding { get; }

    public IReadOnlyDictionary<string, ObservationValue> Parameters =>
        parameters ?? EmptyParameters;

    public ObservationValue? CurrentItem { get; }

    public string? RootIdentity { get; }

    public IReadOnlyList<ObservationValue> SourceRows =>
        sourceRows ?? EmptySourceRows;

    public bool IsFieldAvailable(ValueBindingId binding, FieldPath path) =>
        runtimeAvailability is null
            ? (isFieldAvailable ?? AllFieldsAvailable)(binding, path)
            : runtimeAvailability.IsFieldAvailable(runtimeRow!, binding, path);

    public bool IsParameterAvailable(string parameter) =>
        runtimeAvailability is null
            ? (isParameterAvailable ?? AllParametersAvailable)(parameter)
            : runtimeAvailability.IsParameterAvailable(parameter);

    public bool IsCapabilityAvailable(ExprCapabilityId capability) =>
        runtimeAvailability is null
            ? (isCapabilityAvailable ?? AllCapabilitiesAvailable)(capability)
            : runtimeAvailability.IsCapabilityAvailable(capability);

    /// <summary>
    /// Creates a context over immutable or execution-owned stores whose identities and values were already
    /// validated at their owning boundaries.
    /// </summary>
    /// <param name="bindings">Validated binding store retained by the context.</param>
    /// <param name="implicitBinding">Optional binding used by unqualified field expressions.</param>
    /// <param name="parameters">Optional execution-owned parameter store retained by the context.</param>
    /// <param name="currentItem">Optional current collection item.</param>
    /// <param name="rootIdentity">Optional observation identity of the relation root.</param>
    /// <param name="sourceRows">Optional execution-owned source-row store retained by the context.</param>
    /// <param name="isFieldAvailable">Optional field-availability policy.</param>
    /// <param name="isParameterAvailable">Optional parameter-availability policy.</param>
    /// <param name="isCapabilityAvailable">Optional expression-capability policy.</param>
    /// <returns>A context that does not copy the supplied execution-owned stores.</returns>
    internal static RelationQueryExpressionContext FromPrevalidated(
        IReadOnlyDictionary<ValueBindingId, RelationQueryExpressionBinding> bindings,
        ValueBindingId? implicitBinding = null,
        IReadOnlyDictionary<string, ObservationValue>? parameters = null,
        ObservationValue? currentItem = null,
        string? rootIdentity = null,
        IReadOnlyList<ObservationValue>? sourceRows = null,
        Func<ValueBindingId, FieldPath, bool>? isFieldAvailable = null,
        Func<string, bool>? isParameterAvailable = null,
        Func<ExprCapabilityId, bool>? isCapabilityAvailable = null)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        return new(
            bindings,
            parameters ?? EmptyParameters,
            sourceRows ?? EmptySourceRows,
            implicitBinding,
            currentItem,
            rootIdentity,
            isFieldAvailable ?? AllFieldsAvailable,
            isParameterAvailable ?? AllParametersAvailable,
            isCapabilityAvailable ?? AllCapabilitiesAvailable);
    }

    /// <summary>
    /// Creates an allocation-free context over one execution-owned row and the engine that owns runtime
    /// availability decisions.
    /// </summary>
    /// <param name="row">Execution-owned row whose bindings and provenance are being evaluated.</param>
    /// <param name="implicitBinding">Optional binding used by unqualified field expressions.</param>
    /// <param name="parameters">Execution-owned effective parameter values.</param>
    /// <param name="availability">Execution engine that resolves runtime field, parameter, and capability availability.</param>
    /// <returns>A stack context that retains the execution-owned stores without allocating delegates.</returns>
    internal static RelationQueryExpressionContext FromExecution(
        RelationQueryRuntimeRow row,
        ValueBindingId? implicitBinding,
        IReadOnlyDictionary<string, ObservationValue> parameters,
        IRelationQueryExpressionRuntimeAvailability availability)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(availability);
        return new(
            row.ExpressionBindings,
            parameters,
            EmptySourceRows,
            implicitBinding,
            currentItem: null,
            row.Root?.ObservationIdentity,
            AllFieldsAvailable,
            AllParametersAvailable,
            AllCapabilitiesAvailable,
            availability,
            row);
    }

    public RelationQueryExpressionContext WithCurrentItem(ObservationValue item) => new(this, item);
}

sealed class RelationQueryExpressionEvaluator
{
    static readonly ImmutableHashSet<string> SupportedFunctionNames =
        ImmutableHashSet.Create(
            StringComparer.Ordinal,
            ExprFunctionNames.Contains,
            ExprFunctionNames.Count,
            ExprFunctionNames.EndsWith,
            ExprFunctionNames.StartsWith,
            ExprFunctionNames.TextContains,
            ExprFunctionNames.Object,
            ExprFunctionNames.Select,
            ExprFunctionNames.Append,
            ExprFunctionNames.AppendRange,
            ExprFunctionNames.InsertAt,
            ExprFunctionNames.InsertRangeAt,
            ExprFunctionNames.Concat,
            ExprFunctionNames.Sum,
            ExprFunctionNames.Min,
            ExprFunctionNames.Max,
            ExprFunctionNames.Avg,
            ExprFunctionNames.Any,
            ExprFunctionNames.All);

    internal static ExprCapabilityProfile SupportedCapabilities { get; } =
        CreateSupportedCapabilities();

    internal static bool SupportsFieldPath(FieldPath path) =>
        !path.Segments.Any(static segment => segment.Kind == SegmentKind.Element);

    public ObservationValue Evaluate(Expr expression, in RelationQueryExpressionContext context)
    {
        ArgumentNullException.ThrowIfNull(expression);
        RequireCapabilities(expression, context);

        return expression switch
        {
            ConstantExpr constant => constant.Value,
            LiteralExpr literal => literal.Value,
            FieldExpr field => EvaluateField(field.Path, field.Binding, context),
            FieldRefExpr field => EvaluateField(field.Path, explicitBinding: null, context),
            CurrentItemExpr => RequireCurrentItem(context),
            ParameterExpr parameter => EvaluateParameter(parameter, context),
            UnaryExpr unary => EvaluateUnary(unary, context),
            BinaryExpr binary => EvaluateBinary(binary, context),
            ConditionalExpr conditional => EvaluateConditional(conditional, context),
            CallExpr call => EvaluateCall(call, context),
            AggregateExpr aggregate => EvaluateAggregate(aggregate, context),
            _ => throw Failure(
                RelationQueryExpressionEvaluationError.UnsupportedExpression,
                $"Expression node '{expression.GetType().Name}' is not supported by the canonical in-memory evaluator.")
        };
    }

    public ObservationValue Aggregate(AggregateOperator operation, IReadOnlyList<ObservationValue> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return operation switch
        {
            AggregateOperator.Count => ObservationValue.FromInt64(values.Count),
            AggregateOperator.Sum => Sum(values),
            AggregateOperator.Min => MinOrMax(values, findMaximum: false),
            AggregateOperator.Max => MinOrMax(values, findMaximum: true),
            AggregateOperator.Any => Any(values),
            AggregateOperator.All => All(values),
            AggregateOperator.Average => Average(values),
            _ => throw Failure(
                RelationQueryExpressionEvaluationError.UnsupportedExpression,
                $"Aggregate operator '{operation}' is not supported.")
        };
    }

    ObservationValue EvaluateField(
        FieldPath path,
        ValueBindingId? explicitBinding,
        in RelationQueryExpressionContext context)
    {
        if (path.Segments.IsDefaultOrEmpty)
        {
            throw Failure(RelationQueryExpressionEvaluationError.UnsupportedFieldPath, "A field expression requires a non-empty field path.");
        }

        if (explicitBinding is null && path.Segments[0] is { Kind: SegmentKind.Field, Segment: ExprFieldRoots.CurrentItem })
        {
            return ResolvePath(
                RequireCurrentItem(context),
                path,
                startIndex: 1);
        }

        var binding = explicitBinding ?? context.ImplicitBinding;
        if (binding is null)
        {
            throw Failure(
                RelationQueryExpressionEvaluationError.EvaluationContextUnavailable,
                $"Unqualified field path '{path}' has no implicit binding in its evaluation context.");
        }

        if (!context.Bindings.TryGetValue(binding.Value, out var bound) || !bound.IsPresent)
            return ObservationValue.Undefined;

        if (!context.IsFieldAvailable(binding.Value, path))
        {
            throw Failure(
                RelationQueryExpressionEvaluationError.RuntimeInputUnavailable,
                $"Field '{binding.Value.Value}.{path}' is unavailable in the runtime evidence.");
        }

        return ResolvePath(bound.Value, path, startIndex: 0);
    }

    static ObservationValue EvaluateParameter(
        ParameterExpr parameter,
        in RelationQueryExpressionContext context)
    {
        if (!context.IsParameterAvailable(parameter.Parameter))
        {
            throw Failure(
                RelationQueryExpressionEvaluationError.RuntimeInputUnavailable,
                $"Parameter '{parameter.Parameter}' is unavailable in the runtime evidence.");
        }

        return context.Parameters.GetValueOrDefault(
            parameter.Parameter,
            ObservationValue.Undefined);
    }

    static ObservationValue ResolvePath(
        ObservationValue root,
        FieldPath path,
        int startIndex)
    {
        var current = root;
        for (var index = startIndex; index < path.Segments.Length; index++)
        {
            var segment = path.Segments[index];
            if (segment.Kind == SegmentKind.Element)
            {
                throw Failure(
                    RelationQueryExpressionEvaluationError.UnsupportedFieldPath,
                    $"Collection-element field-path segment '{path}' has no scalar expression semantics.");
            }
            if (segment.Kind != SegmentKind.Field || string.IsNullOrWhiteSpace(segment.Segment))
            {
                throw Failure(
                    RelationQueryExpressionEvaluationError.UnsupportedFieldPath,
                    $"Field path '{path}' contains an unsupported segment.");
            }

            if (current.Kind == ObservationValueKind.Undefined)
                return ObservationValue.Undefined;
            if (current.Kind == ObservationValueKind.Null)
                return ObservationValue.Null;
            if (current.Kind != ObservationValueKind.Object || current.Fields is null)
            {
                throw Failure(
                    RelationQueryExpressionEvaluationError.InvalidOperand,
                    $"Field path '{path}' cannot navigate through value kind '{current.Kind}'.");
            }

            if (!TryGetOrdinalProperty(current.Fields, segment.Segment, out current))
                return ObservationValue.Undefined;
        }

        return current;
    }

    ObservationValue EvaluateUnary(
        UnaryExpr unary,
        in RelationQueryExpressionContext context)
    {
        var operand = Evaluate(unary.Operand, context);
        return unary.Operator switch
        {
            UnaryOperator.Not => ObservationValue.FromBool(!RequireBoolean(operand, "logical not")),
            _ => throw Failure(
                RelationQueryExpressionEvaluationError.UnsupportedExpression,
                $"Unary operator '{unary.Operator}' is not supported.")
        };
    }

    ObservationValue EvaluateBinary(
        BinaryExpr binary,
        in RelationQueryExpressionContext context)
    {
        if (binary.Operator == BinaryOperator.And)
        {
            var left = RequireBoolean(Evaluate(binary.Left, context), "logical and left operand");
            return left
                ? ObservationValue.FromBool(RequireBoolean(
                    Evaluate(binary.Right, context),
                    "logical and right operand"))
                : ObservationValue.FromBool(false);
        }

        if (binary.Operator == BinaryOperator.Or)
        {
            var left = RequireBoolean(Evaluate(binary.Left, context), "logical or left operand");
            return left
                ? ObservationValue.FromBool(true)
                : ObservationValue.FromBool(RequireBoolean(
                    Evaluate(binary.Right, context),
                    "logical or right operand"));
        }

        var leftValue = Evaluate(binary.Left, context);
        var rightValue = Evaluate(binary.Right, context);
        return binary.Operator switch
        {
            BinaryOperator.Eq => ObservationValue.FromBool(
                RelationQueryValueSemantics.Equals(leftValue, rightValue)),
            BinaryOperator.Ne => ObservationValue.FromBool(
                !RelationQueryValueSemantics.Equals(leftValue, rightValue)),
            BinaryOperator.Gt => Comparison(leftValue, rightValue, static value => value > 0),
            BinaryOperator.Ge => Comparison(leftValue, rightValue, static value => value >= 0),
            BinaryOperator.Lt => Comparison(leftValue, rightValue, static value => value < 0),
            BinaryOperator.Le => Comparison(leftValue, rightValue, static value => value <= 0),
            BinaryOperator.Add => RelationQueryValueSemantics.Add(leftValue, rightValue),
            BinaryOperator.Sub => RelationQueryValueSemantics.Subtract(leftValue, rightValue),
            BinaryOperator.Mul => RelationQueryValueSemantics.Multiply(leftValue, rightValue),
            BinaryOperator.Div => RelationQueryValueSemantics.Divide(leftValue, rightValue),
            _ => throw Failure(
                RelationQueryExpressionEvaluationError.UnsupportedExpression,
                $"Binary operator '{binary.Operator}' is not supported.")
        };
    }

    ObservationValue EvaluateConditional(
        ConditionalExpr conditional,
        in RelationQueryExpressionContext context)
    {
        var test = RequireBoolean(Evaluate(conditional.Test, context), "conditional test");
        return Evaluate(test ? conditional.IfTrue : conditional.IfFalse, context);
    }

    ObservationValue EvaluateCall(
        CallExpr call,
        in RelationQueryExpressionContext context)
    {
        if (!ExprSemanticsCatalog.Default.TryGetFunction(call.Function, out var definition))
        {
            throw Failure(
                RelationQueryExpressionEvaluationError.UnsupportedFunction,
                $"Expression function '{call.Function}' is not defined by the canonical expression catalog.");
        }
        if (!definition.Arity.Accepts(call.Arguments.Length))
        {
            throw Failure(
                RelationQueryExpressionEvaluationError.InvalidFunctionArity,
                $"Expression function '{call.Function}' expects {definition.Arity.Describe()} arguments but received {call.Arguments.Length}.");
        }
        if (!SupportedFunctionNames.Contains(call.Function))
        {
            throw Failure(
                RelationQueryExpressionEvaluationError.UnsupportedFunction,
                $"Expression function '{call.Function}' is not supported by the canonical in-memory evaluator.");
        }
        foreach (var capability in definition.AmbientCapabilities)
            RequireCapability(capability, context);

        return call.Function switch
        {
            ExprFunctionNames.Contains => EvaluateContains(call, context),
            ExprFunctionNames.Count => EvaluateCount(call, context),
            ExprFunctionNames.EndsWith => EvaluateEndsWith(call, context),
            ExprFunctionNames.StartsWith => EvaluateStartsWith(call, context),
            ExprFunctionNames.TextContains => EvaluateTextContains(call, context),
            ExprFunctionNames.Object => EvaluateObject(call, context),
            ExprFunctionNames.Select => EvaluateSelect(call, context),
            ExprFunctionNames.Append => EvaluateAppend(call, context),
            ExprFunctionNames.AppendRange => EvaluateAppendRange(call, context),
            ExprFunctionNames.InsertAt => EvaluateInsertAt(call, context),
            ExprFunctionNames.InsertRangeAt => EvaluateInsertRangeAt(call, context),
            ExprFunctionNames.Concat => EvaluateConcat(call, context),
            ExprFunctionNames.Sum
                or ExprFunctionNames.Min
                or ExprFunctionNames.Max
                or ExprFunctionNames.Avg
                or ExprFunctionNames.Any
                or ExprFunctionNames.All => EvaluateSequenceAggregate(call, context),
            _ => throw Failure(
                RelationQueryExpressionEvaluationError.UnsupportedFunction,
                $"Expression function '{call.Function}' is not supported by the canonical in-memory evaluator.")
        };
    }

    ObservationValue EvaluateAggregate(
        AggregateExpr aggregate,
        in RelationQueryExpressionContext context)
    {
        if (!aggregate.GroupBy.IsDefaultOrEmpty)
        {
            throw Failure(
                RelationQueryExpressionEvaluationError.UnsupportedExpression,
                "Grouped AggregateExpr evaluation is not defined; use AggregateQueryNode for grouped query semantics.");
        }

        var values = RequireArray(Evaluate(aggregate.Source, context), aggregate.Operator.ToString());
        return Aggregate(aggregate.Operator, values);
    }

    ObservationValue EvaluateContains(CallExpr call, in RelationQueryExpressionContext context)
    {
        var source = RequireArray(Evaluate(call.Arguments[0], context), call.Function);
        var candidate = Evaluate(call.Arguments[1], context);
        return ObservationValue.FromBool(source.Any(item => RelationQueryValueSemantics.Equals(item, candidate)));
    }

    ObservationValue EvaluateCount(CallExpr call, in RelationQueryExpressionContext context)
    {
        var value = Evaluate(call.Arguments[0], context);
        return value.Kind switch
        {
            ObservationValueKind.Array => ObservationValue.FromInt64(value.Array.IsDefault ? 0 : value.Array.Length),
            ObservationValueKind.Object => ObservationValue.FromInt64(value.Fields?.Count ?? 0),
            _ => throw InvalidOperand(
                $"Expression function '{call.Function}' requires an array or object, but received '{value.Kind}'.")
        };
    }

    ObservationValue EvaluateEndsWith(CallExpr call, in RelationQueryExpressionContext context)
    {
        var value = RequireString(Evaluate(call.Arguments[0], context), call.Function);
        var suffix = RequireString(Evaluate(call.Arguments[1], context), call.Function);
        return ObservationValue.FromBool(value.EndsWith(suffix, StringComparison.Ordinal));
    }

    ObservationValue EvaluateStartsWith(CallExpr call, in RelationQueryExpressionContext context)
    {
        var value = RequireString(Evaluate(call.Arguments[0], context), call.Function);
        var prefix = RequireString(Evaluate(call.Arguments[1], context), call.Function);
        return ObservationValue.FromBool(value.StartsWith(prefix, StringComparison.Ordinal));
    }

    ObservationValue EvaluateTextContains(CallExpr call, in RelationQueryExpressionContext context)
    {
        var value = RequireString(Evaluate(call.Arguments[0], context), call.Function);
        var substring = RequireString(Evaluate(call.Arguments[1], context), call.Function);
        return ObservationValue.FromBool(value.Contains(substring, StringComparison.Ordinal));
    }

    ObservationValue EvaluateObject(CallExpr call, in RelationQueryExpressionContext context)
    {
        var fields = ImmutableSortedDictionary.CreateBuilder<string, ObservationValue>(StringComparer.Ordinal);
        for (var index = 0; index < call.Arguments.Length; index += 2)
        {
            var key = Evaluate(call.Arguments[index], context);
            if (key.Kind != ObservationValueKind.String || string.IsNullOrWhiteSpace(key.String))
                throw InvalidOperand("Expression function 'object' requires non-empty string keys.");
            var fieldValue = Evaluate(call.Arguments[index + 1], context);
            if (fields.ContainsKey(key.String))
                throw InvalidOperand($"Expression function 'object' contains duplicate key '{key.String}'.");
            fields.Add(key.String, fieldValue);
        }

        return ObservationValue.FromObject(fields.ToImmutable());
    }

    ObservationValue EvaluateSelect(CallExpr call, in RelationQueryExpressionContext context)
    {
        var source = RequireArray(Evaluate(call.Arguments[0], context), call.Function);
        ObservationValue[] result = new ObservationValue[source.Count];
        for (var index = 0; index < source.Count; index++)
            result[index] = Evaluate(call.Arguments[1], context.WithCurrentItem(source[index]));
        return FromOwnedArray(result);
    }

    ObservationValue EvaluateAppend(CallExpr call, in RelationQueryExpressionContext context)
    {
        var source = RequireArray(Evaluate(call.Arguments[0], context), call.Function);
        var result = new ObservationValue[source.Count + 1];
        for (var index = 0; index < source.Count; index++)
            result[index] = source[index];
        result[^1] = Evaluate(call.Arguments[1], context);
        return FromOwnedArray(result);
    }

    ObservationValue EvaluateAppendRange(CallExpr call, in RelationQueryExpressionContext context)
    {
        var source = RequireArray(Evaluate(call.Arguments[0], context), call.Function);
        var appended = RequireArray(Evaluate(call.Arguments[1], context), call.Function);
        var result = new ObservationValue[source.Count + appended.Count];
        Copy(source, result, destinationIndex: 0);
        Copy(appended, result, destinationIndex: source.Count);
        return FromOwnedArray(result);
    }

    ObservationValue EvaluateInsertAt(CallExpr call, in RelationQueryExpressionContext context)
    {
        var source = RequireArray(Evaluate(call.Arguments[0], context), call.Function);
        var index = RequireIndex(Evaluate(call.Arguments[1], context), source.Count, call.Function);
        var item = Evaluate(call.Arguments[2], context);
        var result = new ObservationValue[source.Count + 1];
        for (var sourceIndex = 0; sourceIndex < index; sourceIndex++)
            result[sourceIndex] = source[sourceIndex];
        result[index] = item;
        for (var sourceIndex = index; sourceIndex < source.Count; sourceIndex++)
            result[sourceIndex + 1] = source[sourceIndex];
        return FromOwnedArray(result);
    }

    ObservationValue EvaluateInsertRangeAt(CallExpr call, in RelationQueryExpressionContext context)
    {
        var source = RequireArray(Evaluate(call.Arguments[0], context), call.Function);
        var index = RequireIndex(Evaluate(call.Arguments[1], context), source.Count, call.Function);
        var inserted = RequireArray(Evaluate(call.Arguments[2], context), call.Function);
        var result = new ObservationValue[source.Count + inserted.Count];
        for (var sourceIndex = 0; sourceIndex < index; sourceIndex++)
            result[sourceIndex] = source[sourceIndex];
        Copy(inserted, result, destinationIndex: index);
        for (var sourceIndex = index; sourceIndex < source.Count; sourceIndex++)
            result[sourceIndex + inserted.Count] = source[sourceIndex];
        return FromOwnedArray(result);
    }

    ObservationValue EvaluateConcat(CallExpr call, in RelationQueryExpressionContext context)
    {
        StringBuilder result = new();
        foreach (var argument in call.Arguments)
        {
            var value = Evaluate(argument, context);
            if (value.Kind != ObservationValueKind.String)
                throw InvalidOperand($"Expression function 'concat' requires strings, but received '{value.Kind}'.");
            result.Append(value.String);
        }
        return ObservationValue.FromString(result.ToString());
    }

    ObservationValue EvaluateSequenceAggregate(
        CallExpr call,
        in RelationQueryExpressionContext context)
    {
        var source = RequireArray(Evaluate(call.Arguments[0], context), call.Function);
        if (call.Function is ExprFunctionNames.Any or ExprFunctionNames.All)
        {
            if (call.Arguments.Length is < 1 or > 2)
                throw InvalidOperand($"Expression function '{call.Function}' accepts an optional predicate selector.");

            var expected = call.Function == ExprFunctionNames.All;
            for (var index = 0; index < source.Count; index++)
            {
                var selected = call.Arguments.Length == 1
                    ? source[index]
                    : Evaluate(call.Arguments[1], context.WithCurrentItem(source[index]));
                if (RequireBoolean(selected, call.Function) != expected)
                    return ObservationValue.FromBool(!expected);
            }

            return ObservationValue.FromBool(expected);
        }

        ObservationValue[] values;
        if (call.Arguments.Length == 1)
        {
            values = [.. source];
        }
        else
        {
            values = new ObservationValue[source.Count];
            for (var index = 0; index < source.Count; index++)
                values[index] = Evaluate(call.Arguments[1], context.WithCurrentItem(source[index]));
        }

        return call.Function switch
        {
            ExprFunctionNames.Sum => Sum(values),
            ExprFunctionNames.Min => MinOrMax(values, findMaximum: false),
            ExprFunctionNames.Max => MinOrMax(values, findMaximum: true),
            ExprFunctionNames.Avg => Average(values),
            _ => throw new UnreachableException()
        };
    }

    static ObservationValue Sum(IReadOnlyList<ObservationValue> values)
    {
        try
        {
            var sum = 0m;
            foreach (var value in values)
                sum += RelationQueryValueSemantics.RequireDecimal(value, ExprFunctionNames.Sum);
            return ObservationValue.FromDecimal(sum);
        }
        catch (OverflowException exception)
        {
            throw new RelationQueryExpressionEvaluationException(
                RelationQueryExpressionEvaluationError.NumericFailure,
                "Aggregate sum overflowed the supported numeric execution domain.",
                exception);
        }
    }

    static ObservationValue Average(IReadOnlyList<ObservationValue> values)
    {
        if (values.Count == 0)
            return ObservationValue.Undefined;

        try
        {
            var sum = 0m;
            foreach (var value in values)
                sum += RelationQueryValueSemantics.RequireDecimal(value, ExprFunctionNames.Avg);
            return ObservationValue.FromDecimal(sum / values.Count);
        }
        catch (OverflowException exception)
        {
            throw new RelationQueryExpressionEvaluationException(
                RelationQueryExpressionEvaluationError.NumericFailure,
                "Aggregate average overflowed the supported numeric execution domain.",
                exception);
        }
    }

    static ObservationValue MinOrMax(
        IReadOnlyList<ObservationValue> values,
        bool findMaximum)
    {
        if (values.Count == 0)
            return ObservationValue.Undefined;

        var result = values[0];
        for (var index = 1; index < values.Count; index++)
        {
            var comparison = RelationQueryValueSemantics.Compare(values[index], result);
            if (findMaximum ? comparison > 0 : comparison < 0)
                result = values[index];
        }
        return result;
    }

    static ObservationValue Any(IReadOnlyList<ObservationValue> values)
    {
        foreach (var value in values)
        {
            if (RequireBoolean(value, ExprFunctionNames.Any))
                return ObservationValue.FromBool(true);
        }

        return ObservationValue.FromBool(false);
    }

    static ObservationValue All(IReadOnlyList<ObservationValue> values)
    {
        foreach (var value in values)
        {
            if (!RequireBoolean(value, ExprFunctionNames.All))
                return ObservationValue.FromBool(false);
        }

        return ObservationValue.FromBool(true);
    }

    static IReadOnlyList<ObservationValue> RequireArray(ObservationValue value, string operation)
    {
        if (value.Kind != ObservationValueKind.Array || value.Array.IsDefault)
            throw InvalidOperand($"Operation '{operation}' requires an array, but received '{value.Kind}'.");
        return value.Array;
    }

    static int RequireIndex(
        ObservationValue value,
        int maximumInclusive,
        string operation)
    {
        if (value.Kind != ObservationValueKind.Int64
            || value.Int64 < 0
            || value.Int64 > maximumInclusive)
        {
            throw InvalidOperand(
                $"Expression function '{operation}' requires an integer index from 0 through {maximumInclusive}.");
        }
        return (int)value.Int64;
    }

    static bool RequireBoolean(ObservationValue value, string operation)
    {
        if (value.Kind != ObservationValueKind.Bool)
            throw InvalidOperand($"Operation '{operation}' requires a Boolean, but received '{value.Kind}'.");
        return value.Bool;
    }

    static string RequireString(ObservationValue value, string operation)
    {
        if (value.Kind != ObservationValueKind.String || value.String is null)
            throw InvalidOperand($"Operation '{operation}' requires text, but received '{value.Kind}'.");
        return value.String;
    }

    static ObservationValue RequireCurrentItem(in RelationQueryExpressionContext context) =>
        context.CurrentItem ?? throw Failure(
            RelationQueryExpressionEvaluationError.EvaluationContextUnavailable,
            "The expression requires a current-item scope that is not available.");

    static void RequireCapabilities(
        Expr expression,
        in RelationQueryExpressionContext context)
    {
        switch (expression)
        {
            case FieldExpr field:
                RequireCapability(ExprCapabilities.Field, context);
                if (field.Path.Segments.Length > 1)
                    RequireCapability(ExprCapabilities.NestedFieldPath, context);
                break;
            case FieldRefExpr field:
                RequireCapability(ExprCapabilities.TypedField, context);
                RequireCapability(ExprCapabilities.Field, context);
                if (field.Path.Segments.Length > 1)
                    RequireCapability(ExprCapabilities.NestedFieldPath, context);
                break;
            case CurrentItemExpr:
                RequireCapability(ExprCapabilities.CurrentItem, context);
                break;
            case ParameterExpr:
                RequireCapability(ExprCapabilities.Parameter, context);
                break;
            case ConstantExpr:
                RequireCapability(ExprCapabilities.Constant, context);
                break;
            case LiteralExpr:
                RequireCapability(ExprCapabilities.TypedLiteral, context);
                break;
            case UnaryExpr unary:
                RequireCapability(ExprCapabilities.ForUnary(unary.Operator), context);
                break;
            case BinaryExpr binary:
                RequireCapability(ExprCapabilities.ForBinary(binary.Operator), context);
                break;
            case ConditionalExpr:
                RequireCapability(ExprCapabilities.Conditional, context);
                break;
            case CallExpr call:
                RequireCapability(ExprCapabilities.ForFunction(call.Function), context);
                break;
            case AggregateExpr aggregate:
                RequireCapability(ExprCapabilities.ForAggregate(aggregate.Operator), context);
                break;
        }
    }

    static void RequireCapability(
        ExprCapabilityId capability,
        in RelationQueryExpressionContext context)
    {
        if (!context.IsCapabilityAvailable(capability))
        {
            throw Failure(
                RelationQueryExpressionEvaluationError.RuntimeInputUnavailable,
                $"Expression capability '{capability.Value}' is unavailable in the runtime evidence.");
        }
    }

    static ExprCapabilityProfile CreateSupportedCapabilities() => new(
    [
        ExprCapabilities.Field,
        ExprCapabilities.NestedFieldPath,
        ExprCapabilities.Parameter,
        ExprCapabilities.Constant,
        ExprCapabilities.TypedField,
        ExprCapabilities.TypedLiteral,
        ExprCapabilities.Conditional,
        ExprCapabilities.CurrentItem,
        ExprCapabilities.ForUnary(UnaryOperator.Not),
        ExprCapabilities.ForBinary(BinaryOperator.Eq),
        ExprCapabilities.ForBinary(BinaryOperator.Ne),
        ExprCapabilities.ForBinary(BinaryOperator.Gt),
        ExprCapabilities.ForBinary(BinaryOperator.Ge),
        ExprCapabilities.ForBinary(BinaryOperator.Lt),
        ExprCapabilities.ForBinary(BinaryOperator.Le),
        ExprCapabilities.ForBinary(BinaryOperator.And),
        ExprCapabilities.ForBinary(BinaryOperator.Or),
        ExprCapabilities.ForBinary(BinaryOperator.Add),
        ExprCapabilities.ForBinary(BinaryOperator.Sub),
        ExprCapabilities.ForBinary(BinaryOperator.Mul),
        ExprCapabilities.ForBinary(BinaryOperator.Div),
        ExprCapabilities.ForAggregate(AggregateOperator.Count),
        ExprCapabilities.ForAggregate(AggregateOperator.Sum),
        ExprCapabilities.ForAggregate(AggregateOperator.Min),
        ExprCapabilities.ForAggregate(AggregateOperator.Max),
        ExprCapabilities.ForAggregate(AggregateOperator.Any),
        ExprCapabilities.ForAggregate(AggregateOperator.All),
        ExprCapabilities.ForAggregate(AggregateOperator.Average),
        .. SupportedFunctionNames.Select(ExprCapabilities.ForFunction)
    ]);

    static ObservationValue Comparison(
        ObservationValue left,
        ObservationValue right,
        Func<int, bool> predicate) =>
        ObservationValue.FromBool(predicate(RelationQueryValueSemantics.Compare(left, right)));

    static bool TryGetOrdinalProperty(
        IReadOnlyDictionary<string, ObservationValue> fields,
        string name,
        out ObservationValue value)
    {
        foreach (var (key, candidate) in fields)
        {
            if (!string.Equals(key, name, StringComparison.Ordinal))
                continue;
            value = candidate;
            return true;
        }

        value = default;
        return false;
    }

    static void Copy(
        IReadOnlyList<ObservationValue> source,
        ObservationValue[] destination,
        int destinationIndex)
    {
        for (var index = 0; index < source.Count; index++)
            destination[destinationIndex + index] = source[index];
    }

    static ObservationValue FromOwnedArray(ObservationValue[] values) =>
        ObservationValue.FromImmutableArray(ImmutableCollectionsMarshal.AsImmutableArray(values));

    static RelationQueryExpressionEvaluationException InvalidOperand(string message) =>
        Failure(RelationQueryExpressionEvaluationError.InvalidOperand, message);

    static RelationQueryExpressionEvaluationException Failure(
        RelationQueryExpressionEvaluationError error,
        string message) =>
        new(error, message);
}
