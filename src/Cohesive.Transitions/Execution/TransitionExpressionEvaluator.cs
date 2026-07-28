using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using Cohesive.Execution;
using Cohesive.Model.Expressions;
using Cohesive.Model.Serialization;
using Cohesive.Transitions.Compilation;

namespace Cohesive.Transitions.Execution;

internal enum TransitionExpressionEvaluationError
{
    UnsupportedExpression = 0,
    UnsupportedFieldPath = 1,
    UnsupportedFunction = 2,
    InvalidOperand = 3,
    NumericFailure = 4,
    EvaluationContextUnavailable = 5,
    RuntimeInputUnavailable = 6
}

internal sealed class TransitionExpressionEvaluationException(
    TransitionExpressionEvaluationError error,
    string message,
    Exception? innerException = null,
    PortableValueState? valueState = null,
    DocumentValidationDiagnostic? sourceDiagnostic = null)
    : InvalidOperationException(message, innerException)
{
    public TransitionExpressionEvaluationError Error { get; } = error;

    public PortableValueState? ValueState { get; } = valueState;

    public DocumentValidationDiagnostic? SourceDiagnostic { get; } = sourceDiagnostic;
}

internal readonly record struct TransitionRuntimeValue(
    PortableValueState State,
    ObservationValue Observation,
    DocumentValidationDiagnostic? Failure = null)
{
    public static TransitionRuntimeValue FromPortable(PortableValue value) => value.State switch
    {
        PortableValueState.Concrete => Concrete(value.Value!.Value),
        PortableValueState.Missing => new(PortableValueState.Missing, default),
        PortableValueState.Absent => Absent,
        PortableValueState.Null => Null,
        PortableValueState.Unknown => Unknown,
        PortableValueState.Failed => new(PortableValueState.Failed, default, value.Failure),
        _ => throw new ArgumentOutOfRangeException(nameof(value), value.State, "Unsupported portable value state.")
    };

    public static TransitionRuntimeValue FromObservation(ObservationValue value) => value.Kind switch
    {
        ObservationValueKind.Undefined => Absent,
        ObservationValueKind.Null => Null,
        _ => Concrete(value)
    };

    public static TransitionRuntimeValue Concrete(ObservationValue value) =>
        new(PortableValueState.Concrete, value);

    public static TransitionRuntimeValue Absent { get; } = new(PortableValueState.Absent, default);

    public static TransitionRuntimeValue Null { get; } = new(PortableValueState.Null, default);

    public static TransitionRuntimeValue Unknown { get; } = new(PortableValueState.Unknown, default);

    public PortableValue ToPortable(ValueContract contract) => State switch
    {
        PortableValueState.Missing => PortableValue.Missing(contract),
        PortableValueState.Absent => PortableValue.Absent(contract),
        PortableValueState.Null => PortableValue.Null(contract),
        PortableValueState.Unknown => PortableValue.Unknown(contract),
        PortableValueState.Failed => PortableValue.Failed(
            contract,
            Failure ?? new(
                TransitionExecutionDiagnosticCodes.ExpressionEvaluationFailed,
                DiagnosticSeverity.Error,
                "Expression evaluation failed without structured source evidence.")),
        PortableValueState.Concrete => PortableValue.Concrete(contract, Observation),
        _ => throw new ArgumentOutOfRangeException(nameof(State), State, "Unsupported runtime value state.")
    };

    public TransitionRuntimeValue Project(FieldPath path, int startIndex = 0)
    {
        if (State is PortableValueState.Absent or PortableValueState.Null
            or PortableValueState.Unknown or PortableValueState.Failed or PortableValueState.Missing)
        {
            return this;
        }

        var current = Observation;
        for (var index = startIndex; index < path.Segments.Length; index++)
        {
            var segment = path.Segments[index];
            if (segment.Kind != SegmentKind.Field || string.IsNullOrWhiteSpace(segment.Segment))
            {
                throw TransitionExpressionEvaluator.Failure(
                    TransitionExpressionEvaluationError.UnsupportedFieldPath,
                    $"Field path '{path}' contains unsupported collection-element navigation.");
            }

            if (current.Kind == ObservationValueKind.Undefined)
                return Absent;
            if (current.Kind == ObservationValueKind.Null)
                return Null;
            if (current.Kind != ObservationValueKind.Object || current.Fields is null)
            {
                throw TransitionExpressionEvaluator.Failure(
                    TransitionExpressionEvaluationError.InvalidOperand,
                    $"Field path '{path}' cannot navigate through value kind '{current.Kind}'.");
            }

            if (!current.TryGetProperty(segment.Segment, out current))
                return Absent;
        }

        return FromObservation(current);
    }

    public ObservationValue RequireObservation(string operation) => State switch
    {
        PortableValueState.Concrete => Observation,
        PortableValueState.Absent => ObservationValue.Undefined,
        PortableValueState.Null => ObservationValue.Null,
        PortableValueState.Missing => throw TransitionExpressionEvaluator.Failure(
            TransitionExpressionEvaluationError.RuntimeInputUnavailable,
            $"Operation '{operation}' requires a value that was not observed.",
            valueState: State),
        PortableValueState.Unknown => throw TransitionExpressionEvaluator.Failure(
            TransitionExpressionEvaluationError.RuntimeInputUnavailable,
            $"Operation '{operation}' cannot consume an unknown value.",
            valueState: State),
        PortableValueState.Failed => throw TransitionExpressionEvaluator.Failure(
            TransitionExpressionEvaluationError.RuntimeInputUnavailable,
            $"Operation '{operation}' cannot consume a failed value: {Failure?.Code ?? "unknown failure"}.",
            valueState: State,
            sourceDiagnostic: Failure),
        _ => throw new ArgumentOutOfRangeException(nameof(State), State, "Unsupported runtime value state.")
    };

    public ObservationValue RequireConcrete(string operation)
    {
        if (State == PortableValueState.Concrete)
            return Observation;

        _ = RequireObservation(operation);
        throw TransitionExpressionEvaluator.Failure(
            TransitionExpressionEvaluationError.InvalidOperand,
            $"Operation '{operation}' requires a concrete value, but received '{State}'.");
    }

    public ObservationValue RequireEmbeddable(string operation) => State switch
    {
        PortableValueState.Concrete => Observation,
        PortableValueState.Null => ObservationValue.Null,
        PortableValueState.Absent => throw TransitionExpressionEvaluator.Failure(
            TransitionExpressionEvaluationError.InvalidOperand,
            $"Operation '{operation}' cannot embed an absent value without an explicit optional-field policy.",
            valueState: State),
        _ => RequireObservation(operation)
    };
}

