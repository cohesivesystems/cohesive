using System.Collections.Immutable;
using System.Linq.Expressions;
using System.Reflection;
using Cohesive.Relations.IR;

namespace Cohesive.Relations.Authoring;

/// <summary>
/// Deliberately narrow expression translator used to prove that C# expression frontends can lower
/// through <see cref="RelationQueryAuthoringCore"/> without owning a second graph-construction path.
/// </summary>
/// <remarks>
/// This is not the production expression-authoring surface. It accepts one bound CLR value,
/// direct member access, portable literal constants, canonical binary operators, and top-level
/// member-initializer assignments. Broader translation and cached CLR-shape metadata resolution
/// remain follow-on work.
/// </remarks>
internal sealed class RelationQueryExpressionLoweringProof
{
    internal const string Producer = "cohesive.relations.csharp-expression-proof/v1";

    readonly Func<MemberInfo, FieldPath> memberPathResolver;

    public RelationQueryExpressionLoweringProof(Func<MemberInfo, FieldPath> memberPathResolver)
    {
        this.memberPathResolver = Guard.RequireNotNull(memberPathResolver);
    }

    public RelationQueryLoweredExpression LowerValue<TSource, TValue>(
        RelationQueryBindingHandle binding,
        Expression<Func<TSource, TValue>> expression,
        string sourceReference)
    {
        RequireBinding(binding);
        ArgumentNullException.ThrowIfNull(expression);
        sourceReference = Guard.RequireNotNullOrWhiteSpace(sourceReference);

        var value = Translate(
            expression.Body,
            expression.Parameters[0],
            binding,
            expressionPath: "body");
        return new(
            value,
            Source(
                sourceReference + "/body",
                $"Value expression returning '{typeof(TValue).Name}'."));
    }

    public RelationQueryLoweredPredicate LowerPredicate<TSource>(
        RelationQueryBindingHandle binding,
        Expression<Func<TSource, bool>> predicate,
        string sourceReference)
    {
        RequireBinding(binding);
        ArgumentNullException.ThrowIfNull(predicate);
        sourceReference = Guard.RequireNotNullOrWhiteSpace(sourceReference);

        var value = Translate(
            predicate.Body,
            predicate.Parameters[0],
            binding,
            expressionPath: "body");
        return new(
            value,
            Source(sourceReference, "Filter operation lowered from a C# predicate."),
            Source(sourceReference + "/body", "Filter predicate expression."));
    }

    public RelationQueryLoweredProjection LowerProjection<TSource, TResult>(
        RelationQueryBindingHandle binding,
        Expression<Func<TSource, TResult>> projection,
        string sourceReference)
    {
        RequireBinding(binding);
        ArgumentNullException.ThrowIfNull(projection);
        sourceReference = Guard.RequireNotNullOrWhiteSpace(sourceReference);

        var body = StripConvert(projection.Body);
        if (body is not MemberInitExpression initializer)
        {
            throw Unsupported(
                expressionPath: "body",
                $"Projection requires a member initializer but found '{body.NodeType}'.");
        }

        if (initializer.NewExpression.Arguments.Count != 0)
        {
            throw Unsupported(
                expressionPath: "body/new",
                "Projection constructor arguments are outside the narrow lowering proof.");
        }

        if (initializer.Bindings.Count == 0)
        {
            throw Unsupported(
                expressionPath: "body/bindings",
                "Projection requires at least one member assignment.");
        }
        if (initializer.NewExpression.Arguments.Count != 0)
        {
            throw Unsupported(
                expressionPath: "body/new/arguments",
                "Projection constructor arguments are deferred; use a parameterless member initializer in this proof.");
        }

        var assignments = new RelationQueryProjectionAssignment[initializer.Bindings.Count];
        for (var index = 0; index < initializer.Bindings.Count; index++)
        {
            var authoredBinding = initializer.Bindings[index];
            var bindingPath = $"body/bindings/{index}";
            if (authoredBinding is not MemberAssignment assignment)
            {
                throw Unsupported(
                    bindingPath,
                    $"Projection supports only simple member assignments, not '{authoredBinding.BindingType}'.");
            }

            var target = ResolveMemberPath(assignment.Member, bindingPath + "/member");
            var value = Translate(
                assignment.Expression,
                projection.Parameters[0],
                binding,
                bindingPath + "/expression");
            assignments[index] = new(
                target,
                value,
                assignmentSource: Source(
                    sourceReference + "/" + bindingPath,
                    $"Projection assignment to '{assignment.Member.Name}'."),
                valueSource: Source(
                    sourceReference + "/" + bindingPath + "/expression",
                    $"Projection value for '{assignment.Member.Name}'."));
        }

        return new(
            [.. assignments],
            Source(sourceReference, $"Projection operation producing '{typeof(TResult).Name}'."),
            Source(sourceReference + "/body", $"Projected '{typeof(TResult).Name}' result binding."));
    }

