using System.Collections;
using System.Collections.Immutable;
using System.Linq.Expressions;
using System.Reflection;
using Cohesive.Transitions.Model;
using TransitionBindingIds = Cohesive.Transitions.IR.TransitionBindingIds;

namespace Cohesive.Transitions.Authoring;

internal sealed class TransitionExpressionTranslator<TEntity, TParameters> where TEntity : Entity
{
    readonly Dictionary<string, FieldDefinition> fieldByName;
    readonly HashSet<string> parameterNames;
    readonly bool allowCapturedValues;
    readonly IClrTypeRefMapper? typeRefMapper;
    ParameterExpression? currentStateParameter;
    ParameterExpression? currentSnapshotParameter;

    public TransitionExpressionTranslator(
        EntityDefinition entityDefinition,
        IReadOnlySet<string> parameterNames,
        bool allowCapturedValues = true,
        IClrTypeRefMapper? typeRefMapper = null)
        : this(
            Guard.RequireNotNull(entityDefinition).Shape,
            parameterNames,
            allowCapturedValues,
            typeRefMapper)
    {
    }

    public TransitionExpressionTranslator(
        Shape entityShape,
        IReadOnlySet<string> parameterNames,
        bool allowCapturedValues = true,
        IClrTypeRefMapper? typeRefMapper = null)
    {
        ArgumentNullException.ThrowIfNull(argument: entityShape);
        ArgumentNullException.ThrowIfNull(argument: parameterNames);
        fieldByName = entityShape.Fields.ToDictionary(x => x.Name.Value, StringComparer.Ordinal);
        this.parameterNames = new(parameterNames, StringComparer.Ordinal);
        this.allowCapturedValues = allowCapturedValues;
        this.typeRefMapper = typeRefMapper;
    }

    public Expr Translate(LambdaExpression lambda)
    {
        ArgumentNullException.ThrowIfNull(lambda);
        ParameterExpression entityParameter;
        ParameterExpression parametersParameter;
        ParameterExpression? stateParameter = null;
        ParameterExpression? snapshotParameter = null;
        switch (lambda.Parameters.Count)
        {
            case 1:
                parametersParameter = Expression.Parameter(typeof(object), "parameters");
                if (IsSnapshotParameter(lambda.Parameters[0]))
                {
                    snapshotParameter = lambda.Parameters[0];
                    entityParameter = Expression.Parameter(typeof(TEntity), "entity");
                }
                else
                {
                    entityParameter = lambda.Parameters[0];
                }

                break;

            case 2:
                parametersParameter = lambda.Parameters[1];
                if (IsSnapshotParameter(lambda.Parameters[0]))
                {
                    snapshotParameter = lambda.Parameters[0];
                    entityParameter = Expression.Parameter(typeof(TEntity), "entity");
                }
                else
                {
                    entityParameter = lambda.Parameters[0];
                }

                break;
            case 3:
                entityParameter = lambda.Parameters[0];
                stateParameter = lambda.Parameters[1];
                parametersParameter = lambda.Parameters[2];
                if (stateParameter.Type != typeof(EntityState))
                {
                    throw new TransitionExpressionTranslationException("Transition state-aware expressions must declare EntityState as the second lambda parameter.");
                }

                break;
            default:
                throw new TransitionExpressionTranslationException("Transition expressions must declare either two lambda parameters (entity or entity snapshot, and transition parameters) or three (entity, entity state, and transition parameters).");
        }

        var previousStateParameter = currentStateParameter;
        var previousSnapshotParameter = currentSnapshotParameter;
        currentStateParameter = stateParameter;
        currentSnapshotParameter = snapshotParameter;
        try
        {
            return Translate(
                StripConvert(expression: lambda.Body),
                entityParameter: entityParameter,
                parametersParameter: parametersParameter
                );
        }
        finally
        {
            currentStateParameter = previousStateParameter;
            currentSnapshotParameter = previousSnapshotParameter;
        }
    }

    public Expr TranslateInput<TValue>(Expression<Func<TParameters, TValue>> expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        return Translate(
            StripConvert(expression.Body),
            Expression.Parameter(typeof(TEntity), "entity"),
            expression.Parameters[0]);
    }

    public string TranslateFieldTarget<TValue>(Expression<Func<TEntity, Field<TValue>>> fieldExpression)
    {
        ArgumentNullException.ThrowIfNull(fieldExpression);
        var body = StripConvert(fieldExpression.Body);
        if (body is not MemberExpression member)
            throw new TransitionExpressionTranslationException("Field target must be a direct entity field access like 'e => e.Status'.");

        var field = ResolveEntityField(member, fieldExpression.Parameters[0]);
        return ToFieldIdentity(field);
    }

