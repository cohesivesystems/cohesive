using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using Cohesive.Model.Expressions;
using Cohesive.Relations.Model;
using ConditionalExpr = Cohesive.Model.ConditionalExpr;
using LinqExpression = System.Linq.Expressions.Expression;

namespace Cohesive.Relations.Execution;

sealed record RelationEvaluationContext(
    RelationScopedObservation Root,
    IReadOnlyList<RelationScopedObservation> Related,
    IReadOnlyList<RelationScopedObservation> Universe,
    IReadOnlyList<RelationScopedObservation> SourceSet,
    Observation? CurrentObservation
    );

sealed record RelationEvaluationResult(ObservationValue Value, IReadOnlyList<FieldPath> SourcePaths);

sealed class RelExpressionEvaluator
{
    static readonly ConcurrentDictionary<Expr, Func<RelationEvaluationContext, RelationEvaluationResult>> Compiled = [];

    static readonly FieldPath[] EmptySourcePaths = [];
    static readonly FieldPath[] CurrentObservationSourcePaths = [FieldPath.FromField(ExprFieldRoots.CurrentItem)];

    static readonly MethodInfo CreateConstantResultMethod = GetMethod(nameof(CreateConstantResult));
    static readonly MethodInfo EvaluateFieldPathResultMethod = GetMethod(nameof(EvaluateFieldPathResult));
    static readonly MethodInfo EvaluateCurrentItemResultMethod = GetMethod(nameof(EvaluateCurrentItemResult));
    static readonly MethodInfo EvaluateUnaryResultMethod = GetMethod(nameof(EvaluateUnaryResult));
    static readonly MethodInfo EvaluateBinaryResultMethod = GetMethod(nameof(EvaluateBinaryResult));
    static readonly MethodInfo MergeConditionalResultMethod = GetMethod(nameof(MergeConditionalResult));
    static readonly MethodInfo EvaluateSimpleFunctionMethod = GetMethod(nameof(EvaluateSimpleFunction));
    static readonly MethodInfo EvaluateFunctionInterpretedMethod = GetMethod(nameof(EvaluateFunctionInterpreted));
    static readonly MethodInfo ToBooleanMethod = GetMethod(nameof(ToBoolean));