    public RelationQueryAuthoringResult<QueryDefinition> BuildQuery<TSource, TResult>(
        QueryId id,
        QueryName name,
        QualifiedShapeId sourceShape,
        QualifiedShapeId resultShape,
        Expression<Func<TSource, bool>> predicate,
        Expression<Func<TSource, TResult>> projection,
        string sourceReference)
    {
        sourceReference = Guard.RequireNotNullOrWhiteSpace(sourceReference);
        var core = new RelationQueryAuthoringCore();
        var source = core.Source(
            sourceShape,
            source: Source(sourceReference + "/source", "Expression-authored semantic source."),
            bindingSource: Source(
                sourceReference + "/source/binding",
                $"Expression binding for '{typeof(TSource).Name}'."));
        var loweredPredicate = LowerPredicate(
            source.Binding,
            predicate,
            sourceReference + "/filter");
        var filtered = core.Filter(
            source.Node,
            loweredPredicate.Value,
            source: loweredPredicate.NodeSource,
            predicateSource: loweredPredicate.PredicateSource);
        var loweredProjection = LowerProjection(
            source.Binding,
            projection,
            sourceReference + "/projection");
        var projected = core.Project(
            filtered,
            resultShape,
            loweredProjection.Assignments,
            source: loweredProjection.NodeSource,
            bindingSource: loweredProjection.BindingSource);
        var rows = core.Rows(
            projected.Node,
            source: Source(sourceReference + "/result/rows", "Expression-authored rows result."));
        return core.BuildQuery(
            id,
            name,
            [rows],
            Source(sourceReference + "/terminal", "Expression-authored query terminal."));
    }

    Expr Translate(
        Expression expression,
        ParameterExpression parameter,
        RelationQueryBindingHandle binding,
        string expressionPath)
    {
        var current = StripConvert(expression);
        return current switch
        {
            MemberExpression member => TranslateMember(member, parameter, binding, expressionPath),
            ConstantExpression constant => TranslateConstant(constant, expressionPath),
            BinaryExpression binary => new BinaryExpr(
                TranslateBinaryOperator(binary.NodeType, expressionPath),
                Translate(binary.Left, parameter, binding, expressionPath + "/left"),
                Translate(binary.Right, parameter, binding, expressionPath + "/right")),
            _ => throw Unsupported(
                expressionPath,
                $"Expression node '{current.NodeType}' is outside the narrow lowering proof.")
        };
    }

    Expr TranslateMember(
        MemberExpression member,
        ParameterExpression parameter,
        RelationQueryBindingHandle binding,
        string expressionPath)
    {
        var root = member.Expression is null ? null : StripConvert(member.Expression);
        if (!ReferenceEquals(root, parameter))
        {
            throw Unsupported(
                expressionPath,
                "Only direct member access rooted at the lambda parameter is supported; nested and captured members are deferred.");
        }

        return binding.Field(ResolveMemberPath(member.Member, expressionPath));
    }