internal sealed class TransitionExpressionContext
{
    public required Func<ValueBindingId, TransitionRuntimeValue> ResolveBinding { get; init; }

    public required Func<ValueBindingId?, FieldPath, TransitionRuntimeValue> ResolveField { get; init; }

    public required Func<string, TransitionRuntimeValue> ResolveParameter { get; init; }

    public TransitionRuntimeValue? CurrentItem { get; init; }

    public TransitionExpressionContext WithCurrentItem(TransitionRuntimeValue item) => new()
    {
        ResolveBinding = ResolveBinding,
        ResolveField = ResolveField,
        ResolveParameter = ResolveParameter,
        CurrentItem = item
    };
}

internal sealed class TransitionExpressionEvaluator
{
    public TransitionRuntimeValue Evaluate(Expr expression, TransitionExpressionContext context)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(context);

        return expression switch
        {
            BindingExpr binding => context.ResolveBinding(binding.Binding),
            ConstantExpr constant => TransitionRuntimeValue.FromObservation(constant.Value),
            LiteralExpr literal => TransitionRuntimeValue.FromObservation(literal.Value),
            FieldExpr field => EvaluateField(field.Path, field.Binding, context),
            FieldRefExpr field => EvaluateField(field.Path, explicitBinding: null, context),
            CurrentItemExpr => context.CurrentItem ?? throw Failure(
                TransitionExpressionEvaluationError.EvaluationContextUnavailable,
                "The expression requires a current-item scope that is not available."),
            ParameterExpr parameter => context.ResolveParameter(parameter.Parameter),
            UnaryExpr unary => EvaluateUnary(unary, context),
            BinaryExpr binary => EvaluateBinary(binary, context),
            ConditionalExpr conditional => EvaluateConditional(conditional, context),
            CallExpr call => EvaluateCall(call, context),
            AggregateExpr aggregate => EvaluateAggregate(aggregate, context),
            _ => throw Failure(
                TransitionExpressionEvaluationError.UnsupportedExpression,
                $"Expression node '{expression.GetType().Name}' is not supported by the Transition reference interpreter.")
        };
    }

    TransitionRuntimeValue EvaluateField(
        FieldPath path,
        ValueBindingId? explicitBinding,
        TransitionExpressionContext context)
    {
        if (explicitBinding is null
            && path.Segments[0] is { Kind: SegmentKind.Field, Segment: ExprFieldRoots.CurrentItem })
        {
            return (context.CurrentItem ?? throw Failure(
                    TransitionExpressionEvaluationError.EvaluationContextUnavailable,
                    "The expression requires a current-item scope that is not available."))
                .Project(path, startIndex: 1);
        }

        return context.ResolveField(explicitBinding, path);
    }

    TransitionRuntimeValue EvaluateUnary(UnaryExpr unary, TransitionExpressionContext context) =>
        unary.Operator switch
        {
            UnaryOperator.Not => TransitionRuntimeValue.Concrete(ObservationValue.FromBool(
                !RequireBoolean(Evaluate(unary.Operand, context), "logical not"))),
            _ => throw Failure(
                TransitionExpressionEvaluationError.UnsupportedExpression,
                $"Unary operator '{unary.Operator}' is not supported.")
        };

    TransitionRuntimeValue EvaluateBinary(BinaryExpr binary, TransitionExpressionContext context)
    {
        if (binary.Operator == BinaryOperator.And)
        {
            var left = RequireBoolean(Evaluate(binary.Left, context), "logical and left operand");
            return TransitionRuntimeValue.Concrete(ObservationValue.FromBool(
                left && RequireBoolean(Evaluate(binary.Right, context), "logical and right operand")));
        }

        if (binary.Operator == BinaryOperator.Or)
        {
            var left = RequireBoolean(Evaluate(binary.Left, context), "logical or left operand");
            return TransitionRuntimeValue.Concrete(ObservationValue.FromBool(
                left || RequireBoolean(Evaluate(binary.Right, context), "logical or right operand")));
        }

        var leftValue = Evaluate(binary.Left, context);
        var rightValue = Evaluate(binary.Right, context);
        return binary.Operator switch
        {
            BinaryOperator.Eq => Boolean(Equals(leftValue, rightValue)),
            BinaryOperator.Ne => Boolean(!Equals(leftValue, rightValue)),
            BinaryOperator.Gt => Compare(leftValue, rightValue, static value => value > 0),
            BinaryOperator.Ge => Compare(leftValue, rightValue, static value => value >= 0),
            BinaryOperator.Lt => Compare(leftValue, rightValue, static value => value < 0),
            BinaryOperator.Le => Compare(leftValue, rightValue, static value => value <= 0),
            BinaryOperator.Add => Arithmetic(leftValue, rightValue, ObservationValueSemantics.Add),
            BinaryOperator.Sub => Arithmetic(leftValue, rightValue, ObservationValueSemantics.Subtract),
            BinaryOperator.Mul => Arithmetic(leftValue, rightValue, ObservationValueSemantics.Multiply),
            BinaryOperator.Div => Arithmetic(leftValue, rightValue, ObservationValueSemantics.Divide),
            _ => throw Failure(
                TransitionExpressionEvaluationError.UnsupportedExpression,
                $"Binary operator '{binary.Operator}' is not supported.")
        };
    }

    TransitionRuntimeValue EvaluateConditional(
        ConditionalExpr conditional,
        TransitionExpressionContext context) => Evaluate(
        RequireBoolean(Evaluate(conditional.Test, context), "conditional test")
            ? conditional.IfTrue
            : conditional.IfFalse,
        context);

    TransitionRuntimeValue EvaluateCall(CallExpr call, TransitionExpressionContext context)
    {
        if (!ExprSemanticsCatalog.Default.TryGetFunction(call.Function, out var definition)
            || !definition.Arity.Accepts(call.Arguments.Length))
        {
            throw Failure(
                TransitionExpressionEvaluationError.UnsupportedFunction,
                $"Expression function '{call.Function}' is unknown or has invalid arity {call.Arguments.Length}.");
        }
        if (!TransitionExpressionLanguage.SupportedFunctionNames.Contains(call.Function))
        {
            throw Failure(
                TransitionExpressionEvaluationError.UnsupportedFunction,
                $"Expression function '{call.Function}' is not supported by the Transition reference interpreter.");
        }

        return call.Function switch
        {
            ExprFunctionNames.Contains => EvaluateContains(call, context),
            ExprFunctionNames.Count => EvaluateCount(call, context),
            ExprFunctionNames.EndsWith => EvaluateTextPredicate(call, context, static (value, part) => value.EndsWith(part, StringComparison.Ordinal)),
            ExprFunctionNames.StartsWith => EvaluateTextPredicate(call, context, static (value, part) => value.StartsWith(part, StringComparison.Ordinal)),
            ExprFunctionNames.TextContains => EvaluateTextPredicate(call, context, static (value, part) => value.Contains(part, StringComparison.Ordinal)),
            ExprFunctionNames.Object => EvaluateObject(call, context),
            ExprFunctionNames.Select => EvaluateSelect(call, context),
            ExprFunctionNames.Append => EvaluateAppend(call, context),
            ExprFunctionNames.AppendRange => EvaluateAppendRange(call, context),
            ExprFunctionNames.InsertAt => EvaluateInsert(call, context, isRange: false),
            ExprFunctionNames.InsertRangeAt => EvaluateInsert(call, context, isRange: true),
            ExprFunctionNames.Concat => EvaluateConcat(call, context),
            ExprFunctionNames.Sum or ExprFunctionNames.Min or ExprFunctionNames.Max
                or ExprFunctionNames.Avg or ExprFunctionNames.Any or ExprFunctionNames.All =>
                EvaluateSequenceAggregate(call, context),
            _ => throw new UnreachableException()
        };
    }

    TransitionRuntimeValue EvaluateAggregate(
        AggregateExpr aggregate,
        TransitionExpressionContext context)
    {
        if (!aggregate.GroupBy.IsDefaultOrEmpty)
        {
            throw Failure(
                TransitionExpressionEvaluationError.UnsupportedExpression,
                "Grouped AggregateExpr evaluation is outside finite Transition expression semantics.");
        }

        var source = RequireArray(Evaluate(aggregate.Source, context), aggregate.Operator.ToString());
        return TransitionRuntimeValue.FromObservation(Aggregate(aggregate.Operator, source));
    }

    TransitionRuntimeValue EvaluateContains(CallExpr call, TransitionExpressionContext context)
    {
        var source = RequireArray(Evaluate(call.Arguments[0], context), call.Function);
        var candidate = Evaluate(call.Arguments[1], context);
        return Boolean(source.Any(item => Equals(TransitionRuntimeValue.FromObservation(item), candidate)));
    }

    TransitionRuntimeValue EvaluateCount(CallExpr call, TransitionExpressionContext context)
    {
        var value = Evaluate(call.Arguments[0], context).RequireConcrete(call.Function);
        return value.Kind switch
        {
            ObservationValueKind.Array => TransitionRuntimeValue.Concrete(
                ObservationValue.FromInt64(value.Array.IsDefault ? 0 : value.Array.Length)),
            ObservationValueKind.Object => TransitionRuntimeValue.Concrete(
                ObservationValue.FromInt64(value.Fields?.Count ?? 0)),
            _ => throw InvalidOperand(
                $"Expression function '{call.Function}' requires an array or object, but received '{value.Kind}'.")
        };
    }

    TransitionRuntimeValue EvaluateTextPredicate(
        CallExpr call,
        TransitionExpressionContext context,
        Func<string, string, bool> predicate) => Boolean(predicate(
        RequireString(Evaluate(call.Arguments[0], context), call.Function),
        RequireString(Evaluate(call.Arguments[1], context), call.Function)));

    TransitionRuntimeValue EvaluateObject(CallExpr call, TransitionExpressionContext context)
    {
        var fields = ImmutableSortedDictionary.CreateBuilder<string, ObservationValue>(StringComparer.Ordinal);
        for (var index = 0; index < call.Arguments.Length; index += 2)
        {
            var key = RequireString(Evaluate(call.Arguments[index], context), call.Function);
            if (string.IsNullOrWhiteSpace(key) || fields.ContainsKey(key))
                throw InvalidOperand($"Expression function 'object' requires unique non-empty string keys; received '{key}'.");
            fields.Add(key, Evaluate(call.Arguments[index + 1], context).RequireEmbeddable(call.Function));
        }
        return TransitionRuntimeValue.Concrete(ObservationValue.FromObject(fields.ToImmutable()));
    }

    TransitionRuntimeValue EvaluateSelect(CallExpr call, TransitionExpressionContext context)
    {
        var source = RequireArray(Evaluate(call.Arguments[0], context), call.Function);
        var result = new ObservationValue[source.Count];
        for (var index = 0; index < source.Count; index++)
        {
            result[index] = Evaluate(
                    call.Arguments[1],
                    context.WithCurrentItem(TransitionRuntimeValue.FromObservation(source[index])))
                .RequireEmbeddable(call.Function);
        }
        return TransitionRuntimeValue.Concrete(ObservationValue.FromArray(result));
    }

    TransitionRuntimeValue EvaluateAppend(CallExpr call, TransitionExpressionContext context)
    {
        var source = RequireArray(Evaluate(call.Arguments[0], context), call.Function);
        var result = new ObservationValue[source.Count + 1];
        Copy(source, result, destinationIndex: 0);
        result[^1] = Evaluate(call.Arguments[1], context).RequireEmbeddable(call.Function);
        return TransitionRuntimeValue.Concrete(ObservationValue.FromArray(result));
    }

    TransitionRuntimeValue EvaluateAppendRange(CallExpr call, TransitionExpressionContext context)
    {
        var source = RequireArray(Evaluate(call.Arguments[0], context), call.Function);
        var appended = RequireArray(Evaluate(call.Arguments[1], context), call.Function);
        var result = new ObservationValue[source.Count + appended.Count];
        Copy(source, result, destinationIndex: 0);
        Copy(appended, result, destinationIndex: source.Count);
        return TransitionRuntimeValue.Concrete(ObservationValue.FromArray(result));
    }

    TransitionRuntimeValue EvaluateInsert(
        CallExpr call,
        TransitionExpressionContext context,
        bool isRange)
    {
        var source = RequireArray(Evaluate(call.Arguments[0], context), call.Function);
        var index = RequireIndex(Evaluate(call.Arguments[1], context), source.Count, call.Function);
        var inserted = isRange
            ? RequireArray(Evaluate(call.Arguments[2], context), call.Function).ToArray()
            : [Evaluate(call.Arguments[2], context).RequireEmbeddable(call.Function)];
        var result = new ObservationValue[source.Count + inserted.Length];
        for (var sourceIndex = 0; sourceIndex < index; sourceIndex++)
            result[sourceIndex] = source[sourceIndex];
        inserted.CopyTo(result, index);
        for (var sourceIndex = index; sourceIndex < source.Count; sourceIndex++)
            result[sourceIndex + inserted.Length] = source[sourceIndex];
        return TransitionRuntimeValue.Concrete(ObservationValue.FromArray(result));
    }

    TransitionRuntimeValue EvaluateConcat(CallExpr call, TransitionExpressionContext context)
    {
        StringBuilder result = new();
        foreach (var argument in call.Arguments)
            result.Append(RequireString(Evaluate(argument, context), call.Function));
        return TransitionRuntimeValue.Concrete(ObservationValue.FromString(result.ToString()));
    }

    TransitionRuntimeValue EvaluateSequenceAggregate(CallExpr call, TransitionExpressionContext context)
    {
        var source = RequireArray(Evaluate(call.Arguments[0], context), call.Function);
        if (call.Function is ExprFunctionNames.Any or ExprFunctionNames.All)
        {
            var expected = call.Function == ExprFunctionNames.All;
            foreach (var item in source)
            {
                var selected = call.Arguments.Length == 1
                    ? TransitionRuntimeValue.FromObservation(item)
                    : Evaluate(call.Arguments[1], context.WithCurrentItem(TransitionRuntimeValue.FromObservation(item)));
                if (RequireBoolean(selected, call.Function) != expected)
                    return Boolean(!expected);
            }
            return Boolean(expected);
        }

        var values = new ObservationValue[source.Count];
        for (var index = 0; index < source.Count; index++)
        {
            values[index] = (call.Arguments.Length == 1
                    ? TransitionRuntimeValue.FromObservation(source[index])
                    : Evaluate(call.Arguments[1], context.WithCurrentItem(
                        TransitionRuntimeValue.FromObservation(source[index]))))
                .RequireConcrete(call.Function);
        }

        var operation = call.Function switch
        {
            ExprFunctionNames.Sum => AggregateOperator.Sum,
            ExprFunctionNames.Min => AggregateOperator.Min,
            ExprFunctionNames.Max => AggregateOperator.Max,
            ExprFunctionNames.Avg => AggregateOperator.Average,
            _ => throw new UnreachableException()
        };
        return TransitionRuntimeValue.FromObservation(Aggregate(operation, values));
    }

    static ObservationValue Aggregate(AggregateOperator operation, IReadOnlyList<ObservationValue> values) =>
        operation switch
        {
            AggregateOperator.Count => ObservationValue.FromInt64(values.Count),
            AggregateOperator.Sum => Sum(values),
            AggregateOperator.Min => MinOrMax(values, findMaximum: false),
            AggregateOperator.Max => MinOrMax(values, findMaximum: true),
            AggregateOperator.Any => ObservationValue.FromBool(values.Any(static value => RequireBoolean(
                TransitionRuntimeValue.FromObservation(value), ExprFunctionNames.Any))),
            AggregateOperator.All => ObservationValue.FromBool(values.All(static value => RequireBoolean(
                TransitionRuntimeValue.FromObservation(value), ExprFunctionNames.All))),
            AggregateOperator.Average => Average(values),
            _ => throw Failure(
                TransitionExpressionEvaluationError.UnsupportedExpression,
                $"Aggregate operator '{operation}' is not supported.")
        };

    static ObservationValue Sum(IReadOnlyList<ObservationValue> values)
    {
        try
        {
            var result = 0m;
            foreach (var value in values)
                result += ObservationValueSemantics.RequireDecimal(value, ExprFunctionNames.Sum);
            return ObservationValue.FromDecimal(result);
        }
        catch (Exception exception) when (exception is OverflowException or InvalidOperationException)
        {
            throw Failure(TransitionExpressionEvaluationError.NumericFailure, "Aggregate sum failed.", exception);
        }
    }

    static ObservationValue Average(IReadOnlyList<ObservationValue> values)
    {
        if (values.Count == 0)
            return ObservationValue.Undefined;
        try
        {
            var result = 0m;
            foreach (var value in values)
                result += ObservationValueSemantics.RequireDecimal(value, ExprFunctionNames.Avg);
            return ObservationValue.FromDecimal(result / values.Count);
        }
        catch (Exception exception) when (exception is OverflowException or InvalidOperationException)
        {
            throw Failure(TransitionExpressionEvaluationError.NumericFailure, "Aggregate average failed.", exception);
        }
    }

    static ObservationValue MinOrMax(IReadOnlyList<ObservationValue> values, bool findMaximum)
    {
        if (values.Count == 0)
            return ObservationValue.Undefined;
        try
        {
            var result = values[0];
            for (var index = 1; index < values.Count; index++)
            {
                var comparison = ObservationValueSemantics.Compare(values[index], result);
                if (findMaximum ? comparison > 0 : comparison < 0)
                    result = values[index];
            }
            return result;
        }
        catch (InvalidOperationException exception)
        {
            throw Failure(TransitionExpressionEvaluationError.InvalidOperand, exception.Message, exception);
        }
    }

    static TransitionRuntimeValue Arithmetic(
        TransitionRuntimeValue left,
        TransitionRuntimeValue right,
        Func<ObservationValue, ObservationValue, ObservationValue> operation)
    {
        try
        {
            return TransitionRuntimeValue.Concrete(operation(
                left.RequireConcrete("arithmetic"),
                right.RequireConcrete("arithmetic")));
        }
        catch (TransitionExpressionEvaluationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is OverflowException or DivideByZeroException or InvalidOperationException)
        {
            throw Failure(TransitionExpressionEvaluationError.NumericFailure, "Numeric expression evaluation failed.", exception);
        }
    }

    static TransitionRuntimeValue Compare(
        TransitionRuntimeValue left,
        TransitionRuntimeValue right,
        Func<int, bool> predicate)
    {
        try
        {
            return Boolean(predicate(ObservationValueSemantics.Compare(
                left.RequireConcrete("comparison"),
                right.RequireConcrete("comparison"))));
        }
        catch (TransitionExpressionEvaluationException)
        {
            throw;
        }
        catch (InvalidOperationException exception)
        {
            throw Failure(TransitionExpressionEvaluationError.InvalidOperand, exception.Message, exception);
        }
    }

    static bool Equals(TransitionRuntimeValue left, TransitionRuntimeValue right)
    {
        if (left.State is PortableValueState.Missing or PortableValueState.Unknown or PortableValueState.Failed)
            _ = left.RequireConcrete("equality");
        if (right.State is PortableValueState.Missing or PortableValueState.Unknown or PortableValueState.Failed)
            _ = right.RequireConcrete("equality");
        if (left.State != right.State)
            return false;
        return left.State != PortableValueState.Concrete
            || ObservationValueSemantics.Equals(left.Observation, right.Observation);
    }

    static IReadOnlyList<ObservationValue> RequireArray(TransitionRuntimeValue value, string operation)
    {
        var observation = value.RequireConcrete(operation);
        if (observation.Kind != ObservationValueKind.Array || observation.Array.IsDefault)
            throw InvalidOperand($"Operation '{operation}' requires an array, but received '{observation.Kind}'.");
        return observation.Array;
    }

    static int RequireIndex(TransitionRuntimeValue value, int maximumInclusive, string operation)
    {
        var observation = value.RequireConcrete(operation);
        if (!observation.TryGetInt32(out var index)
            || index < 0
            || index > maximumInclusive)
        {
            throw InvalidOperand(
                $"Expression function '{operation}' requires an integer index from 0 through {maximumInclusive}.");
        }
        return index;
    }

    internal static bool RequireBoolean(TransitionRuntimeValue value, string operation)
    {
        var observation = value.RequireConcrete(operation);
        if (observation.Kind != ObservationValueKind.Bool)
            throw InvalidOperand($"Operation '{operation}' requires a Boolean, but received '{observation.Kind}'.");
        return observation.Bool;
    }

    static string RequireString(TransitionRuntimeValue value, string operation)
    {
        var observation = value.RequireConcrete(operation);
        if (observation.Kind != ObservationValueKind.String || observation.String is null)
            throw InvalidOperand($"Operation '{operation}' requires text, but received '{observation.Kind}'.");
        return observation.String;
    }

    static TransitionRuntimeValue Boolean(bool value) =>
        TransitionRuntimeValue.Concrete(ObservationValue.FromBool(value));

    static void Copy(
        IReadOnlyList<ObservationValue> source,
        ObservationValue[] destination,
        int destinationIndex)
    {
        for (var index = 0; index < source.Count; index++)
            destination[destinationIndex + index] = source[index];
    }

    internal static TransitionExpressionEvaluationException Failure(
        TransitionExpressionEvaluationError error,
        string message,
        Exception? innerException = null,
        PortableValueState? valueState = null,
        DocumentValidationDiagnostic? sourceDiagnostic = null) =>
        new(error, message, innerException, valueState, sourceDiagnostic);

    static TransitionExpressionEvaluationException InvalidOperand(string message) =>
        Failure(TransitionExpressionEvaluationError.InvalidOperand, message);
}