    public string TranslateCollectionFieldTarget<TValue>(Expression<Func<TEntity, Field<IReadOnlyList<TValue>>>> fieldExpression)
    {
        ArgumentNullException.ThrowIfNull(fieldExpression);
        var body = StripConvert(fieldExpression.Body);
        if (body is not MemberExpression member)
            throw new TransitionExpressionTranslationException("Collection field target must be a direct entity field access like 'e => e.Stops'.");

        var field = ResolveEntityField(member, fieldExpression.Parameters[0]);
        if (field.Cardinality != FieldCardinality.Many)
            throw new TransitionExpressionTranslationException($"Field '{field.Name.Value}' must be declared with Many cardinality for collection updates.");

        return ToFieldIdentity(field);
    }

    Expr Translate(Expression expression, ParameterExpression entityParameter, ParameterExpression parametersParameter, Type? constantTypeHint = null)
    {
        if (allowCapturedValues && TryTranslateCapturedConstant(expression, out var captured))
            return new ConstantExpr(ToConstant(captured, constantTypeHint ?? expression.Type));

        switch (expression)
        {
            case ParameterExpression parameter
                when !allowCapturedValues && ReferenceEquals(parameter, parametersParameter):
                return Expr.BoundValue(TransitionBindingIds.Input);

            case ConstantExpression constant:
                return new ConstantExpr(ToConstant(constant.Value, constantTypeHint ?? constant.Type));

            case MemberExpression member:
                return TranslateMember(
                    member,
                    entityParameter: entityParameter,
                    parametersParameter: parametersParameter,
                    constantTypeHint: constantTypeHint
                    );

            case UnaryExpression unary when unary.NodeType is ExpressionType.Not:
                return new UnaryExpr(
                    Operator: UnaryOperator.Not,
                    Operand: Translate(
                        StripConvert(expression: unary.Operand),
                        entityParameter: entityParameter,
                        parametersParameter: parametersParameter
                        ));

            case BinaryExpression binary:
                return TranslateBinary(
                    binary: binary,
                    entityParameter: entityParameter,
                    parametersParameter: parametersParameter
                    );

            case ConditionalExpression conditional:
                return TranslateConditional(
                    conditional: conditional,
                    entityParameter: entityParameter,
                    parametersParameter: parametersParameter
                    );

            case MethodCallExpression call:
                return TranslateCall(
                    call: call,
                    entityParameter: entityParameter,
                    parametersParameter: parametersParameter
                    );

            case NewExpression @new:
                return TranslateNew(
                    newExpr: @new,
                    entityParameter: entityParameter,
                    parametersParameter: parametersParameter
                    );

            case MemberInitExpression init:
                return TranslateMemberInit(
                    init: init,
                    entityParameter: entityParameter,
                    parametersParameter: parametersParameter
                    );
        }

        throw new TransitionExpressionTranslationException($"Unsupported expression node '{expression.NodeType}'.");
    }

    Expr TranslateMember(MemberExpression member, ParameterExpression entityParameter, ParameterExpression parametersParameter, Type? constantTypeHint = null)
    {
        if (TryResolveEntityFieldReference(member, entityParameter, out var field))
            return new FieldExpr(CreateFieldPath(field));

        if (TryResolveStateEntityId(member, out var entityIdExpr))
            return entityIdExpr;

        if (TryResolveSnapshotEntityId(member, out var snapshotEntityIdExpr))
            return snapshotEntityIdExpr;

        if (TryResolveTransitionParameterPath(member, parametersParameter, out var parameterPath))
        {
            return parameterPath.Length == 1
                ? new ParameterExpr(parameterPath[0])
                : new FieldExpr(
                    new([.. parameterPath.Select(FieldPathSegment.ForField)]),
                    TransitionBindingIds.Input);
        }

        if (TryTranslateCountProperty(
                member: member,
                entityParameter: entityParameter,
                parametersParameter: parametersParameter,
                out var countExpr))
        {
            return countExpr;
        }

        if (allowCapturedValues && TryTranslateCapturedConstant(expression: member, out var captured))
            return new ConstantExpr(Value: ToConstant(captured, constantTypeHint ?? member.Type));

        if (!allowCapturedValues && IsCapturedMember(member))
        {
            throw new TransitionExpressionTranslationException(
                $"Captured member '{member.Member.Name}' is not portable canonical Transition semantics. "
                + "Declare the value as typed Transition input or an authored local binding.");
        }

        throw new TransitionExpressionTranslationException($"Unsupported member access '{member.Member.Name}'.");
    }

    Expr TranslateBinary(BinaryExpression binary, ParameterExpression entityParameter, ParameterExpression parametersParameter)
    {
        if (binary.NodeType == ExpressionType.Add && IsStringConcatenation(binary))
        {
            return TranslateStringConcatenation(
                binary: binary,
                entityParameter: entityParameter,
                parametersParameter: parametersParameter
                );
        }

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
            _ => throw new TransitionExpressionTranslationException($"Unsupported binary operator '{binary.NodeType}'.")
        };

