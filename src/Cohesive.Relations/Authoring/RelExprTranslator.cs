using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json.Serialization;
using Cohesive.Model;
using Cohesive.Model.Expressions;
using Cohesive.Prelude;
using Cohesive.Relations.Model;

namespace Cohesive.Relations.Authoring;

sealed class RelExprTranslator
{
    readonly IReadOnlyDictionary<ParameterExpression, ParameterBinding> bindings;

    RelExprTranslator(IReadOnlyDictionary<ParameterExpression, ParameterBinding> bindings)
    {
        this.bindings = bindings;
    }

    public static RelExprTranslator ForSource(ParameterExpression sourceParameter, string prefix)
    {
        return new RelExprTranslator(new Dictionary<ParameterExpression, ParameterBinding>
        {
            [sourceParameter] = new SourceBinding(prefix)
        });
    }

    public static RelExprTranslator ForSourceWithJoins(
        ParameterExpression sourceParameter,
        IReadOnlyList<(ParameterExpression Parameter, ShapeId Schema, Expr LeftKeyExpression)> joins,
        string prefix = "")
    {
        Dictionary<ParameterExpression, ParameterBinding> map = new()
        {
            [sourceParameter] = new SourceBinding(prefix)
        };
        foreach (var join in joins)
            map[join.Parameter] = new JoinedBinding(join.Schema, join.LeftKeyExpression);
        return new RelExprTranslator(map);
    }

    public static RelExprTranslator ForJoin(
        ParameterExpression leftParameter,
        ParameterExpression rightParameter,
        ShapeId rightSchema,
        Expr leftKeyExpression)
    {
        return ForSourceWithJoins(
            sourceParameter: leftParameter,
            joins:
            [
                (rightParameter, rightSchema, leftKeyExpression)
            ]);
    }

    public static RelExprTranslator ForGroup(ParameterExpression groupParameter)
    {
        return new RelExprTranslator(new Dictionary<ParameterExpression, ParameterBinding>
        {
            [groupParameter] = new GroupBinding()
        });
    }

    RelExprTranslator WithSourceBinding(ParameterExpression parameter, string prefix)
    {
        Dictionary<ParameterExpression, ParameterBinding> map = new(bindings)
        {
            [parameter] = new SourceBinding(prefix)
        };
        return new RelExprTranslator(map);
    }

    public Expr Translate(Expression expression)
    {
        var stripped = StripConvert(expression);
        if (TryReadCapturedConstant(stripped, out var captured))
            return new ConstantExpr(ToConstant(captured, stripped.Type));

        return stripped switch
        {
            System.Linq.Expressions.ConstantExpression constant => new ConstantExpr(ToConstant(constant.Value, constant.Type)),
            MemberExpression member => TranslateMember(member),
            IndexExpression index => TranslateIndex(index),
            System.Linq.Expressions.UnaryExpression unary when unary.NodeType == ExpressionType.Not => new UnaryExpr(UnaryOperator.Not, Translate(unary.Operand)),
            System.Linq.Expressions.BinaryExpression binary => TranslateBinary(binary),
            System.Linq.Expressions.ConditionalExpression conditional => new ConditionalExpr(
                test: Translate(conditional.Test),
                ifTrue: Translate(conditional.IfTrue),
                ifFalse: Translate(conditional.IfFalse),
                returnType: new OpaqueRuntimeTypeRef("unknown")),
            NewExpression @new => TranslateObject(@new),
            MemberInitExpression init => TranslateObject(init),
            MethodCallExpression call => TranslateCall(call),
            _ => throw new RelationDslException($"Unsupported expression node '{stripped.NodeType}'.")
        };
    }

    Expr TranslateMember(MemberExpression member)
    {
        if (TryTranslateBoundMember(member, out var translated))
            return translated;

        if (TryTranslateIndexedAccess(member, out translated))
            return translated;

        if (TryReadCapturedConstant(member, out var captured))
            return new ConstantExpr(ToConstant(captured, member.Type));

        throw new RelationDslException($"Unsupported member access '{member.Member.Name}'.");
    }