    static MethodInfo GetMethod(string name) => 
        typeof(RelExpressionEvaluator).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic) ?? throw new InvalidOperationException($"Method '{name}' not found.");

    public RelationEvaluationResult Evaluate(Expr expr, RelationEvaluationContext context) => EvaluateCached(expr, context);

    static RelationEvaluationResult EvaluateCached(Expr expr, RelationEvaluationContext context)
    {
        ArgumentNullException.ThrowIfNull(expr);
        var compiled = Compiled.GetOrAdd(expr, Compile);
        return compiled(context);
    }

    static Func<RelationEvaluationContext, RelationEvaluationResult> Compile(Expr expr)
    {
        var context = LinqExpression.Parameter(typeof(RelationEvaluationContext), name: "context");
        var body = BuildCompiledBody(expr, context);
        return LinqExpression.Lambda<Func<RelationEvaluationContext, RelationEvaluationResult>>(body, context).Compile();
    }

    static LinqExpression BuildCompiledBody(Expr expr, ParameterExpression context)
    {
        return expr switch
        {
            ConstantExpr constant => LinqExpression.Call(CreateConstantResultMethod, LinqExpression.Constant(constant.Value)),
            LiteralExpr literal => LinqExpression.Call(CreateConstantResultMethod, LinqExpression.Constant(literal.Value)),
            FieldExpr field => LinqExpression.Call(EvaluateFieldPathResultMethod, LinqExpression.Constant(field.Path), context),
            FieldRefExpr fieldRef => LinqExpression.Call(EvaluateFieldPathResultMethod, LinqExpression.Constant(fieldRef.Path), context),
            CurrentItemExpr => LinqExpression.Call(EvaluateCurrentItemResultMethod, context),
            ParameterExpr parameter => throw new InvalidOperationException($"Expression node '{nameof(ParameterExpr)}' cannot be evaluated in a relation context (parameter '{parameter.Parameter}')."),
            UnaryExpr unary => LinqExpression.Call(EvaluateUnaryResultMethod, LinqExpression.Constant(unary.Operator), BuildCompiledBody(unary.Operand, context)),
            BinaryExpr binary => LinqExpression.Call(method: EvaluateBinaryResultMethod, arg0: LinqExpression.Constant(binary.Operator), arg1: BuildCompiledBody(binary.Left, context), arg2: BuildCompiledBody(binary.Right, context)),
            ConditionalExpr conditional => BuildConditional(conditional, context),
            CallExpr call => BuildFunction(call, context),
            AggregateExpr aggregate => BuildFunction(new(function: ToAggregateFunctionName(aggregate.Operator), arguments: aggregate.GroupBy.Length == 0 ? [aggregate.Source] : [aggregate.Source, .. aggregate.GroupBy]), context),
            _ => throw new InvalidOperationException($"Unsupported expression node '{expr.GetType().Name}'.")
        };
    }

    static LinqExpression BuildConditional(ConditionalExpr conditional, ParameterExpression context)
    {
        var testVar = LinqExpression.Variable(typeof(RelationEvaluationResult), "test");
        var branchVar = LinqExpression.Variable(typeof(RelationEvaluationResult), "branch");

        var assignTest = LinqExpression.Assign(testVar, BuildCompiledBody(conditional.Test, context));
        var condition = LinqExpression.Call(ToBooleanMethod, LinqExpression.Property(testVar, nameof(RelationEvaluationResult.Value)));
        var assignBranch = LinqExpression.Assign(
            branchVar,
            LinqExpression.Condition(test: condition, ifTrue: BuildCompiledBody(conditional.IfTrue, context), ifFalse: BuildCompiledBody(conditional.IfFalse, context))
            );
        
        var merged = LinqExpression.Call(MergeConditionalResultMethod, testVar, branchVar);

        return LinqExpression.Block(
            variables: [testVar, branchVar],
            assignTest,
            assignBranch,
            merged
            );
    }

    static LinqExpression BuildFunction(CallExpr function, ParameterExpression context)
    {
        if (IsComplexFunction(function.Function))
            return LinqExpression.Call(EvaluateFunctionInterpretedMethod, LinqExpression.Constant(function), context);

        var argumentResults = function.Arguments.Select(arg => BuildCompiledBody(arg, context)).ToArray();
        var argumentArray = LinqExpression.NewArrayInit(typeof(RelationEvaluationResult), argumentResults);
        return LinqExpression.Call(
            EvaluateSimpleFunctionMethod,
            LinqExpression.Constant(function.Function),
            argumentArray,
            context
            );
    }

    static bool IsComplexFunction(string function)
    {
        return function is ExprFunctionNames.Join
            or ExprFunctionNames.GroupBy
            or ExprFunctionNames.GroupByRows
            or ExprFunctionNames.Sum
            or ExprFunctionNames.Min
            or ExprFunctionNames.Max
            or ExprFunctionNames.Avg
            or ExprFunctionNames.Any
            or ExprFunctionNames.All
            or ExprFunctionNames.Select;
    }

    static RelationEvaluationResult CreateConstantResult(ObservationValue value) => new(value, EmptySourcePaths);

    static RelationEvaluationResult EvaluateCurrentItemResult(RelationEvaluationContext context)
        => new(context.CurrentObservation is null ? ObservationValue.Null : ToSourceRow(context.CurrentObservation), CurrentObservationSourcePaths);

    static RelationEvaluationResult EvaluateFieldPathResult(FieldPath path, RelationEvaluationContext context)
    {
        var value = ResolveFieldPath(path, context);
        return new(value, [path]);
    }

    static RelationEvaluationResult EvaluateUnaryResult(UnaryOperator op, RelationEvaluationResult operand)
    {
        return op switch
        {
            UnaryOperator.Not => new(ObservationValue.FromBool(!ToBoolean(operand.Value)), operand.SourcePaths),
            _ => throw new InvalidOperationException($"Unsupported unary projection operator '{op}'.")
        };
    }

    static RelationEvaluationResult EvaluateBinaryResult(BinaryOperator op, RelationEvaluationResult left, RelationEvaluationResult right)
    {
        var sources = left.SourcePaths.Concat(right.SourcePaths).Distinct().ToArray();
        var result = op switch
        {
            BinaryOperator.Eq => ObservationValue.FromBool(AreEqual(left.Value, right.Value)),
            BinaryOperator.Ne => ObservationValue.FromBool(!AreEqual(left.Value, right.Value)),
            BinaryOperator.Gt => ObservationValue.FromBool(Compare(left.Value, right.Value) > 0),
            BinaryOperator.Ge => ObservationValue.FromBool(Compare(left.Value, right.Value) >= 0),
            BinaryOperator.Lt => ObservationValue.FromBool(Compare(left.Value, right.Value) < 0),
            BinaryOperator.Le => ObservationValue.FromBool(Compare(left.Value, right.Value) <= 0),
            BinaryOperator.And => ObservationValue.FromBool(ToBoolean(left.Value) && ToBoolean(right.Value)),
            BinaryOperator.Or => ObservationValue.FromBool(ToBoolean(left.Value) || ToBoolean(right.Value)),
            BinaryOperator.Add => Add(left.Value, right.Value),
            BinaryOperator.Sub => ObservationValue.FromDecimal(ToDecimal(left.Value) - ToDecimal(right.Value)),
            BinaryOperator.Mul => ObservationValue.FromDecimal(ToDecimal(left.Value) * ToDecimal(right.Value)),
            BinaryOperator.Div => ObservationValue.FromDecimal(ToDecimal(left.Value) / ToDecimal(right.Value)),
            _ => throw new InvalidOperationException($"Unsupported binary projection operator '{op}'.")
        };

        return new(Value: result, SourcePaths: sources);
    }

    static RelationEvaluationResult MergeConditionalResult(RelationEvaluationResult test, RelationEvaluationResult selectedBranch) => 
        selectedBranch with { SourcePaths = test.SourcePaths.Concat(selectedBranch.SourcePaths).Distinct().ToArray() };

    static RelationEvaluationResult EvaluateSimpleFunction(string function, RelationEvaluationResult[] args, RelationEvaluationContext context)
    {
        var sources = args.SelectMany(x => x.SourcePaths).Distinct().ToArray();
        if (string.Equals(function, ExprFunctionNames.RelatedField, StringComparison.Ordinal))
        {
            var resolved = RelatedField(args, context, out var resolvedPath);
            var combinedSources = resolvedPath is null
                ? sources
                : sources.Append(resolvedPath.GetValueOrDefault()).Distinct().ToArray();
            return new(resolved, SourcePaths: combinedSources);
        }

        var result = function switch
        {
            ExprFunctionNames.EntityId => ObservationValue.FromString(context.Root.LogicalEntityId),
            ExprFunctionNames.Key => ObservationValue.FromString(context.Root.Id),
            ExprFunctionNames.SourceRows => SourceRows(context),
            ExprFunctionNames.Count => ObservationValue.FromInt64(Count(args.Length == 0 ? ObservationValue.Null : args[0].Value)),
            ExprFunctionNames.Contains => ObservationValue.FromBool(Contains(args[0].Value, args[1].Value)),
            ExprFunctionNames.Object => BuildObject(args),
            _ => throw new InvalidOperationException($"Unsupported projection function '{function}'.")
        };

        return new(result, sources);
    }

    static RelationEvaluationResult EvaluateFunctionInterpreted(CallExpr functionRel, RelationEvaluationContext context)
    {
        var args = functionRel.Arguments.Select(x => EvaluateCached(x, context)).ToArray();
        if (!IsComplexFunction(functionRel.Function))
            return EvaluateSimpleFunction(functionRel.Function, args, context);

        var sources = args.SelectMany(x => x.SourcePaths).Distinct().ToArray();
        var result = functionRel.Function switch
        {
            ExprFunctionNames.Join => Join(functionRel, context),
            ExprFunctionNames.GroupBy => GroupBy(functionRel, context),
            ExprFunctionNames.GroupByRows => GroupByRows(functionRel, context),
            ExprFunctionNames.Sum => Aggregate(functionRel, context, ExprFunctionNames.Sum),
            ExprFunctionNames.Min => Aggregate(functionRel, context, ExprFunctionNames.Min),
            ExprFunctionNames.Max => Aggregate(functionRel, context, ExprFunctionNames.Max),
            ExprFunctionNames.Avg => Aggregate(functionRel, context, ExprFunctionNames.Avg),
            ExprFunctionNames.Any => Aggregate(functionRel, context, ExprFunctionNames.Any),
            ExprFunctionNames.All => Aggregate(functionRel, context, ExprFunctionNames.All),
            ExprFunctionNames.Select => Select(functionRel, context),
            _ => throw new InvalidOperationException($"Unsupported projection function '{functionRel.Function}'.")
        };

        return new(result, sources);
    }

    static ObservationValue RelatedField(IReadOnlyList<RelationEvaluationResult> args, RelationEvaluationContext context, out FieldPath? resolvedPath)
    {
        if (args.Count != 3)
            throw new InvalidOperationException("Function 'relatedField' expects schema, entityOrKey, and fieldName.");

        var schema = ConvertToString(args[0].Value);
        var entityOrKey = ConvertToString(args[1].Value);
        var fieldName = ConvertToString(args[2].Value);
        resolvedPath = null;

        if (string.IsNullOrWhiteSpace(schema)
            || string.IsNullOrWhiteSpace(entityOrKey)
            || string.IsNullOrWhiteSpace(fieldName))
        {
            return ObservationValue.Null;
        }

        var related = context.Related.FirstOrDefault(x =>
            string.Equals(x.ShapeId.Value, schema, StringComparison.Ordinal)
            && string.Equals(x.Id, entityOrKey, StringComparison.Ordinal));

        related ??= context.Universe.FirstOrDefault(x =>
            string.Equals(x.ShapeId.Value, schema, StringComparison.Ordinal)
            && string.Equals(x.Id, entityOrKey, StringComparison.Ordinal));

        if (related is null)
            return ObservationValue.Null;

        if (!related.Observation.TryGetField(fieldName, out var value))
            return ObservationValue.Null;

        resolvedPath = new FieldPath(
        [
            FieldPathSegment.ForField(schema),
            FieldPathSegment.ForField(entityOrKey),
            FieldPathSegment.ForField(fieldName)
        ]);
        return value;
    }

    static ObservationValue ResolveFieldPath(FieldPath path, RelationEvaluationContext context)
    {
        var segments = path.Segments;
        if (segments.Length == 0)
            return ObservationValue.Null;

        if (!TryGetSegmentToken(segments[0], out var rootToken))
            return ObservationValue.Null;

        if (string.Equals(rootToken, "source", StringComparison.Ordinal) || string.Equals(rootToken, "root", StringComparison.Ordinal))
            return TryReadFromObservation(context.Root.Observation, segments, startIndex: 1, out var sourceValue)
                ? sourceValue
                : ObservationValue.Null;

        if (string.Equals(rootToken, ExprFieldRoots.CurrentItem, StringComparison.Ordinal))
        {
            if (context.CurrentObservation is not null && TryReadFromObservation(context.CurrentObservation, segments, startIndex: 1, out var itemValue))
                return itemValue;

            return ObservationValue.Null;
        }

        if (TryReadFromObservation(context.Root.Observation, segments, startIndex: 0, out var rootValue))
            return rootValue;

        return ObservationValue.Null;
    }

    static bool TryReadFromObservation(Observation observation, IReadOnlyList<FieldPathSegment> segments, int startIndex, out ObservationValue value)
    {
        if (startIndex >= segments.Count)
        {
            value = ToSourceRow(observation);
            return true;
        }

        if (!segments[startIndex].TryGetFieldIdentity(out var fieldIdentity))
        {
            value = default;
            return false;
        }

        if (!observation.TryGetField(fieldIdentity, out var cursor))
        {
            value = default;
            return false;
        }

        if (startIndex == segments.Count - 1)
        {
            value = cursor;
            return true;
        }

        for (var i = startIndex + 1; i < segments.Count; i++)
        {
            if (!TryGetSegmentToken(segments[i], out var segment))
            {
                value = default;
                return false;
            }

            if (cursor.Kind == ObservationValueKind.Array
                && cursor.Array is not null
                && int.TryParse(segment, out var index)
                && index >= 0
                && index < cursor.Array.Length)
            {
                cursor = cursor.Array[index];
                continue;
            }

            if (!cursor.TryGetProperty(segment, out var property))
            {
                value = default;
                return false;
            }

            cursor = property;
        }

        value = cursor;
        return true;
    }

    static bool TryGetSegmentToken(FieldPathSegment segment, out string token)
    {
        if (segment.Segment is not null)
        {
            token = segment.Segment;
            return true;
        }

        token = string.Empty;
        return false;
    }

    static ObservationValue BuildObject(IReadOnlyList<RelationEvaluationResult> arguments)
    {
        if (arguments.Count % 2 != 0)
            throw new InvalidOperationException("Function 'object' expects key/value argument pairs.");

        Dictionary<string, ObservationValue> obj = new(StringComparer.Ordinal);
        for (var i = 0; i < arguments.Count; i += 2)
        {
            var key = ConvertToString(arguments[i].Value);
            if (string.IsNullOrWhiteSpace(key))
                throw new InvalidOperationException("Function 'object' requires non-empty string keys.");
            obj[key] = arguments[i + 1].Value;
        }

        return ObservationValue.FromObject(obj);
    }

    static ObservationValue Join(CallExpr functionRel, RelationEvaluationContext context)
    {
        if (functionRel.Arguments.Length != 3)
            throw new InvalidOperationException("Function 'join' expects leftKey, rightKey, rightCollection.");

        var left = EvaluateCached(functionRel.Arguments[0], context).Value;
        var rightItems = ToEnumerable(EvaluateCached(functionRel.Arguments[2], context).Value);

        List<ObservationValue> joined = [];
        foreach (var item in rightItems)
        {
            var itemContext = context with { CurrentObservation = ToCurrentObservation(context.Root.Observation, item) };
            var right = EvaluateCached(functionRel.Arguments[1], itemContext).Value;
            if (AreEqual(left, right))
                joined.Add(item);
        }

        return ObservationValue.FromArray([.. joined]);
    }

    static ObservationValue SourceRows(RelationEvaluationContext context)
    {
        var rows = context.SourceSet.Select(x => ToSourceRow(x.Observation)).ToArray();
        return ObservationValue.FromArray(rows);
    }

    static ObservationValue ToSourceRow(Observation observation)
    {
        Dictionary<string, ObservationValue> row = new(StringComparer.Ordinal);
        foreach (var fieldName in observation.Layout.FieldNames)
        {
            if (!observation.TryGetField(fieldName, out var value))
                continue;

            row[fieldName] = value;
        }

        row["Id"] = ObservationValue.FromString(observation.Id);
        row["Version"] = ObservationValue.FromInt64(observation.Version);
        return ObservationValue.FromObject(new ReadOnlyDictionary<string, ObservationValue>(row));
    }

    static ObservationValue GroupBy(CallExpr functionRel, RelationEvaluationContext context)
    {
        if (functionRel.Arguments.Length != 2)
            throw new InvalidOperationException("Function 'groupBy' expects source and keySelector.");

        var items = ToEnumerable(EvaluateCached(functionRel.Arguments[0], context).Value);
        Dictionary<string, List<ObservationValue>> grouped = new(StringComparer.Ordinal);
        foreach (var item in items)
        {
            var itemContext = context with { CurrentObservation = ToCurrentObservation(context.Root.Observation, item) };
            var key = ConvertToString(EvaluateCached(functionRel.Arguments[1], itemContext).Value) ?? string.Empty;
            if (!grouped.TryGetValue(key, out var bucket))
            {
                bucket = [];
                grouped[key] = bucket;
            }

            bucket.Add(item);
        }

        Dictionary<string, ObservationValue> obj = new(StringComparer.Ordinal);
        foreach (var (key, values) in grouped)
            obj[key] = ObservationValue.FromArray([.. values]);
        
        return ObservationValue.FromObject(new ReadOnlyDictionary<string, ObservationValue>(obj));
    }

    static ObservationValue GroupByRows(CallExpr functionRel, RelationEvaluationContext context)
    {
        if (functionRel.Arguments.Length != 2)
            throw new InvalidOperationException("Function 'groupByRows' expects source and keySelector.");

        var items = ToEnumerable(EvaluateCached(functionRel.Arguments[0], context).Value).ToArray();
        Dictionary<string, List<ObservationValue>> grouped = new(StringComparer.Ordinal);
        foreach (var item in items)
        {
            var itemContext = context with { CurrentObservation = ToCurrentObservation(context.Root.Observation, item) };
            var key = ConvertToString(EvaluateCached(functionRel.Arguments[1], itemContext).Value) ?? string.Empty;
            if (!grouped.TryGetValue(key, out var bucket))
            {
                bucket = [];
                grouped[key] = bucket;
            }

            bucket.Add(item);
        }

        var buckets = grouped
            .Select(pair => ObservationValue.FromObject(
                new ReadOnlyDictionary<string, ObservationValue>(
                    new Dictionary<string, ObservationValue>(StringComparer.Ordinal)
                    {
                        ["Id"] = ObservationValue.FromString(pair.Key),
                        ["Items"] = ObservationValue.FromArray([.. pair.Value])
                    })
                )
            ).ToArray();
        return ObservationValue.FromArray(buckets);
    }

    static ObservationValue Select(CallExpr functionRel, RelationEvaluationContext context)
    {
        if (functionRel.Arguments.Length != 2)
            throw new InvalidOperationException("Function 'select' expects source and selector.");
        
        var projected = ToEnumerable(EvaluateCached(functionRel.Arguments[0], context).Value)
            .Select(item =>
            {
                var itemContext = context with { CurrentObservation = ToCurrentObservation(context.Root.Observation, item) };
                return EvaluateCached(functionRel.Arguments[1], itemContext).Value;
            }).ToArray();
        
        return ObservationValue.FromArray(projected);
    }

    static ObservationValue Aggregate(CallExpr functionRel, RelationEvaluationContext context, string aggregate)
    {
        if (functionRel.Arguments.Length < 1)
            throw new InvalidOperationException($"Function '{aggregate}' expects at least one argument.");

        var values = ToEnumerable(EvaluateCached(functionRel.Arguments[0], context).Value).ToArray();
        if (functionRel.Arguments.Length > 1)
        {
            values = values.Select(item =>
            {
                var itemContext = context with { CurrentObservation = ToCurrentObservation(context.Root.Observation, item) };
                return EvaluateCached(functionRel.Arguments[1], itemContext).Value;
            }).ToArray();
        }

        if (values.Length == 0)
        {
            return aggregate switch
            {
                ExprFunctionNames.Avg => ObservationValue.FromInt64(0),
                ExprFunctionNames.Any => ObservationValue.FromBool(false),
                ExprFunctionNames.All => ObservationValue.FromBool(true),
                _ => ObservationValue.FromInt64(0)
            };
        }

        return aggregate switch
        {
            ExprFunctionNames.Sum => ObservationValue.FromDecimal(values.Sum(ToDecimal)),
            ExprFunctionNames.Min => ObservationValue.FromDecimal(values.Select(ToDecimal).Min()),
            ExprFunctionNames.Max => ObservationValue.FromDecimal(values.Select(ToDecimal).Max()),
            ExprFunctionNames.Avg => ObservationValue.FromDecimal(values.Select(ToDecimal).Average()),
            ExprFunctionNames.Any => ObservationValue.FromBool(values.Any(ToBoolean)),
            ExprFunctionNames.All => ObservationValue.FromBool(values.All(ToBoolean)),
            _ => throw new InvalidOperationException($"Unsupported aggregate '{aggregate}'.")
        };
    }

    static string ToAggregateFunctionName(AggregateOperator @operator)
    {
        return @operator switch
        {
            AggregateOperator.Count => ExprFunctionNames.Count,
            AggregateOperator.Sum => ExprFunctionNames.Sum,
            AggregateOperator.Min => ExprFunctionNames.Min,
            AggregateOperator.Max => ExprFunctionNames.Max,
            AggregateOperator.Any => ExprFunctionNames.Any,
            AggregateOperator.All => ExprFunctionNames.All,
            _ => throw new InvalidOperationException($"Unsupported aggregate operator '{@operator}'.")
        };
    }

    static int Count(ObservationValue value)
    {
        return value.Kind switch
        {
            ObservationValueKind.Undefined => 0,
            ObservationValueKind.Null => 0,
            ObservationValueKind.String => value.GetString()?.Length ?? 0,
            ObservationValueKind.Array => value.GetArrayLength(),
            _ => 1
        };
    }

    static bool Contains(ObservationValue sequence, ObservationValue value)
    {
        var left = ToEnumerable(sequence).Select(Normalize).ToArray();
        var right = Normalize(value);
        return left.Contains(right);
    }

    static ObservationValue Add(ObservationValue left, ObservationValue right)
    {
        if (left.Kind == ObservationValueKind.String || right.Kind == ObservationValueKind.String)
            return ObservationValue.FromString($"{ConvertToString(left) ?? string.Empty}{ConvertToString(right) ?? string.Empty}");

        return ObservationValue.FromDecimal(ToDecimal(left) + ToDecimal(right));
    }

    static int Compare(ObservationValue left, ObservationValue right)
    {
        var normalizedLeft = Normalize(left);
        var normalizedRight = Normalize(right);

        if (normalizedLeft is null && normalizedRight is null)
            return 0;

        if (normalizedLeft is null)
            return -1;

        if (normalizedRight is null)
            return 1;

        if (normalizedLeft is IComparable comparable && normalizedLeft.GetType() == normalizedRight.GetType())
            return comparable.CompareTo(normalizedRight);

        var leftNumber = ToDecimal(left);
        var rightNumber = ToDecimal(right);
        return leftNumber.CompareTo(rightNumber);
    }

    static decimal ToDecimal(ObservationValue value)
    {
        if (value.Kind is ObservationValueKind.Undefined or ObservationValueKind.Null)
            return 0m;

        if (value.Kind == ObservationValueKind.Bool)
            return value.GetBoolean() ? 1m : 0m;

        if (value.TryGetDecimal(out var decimalValue))
            return decimalValue;

        throw new InvalidOperationException($"Value kind '{value.Kind}' cannot be interpreted as a decimal.");
    }

    static object? Normalize(ObservationValue value)
    {
        return value.Kind switch
        {
            ObservationValueKind.Null => null,
            ObservationValueKind.Undefined => null,
            ObservationValueKind.String => value.GetString(),
            ObservationValueKind.Int64 => value.GetInt64(),
            ObservationValueKind.Double => value.GetDouble(),
            ObservationValueKind.Bool => value.GetBoolean(),
            _ => value
        };
    }

    static bool AreEqual(ObservationValue left, ObservationValue right)
    {
        if (left.Kind == ObservationValueKind.Bytes || right.Kind == ObservationValueKind.Bytes)
        {
            return left.Kind == ObservationValueKind.Bytes
                   && right.Kind == ObservationValueKind.Bytes
                   && left.GetBytes().Span.SequenceEqual(right.GetBytes().Span);
        }

        return Equals(Normalize(left), Normalize(right));
    }

    public static Observation? ToCurrentObservation(Observation root, ObservationValue item)
    {
        if (item.Kind is ObservationValueKind.Undefined or ObservationValueKind.Null)
            return null;

        if (item.Kind == ObservationValueKind.Object && item.Fields is not null)
            return ToSyntheticObservation(root, item.Fields);

        return ToSyntheticObservation(
            root,
            new Dictionary<string, ObservationValue>(StringComparer.Ordinal)
            {
                ["Value"] = item
            });
    }

    static Observation ToSyntheticObservation(Observation root, IReadOnlyDictionary<string, ObservationValue> values)
    {
        return new(
            shapeId: new("__item"),
            id: "__item",
            fields: new Dictionary<string, ObservationValue>(values, StringComparer.Ordinal),
            version: root.Version
            );
    }

    public static IEnumerable<ObservationValue> ToEnumerable(ObservationValue value)
    {
        if (value.Kind is ObservationValueKind.Undefined or ObservationValueKind.Null)
            return [];

        if (value.Kind == ObservationValueKind.Array)
            return value.EnumerateArray();

        return [value];
    }

    static bool ToBoolean(ObservationValue value)
    {
        return value.Kind switch
        {
            ObservationValueKind.Undefined => false,
            ObservationValueKind.Null => false,
            ObservationValueKind.Bool => value.GetBoolean(),
            ObservationValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            _ when value.TryGetDecimal(out var dec) => dec != 0m,
            _ => throw new InvalidOperationException($"Value '{value}' cannot be interpreted as a boolean.")
        };
    }

    static string? ConvertToString(ObservationValue value) => 
        value.ToScalarString(formatProvider: CultureInfo.InvariantCulture, bytesEncoding: ObservationBytesJsonEncoding.Base64String);
}
