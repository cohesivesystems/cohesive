using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using Cohesive.Model;
using Cohesive.Model.Expressions;
using Cohesive.Model.Serialization;

namespace Cohesive.Execution;

internal enum PortableExpressionEvaluationError
{
    UnsupportedExpression = 0,
    UnsupportedFieldPath = 1,
    UnsupportedFunction = 2,
    InvalidOperand = 3,
    NumericFailure = 4,
    EvaluationContextUnavailable = 5,
    RuntimeInputUnavailable = 6
}

internal sealed class PortableExpressionEvaluationException(
    PortableExpressionEvaluationError error,
    string message,
    Exception? innerException = null,
    PortableValueState? valueState = null,
    DocumentValidationDiagnostic? sourceDiagnostic = null)
    : InvalidOperationException(message, innerException)
{
    public PortableExpressionEvaluationError Error { get; } = error;

    public PortableValueState? ValueState { get; } = valueState;

    public DocumentValidationDiagnostic? SourceDiagnostic { get; } = sourceDiagnostic;
}

internal readonly record struct PortableExpressionValue(
    PortableValueState State,
    ObservationValue Observation,
    DocumentValidationDiagnostic? Failure = null)
{
    const string EvaluationFailureCode = "execution.expression.failed";

    public static PortableExpressionValue FromPortable(PortableValue value) => value.State switch
    {
        PortableValueState.Concrete => Concrete(value.Value!.Value),
        PortableValueState.Missing => new(PortableValueState.Missing, default),
        PortableValueState.Absent => Absent,
        PortableValueState.Null => Null,
        PortableValueState.Unknown => Unknown,
        PortableValueState.Failed => new(PortableValueState.Failed, default, value.Failure),
        _ => throw new ArgumentOutOfRangeException(nameof(value), value.State, "Unsupported portable value state.")
    };

    public static PortableExpressionValue FromObservation(ObservationValue value) => value.Kind switch
    {
        ObservationValueKind.Undefined => Absent,
        ObservationValueKind.Null => Null,
        _ => Concrete(value)
    };

    public static PortableExpressionValue Concrete(ObservationValue value) =>
        new(PortableValueState.Concrete, value);

    public static PortableExpressionValue Absent { get; } = new(PortableValueState.Absent, default);

    public static PortableExpressionValue Null { get; } = new(PortableValueState.Null, default);

    public static PortableExpressionValue Unknown { get; } = new(PortableValueState.Unknown, default);

    public PortableValue ToPortable(ValueContract contract) => State switch
    {
        PortableValueState.Missing => PortableValue.Missing(contract),
        PortableValueState.Absent => PortableValue.Absent(contract),
        PortableValueState.Null => PortableValue.Null(contract),
        PortableValueState.Unknown => PortableValue.Unknown(contract),
        PortableValueState.Failed => PortableValue.Failed(
            contract,
            Failure ?? new(
                EvaluationFailureCode,
                DiagnosticSeverity.Error,
                "Expression evaluation failed without structured source evidence.")),
        PortableValueState.Concrete => PortableValue.Concrete(contract, Observation),
        _ => throw new ArgumentOutOfRangeException(nameof(State), State, "Unsupported runtime value state.")
    };

    public PortableExpressionValue Project(FieldPath path, int startIndex = 0)
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
                throw PortableExpressionReferenceEvaluator.Failure(
                    PortableExpressionEvaluationError.UnsupportedFieldPath,
                    $"Field path '{path}' contains unsupported collection-element navigation.");
            }

            if (current.Kind == ObservationValueKind.Undefined)
                return Absent;
            if (current.Kind == ObservationValueKind.Null)
                return Null;
            if (current.Kind != ObservationValueKind.Object || current.Fields is null)
            {
                throw PortableExpressionReferenceEvaluator.Failure(
                    PortableExpressionEvaluationError.InvalidOperand,
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
        PortableValueState.Missing => throw PortableExpressionReferenceEvaluator.Failure(
            PortableExpressionEvaluationError.RuntimeInputUnavailable,
            $"Operation '{operation}' requires a value that was not observed.",
            valueState: State),
        PortableValueState.Unknown => throw PortableExpressionReferenceEvaluator.Failure(
            PortableExpressionEvaluationError.RuntimeInputUnavailable,
            $"Operation '{operation}' cannot consume an unknown value.",
            valueState: State),
        PortableValueState.Failed => throw PortableExpressionReferenceEvaluator.Failure(
            PortableExpressionEvaluationError.RuntimeInputUnavailable,
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
        throw PortableExpressionReferenceEvaluator.Failure(
            PortableExpressionEvaluationError.InvalidOperand,
            $"Operation '{operation}' requires a concrete value, but received '{State}'.");
    }

    public ObservationValue RequireEmbeddable(string operation) => State switch
    {
        PortableValueState.Concrete => Observation,
        PortableValueState.Null => ObservationValue.Null,
        PortableValueState.Absent => throw PortableExpressionReferenceEvaluator.Failure(
            PortableExpressionEvaluationError.InvalidOperand,
            $"Operation '{operation}' cannot embed an absent value without an explicit optional-field policy.",
            valueState: State),
        _ => RequireObservation(operation)
    };
}

internal sealed class PortableExpressionEvaluationContext
{
    public required Func<ValueBindingId, PortableExpressionValue> ResolveBinding { get; init; }

    public required Func<ValueBindingId?, FieldPath, PortableExpressionValue> ResolveField { get; init; }

    public required Func<string, PortableExpressionValue> ResolveParameter { get; init; }

    public PortableExpressionValue? CurrentItem { get; init; }

    public PortableExpressionEvaluationContext WithCurrentItem(PortableExpressionValue item) => new()
    {
        ResolveBinding = ResolveBinding,
        ResolveField = ResolveField,
        ResolveParameter = ResolveParameter,
        CurrentItem = item
    };
}

internal sealed class PortableExpressionReferenceEvaluator
{
    readonly ExprCapabilityProfile capabilities;
    readonly string interpreterName;

    public PortableExpressionReferenceEvaluator(
        ExprCapabilityProfile capabilities,
        string interpreterName)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        if (string.IsNullOrWhiteSpace(interpreterName))
            throw new ArgumentException("An interpreter name is required.", nameof(interpreterName));

        this.capabilities = capabilities;
        this.interpreterName = interpreterName;
    }

    public PortableExpressionValue Evaluate(Expr expression, PortableExpressionEvaluationContext context)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(context);
        EnsureSupported(expression);

        return expression switch
        {
            BindingExpr binding => context.ResolveBinding(binding.Binding),
            ConstantExpr constant => PortableExpressionValue.FromObservation(constant.Value),
            LiteralExpr literal => PortableExpressionValue.FromObservation(literal.Value),
            FieldExpr field => EvaluateField(field.Path, field.Binding, context),
            FieldRefExpr field => EvaluateField(field.Path, explicitBinding: null, context),
            CurrentItemExpr => context.CurrentItem ?? throw Failure(
                PortableExpressionEvaluationError.EvaluationContextUnavailable,
                "The expression requires a current-item scope that is not available."),
            ParameterExpr parameter => context.ResolveParameter(parameter.Parameter),
            UnaryExpr unary => EvaluateUnary(unary, context),
            BinaryExpr binary => EvaluateBinary(binary, context),
            ConditionalExpr conditional => EvaluateConditional(conditional, context),
            CallExpr call => EvaluateCall(call, context),
            AggregateExpr aggregate => EvaluateAggregate(aggregate, context),
            _ => throw Failure(
                PortableExpressionEvaluationError.UnsupportedExpression,
                $"Expression node '{expression.GetType().Name}' is not supported by the {interpreterName}.")
        };
    }

    void EnsureSupported(Expr expression)
    {
        switch (expression)
        {
            case BindingExpr:
                RequireCapability(ExprCapabilities.Binding, expression);
                break;
            case ConstantExpr:
                RequireCapability(ExprCapabilities.Constant, expression);
                break;
            case LiteralExpr:
                RequireCapability(ExprCapabilities.TypedLiteral, expression);
                break;
            case FieldExpr field:
                RequireFieldCapabilities(field.Path, expression, typed: false);
                break;
            case FieldRefExpr field:
                RequireFieldCapabilities(field.Path, expression, typed: true);
                break;
            case CurrentItemExpr:
                RequireCapability(ExprCapabilities.CurrentItem, expression);
                break;
            case ParameterExpr:
                RequireCapability(ExprCapabilities.Parameter, expression);
                break;
            case UnaryExpr unary:
                RequireCapability(ExprCapabilities.ForUnary(unary.Operator), expression);
                break;
            case BinaryExpr binary:
                RequireCapability(ExprCapabilities.ForBinary(binary.Operator), expression);
                break;
            case ConditionalExpr:
                RequireCapability(ExprCapabilities.Conditional, expression);
                break;
            case CallExpr call:
                if (string.IsNullOrWhiteSpace(call.Function))
                {
                    throw Failure(
                        PortableExpressionEvaluationError.UnsupportedFunction,
                        "An expression function requires a non-empty semantic identifier.");
                }
                RequireCapability(
                    ExprCapabilities.ForFunction(call.Function),
                    expression,
                    PortableExpressionEvaluationError.UnsupportedFunction);
                break;
            case AggregateExpr aggregate:
                RequireCapability(ExprCapabilities.ForAggregate(aggregate.Operator), expression);
                break;
        }
    }

    void RequireFieldCapabilities(FieldPath path, Expr expression, bool typed)
    {
        if (path.Segments.IsDefaultOrEmpty)
        {
            throw Failure(
                PortableExpressionEvaluationError.UnsupportedFieldPath,
                "A field expression requires a non-empty field path.");
        }

        RequireCapability(ExprCapabilities.Field, expression);
        if (typed)
            RequireCapability(ExprCapabilities.TypedField, expression);
        if (path.Segments.Length > 1)
            RequireCapability(ExprCapabilities.NestedFieldPath, expression);
        if (path.Segments[0] is { Kind: SegmentKind.Field, Segment: ExprFieldRoots.CurrentItem })
            RequireCapability(ExprCapabilities.CurrentItem, expression);
    }

    void RequireCapability(
        ExprCapabilityId capability,
        Expr expression,
        PortableExpressionEvaluationError error = PortableExpressionEvaluationError.UnsupportedExpression)
    {
        if (capabilities.Supports(capability))
            return;

        throw Failure(
            error,
            $"Expression capability '{capability.Value}' required by '{expression.GetType().Name}' "
            + $"is not supported by the {interpreterName}.");
    }

    PortableExpressionValue EvaluateField(
        FieldPath path,
        ValueBindingId? explicitBinding,
        PortableExpressionEvaluationContext context)
    {
        if (explicitBinding is null
            && path.Segments[0] is { Kind: SegmentKind.Field, Segment: ExprFieldRoots.CurrentItem })
        {
            return (context.CurrentItem ?? throw Failure(
                    PortableExpressionEvaluationError.EvaluationContextUnavailable,
                    "The expression requires a current-item scope that is not available."))
                .Project(path, startIndex: 1);
        }

        return context.ResolveField(explicitBinding, path);
    }

    PortableExpressionValue EvaluateUnary(UnaryExpr unary, PortableExpressionEvaluationContext context) =>
        unary.Operator switch
        {
            UnaryOperator.Not => PortableExpressionValue.Concrete(ObservationValue.FromBool(
                !RequireBoolean(Evaluate(unary.Operand, context), "logical not"))),
            _ => throw Failure(
                PortableExpressionEvaluationError.UnsupportedExpression,
                $"Unary operator '{unary.Operator}' is not supported.")
        };

    PortableExpressionValue EvaluateBinary(BinaryExpr binary, PortableExpressionEvaluationContext context)
    {
        if (binary.Operator == BinaryOperator.And)
        {
            var left = RequireBoolean(Evaluate(binary.Left, context), "logical and left operand");
            return PortableExpressionValue.Concrete(ObservationValue.FromBool(
                left && RequireBoolean(Evaluate(binary.Right, context), "logical and right operand")));
        }

        if (binary.Operator == BinaryOperator.Or)
        {
            var left = RequireBoolean(Evaluate(binary.Left, context), "logical or left operand");
            return PortableExpressionValue.Concrete(ObservationValue.FromBool(
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
                PortableExpressionEvaluationError.UnsupportedExpression,
                $"Binary operator '{binary.Operator}' is not supported.")
        };
    }

    PortableExpressionValue EvaluateConditional(
        ConditionalExpr conditional,
        PortableExpressionEvaluationContext context) => Evaluate(
        RequireBoolean(Evaluate(conditional.Test, context), "conditional test")
            ? conditional.IfTrue
            : conditional.IfFalse,
        context);

    PortableExpressionValue EvaluateCall(CallExpr call, PortableExpressionEvaluationContext context)
    {
        if (!ExprSemanticsCatalog.Default.TryGetFunction(call.Function, out var definition)
            || !definition.Arity.Accepts(call.Arguments.Length))
        {
            throw Failure(
                PortableExpressionEvaluationError.UnsupportedFunction,
                $"Expression function '{call.Function}' is unknown or has invalid arity {call.Arguments.Length}.");
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
            _ => throw Failure(
                PortableExpressionEvaluationError.UnsupportedFunction,
                $"Expression function '{call.Function}' is not implemented by the {interpreterName}.")
        };
    }

    PortableExpressionValue EvaluateAggregate(
        AggregateExpr aggregate,
        PortableExpressionEvaluationContext context)
    {
        if (!aggregate.GroupBy.IsDefaultOrEmpty)
        {
            throw Failure(
                PortableExpressionEvaluationError.UnsupportedExpression,
                $"Grouped AggregateExpr evaluation is not implemented by the {interpreterName}.");
        }

        var source = RequireArray(Evaluate(aggregate.Source, context), aggregate.Operator.ToString());
        return PortableExpressionValue.FromObservation(Aggregate(aggregate.Operator, source));
    }

    PortableExpressionValue EvaluateContains(CallExpr call, PortableExpressionEvaluationContext context)
    {
        var source = RequireArray(Evaluate(call.Arguments[0], context), call.Function);
        var candidate = Evaluate(call.Arguments[1], context);
        return Boolean(source.Any(item => Equals(PortableExpressionValue.FromObservation(item), candidate)));
    }

    PortableExpressionValue EvaluateCount(CallExpr call, PortableExpressionEvaluationContext context)
    {
        var value = Evaluate(call.Arguments[0], context).RequireConcrete(call.Function);
        return value.Kind switch
        {
            ObservationValueKind.Array => PortableExpressionValue.Concrete(
                ObservationValue.FromInt64(value.Array.IsDefault ? 0 : value.Array.Length)),
            ObservationValueKind.Object => PortableExpressionValue.Concrete(
                ObservationValue.FromInt64(value.Fields?.Count ?? 0)),
            _ => throw InvalidOperand(
                $"Expression function '{call.Function}' requires an array or object, but received '{value.Kind}'.")
        };
    }

    PortableExpressionValue EvaluateTextPredicate(
        CallExpr call,
        PortableExpressionEvaluationContext context,
        Func<string, string, bool> predicate) => Boolean(predicate(
        RequireString(Evaluate(call.Arguments[0], context), call.Function),
        RequireString(Evaluate(call.Arguments[1], context), call.Function)));

    PortableExpressionValue EvaluateObject(CallExpr call, PortableExpressionEvaluationContext context)
    {
        var fields = ImmutableSortedDictionary.CreateBuilder<string, ObservationValue>(StringComparer.Ordinal);
        for (var index = 0; index < call.Arguments.Length; index += 2)
        {
            var key = RequireString(Evaluate(call.Arguments[index], context), call.Function);
            if (string.IsNullOrWhiteSpace(key) || fields.ContainsKey(key))
                throw InvalidOperand($"Expression function 'object' requires unique non-empty string keys; received '{key}'.");
            fields.Add(key, Evaluate(call.Arguments[index + 1], context).RequireEmbeddable(call.Function));
        }
        return PortableExpressionValue.Concrete(ObservationValue.FromObject(fields.ToImmutable()));
    }

    PortableExpressionValue EvaluateSelect(CallExpr call, PortableExpressionEvaluationContext context)
    {
        var source = RequireArray(Evaluate(call.Arguments[0], context), call.Function);
        var result = new ObservationValue[source.Count];
        for (var index = 0; index < source.Count; index++)
        {
            result[index] = Evaluate(
                    call.Arguments[1],
                    context.WithCurrentItem(PortableExpressionValue.FromObservation(source[index])))
                .RequireEmbeddable(call.Function);
        }
        return PortableExpressionValue.Concrete(ObservationValue.FromArray(result));
    }

    PortableExpressionValue EvaluateAppend(CallExpr call, PortableExpressionEvaluationContext context)
    {
        var source = RequireArray(Evaluate(call.Arguments[0], context), call.Function);
        var result = new ObservationValue[source.Count + 1];
        Copy(source, result, destinationIndex: 0);
        result[^1] = Evaluate(call.Arguments[1], context).RequireEmbeddable(call.Function);
        return PortableExpressionValue.Concrete(ObservationValue.FromArray(result));
    }

    PortableExpressionValue EvaluateAppendRange(CallExpr call, PortableExpressionEvaluationContext context)
    {
        var source = RequireArray(Evaluate(call.Arguments[0], context), call.Function);
        var appended = RequireArray(Evaluate(call.Arguments[1], context), call.Function);
        var result = new ObservationValue[source.Count + appended.Count];
        Copy(source, result, destinationIndex: 0);
        Copy(appended, result, destinationIndex: source.Count);
        return PortableExpressionValue.Concrete(ObservationValue.FromArray(result));
    }

    PortableExpressionValue EvaluateInsert(
        CallExpr call,
        PortableExpressionEvaluationContext context,
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
        return PortableExpressionValue.Concrete(ObservationValue.FromArray(result));
    }

    PortableExpressionValue EvaluateConcat(CallExpr call, PortableExpressionEvaluationContext context)
    {
        StringBuilder result = new();
        foreach (var argument in call.Arguments)
            result.Append(RequireString(Evaluate(argument, context), call.Function));
        return PortableExpressionValue.Concrete(ObservationValue.FromString(result.ToString()));
    }

    PortableExpressionValue EvaluateSequenceAggregate(CallExpr call, PortableExpressionEvaluationContext context)
    {
        var source = RequireArray(Evaluate(call.Arguments[0], context), call.Function);
        if (call.Function is ExprFunctionNames.Any or ExprFunctionNames.All)
        {
            var expected = call.Function == ExprFunctionNames.All;
            foreach (var item in source)
            {
                var selected = call.Arguments.Length == 1
                    ? PortableExpressionValue.FromObservation(item)
                    : Evaluate(call.Arguments[1], context.WithCurrentItem(PortableExpressionValue.FromObservation(item)));
                if (RequireBoolean(selected, call.Function) != expected)
                    return Boolean(!expected);
            }
            return Boolean(expected);
        }

        var values = new ObservationValue[source.Count];
        for (var index = 0; index < source.Count; index++)
        {
            values[index] = (call.Arguments.Length == 1
                    ? PortableExpressionValue.FromObservation(source[index])
                    : Evaluate(call.Arguments[1], context.WithCurrentItem(
                        PortableExpressionValue.FromObservation(source[index]))))
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
        return PortableExpressionValue.FromObservation(Aggregate(operation, values));
    }

    static ObservationValue Aggregate(AggregateOperator operation, IReadOnlyList<ObservationValue> values) =>
        operation switch
        {
            AggregateOperator.Count => ObservationValue.FromInt64(values.Count),
            AggregateOperator.Sum => Sum(values),
            AggregateOperator.Min => MinOrMax(values, findMaximum: false),
            AggregateOperator.Max => MinOrMax(values, findMaximum: true),
            AggregateOperator.Any => ObservationValue.FromBool(values.Any(static value => RequireBoolean(
                PortableExpressionValue.FromObservation(value), ExprFunctionNames.Any))),
            AggregateOperator.All => ObservationValue.FromBool(values.All(static value => RequireBoolean(
                PortableExpressionValue.FromObservation(value), ExprFunctionNames.All))),
            AggregateOperator.Average => Average(values),
            _ => throw Failure(
                PortableExpressionEvaluationError.UnsupportedExpression,
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
            throw Failure(PortableExpressionEvaluationError.NumericFailure, "Aggregate sum failed.", exception);
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
            throw Failure(PortableExpressionEvaluationError.NumericFailure, "Aggregate average failed.", exception);
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
            throw Failure(PortableExpressionEvaluationError.InvalidOperand, exception.Message, exception);
        }
    }

    static PortableExpressionValue Arithmetic(
        PortableExpressionValue left,
        PortableExpressionValue right,
        Func<ObservationValue, ObservationValue, ObservationValue> operation)
    {
        try
        {
            return PortableExpressionValue.Concrete(operation(
                left.RequireConcrete("arithmetic"),
                right.RequireConcrete("arithmetic")));
        }
        catch (PortableExpressionEvaluationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is OverflowException or DivideByZeroException or InvalidOperationException)
        {
            throw Failure(PortableExpressionEvaluationError.NumericFailure, "Numeric expression evaluation failed.", exception);
        }
    }

    static PortableExpressionValue Compare(
        PortableExpressionValue left,
        PortableExpressionValue right,
        Func<int, bool> predicate)
    {
        try
        {
            return Boolean(predicate(ObservationValueSemantics.Compare(
                left.RequireConcrete("comparison"),
                right.RequireConcrete("comparison"))));
        }
        catch (PortableExpressionEvaluationException)
        {
            throw;
        }
        catch (InvalidOperationException exception)
        {
            throw Failure(PortableExpressionEvaluationError.InvalidOperand, exception.Message, exception);
        }
    }

    static bool Equals(PortableExpressionValue left, PortableExpressionValue right)
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

    static IReadOnlyList<ObservationValue> RequireArray(PortableExpressionValue value, string operation)
    {
        var observation = value.RequireConcrete(operation);
        if (observation.Kind != ObservationValueKind.Array || observation.Array.IsDefault)
            throw InvalidOperand($"Operation '{operation}' requires an array, but received '{observation.Kind}'.");
        return observation.Array;
    }

    static int RequireIndex(PortableExpressionValue value, int maximumInclusive, string operation)
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

    internal static bool RequireBoolean(PortableExpressionValue value, string operation)
    {
        var observation = value.RequireConcrete(operation);
        if (observation.Kind != ObservationValueKind.Bool)
            throw InvalidOperand($"Operation '{operation}' requires a Boolean, but received '{observation.Kind}'.");
        return observation.Bool;
    }

    static string RequireString(PortableExpressionValue value, string operation)
    {
        var observation = value.RequireConcrete(operation);
        if (observation.Kind != ObservationValueKind.String || observation.String is null)
            throw InvalidOperand($"Operation '{operation}' requires text, but received '{observation.Kind}'.");
        return observation.String;
    }

    static PortableExpressionValue Boolean(bool value) =>
        PortableExpressionValue.Concrete(ObservationValue.FromBool(value));

    static void Copy(
        IReadOnlyList<ObservationValue> source,
        ObservationValue[] destination,
        int destinationIndex)
    {
        for (var index = 0; index < source.Count; index++)
            destination[destinationIndex + index] = source[index];
    }

    internal static PortableExpressionEvaluationException Failure(
        PortableExpressionEvaluationError error,
        string message,
        Exception? innerException = null,
        PortableValueState? valueState = null,
        DocumentValidationDiagnostic? sourceDiagnostic = null) =>
        new(error, message, innerException, valueState, sourceDiagnostic);

    static PortableExpressionEvaluationException InvalidOperand(string message) =>
        Failure(PortableExpressionEvaluationError.InvalidOperand, message);
}