    Expr TranslateIndex(IndexExpression index)
    {
        if (TryTranslateIndexedAccess(index, out var translated))
            return translated;

        throw new RelationDslException("Unsupported index access.");
    }

    BinaryExpr TranslateBinary(System.Linq.Expressions.BinaryExpression binary)
    {
        var op = binary.NodeType switch
        {
            ExpressionType.Equal => BinaryOperator.Eq,
            ExpressionType.NotEqual => BinaryOperator.Ne,
            ExpressionType.GreaterThan => BinaryOperator.Gt,
            ExpressionType.GreaterThanOrEqual => BinaryOperator.Ge,
            ExpressionType.LessThan => BinaryOperator.Lt,
            ExpressionType.LessThanOrEqual => BinaryOperator.Le,
            ExpressionType.AndAlso => BinaryOperator.And,
            ExpressionType.OrElse => BinaryOperator.Or,
            ExpressionType.Add => BinaryOperator.Add,
            ExpressionType.Subtract => BinaryOperator.Sub,
            ExpressionType.Multiply => BinaryOperator.Mul,
            ExpressionType.Divide => BinaryOperator.Div,
            _ => throw new RelationDslException($"Unsupported binary operator '{binary.NodeType}'.")
        };

        return new BinaryExpr(op, Translate(binary.Left), Translate(binary.Right));
    }

    Expr TranslateCall(MethodCallExpression call)
    {
        if (TryTranslateGroupAggregate(call, out var aggregate))
            return aggregate;

        if (TryTranslateIndexerCall(call, out var indexer))
            return indexer;

        if (TryTranslateEnumerableSelect(call, out var select))
            return select;

        if (TryTranslateEnumerableMaterializer(call, out var materialized))
            return materialized;

        if (!call.Method.IsStatic
            && string.Equals(call.Method.Name, nameof(string.Contains), StringComparison.Ordinal)
            && call.Object is not null
            && call.Arguments.Count == 1)
        {
            return Expr.Call(
                ExprFunctionNames.Contains,
                Translate(call.Object),
                Translate(call.Arguments[0]));
        }

        if (!call.Method.IsStatic
            && string.Equals(call.Method.Name, nameof(string.EndsWith), StringComparison.Ordinal)
            && call.Object is not null
            && call.Arguments.Count == 2
            && call.Arguments[1] is ConstantExpression
            {
                Value: StringComparison.Ordinal
            })
        {
            return Expr.EndsWith(
                Translate(call.Object),
                Translate(call.Arguments[0]));
        }

        throw new RelationDslException($"Unsupported method call '{call.Method.DeclaringType?.Name}.{call.Method.Name}'.");
    }

    bool TryTranslateIndexerCall(MethodCallExpression call, out Expr relExpression)
    {
        relExpression = null!;
        if (!IsIndexerGetter(call))
            return false;

        return TryTranslateIndexedAccess(call, out relExpression);
    }

    bool TryTranslateEnumerableSelect(MethodCallExpression call, out Expr relExpression)
    {
        relExpression = null!;
        if (!IsEnumerableMethod(call, nameof(Enumerable.Select)) || call.Arguments.Count != 2)
            return false;

        var lambda = ReadLambda(call.Arguments[1]);
        if (lambda.Parameters.Count != 1)
            throw new RelationDslException("Enumerable.Select(...) selector must have exactly one parameter.");

        var source = Translate(call.Arguments[0]);
        var selector = WithSourceBinding(
            lambda.Parameters[0],
            $"{ExprFieldRoots.CurrentItem}{FieldPath.Separator}").Translate(lambda.Body);
        relExpression = Expr.Call(ExprFunctionNames.Select, source, selector);
        return true;
    }

    bool TryTranslateEnumerableMaterializer(MethodCallExpression call, out Expr relExpression)
    {
        relExpression = null!;
        if (!IsEnumerableMethod(call, nameof(Enumerable.ToArray))
            && !IsEnumerableMethod(call, nameof(Enumerable.ToList)))
        {
            return false;
        }

        if (call.Arguments.Count != 1)
            throw new RelationDslException($"Enumerable.{call.Method.Name}(...) requires exactly one source argument.");

        relExpression = Translate(call.Arguments[0]);
        return true;
    }