        return new BinaryExpr(
            op,
            Left: Translate(
                StripConvert(binary.Left),
                entityParameter: entityParameter,
                parametersParameter: parametersParameter,
                constantTypeHint: ResolveEnumConstantHint(StripConvert(binary.Left), StripConvert(binary.Right))
                ),
            Right: Translate(
                StripConvert(binary.Right),
                entityParameter: entityParameter,
                parametersParameter: parametersParameter,
                constantTypeHint: ResolveEnumConstantHint(StripConvert(binary.Right), StripConvert(binary.Left))
                )
            );
    }

    Expr TranslateConditional(ConditionalExpression conditional, ParameterExpression entityParameter, ParameterExpression parametersParameter)
    {
        return new ConditionalExpr(
            test: Translate(
                expression: StripConvert(expression: conditional.Test),
                entityParameter: entityParameter,
                parametersParameter: parametersParameter),
            ifTrue: Translate(
                expression: StripConvert(expression: conditional.IfTrue),
                entityParameter: entityParameter,
                parametersParameter: parametersParameter),
            ifFalse: Translate(
                expression: StripConvert(expression: conditional.IfFalse),
                entityParameter: entityParameter,
                parametersParameter: parametersParameter),
            returnType: ReturnType(conditional.Type)
            );
    }

    Expr TranslateCall(MethodCallExpression call, ParameterExpression entityParameter, ParameterExpression parametersParameter)
    {
        if (TryTranslateFieldMembershipCall(
                call,
                entityParameter: entityParameter,
                out var membershipExpr))
        {
            return membershipExpr;
        }

        if (TryTranslateStringConcatCall(
                call: call,
                entityParameter: entityParameter,
                parametersParameter: parametersParameter,
                out var concatExpr))
        {
            return concatExpr;
        }

        if (TryTranslateCollectionContainsCall(
                call,
                entityParameter,
                parametersParameter,
                out var containsExpr))
        {
            return containsExpr;
        }

        if (TryResolveStateFieldGet(call, entityParameter, out var field))
            return new FieldExpr(CreateFieldPath(field));

        if (TryResolveSnapshotFieldGet(call, out field))
            return new FieldExpr(CreateFieldPath(field));

        if (TryTranslateCountCall(
                call,
                entityParameter: entityParameter,
                parametersParameter: parametersParameter,
                out var countExpr))
        {
            return countExpr;
        }

        throw new TransitionExpressionTranslationException($"Unsupported method call '{call.Method.DeclaringType?.Name}.{call.Method.Name}'.");
    }

    Expr TranslateNew(
        NewExpression newExpr,
        ParameterExpression entityParameter,
        ParameterExpression parametersParameter
        )
    {
        if (allowCapturedValues)
        {
            IReadOnlyList<string> capturedMemberNames;
            if (newExpr.Members is not null && newExpr.Members.Count == newExpr.Arguments.Count)
            {
                capturedMemberNames = [.. newExpr.Members.Select(static member => member.Name)];
            }
            else
            {
                var parameters = newExpr.Constructor?.GetParameters();
                if (parameters is null || parameters.Length != newExpr.Arguments.Count)
                {
                    throw new TransitionExpressionTranslationException(
                        "Only named object creation is supported for effect payload expressions.");
                }

                capturedMemberNames =
                [
                    .. parameters.Select(static parameter => parameter.Name
                        ?? throw new TransitionExpressionTranslationException(
                            "Effect payload constructor parameters must be named."))
                ];
            }

            List<Expr> capturedArguments = [];
            for (var index = 0; index < capturedMemberNames.Count; index++)
            {
                capturedArguments.Add(Expr.Const(value: capturedMemberNames[index]));
                capturedArguments.Add(Translate(
                    expression: StripConvert(expression: newExpr.Arguments[index]),
                    entityParameter: entityParameter,
                    parametersParameter: parametersParameter));
            }

            return Expr.Call(function: ExprFunctionNames.Object, [.. capturedArguments]);
        }

        IReadOnlyList<string> memberNames;
        if (newExpr.Members is not null && newExpr.Members.Count == newExpr.Arguments.Count)
        {
            memberNames = [.. newExpr.Members.Select(DefaultClrTypeRefMapper.GetSerializedMemberName)];
        }
        else
        {
            var constructor = newExpr.Constructor;
            var parameters = constructor?.GetParameters();
            if (parameters is null || parameters.Length != newExpr.Arguments.Count)
                throw new TransitionExpressionTranslationException("Only named object creation is supported for effect payload expressions.");

            var properties = ShapeTypeInspector.GetReadableProperties(newExpr.Type);
            memberNames =
            [
                .. parameters.Select(parameter => ResolveConstructorMemberName(
                    parameter,
                    properties,
                    newExpr.Type))
            ];
        }

        var members = memberNames
            .Select((name, index) => (Name: name, Index: index))
            .OrderBy(static member => member.Name, StringComparer.Ordinal)
            .ToArray();
        if (members.Select(static member => member.Name).Distinct(StringComparer.Ordinal).Count() != members.Length)
        {
            throw new TransitionExpressionTranslationException(
                $"Object creation for '{newExpr.Type.Name}' maps more than one value to the same semantic field name.");
        }

        List<Expr> arguments = [];
        foreach (var member in members)
        {
            arguments.Add(Expr.Const(value: member.Name));
            arguments.Add(item: Translate(
                expression: StripConvert(expression: newExpr.Arguments[member.Index]),
                entityParameter: entityParameter,
                parametersParameter: parametersParameter));
        }

        return new CallExpr(
            ExprFunctionNames.Object,
            [.. arguments],
            ReturnType(newExpr.Type));
    }

    Expr TranslateMemberInit(
        MemberInitExpression init,
        ParameterExpression entityParameter,
        ParameterExpression parametersParameter
        )
    {
        if (allowCapturedValues)
        {
            List<Expr> capturedArguments = [];
            foreach (var binding in init.Bindings)
            {
                if (binding is not MemberAssignment assignment)
                {
                    throw new TransitionExpressionTranslationException(
                        message: "Only simple member assignments are supported in object initializer payload expressions.");
                }

                capturedArguments.Add(item: Expr.Const(value: binding.Member.Name));
                capturedArguments.Add(item: Translate(
                    expression: StripConvert(expression: assignment.Expression),
                    entityParameter: entityParameter,
                    parametersParameter: parametersParameter));
            }

            return Expr.Call(function: ExprFunctionNames.Object, arguments: [.. capturedArguments]);
        }

        List<(string Name, Expression Value)> assignments = [];
        foreach (var binding in init.Bindings)
        {
            if (binding is not MemberAssignment assignment)
            {
                throw new TransitionExpressionTranslationException(
                    message: "Only simple member assignments are supported in object initializer payload expressions.");
            }

            assignments.Add((DefaultClrTypeRefMapper.GetSerializedMemberName(binding.Member), assignment.Expression));
        }

        assignments.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));
        for (var index = 1; index < assignments.Count; index++)
        {
            if (string.Equals(assignments[index - 1].Name, assignments[index].Name, StringComparison.Ordinal))
            {
                throw new TransitionExpressionTranslationException(
                    $"Object initializer for '{init.Type.Name}' assigns semantic field '{assignments[index].Name}' more than once.");
            }
        }

        List<Expr> arguments = [];
        foreach (var assignment in assignments)
        {
            arguments.Add(item: Expr.Const(value: assignment.Name));
            arguments.Add(item: Translate(
                expression: StripConvert(expression: assignment.Value),
                entityParameter: entityParameter,
                parametersParameter: parametersParameter));
        }

        return new CallExpr(
            ExprFunctionNames.Object,
            [.. arguments],
            ReturnType(init.Type));
    }

    Expr TranslateStringConcatenation(BinaryExpression binary, ParameterExpression entityParameter, ParameterExpression parametersParameter)
    {
        List<Expr> arguments = [];
        CollectStringConcatArguments(
            expression: binary,
            arguments: arguments,
            entityParameter: entityParameter,
            parametersParameter: parametersParameter);
        return new CallExpr(
            ExprFunctionNames.Concat,
            [.. arguments],
            ReturnType(typeof(string)));
    }

    void CollectStringConcatArguments(Expression expression, List<Expr> arguments, ParameterExpression entityParameter, ParameterExpression parametersParameter)
    {
        var stripped = StripConvert(expression);
        if (stripped is BinaryExpression binary
            && binary.NodeType == ExpressionType.Add
            && IsStringConcatenation(binary))
        {
            CollectStringConcatArguments(
                expression: binary.Left,
                arguments: arguments,
                entityParameter: entityParameter,
                parametersParameter: parametersParameter);
            CollectStringConcatArguments(
                expression: binary.Right,
                arguments: arguments,
                entityParameter: entityParameter,
                parametersParameter: parametersParameter);
            return;
        }

        if (!IsStringExpression(stripped))
        {
            throw new TransitionExpressionTranslationException(
                "String concatenation is only supported for string operands.");
        }

        arguments.Add(Translate(
            expression: stripped,
            entityParameter: entityParameter,
            parametersParameter: parametersParameter));
    }

    FieldDefinition ResolveEntityField(MemberExpression member, ParameterExpression entityParameter)
    {
        var source = StripConvert(expression: member.Expression ?? throw new TransitionExpressionTranslationException(message: "Entity field access requires an entity instance."));
        if (!ReferenceEquals(source, entityParameter))
            throw new TransitionExpressionTranslationException(message: "Field access must reference the entity lambda parameter.");

        if (member.Member is not PropertyInfo property)
            throw new TransitionExpressionTranslationException(message: $"Field access '{member.Member.Name}' is not a property.");

        if (!IsFieldProperty(type: property.PropertyType))
            throw new TransitionExpressionTranslationException(message: $"Property '{property.Name}' is not a semantic field.");

        if (!fieldByName.TryGetValue(property.Name, out var field))
            throw new TransitionExpressionTranslationException(message: $"Entity definition does not declare a field named '{property.Name}'.");

        return field;
    }

    static string ToFieldIdentity(FieldDefinition field) => field.Name.Value;

    static FieldPath CreateFieldPath(FieldDefinition field) => FieldPath.FromField(ToFieldIdentity(field));

    bool TryResolveEntityFieldReference(MemberExpression member, ParameterExpression entityParameter, out FieldDefinition field)
    {
        field = null!;
        var source = member.Expression is null ? null : StripConvert(member.Expression);
        if (!ReferenceEquals(source, entityParameter))
            return false;

        if (member.Member is not PropertyInfo property || !IsFieldProperty(property.PropertyType))
            return false;

        field = ResolveEntityField(member: member, entityParameter: entityParameter);
        return true;
    }

    bool TryResolveStateEntityId(MemberExpression member, out Expr expression)
    {
        expression = null!;
        if (currentStateParameter is null)
            return false;

        if (!string.Equals(member.Member.Name, nameof(EntityId.Value), StringComparison.Ordinal))
            return false;

        if (member.Member is not PropertyInfo property || property.PropertyType != typeof(string))
            return false;

        if (member.Expression is not MemberExpression entityIdMember)
            return false;

        if (!string.Equals(entityIdMember.Member.Name, nameof(EntityState.EntityId), StringComparison.Ordinal))
            return false;

        if (entityIdMember.Member is not PropertyInfo entityIdProperty || entityIdProperty.PropertyType != typeof(EntityId))
            return false;

        var source = entityIdMember.Expression is null ? null : StripConvert(entityIdMember.Expression);
        if (!ReferenceEquals(source, currentStateParameter))
            return false;

        expression = Expr.Call(ExprFunctionNames.EntityId);
        return true;
    }

    bool TryResolveSnapshotEntityId(MemberExpression member, out Expr expression)
    {
        expression = null!;
        if (currentSnapshotParameter is null)
            return false;

        if (!string.Equals(member.Member.Name, nameof(EntityId.Value), StringComparison.Ordinal))
            return false;

        if (member.Member is not PropertyInfo property || property.PropertyType != typeof(string))
            return false;

        if (member.Expression is not MemberExpression entityIdMember)
            return false;

        if (!string.Equals(entityIdMember.Member.Name, nameof(EntitySnapshot<TEntity>.EntityId), StringComparison.Ordinal))
            return false;

        if (entityIdMember.Member is not PropertyInfo entityIdProperty || entityIdProperty.PropertyType != typeof(EntityId))
            return false;

        var source = entityIdMember.Expression is null ? null : StripConvert(entityIdMember.Expression);
        if (!ReferenceEquals(source, currentSnapshotParameter))
            return false;

        expression = Expr.Call(ExprFunctionNames.EntityId);
        return true;
    }

    bool TryResolveStateFieldGet(MethodCallExpression call, ParameterExpression entityParameter, out FieldDefinition field)
    {
        field = null!;
        if (currentStateParameter is null)
            return false;

        if (!string.Equals(call.Method.Name, nameof(Field<int>.Get), StringComparison.Ordinal))
            return false;

        if (call.Object is not MemberExpression fieldMember || call.Arguments.Count != 1)
            return false;

        var stateArgument = StripConvert(call.Arguments[0]);
        if (!ReferenceEquals(stateArgument, currentStateParameter))
            return false;

        field = ResolveEntityField(fieldMember, entityParameter);
        return true;
    }

    bool TryResolveSnapshotFieldGet(MethodCallExpression call, out FieldDefinition field)
    {
        field = null!;
        if (currentSnapshotParameter is null)
            return false;

        if (!string.Equals(call.Method.Name, nameof(EntitySnapshot<TEntity>.Get), StringComparison.Ordinal))
            return false;

        if (call.Object is null || call.Arguments.Count != 1)
            return false;

        var source = StripConvert(call.Object);
        if (!ReferenceEquals(source, currentSnapshotParameter))
            return false;

        if (!TryResolveSnapshotFieldSelector(call.Arguments[0], out field))
        {
            throw new TransitionExpressionTranslationException("Snapshot.Get(...) requires a direct entity field selector like 'snapshot.Get(e => e.Status)'.");
        }

        return true;
    }

    bool TryResolveTransitionParameterPath(
        MemberExpression member,
        ParameterExpression parametersParameter,
        out string[] parameterPath)
    {
        List<MemberInfo> reversedMembers = [];
        Expression? current = member;
        while (current is MemberExpression currentMember)
        {
            reversedMembers.Add(currentMember.Member);
            current = currentMember.Expression is null
                ? null
                : StripConvert(currentMember.Expression);
        }

        if (!ReferenceEquals(current, parametersParameter))
        {
            parameterPath = [];
            return false;
        }

        reversedMembers.Reverse();
        List<string> reversedPath = new(reversedMembers.Count);
        foreach (var parameterMember in reversedMembers)
        {
            if (parameterMember is not PropertyInfo property)
            {
                throw new TransitionExpressionTranslationException(
                    $"Transition parameter reference '{parameterMember.Name}' is not a property.");
            }

            reversedPath.Add(allowCapturedValues
                ? property.Name
                : DefaultClrTypeRefMapper.GetSerializedMemberName(property));
        }

        if (reversedPath.Count == 0 || !parameterNames.Contains(reversedPath[0]))
        {
            var undeclared = reversedPath.Count == 0 ? member.Member.Name : reversedPath[0];
            throw new TransitionExpressionTranslationException(
                $"Transition parameter '{undeclared}' is not declared.");
        }

        parameterPath = [.. reversedPath];
        return true;
    }

    bool TryTranslateCollectionContainsCall(
        MethodCallExpression call,
        ParameterExpression entityParameter,
        ParameterExpression parametersParameter,
        out Expr expression)
    {
        expression = null!;
        Expression? collection = null;
        Expression? candidate = null;
        Type? elementType = null;

        if (call.Method.IsStatic
            && call.Method.DeclaringType == typeof(Enumerable)
            && string.Equals(call.Method.Name, nameof(Enumerable.Contains), StringComparison.Ordinal)
            && call.Arguments.Count == 2)
        {
            collection = call.Arguments[0];
            candidate = call.Arguments[1];
            elementType = call.Method.IsGenericMethod
                ? call.Method.GetGenericArguments()[0]
                : null;
        }
        else if (call.Method.IsStatic
                 && call.Method.DeclaringType == typeof(FieldCollectionExpressionExtensions)
                 && string.Equals(
                     call.Method.Name,
                     nameof(FieldCollectionExpressionExtensions.Contains),
                     StringComparison.Ordinal)
                 && call.Arguments.Count == 2)
        {
            collection = call.Arguments[0];
            candidate = call.Arguments[1];
            elementType = call.Method.IsGenericMethod
                ? call.Method.GetGenericArguments()[0]
                : null;
        }
        if (collection is null || candidate is null)
            return false;

        expression = new CallExpr(
            ExprFunctionNames.Contains,
            [
                Translate(
                    StripConvert(collection),
                    entityParameter,
                    parametersParameter),
                Translate(
                    StripConvert(candidate),
                    entityParameter,
                    parametersParameter,
                    constantTypeHint: elementType)
            ],
            ReturnType(typeof(bool)));
        return true;
    }

    bool TryTranslateFieldMembershipCall(MethodCallExpression call, ParameterExpression entityParameter, out Expr expression)
    {
        expression = null!;

        var methodName = call.Method.Name;
        if (!string.Equals(methodName, nameof(Field<int>.IsOneOf), StringComparison.Ordinal) && !string.Equals(methodName, nameof(Field<int>.IsNotOneOf), StringComparison.Ordinal))
        {
            return false;
        }

        if (call.Object is null || call.Arguments.Count != 1)
            return false;

        if (StripConvert(call.Object) is not MemberExpression fieldMember || !TryResolveEntityFieldReference(fieldMember, entityParameter, out var field))
        {
            return false;
        }

        if (!TryResolveMembershipValues(call.Arguments[0], out var values))
        {
            throw new TransitionExpressionTranslationException($"'{methodName}' requires a constant set of comparison values.");
        }

        if (values.Count == 0)
        {
            throw new TransitionExpressionTranslationException($"'{methodName}' requires at least one comparison value.");
        }

        var fieldValueType = call.Method.DeclaringType?.IsGenericType == true
            ? call.Method.DeclaringType.GetGenericArguments()[0]
            : null;

        var membership = Expr.Eq(
            Expr.Field(CreateFieldPath(field)),
            Expr.Const(ToConstant(values[0], fieldValueType))
            );

        for (var i = 1; i < values.Count; i++)
        {
            membership = Expr.Or(
                membership,
                Expr.Eq(
                    Expr.Field(CreateFieldPath(field)),
                    Expr.Const(ToConstant(values[i], fieldValueType))
                    )
                );
        }

        expression = string.Equals(methodName, nameof(Field<int>.IsNotOneOf), StringComparison.Ordinal)
            ? Expr.Not(membership)
            : membership;
        return true;
    }

    bool TryTranslateCountProperty(MemberExpression member, ParameterExpression entityParameter, ParameterExpression parametersParameter, out Expr expression)
    {
        expression = null!;
        if (!string.Equals(a: member.Member.Name, b: "Count", comparisonType: StringComparison.Ordinal))
            return false;

        if (member.Member is not PropertyInfo property || property.PropertyType != typeof(int))
            return false;

        if (member.Expression is null)
            return false;

        var collectionExpression = StripConvert(member.Expression);
        if (collectionExpression is MemberExpression fieldMember
            && TryResolveEntityFieldReference(fieldMember, entityParameter, out var field)
            && field.Cardinality != FieldCardinality.Many)
        {
            throw new TransitionExpressionTranslationException($"Count is only supported for collection fields. Field '{field.Name.Value}' is not declared with Many cardinality.");
        }

        expression = new CallExpr(
            ExprFunctionNames.Count,
            [Translate(
                expression: collectionExpression,
                entityParameter: entityParameter,
                parametersParameter: parametersParameter)],
            ReturnType(typeof(long)));
        return true;
    }

    bool TryResolveSnapshotFieldSelector(Expression expression, out FieldDefinition field)
    {
        field = null!;
        var selector = StripConvert(expression);
        if (selector is UnaryExpression quote && quote.NodeType == ExpressionType.Quote)
            selector = quote.Operand;

        if (selector is not LambdaExpression lambda || lambda.Parameters.Count != 1)
            return false;

        var body = StripConvert(lambda.Body);
        if (body is not MemberExpression member)
            return false;

        field = ResolveEntityField(member, lambda.Parameters[0]);
        return true;
    }

    static bool IsSnapshotParameter(ParameterExpression parameter) =>
        parameter.Type == typeof(EntitySnapshot<TEntity>);

    bool TryTranslateCountCall(MethodCallExpression call, ParameterExpression entityParameter, ParameterExpression parametersParameter, out Expr expression)
    {
        expression = null!;
        if (!string.Equals(a: call.Method.Name, b: "Count", comparisonType: StringComparison.Ordinal))
            return false;

        Expression? collectionExpression = null;
        if (call.Method.IsStatic && call.Method.DeclaringType == typeof(Enumerable) && call.Arguments.Count == 1)
        {
            collectionExpression = call.Arguments[0];
        }
        else if (!call.Method.IsStatic && call.Arguments.Count == 0 && call.Object is not null)
        {
            collectionExpression = call.Object;
        }

        if (collectionExpression is null)
            return false;

        expression = new CallExpr(
            ExprFunctionNames.Count,
            [Translate(
                expression: StripConvert(expression: collectionExpression),
                entityParameter: entityParameter,
                parametersParameter: parametersParameter)],
            ReturnType(typeof(long)));
        return true;
    }

    bool TryTranslateStringConcatCall(MethodCallExpression call, ParameterExpression entityParameter, ParameterExpression parametersParameter, out Expr expression)
    {
        expression = null!;
        if (!IsStringConcatCall(call))
            return false;

        List<Expr> arguments = [];
        foreach (var argument in call.Arguments)
        {
            CollectStringConcatArguments(
                expression: argument,
                arguments: arguments,
                entityParameter: entityParameter,
                parametersParameter: parametersParameter
                );
        }

        expression = new CallExpr(
            ExprFunctionNames.Concat,
            [.. arguments],
            ReturnType(typeof(string)));
        return true;
    }

    static bool IsStringConcatenation(BinaryExpression expression) =>
        expression.Type == typeof(string) || IsStringConcatMethod(expression.Method);

    static bool IsStringConcatCall(MethodCallExpression call) =>
        call.Method.IsStatic && IsStringConcatMethod(call.Method);

    static bool IsStringConcatMethod(MethodInfo? method) =>
        method is not null && method.DeclaringType == typeof(string) && string.Equals(method.Name, nameof(string.Concat), StringComparison.Ordinal);

    static bool IsStringExpression(Expression expression) =>
        UnwrapSemanticValueType(expression.Type) == typeof(string);

    static bool IsFieldProperty(Type type) =>
        type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Field<>);

    static Expression StripConvert(Expression expression)
    {
        var current = expression;
        while (current is UnaryExpression unary && unary.NodeType is ExpressionType.Convert or ExpressionType.ConvertChecked or ExpressionType.TypeAs)
        {
            current = unary.Operand;
        }

        return current;
    }

    static bool TryTranslateCapturedConstant(Expression expression, out object? value)
    {
        switch (expression)
        {
            case ConstantExpression constant:
                value = constant.Value;
                return true;

            case MemberExpression member:
                return TryReadCapturedMember(member: member, out value);
        }

        value = null;
        return false;
    }

    static bool IsCapturedMember(MemberExpression member)
    {
        if (member.Expression is null)
            return true;

        var source = StripConvert(member.Expression);
        return source is ConstantExpression
               || source is MemberExpression parent && IsCapturedMember(parent);
    }

    static string ResolveConstructorMemberName(
        ParameterInfo parameter,
        IReadOnlyList<PropertyInfo> properties,
        Type constructedType)
    {
        var parameterName = parameter.Name
            ?? throw new TransitionExpressionTranslationException(
                $"Constructor parameters for '{constructedType.Name}' must be named.");
        PropertyInfo? resolved = null;
        foreach (var property in properties)
        {
            if (!string.Equals(property.Name, parameterName, StringComparison.OrdinalIgnoreCase)
                || property.PropertyType != parameter.ParameterType)
            {
                continue;
            }

            if (resolved is not null)
            {
                throw new TransitionExpressionTranslationException(
                    $"Constructor parameter '{parameterName}' on '{constructedType.Name}' maps to more than one readable property.");
            }

            resolved = property;
        }

        return resolved is null
            ? parameterName
            : DefaultClrTypeRefMapper.GetSerializedMemberName(resolved);
    }

    TypeRef ReturnType(Type type) =>
        typeRefMapper?.Map(type, nullability: null) ?? new OpaqueRuntimeTypeRef("unknown");

    bool TryResolveMembershipValues(Expression expression, out IReadOnlyList<object?> values)
    {
        switch (StripConvert(expression))
        {
            case NewArrayExpression newArray:
                List<object?> arrayValues = [];
                foreach (var element in newArray.Expressions)
                {
                    if (!TryResolveMembershipConstant(element, out var elementValue))
                    {
                        values = [];
                        return false;
                    }

                    arrayValues.Add(elementValue);
                }

                values = arrayValues;
                return true;

            default:
                if (!TryResolveMembershipConstant(expression, out var captured) || captured is null)
                {
                    values = [];
                    return false;
                }

                if (captured is string)
                {
                    values = [captured];
                    return true;
                }

                if (captured is IEnumerable enumerable)
                {
                    List<object?> capturedValues = [];
                    foreach (var item in enumerable)
                        capturedValues.Add(item);

                    values = capturedValues;
                    return true;
                }

                values = [captured];
                return true;
        }
    }

    bool TryResolveMembershipConstant(Expression expression, out object? value)
    {
        var candidate = StripConvert(expression);
        if (candidate is ConstantExpression constant)
        {
            value = constant.Value;
            return true;
        }

        if (allowCapturedValues)
            return TryTranslateCapturedConstant(candidate, out value);

        if (candidate is MemberExpression member && IsCapturedMember(member))
        {
            throw new TransitionExpressionTranslationException(
                $"Captured membership value '{member.Member.Name}' is not portable canonical Transition semantics. "
                + "Declare the comparison set directly in the authored expression.");
        }

        value = null;
        return false;
    }

    static bool TryReadCapturedMember(MemberExpression member, out object? value)
    {
        if (member.Expression is null)
        {
            value = ReadMemberValue(member: member, target: null);
            return true;
        }

        var source = StripConvert(expression: member.Expression);
        if (source is ConstantExpression constant)
        {
            value = ReadMemberValue(member: member, target: constant.Value);
            return true;
        }

        if (source is MemberExpression parent && TryReadCapturedMember(member: parent, out var parentValue))
        {
            value = ReadMemberValue(member: member, target: parentValue);
            return true;
        }

        value = null;
        return false;
    }

    static object? ReadMemberValue(MemberExpression member, object? target)
    {
        return member.Member switch
        {
            FieldInfo field => field.GetValue(obj: target),
            PropertyInfo property when property.GetMethod is not null => property.GetValue(obj: target),
            _ => throw new TransitionExpressionTranslationException($"Member '{member.Member.Name}' is not readable as a constant value.")
        };
    }

    static ObservationValue ToConstant(object? value, Type? typeHint = null)
    {
        var hintedType = typeHint is null
            ? null
            : Nullable.GetUnderlyingType(typeHint) ?? typeHint;

        return value switch
        {
            null => ObservationValue.Null,
            _ when hintedType?.IsEnum == true => ObservationValue.FromString(ToEnumString(value, hintedType)),
            string text => ObservationValue.FromString(text),
            int intValue => ObservationValue.FromInt64(intValue),
            long longValue => ObservationValue.FromInt64(longValue),
            decimal decimalValue => ObservationValue.FromObject(decimalValue),
            bool boolValue => ObservationValue.FromBool(boolValue),
            double doubleValue => ObservationValue.FromDouble(doubleValue),
            float floatValue => ObservationValue.FromDouble(floatValue),
            Guid guidValue => ObservationValue.FromString(guidValue.ToString()),
            DateTime dateTimeValue => ObservationValue.FromString(dateTimeValue.ToString("O")),
            DateTimeOffset dateTimeOffsetValue => ObservationValue.FromString(dateTimeOffsetValue.ToString("O")),
            Enum enumValue => ObservationValue.FromString(enumValue.ToString()),
            _ => throw new TransitionExpressionTranslationException($"Constant value type '{value.GetType().Name}' is not supported.")
        };
    }

    static string ToEnumString(object value, Type enumType)
    {
        if (value.GetType().IsEnum)
            return value.ToString()!;

        var enumValue = Enum.ToObject(enumType, value);
        return enumValue.ToString()!;
    }

    static Type? ResolveEnumConstantHint(Expression expression, Expression peerExpression)
    {
        if (expression is not ConstantExpression && expression is not MemberExpression)
            return null;

        var peerType = UnwrapSemanticValueType(peerExpression.Type);
        if (!peerType.IsEnum)
            return null;

        return peerType;
    }

    static Type UnwrapSemanticValueType(Type type)
    {
        var unwrapped = Nullable.GetUnderlyingType(type) ?? type;
        if (!unwrapped.IsGenericType || unwrapped.GetGenericTypeDefinition() != typeof(Field<>))
            return unwrapped;

        var fieldType = unwrapped.GetGenericArguments()[0];
        return Nullable.GetUnderlyingType(fieldType) ?? fieldType;
    }
}