    FieldPath ResolveMemberPath(MemberInfo member, string expressionPath)
    {
        FieldPath path;
        try
        {
            path = memberPathResolver(member);
        }
        catch (Exception exception) when (
            exception is not RelationQueryExpressionLoweringProofException
            && exception is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            throw new RelationQueryExpressionLoweringProofException(
                expressionPath,
                $"The member-path resolver rejected '{member.DeclaringType?.Name}.{member.Name}'.",
                exception);
        }

        if (path.Segments.IsDefaultOrEmpty)
        {
            throw Unsupported(
                expressionPath,
                $"The member-path resolver returned an empty path for '{member.DeclaringType?.Name}.{member.Name}'.");
        }

        return path;
    }

    static Expr TranslateConstant(ConstantExpression constant, string expressionPath)
    {
        if (constant.Value is null)
            return new ConstantExpr(ObservationValue.Null);

        var type = Nullable.GetUnderlyingType(constant.Value.GetType()) ?? constant.Value.GetType();
        if (!IsPortableLiteralType(type))
        {
            throw Unsupported(
                expressionPath,
                $"Literal type '{type.FullName}' is outside the narrow lowering proof.");
        }

        return new ConstantExpr(ObservationValue.FromObject(constant.Value));
    }

    static bool IsPortableLiteralType(Type type) =>
        type.IsEnum
        || type == typeof(ObservationValue)
        || type == typeof(string)
        || type == typeof(char)
        || type == typeof(bool)
        || type == typeof(byte)
        || type == typeof(sbyte)
        || type == typeof(short)
        || type == typeof(ushort)
        || type == typeof(int)
        || type == typeof(uint)
        || type == typeof(long)
        || type == typeof(ulong)
        || type == typeof(float)
        || type == typeof(double)
        || type == typeof(decimal)
        || type == typeof(Guid)
        || type == typeof(DateTime)
        || type == typeof(DateTimeOffset)
        || type == typeof(DateOnly)
        || type == typeof(TimeOnly)
        || type == typeof(TimeSpan)
        || type == typeof(Uri);

    static BinaryOperator TranslateBinaryOperator(ExpressionType type, string expressionPath) => type switch
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
        _ => throw Unsupported(
            expressionPath,
            $"Binary operator '{type}' is outside the narrow lowering proof.")
    };

    static Expression StripConvert(Expression expression)
    {
        var current = expression;
        while (current is UnaryExpression unary
               && unary.NodeType is ExpressionType.Convert or ExpressionType.ConvertChecked
               && (unary.Type == unary.Operand.Type || unary.Type == typeof(object)))
        {
            current = unary.Operand;
        }

        return current;
    }

    static RelationQueryAuthoringSource Source(string reference, string description) =>
        new(Producer, reference, description);

    static void RequireBinding(RelationQueryBindingHandle binding)
    {
        if (binding.Owner is null || string.IsNullOrWhiteSpace(binding.Id.Value))
            throw new ArgumentException("A binding owned by a structural authoring core is required.", nameof(binding));
    }

    static RelationQueryExpressionLoweringProofException Unsupported(
        string expressionPath,
        string message) =>
        new(expressionPath, message);
}

internal sealed record RelationQueryLoweredExpression(
    Expr Value,
    RelationQueryAuthoringSource ValueSource);

internal sealed record RelationQueryLoweredPredicate(
    Expr Value,
    RelationQueryAuthoringSource NodeSource,
    RelationQueryAuthoringSource PredicateSource);

internal sealed record RelationQueryLoweredProjection(
    ImmutableArray<RelationQueryProjectionAssignment> Assignments,
    RelationQueryAuthoringSource NodeSource,
    RelationQueryAuthoringSource BindingSource);

internal sealed class RelationQueryExpressionLoweringProofException : NotSupportedException
{
    public RelationQueryExpressionLoweringProofException(
        string expressionPath,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        ExpressionPath = Guard.RequireNotNullOrWhiteSpace(expressionPath);
    }

    public string ExpressionPath { get; }
}