    static bool IsEnumerableMethod(MethodCallExpression call, string methodName)
        => call.Method.IsStatic
           && string.Equals(call.Method.Name, methodName, StringComparison.Ordinal)
           && call.Method.DeclaringType == typeof(Enumerable);

    Expr TranslateObject(NewExpression expression)
        => BuildObjectExpression(ReadObjectMembers(expression));

    Expr TranslateObject(MemberInitExpression expression)
        => BuildObjectExpression(ReadObjectMembers(expression));

    Expr BuildObjectExpression(IReadOnlyList<ObjectMemberBinding> members)
    {
        List<Expr> args = [];
        foreach (var member in members)
        {
            args.Add(Expr.Const(member.Name));
            args.Add(Translate(member.Expression));
        }

        return Expr.Call(ExprFunctionNames.Object, [.. args]);
    }

    static IReadOnlyList<ObjectMemberBinding> ReadObjectMembers(NewExpression expression)
    {
        if (expression.Members is not null && expression.Members.Count == expression.Arguments.Count)
        {
            return expression.Arguments
                .Select((argument, index) =>
                    new ObjectMemberBinding(
                        ResolveObjectMemberName(expression.Type, expression.Members[index].Name),
                        argument))
                .ToArray();
        }

        var parameters = expression.Constructor?.GetParameters()
                         ?? throw new RelationDslException("Object construction requires either members or constructor parameter metadata.");
        if (parameters.Length != expression.Arguments.Count)
            throw new RelationDslException("Object constructor arguments do not match constructor parameter count.");

        return expression.Arguments
            .Select((argument, index) =>
                new ObjectMemberBinding(
                    ResolveObjectMemberName(
                        expression.Type,
                        parameters[index].Name ?? throw new RelationDslException("Object constructor parameter names are required.")),
                    argument))
            .ToArray();
    }

    static IReadOnlyList<ObjectMemberBinding> ReadObjectMembers(MemberInitExpression expression)
    {
        return expression.Bindings
            .Select(binding =>
            {
                if (binding is not MemberAssignment assignment)
                    throw new RelationDslException("Object initializer supports only simple member assignments.");

                return new ObjectMemberBinding(
                    ResolveObjectMemberName(expression.Type, binding.Member.Name),
                    assignment.Expression
                );
            })
            .ToArray();
    }

    static string ResolveObjectMemberName(Type objectType, string memberName)
    {
        var property = objectType.GetProperty(memberName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        var attribute = property?.GetCustomAttribute<JsonPropertyNameAttribute>(inherit: true);
        return attribute?.Name ?? memberName;
    }

    bool TryTranslateGroupAggregate(MethodCallExpression call, out Expr relExpression)
    {
        relExpression = null!;
        if (call.Object is null)
            return false;

        var groupObject = StripConvert(call.Object);
        if (groupObject is not ParameterExpression parameter
            || !bindings.TryGetValue(parameter, out var binding)
            || binding is not GroupBinding)
        {
            return false;
        }

        var methodName = call.Method.Name;
        if (string.Equals(methodName, nameof(RelationGroup<int, int>.Count), StringComparison.Ordinal))
        {
            if (call.Arguments.Count != 0)
                throw new RelationDslException("Group.Count() does not accept arguments.");
            relExpression = Expr.Call(
                ExprFunctionNames.Count,
                Expr.Field($"{ExprFieldRoots.CurrentItem}{FieldPath.Separator}Items"));
            return true;
        }

        var aggregate = methodName switch
        {
            nameof(RelationGroup<int, int>.Sum) => ExprFunctionNames.Sum,
            nameof(RelationGroup<int, int>.Min) => ExprFunctionNames.Min,
            nameof(RelationGroup<int, int>.Max) => ExprFunctionNames.Max,
            nameof(RelationGroup<int, int>.Average) => ExprFunctionNames.Avg,
            _ => null
        };
        if (aggregate is null)
            return false;

        if (call.Arguments.Count != 1)
            throw new RelationDslException($"Group.{methodName}(...) requires exactly one selector lambda.");

        var lambda = ReadLambda(call.Arguments[0]);
        if (lambda.Parameters.Count != 1)
            throw new RelationDslException($"Group.{methodName}(...) selector must have exactly one parameter.");
        var selector = ForSource(
            lambda.Parameters[0],
            prefix: $"{ExprFieldRoots.CurrentItem}{FieldPath.Separator}").Translate(lambda.Body);
        relExpression = Expr.Call(
            aggregate,
            Expr.Field($"{ExprFieldRoots.CurrentItem}{FieldPath.Separator}Items"),
            selector);
        return true;
    }

    sealed record ObjectMemberBinding(string Name, Expression Expression);

    bool TryTranslateBoundMember(MemberExpression member, out Expr relExpression)
    {
        relExpression = null!;
        if (!TryReadMemberPath(member, out var rootParameter, out var properties))
            return false;
        if (!bindings.TryGetValue(rootParameter, out var binding))
            return false;

        switch (binding)
        {
            case SourceBinding source:
            {
                if (TryTranslateSourceCount(source, properties, out relExpression))
                    return true;

                var fullPath = BuildSourcePath(source, properties);
                relExpression = Expr.Field(fullPath);
                return true;
            }
            case JoinedBinding joined:
            {
                if (properties.Count != 1)
                    throw new RelationDslException("Joined-side projection supports direct property access only.");

                var fieldIdentity = RelationDslCompiler.ResolveFieldIdentity(properties[0]);
                relExpression = Expr.RelatedField(
                    Expr.Const(joined.RightSchema.Value),
                    joined.LeftKeyExpression,
                    Expr.Const(fieldIdentity));
                return true;
            }
            case GroupBinding:
            {
                if (properties.Count != 1)
                    throw new RelationDslException("Group projection supports direct group members only.");

                if (string.Equals(properties[0].Name, nameof(RelationGroup<int, int>.Key), StringComparison.Ordinal))
                {
                    relExpression = Expr.Field($"{ExprFieldRoots.CurrentItem}{FieldPath.Separator}Id");
                    return true;
                }

                if (string.Equals(properties[0].Name, nameof(RelationGroup<int, int>.Items), StringComparison.Ordinal))
                {
                    relExpression = Expr.Field($"{ExprFieldRoots.CurrentItem}{FieldPath.Separator}Items");
                    return true;
                }

                throw new RelationDslException($"Unsupported group member '{properties[0].Name}'.");
            }
            default:
                throw new RelationDslException($"Unsupported parameter binding '{binding.GetType().Name}'.");
        }
    }

    bool TryTranslateIndexedAccess(Expression expression, out Expr relExpression)
    {
        relExpression = null!;
        if (!TryReadIndexedAccessPath(expression, out var rootParameter, out var segments))
            return false;
        if (!bindings.TryGetValue(rootParameter, out var binding))
            return false;

        if (binding is not SourceBinding source)
            throw new RelationDslException("Indexed access is supported only for source-bound projection paths.");

        relExpression = Expr.Field(BuildSourcePath(source.Prefix, segments));
        return true;
    }

    static bool TryTranslateSourceCount(
        SourceBinding source,
        IReadOnlyList<PropertyInfo> properties,
        out Expr relExpression
    )
    {
        relExpression = null!;
        if (properties.Count < 2)
            return false;

        var countProperty = properties[^1];
        var enumerableProperty = properties[^2];
        if (!string.Equals(countProperty.Name, "Count", StringComparison.Ordinal)
            || countProperty.PropertyType != typeof(int)
            || !IsCountSource(enumerableProperty.PropertyType))
        {
            return false;
        }

        var sourcePath = BuildSourcePath(source, properties.Slice(0, properties.Count - 1));
        relExpression = Expr.Call(ExprFunctionNames.Count, Expr.Field(sourcePath));
        return true;
    }

    static bool IsCountSource(Type type)
    {
        if (type == typeof(string))
            return false;

        return typeof(System.Collections.IEnumerable).IsAssignableFrom(type);
    }

    static string BuildSourcePath(SourceBinding source, IReadOnlyList<PropertyInfo> properties)
    {
        var path = string.Join('.', properties.Select(RelationDslCompiler.ResolveFieldPath));
        return string.IsNullOrWhiteSpace(source.Prefix)
            ? path
            : $"{source.Prefix}{path}";
    }

    static string BuildSourcePath(string prefix, IReadOnlyList<AccessPathSegment> segments)
    {
        var path = string.Join('.', segments.Select(segment => segment switch
        {
            PropertyAccess property => RelationDslCompiler.ResolveFieldPath(property.Property),
            IndexAccess index => index.Index.ToString(CultureInfo.InvariantCulture),
            _ => throw new RelationDslException($"Unsupported access path segment '{segment.GetType().Name}'.")
        }));
        return string.IsNullOrWhiteSpace(prefix)
            ? path
            : $"{prefix}{path}";
    }

    static bool TryReadIndexedAccessPath(
        Expression expression,
        out ParameterExpression rootParameter,
        out IReadOnlyList<AccessPathSegment> segments
    )
    {
        List<AccessPathSegment> chain = [];
        if (!TryReadIndexedAccessPathCore(StripConvert(expression), chain, out rootParameter))
        {
            segments = [];
            return false;
        }

        chain.Reverse();
        segments = chain;
        return chain.Any(x => x is IndexAccess);
    }

    static bool TryReadIndexedAccessPathCore(
        Expression expression,
        List<AccessPathSegment> chain,
        out ParameterExpression rootParameter
    )
    {
        expression = StripConvert(expression);
        switch (expression)
        {
            case ParameterExpression parameter:
                rootParameter = parameter;
                return chain.Count > 0;
            case MemberExpression { Member: PropertyInfo property } member:
                chain.Add(new PropertyAccess(property));
                return TryReadIndexedAccessPathCore(member.Expression!, chain, out rootParameter);
            case IndexExpression index when index.Object is not null && index.Arguments.Count == 1 && TryReadIntIndex(index.Arguments[0], out var indexValue):
                chain.Add(new IndexAccess(indexValue));
                return TryReadIndexedAccessPathCore(index.Object, chain, out rootParameter);
            case MethodCallExpression call when IsIndexerGetter(call) && call.Object is not null && TryReadIntIndex(call.Arguments[0], out var indexValue):
                chain.Add(new IndexAccess(indexValue));
                return TryReadIndexedAccessPathCore(call.Object, chain, out rootParameter);
            default:
                rootParameter = null!;
                return false;
        }
    }

    static bool IsIndexerGetter(MethodCallExpression call)
        => !call.Method.IsStatic
           && string.Equals(call.Method.Name, "get_Item", StringComparison.Ordinal)
           && call.Object is not null
           && call.Arguments.Count == 1;

    static bool TryReadIntIndex(Expression expression, out int index)
    {
        expression = StripConvert(expression);
        if (expression is System.Linq.Expressions.ConstantExpression constant
            && constant.Value is int intValue)
        {
            index = intValue;
            return true;
        }

        if (TryReadCapturedConstant(expression, out var captured) && captured is int capturedValue)
        {
            index = capturedValue;
            return true;
        }

        index = 0;
        return false;
    }

    static bool TryReadMemberPath(
        MemberExpression member,
        out ParameterExpression rootParameter,
        out IReadOnlyList<PropertyInfo> properties
    )
    {
        List<PropertyInfo> chain = [];
        Expression cursor = member;
        while (cursor is MemberExpression memberExpression)
        {
            if (memberExpression.Member is not PropertyInfo property)
                break;
            chain.Add(property);
            cursor = StripConvert(memberExpression.Expression!);
        }

        if (cursor is not ParameterExpression parameter || chain.Count == 0)
        {
            rootParameter = null!;
            properties = [];
            return false;
        }

        chain.Reverse();
        rootParameter = parameter;
        properties = chain;
        return true;
    }

    static LambdaExpression ReadLambda(Expression expression)
    {
        var stripped = StripConvert(expression);
        if (stripped is LambdaExpression lambda)
            return lambda;

        if (stripped is System.Linq.Expressions.UnaryExpression unary
            && unary.NodeType == ExpressionType.Quote
            && unary.Operand is LambdaExpression quoted)
        {
            return quoted;
        }

        if (stripped is System.Linq.Expressions.ConstantExpression constant && constant.Value is LambdaExpression capturedLambda)
            return capturedLambda;

        if (stripped is MemberExpression member
            && TryReadCapturedConstant(member, out var captured)
            && captured is LambdaExpression memberLambda)
        {
            return memberLambda;
        }

        throw new RelationDslException("Expected a lambda expression argument.");
    }

    static bool TryReadCapturedConstant(Expression expression, out object? value)
    {
        switch (expression)
        {
            case System.Linq.Expressions.ConstantExpression constant:
                value = constant.Value;
                return true;
            case MemberExpression member:
                return TryReadCapturedMember(member, out value);
            default:
                value = null;
                return false;
        }
    }

    static bool TryReadCapturedMember(MemberExpression member, out object? value)
    {
        if (member.Expression is null)
        {
            value = ReadMemberValue(member, target: null);
            return true;
        }

        var source = StripConvert(member.Expression);
        if (source is System.Linq.Expressions.ConstantExpression constant)
        {
            value = ReadMemberValue(member, target: constant.Value);
            return true;
        }

        if (source is MemberExpression parent && TryReadCapturedMember(parent, out var parentValue))
        {
            value = ReadMemberValue(member, target: parentValue);
            return true;
        }

        value = null;
        return false;
    }

    static object? ReadMemberValue(MemberExpression member, object? target)
    {
        return member.Member switch
        {
            FieldInfo field => field.GetValue(target),
            PropertyInfo property when property.GetMethod is not null => property.GetValue(target),
            _ => throw new RelationDslException($"Member '{member.Member.Name}' cannot be read as a constant.")
        };
    }

    static ObservationValue ToConstant(object? value, Type? typeHint)
    {
        var hintedType = typeHint is null
            ? null
            : Nullable.GetUnderlyingType(typeHint) ?? typeHint;

        return value switch
        {
            null => ObservationValue.Null,
            _ when hintedType?.IsEnum == true => ObservationValue.FromString(ToEnumString(value, hintedType)),
            string text => ObservationValue.FromString(text),
            int int32 => ObservationValue.FromInt64(int32),
            long int64 => ObservationValue.FromInt64(int64),
            decimal dec => ObservationValue.FromDouble((double)dec),
            double dbl => ObservationValue.FromDouble(dbl),
            float flt => ObservationValue.FromDouble(flt),
            bool b => ObservationValue.FromBool(b),
            Guid guid => ObservationValue.FromString(guid.ToString()),
            DateTime dt => ObservationValue.FromString(dt.ToString("O")),
            DateTimeOffset dto => ObservationValue.FromString(dto.ToString("O")),
            Enum @enum => ObservationValue.FromString(@enum.ToString()),
            _ => throw new RelationDslException($"Constant value type '{value.GetType().Name}' is not supported in relation DSL.")
        };
    }

    static string ToEnumString(object value, Type enumType)
    {
        if (value.GetType().IsEnum)
            return value.ToString()!;

        var enumValue = Enum.ToObject(enumType, value);
        return enumValue.ToString()!;
    }

    public static Expression StripConvert(Expression expression)
    {
        var current = expression;
        while (current is System.Linq.Expressions.UnaryExpression unary
               && (unary.NodeType == ExpressionType.Convert
                   || unary.NodeType == ExpressionType.ConvertChecked
                   || unary.NodeType == ExpressionType.TypeAs))
        {
            current = unary.Operand;
        }

        return current;
    }

    abstract record ParameterBinding;

    abstract record AccessPathSegment;

    sealed record PropertyAccess(PropertyInfo Property) : AccessPathSegment;

    sealed record IndexAccess(int Index) : AccessPathSegment;

    sealed record SourceBinding(string Prefix) : ParameterBinding;

    sealed record JoinedBinding(ShapeId RightSchema, Expr LeftKeyExpression) : ParameterBinding;

    sealed record GroupBinding : ParameterBinding;
}
