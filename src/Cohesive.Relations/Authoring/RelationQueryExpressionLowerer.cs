using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Cohesive.Model.Authoring;
using Cohesive.Model.Expressions;
using Cohesive.Relations.IR;

namespace Cohesive.Relations.Authoring;

/// <summary>Resolves one complete CLR member chain to its canonical field path.</summary>
/// <param name="rootType">CLR type at which <paramref name="members"/> is rooted.</param>
/// <param name="members">Ordered CLR member chain from the root value to the selected value.</param>
/// <returns>The canonical field path represented by the complete member chain.</returns>
/// <exception cref="ArgumentNullException">
/// <paramref name="rootType"/> or <paramref name="members"/> is <see langword="null"/>.
/// </exception>
/// <exception cref="ArgumentException">The member chain is invalid for the selected metadata profile.</exception>
/// <exception cref="InvalidOperationException">The selected metadata profile cannot resolve the member chain.</exception>
/// <exception cref="NotSupportedException">The metadata profile does not support a member in the chain.</exception>
public delegate FieldPath RelationQueryExpressionMemberPathResolver(
    Type rootType,
    IReadOnlyList<PropertyInfo> members);

/// <summary>Immutable canonical value and source attribution lowered from one C# value expression.</summary>
public sealed record RelationQueryExpressionLowering
{
    /// <summary>Creates a lowered expression value.</summary>
    /// <param name="value">Canonical semantic expression.</param>
    /// <param name="source">Source attribution for the complete value expression.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> or <paramref name="source"/> is <see langword="null"/>.</exception>
    public RelationQueryExpressionLowering(Expr value, RelationQueryAuthoringSource source)
    {
        Value = Guard.RequireNotNull(value);
        Source = Guard.RequireNotNull(source);
    }

    /// <summary>Canonical semantic expression.</summary>
    public Expr Value { get; }

    /// <summary>Source attribution for the complete value expression.</summary>
    public RelationQueryAuthoringSource Source { get; }
}

/// <summary>Immutable projection assignments and source attribution lowered from one C# object projection.</summary>
public sealed record RelationQueryExpressionProjection
{
    /// <summary>Creates a lowered object projection.</summary>
    /// <param name="assignments">Canonical structural projection assignments.</param>
    /// <param name="nodeSource">Source attribution for the projection operation.</param>
    /// <param name="bindingSource">Source attribution for the projected result binding.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="assignments"/> is empty or contains a <see langword="null"/> entry.
    /// </exception>
    /// <exception cref="ArgumentNullException"><paramref name="nodeSource"/> or <paramref name="bindingSource"/> is <see langword="null"/>.</exception>
    public RelationQueryExpressionProjection(
        ImmutableArray<RelationQueryProjectionAssignment> assignments,
        RelationQueryAuthoringSource nodeSource,
        RelationQueryAuthoringSource bindingSource)
    {
        if (assignments.IsDefaultOrEmpty || assignments.Any(static assignment => assignment is null))
        {
            throw new ArgumentException(
                "A lowered projection requires at least one non-null assignment.",
                nameof(assignments));
        }

        Assignments = assignments;
        NodeSource = Guard.RequireNotNull(nodeSource);
        BindingSource = Guard.RequireNotNull(bindingSource);
    }

    /// <summary>Canonical structural projection assignments.</summary>
    public ImmutableArray<RelationQueryProjectionAssignment> Assignments { get; }

    /// <summary>Source attribution for the projection operation.</summary>
    public RelationQueryAuthoringSource NodeSource { get; }

    /// <summary>Source attribution for the projected result binding.</summary>
    public RelationQueryAuthoringSource BindingSource { get; }
}

/// <summary>
/// Translates a deliberately exact subset of C# expression trees into the canonical Cohesive expression IR.
/// </summary>
/// <remarks>
/// The translator is fail closed. It never compiles a supplied expression, invokes an arbitrary method,
/// evaluates an arbitrary property getter, or treats captured CLR state as a constant. Runtime inputs must be
/// declared as query parameters and referenced through framework-owned parameter markers. Source bindings are
/// associated with top-level lambda parameters by position. Nested sequence lambdas lower to canonical
/// current-item scopes while retaining access to outer semantic bindings.
/// </remarks>
public sealed class RelationQueryExpressionLowerer
{
    /// <summary>Stable producer identity used for source attribution emitted by this translator.</summary>
    public const string Producer = "cohesive.relations.csharp-expression/v1";

    readonly RelationQueryExpressionMemberPathResolver memberPathResolver;
    readonly Func<Type, TypeRef> literalTypeResolver;
    readonly RelationQueryExpressionAuthoring? expectedParameterOwner;
    static readonly RelationQueryClrAuthoringContext DefaultLiteralTypeContext = new();
    static readonly IClrTypeRefMapper DefaultLiteralTypeMapper = new DefaultClrTypeRefMapper();
    static readonly ConcurrentDictionary<PropertyInfo, bool> NonNullProperties = [];
    static readonly ConcurrentDictionary<PropertyInfo, bool> NonNullSequenceElementProperties = [];

    /// <summary>Creates a production C# expression translator.</summary>
    /// <param name="memberPathResolver">
    /// Metadata-aware resolver that maps complete source and projection member chains to canonical paths.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="memberPathResolver"/> is <see langword="null"/>.</exception>
    public RelationQueryExpressionLowerer(
        RelationQueryExpressionMemberPathResolver memberPathResolver)
        : this(memberPathResolver, expectedParameterOwner: null)
    {
    }

    internal RelationQueryExpressionLowerer(
        RelationQueryExpressionMemberPathResolver memberPathResolver,
        RelationQueryExpressionAuthoring? expectedParameterOwner)
    {
        this.memberPathResolver = Guard.RequireNotNull(memberPathResolver);
        this.expectedParameterOwner = expectedParameterOwner;
        literalTypeResolver = (expectedParameterOwner?.Clr ?? DefaultLiteralTypeContext).GetTypeRef;
    }

    /// <summary>Lowers a value lambda with no source-binding parameters.</summary>
    /// <param name="expression">Expression-authoring lambda to translate.</param>
    /// <param name="sourceReference">Stable producer-defined reference for the lambda.</param>
    /// <returns>A fail-closed result containing either a canonical value or structured diagnostics.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="expression"/> or <paramref name="sourceReference"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="sourceReference"/> is empty or white space.</exception>
    public RelationQueryExpressionLoweringResult<RelationQueryExpressionLowering> LowerValue(
        LambdaExpression expression,
        string sourceReference) =>
        LowerValue(expression, bindings: [], sourceReference);

    /// <summary>Lowers a value lambda whose top-level parameters correspond positionally to semantic bindings.</summary>
    /// <param name="expression">Expression-authoring lambda to translate.</param>
    /// <param name="bindings">Semantic source bindings corresponding to the lambda parameters in declaration order.</param>
    /// <param name="sourceReference">Stable producer-defined reference for the lambda.</param>
    /// <returns>A fail-closed result containing either a canonical value or structured diagnostics.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="expression"/>, <paramref name="bindings"/>, or <paramref name="sourceReference"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="sourceReference"/> is empty or white space.</exception>
    public RelationQueryExpressionLoweringResult<RelationQueryExpressionLowering> LowerValue(
        LambdaExpression expression,
        IReadOnlyList<RelationQueryExpressionValueBinding> bindings,
        string sourceReference)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(bindings);
        sourceReference = Guard.RequireNotNullOrWhiteSpace(sourceReference);

        try
        {
            var scope = CreateRootScope(expression, bindings, sourceReference);
            var value = Translate(expression.Body, scope, sourceReference, expressionPath: "body");
            return Success(
                new RelationQueryExpressionLowering(
                    value,
                    Source(sourceReference + "/body", $"Value expression returning '{Display(expression.ReturnType)}'.")));
        }
        catch (LoweringFailure failure)
        {
            return Failure<RelationQueryExpressionLowering>(failure.Diagnostic);
        }
    }

    /// <summary>Lowers an object-projection lambda with no source-binding parameters.</summary>
    /// <param name="expression">Object-projection lambda to translate.</param>
    /// <param name="sourceReference">Stable producer-defined reference for the lambda.</param>
    /// <returns>A fail-closed result containing either structural assignments or structured diagnostics.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="expression"/> or <paramref name="sourceReference"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="sourceReference"/> is empty or white space.</exception>
    public RelationQueryExpressionLoweringResult<RelationQueryExpressionProjection> LowerProjection(
        LambdaExpression expression,
        string sourceReference) =>
        LowerProjection(expression, bindings: [], sourceReference);

    /// <summary>Lowers an object-projection lambda whose parameters correspond positionally to semantic bindings.</summary>
    /// <param name="expression">Object-projection lambda to translate.</param>
    /// <param name="bindings">Semantic source bindings corresponding to the lambda parameters in declaration order.</param>
    /// <param name="sourceReference">Stable producer-defined reference for the lambda.</param>
    /// <returns>A fail-closed result containing either structural assignments or structured diagnostics.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="expression"/>, <paramref name="bindings"/>, or <paramref name="sourceReference"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="sourceReference"/> is empty or white space.</exception>
    public RelationQueryExpressionLoweringResult<RelationQueryExpressionProjection> LowerProjection(
        LambdaExpression expression,
        IReadOnlyList<RelationQueryExpressionValueBinding> bindings,
        string sourceReference)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(bindings);
        sourceReference = Guard.RequireNotNullOrWhiteSpace(sourceReference);

        try
        {
            var scope = CreateRootScope(expression, bindings, sourceReference);
            List<RelationQueryProjectionAssignment> assignments = [];
            HashSet<FieldPath> targets = [];
            LowerProjectionObject(
                StripExactConversions(expression.Body, sourceReference, "body"),
                expression.ReturnType,
                memberPrefix: [],
                scope,
                sourceReference,
                expressionPath: "body",
                assignments,
                targets,
                topLevel: true);

            if (assignments.Count == 0)
            {
                throw Fail(
                    RelationQueryExpressionDiagnosticCodes.ProjectionInvalid,
                    "The projection does not produce any canonical field assignments.",
                    "body",
                    sourceReference,
                    symbol: Display(expression.ReturnType),
                    suggestion: "Construct an object or record with at least one explicitly assigned result member.");
            }

            return Success(
                new RelationQueryExpressionProjection(
                    [.. assignments],
                    Source(sourceReference, $"Projection operation producing '{Display(expression.ReturnType)}'."),
                    Source(sourceReference + "/body", $"Projected '{Display(expression.ReturnType)}' result binding.")));
        }
        catch (LoweringFailure failure)
        {
            return Failure<RelationQueryExpressionProjection>(failure.Diagnostic);
        }
    }

    RootScope CreateRootScope(
        LambdaExpression expression,
        IReadOnlyList<RelationQueryExpressionValueBinding> bindings,
        string sourceReference)
    {
        if (expression.Parameters.Count != bindings.Count)
        {
            throw Fail(
                RelationQueryExpressionDiagnosticCodes.LambdaBindingCountMismatch,
                $"The lambda declares {expression.Parameters.Count} top-level parameter(s), but {bindings.Count} semantic binding(s) were supplied.",
                "parameters",
                sourceReference,
                symbol: Display(expression.Type),
                suggestion: "Supply one semantic binding for each top-level lambda parameter, in declaration order.");
        }

        Dictionary<ParameterExpression, ParameterTarget> parameters =
            new(expression.Parameters.Count, ReferenceEqualityComparer.Instance);
        RelationQueryAuthoringCore? owner = null;
        for (var index = 0; index < bindings.Count; index++)
        {
            var binding = bindings[index];
            if (binding is null || string.IsNullOrWhiteSpace(binding.Id.Value))
            {
                throw Fail(
                    RelationQueryExpressionDiagnosticCodes.BindingInvalid,
                    $"Semantic binding {index} is not owned by a structural authoring core.",
                    $"parameters/{index}",
                    sourceReference,
                    symbol: expression.Parameters[index].Name,
                    suggestion: "Use a binding returned by the same relation/query authoring session.");
            }

            if (expectedParameterOwner is not null
                && !ReferenceEquals(binding.Owner, expectedParameterOwner))
            {
                throw Fail(
                    RelationQueryExpressionDiagnosticCodes.BindingInvalid,
                    "A semantic binding belongs to another expression-authoring session.",
                    $"parameters/{index}",
                    sourceReference,
                    symbol: binding.Id.Value,
                    suggestion: "Use bindings created by the expression-authoring session that owns this translator.");
            }

            var structuralOwner = binding.Structural.Owner;
            if (owner is not null && !ReferenceEquals(owner, structuralOwner))
            {
                throw Fail(
                    RelationQueryExpressionDiagnosticCodes.BindingInvalid,
                    "All semantic bindings supplied to one lambda must belong to the same structural authoring core.",
                    $"parameters/{index}",
                    sourceReference,
                    symbol: binding.Id.Value,
                    suggestion: "Use bindings created by one relation/query authoring session.");
            }

            if (expression.Parameters[index].Type != binding.ClrType)
            {
                throw Fail(
                    RelationQueryExpressionDiagnosticCodes.BindingInvalid,
                    $"Lambda parameter type '{Display(expression.Parameters[index].Type)}' does not match "
                    + $"binding CLR type '{Display(binding.ClrType)}'.",
                    $"parameters/{index}",
                    sourceReference,
                    symbol: binding.Id.Value,
                    suggestion: "Use one typed binding whose CLR type exactly matches each lambda parameter.");
            }

            owner ??= structuralOwner;
            parameters.Add(
                expression.Parameters[index],
                ParameterTarget.ForBinding(
                    binding.Id,
                    binding.ClrType,
                    binding.MemberPathResolver,
                    binding.UsesImportedMapping));
        }

        return new(parameters, activeCurrentItem: null);
    }

    Expr Translate(
        Expression expression,
        RootScope scope,
        string sourceReference,
        string expressionPath)
    {
        var current = StripExactConversions(expression, sourceReference, expressionPath);
        return current switch
        {
            ConstantExpression constant => TranslateConstant(constant, sourceReference, expressionPath),
            MemberExpression member => TranslateMember(member, scope, sourceReference, expressionPath),
            ParameterExpression parameter => TranslateParameter(parameter, scope, sourceReference, expressionPath),
            UnaryExpression unary => TranslateUnary(unary, scope, sourceReference, expressionPath),
            BinaryExpression binary => TranslateBinary(binary, scope, sourceReference, expressionPath),
            ConditionalExpression conditional => TranslateConditional(
                conditional,
                scope,
                sourceReference,
                expressionPath),
            MethodCallExpression call => TranslateCall(call, scope, sourceReference, expressionPath),
            NewExpression or MemberInitExpression => TranslateObject(current, scope, sourceReference, expressionPath),
            NewArrayExpression array => TranslateArray(array, scope, sourceReference, expressionPath),
            _ => throw Fail(
                RelationQueryExpressionDiagnosticCodes.NodeUnsupported,
                $"Expression node '{current.NodeType}' has no exact canonical relation/query lowering.",
                expressionPath,
                sourceReference,
                symbol: current.NodeType.ToString(),
                suggestion: "Rewrite the expression using fields, declared parameters, portable literals, and supported canonical operators or functions.")
        };
    }

    Expr TranslateParameter(
        ParameterExpression parameter,
        RootScope scope,
        string sourceReference,
        string expressionPath)
    {
        if (!scope.Parameters.TryGetValue(parameter, out var target))
        {
            throw Fail(
                RelationQueryExpressionDiagnosticCodes.NodeUnsupported,
                "The lambda parameter is not visible in the current expression-authoring scope.",
                expressionPath,
                sourceReference,
                symbol: parameter.Name,
                suggestion: "Reference a top-level source binding or the current nested sequence item.");
        }

        if (target.Kind == ParameterTargetKind.CurrentItem
            && ReferenceEquals(parameter, scope.ActiveCurrentItem))
        {
            return Expr.CurrentItem();
        }

        var message = target.Kind == ParameterTargetKind.CurrentItem
            ? "An outer sequence item cannot be referenced from a nested current-item scope because the canonical expression has no depth-qualified item reference."
            : "A complete bound object cannot be embedded as a scalar expression; select one or more of its fields.";
        throw Fail(
            RelationQueryExpressionDiagnosticCodes.NodeUnsupported,
            message,
            expressionPath,
            sourceReference,
            symbol: parameter.Name,
            suggestion: "Select a field from the semantic binding or current item.");
    }

    Expr TranslateMember(
        MemberExpression member,
        RootScope scope,
        string sourceReference,
        string expressionPath)
    {
        if (TryTranslateParameterMarker(member, sourceReference, expressionPath, out var parameter))
            return parameter;

        if (TryGetNullableHasValueOperand(member, out var nullableOperand))
        {
            if (!TryGetGuardableMemberAccess(nullableOperand, scope, out _))
            {
                throw Fail(
                    RelationQueryExpressionDiagnosticCodes.NodeUnsupported,
                    "Nullable HasValue can be lowered only for a required convention-inferred CLR field.",
                    expressionPath,
                    sourceReference,
                    symbol: Display(member.Member),
                    suggestion: "Use a convention-inferred nullable field with required presence, or author explicit presence and null semantics structurally.");
            }

            return Expr.Ne(
                TranslateMember(nullableOperand, scope, sourceReference, expressionPath + "/operand"),
                Expr.Null());
        }

        if (IsCollectionCountMember(member))
        {
            throw Fail(
                RelationQueryExpressionDiagnosticCodes.NodeUnsupported,
                "CLR collection Count/Length returns Int32 and can overflow where canonical count returns Int64.",
                expressionPath,
                sourceReference,
                symbol: Display(member.Member),
                suggestion: "Author canonical count through the structural builder or an aggregate Count target typed as Int64.");
        }

        if (!TryReadMemberChain(member, out var root, out var members))
        {
            throw CapturedOrUnsupportedMember(member, expressionPath, sourceReference);
        }

        if (root is not ParameterExpression rootParameter
            || !scope.Parameters.TryGetValue(rootParameter, out var target))
        {
            throw CapturedOrUnsupportedMember(member, expressionPath, sourceReference);
        }

        members = NormalizeGuardedNullableValueMembers(
            rootParameter,
            target,
            members,
            scope,
            expressionPath,
            sourceReference);
        ValidateMemberNavigation(
            rootParameter,
            members,
            scope,
            expressionPath,
            sourceReference);

        if (target.Kind == ParameterTargetKind.CurrentItem
            && ReferenceEquals(rootParameter, scope.ActiveCurrentItem)
            && !target.IsProvablyNonNull)
        {
            throw Fail(
                RelationQueryExpressionDiagnosticCodes.NodeUnsupported,
                $"Member path navigation dereferences sequence item '{Display(target.RootType)}', which may be null; "
                + "C# throws for a null item while canonical current-item navigation preserves null/missing.",
                expressionPath + "/root",
                sourceReference,
                symbol: rootParameter.Name,
                suggestion: "Declare non-null collection elements in CLR metadata, or author explicit null/missing behavior structurally.");
        }

        var path = ResolveMemberPath(
            target.RootType,
            members,
            expressionPath,
            sourceReference,
            target.MemberPathResolver);
        return target.Kind switch
        {
            ParameterTargetKind.Binding => Expr.Field(target.Binding, path),
            ParameterTargetKind.CurrentItem when ReferenceEquals(rootParameter, scope.ActiveCurrentItem) =>
                new FieldExpr(new FieldPath([FieldPathSegment.ForField(ExprFieldRoots.CurrentItem), .. path.Segments])),
            ParameterTargetKind.CurrentItem => throw Fail(
                RelationQueryExpressionDiagnosticCodes.NodeUnsupported,
                "A member of an outer sequence item cannot be referenced from a nested current-item scope.",
                expressionPath,
                sourceReference,
                symbol: Display(member.Member),
                suggestion: "Restructure the query so the value is projected before entering the nested sequence scope."),
            _ => throw new UnreachableException()
        };
    }

    Expr TranslateConditional(
        ConditionalExpression conditional,
        RootScope scope,
        string sourceReference,
        string expressionPath)
    {
        var test = Translate(conditional.Test, scope, sourceReference, expressionPath + "/test");
        var ifTrueScope = ApplyConditionFacts(conditional.Test, whenTrue: true, scope);
        var ifFalseScope = ApplyConditionFacts(conditional.Test, whenTrue: false, scope);
        return new ConditionalExpr(
            test,
            Translate(conditional.IfTrue, ifTrueScope, sourceReference, expressionPath + "/ifTrue"),
            Translate(conditional.IfFalse, ifFalseScope, sourceReference, expressionPath + "/ifFalse"));
    }

    Expr TranslateUnary(
        UnaryExpression unary,
        RootScope scope,
        string sourceReference,
        string expressionPath)
    {
        if (unary.NodeType == ExpressionType.ArrayLength)
        {
            throw Fail(
                RelationQueryExpressionDiagnosticCodes.NodeUnsupported,
                "CLR array Length returns Int32 and can overflow where canonical count returns Int64.",
                expressionPath,
                sourceReference,
                symbol: unary.NodeType.ToString(),
                suggestion: "Author canonical count through the structural builder or an aggregate Count target typed as Int64.");
        }

        if (unary.IsLifted || unary.IsLiftedToNull)
        {
            throw Fail(
                RelationQueryExpressionDiagnosticCodes.OperatorUnsupported,
                "Lifted nullable unary operators do not have the same missing/null contract as canonical operators.",
                expressionPath,
                sourceReference,
                symbol: unary.NodeType.ToString(),
                suggestion: "Make null handling explicit before applying the operator.");
        }
        if (unary.Method is not null)
        {
            throw Fail(
                RelationQueryExpressionDiagnosticCodes.OperatorUnsupported,
                "User-defined unary operators are not executed or inferred by expression authoring.",
                expressionPath,
                sourceReference,
                symbol: Display(unary.Method),
                suggestion: "Express the operation using a canonical built-in operator.");
        }

        return unary.NodeType switch
        {
            ExpressionType.Not when unary.Type == typeof(bool) && unary.Operand.Type == typeof(bool) => new UnaryExpr(
                UnaryOperator.Not,
                Translate(unary.Operand, scope, sourceReference, expressionPath + "/operand")),
            _ => throw Fail(
                RelationQueryExpressionDiagnosticCodes.OperatorUnsupported,
                $"Unary operator '{unary.NodeType}' has no exact canonical lowering.",
                expressionPath,
                sourceReference,
                symbol: unary.NodeType.ToString(),
                suggestion: "Use logical negation or move this computation to an explicit supported extension.")
        };
    }

    Expr TranslateBinary(
        BinaryExpression binary,
        RootScope scope,
        string sourceReference,
        string expressionPath)
    {
        if (binary.NodeType == ExpressionType.Coalesce)
            return TranslateExactCoalesce(binary, scope, sourceReference, expressionPath);

        if (TryTranslateExactEnumComparison(
                binary,
                scope,
                sourceReference,
                expressionPath,
                out var enumComparison))
        {
            return enumComparison;
        }

        if (TryTranslateGuardableNullComparison(
                binary,
                scope,
                sourceReference,
                expressionPath,
                out var guardableNullComparison))
        {
            return guardableNullComparison;
        }

        if (binary.IsLifted || binary.IsLiftedToNull)
        {
            throw Fail(
                RelationQueryExpressionDiagnosticCodes.OperatorUnsupported,
                "Lifted nullable binary operators do not have the same missing/null contract as canonical operators.",
                expressionPath,
                sourceReference,
                symbol: binary.NodeType.ToString(),
                suggestion: "Make null handling explicit before applying the operator.");
        }

        if (binary.NodeType == ExpressionType.Add && binary.Type == typeof(string))
        {
            if (binary.Left.Type != typeof(string)
                || binary.Right.Type != typeof(string)
                || !IsProvablyNonNullString(binary.Left)
                || !IsProvablyNonNullString(binary.Right)
                || binary.Method is not null && !IsExactStringConcatOperator(binary.Method))
            {
                throw Fail(
                    RelationQueryExpressionDiagnosticCodes.OperatorUnsupported,
                    "Only the built-in string + string operator has an exact canonical concat lowering.",
                    expressionPath,
                    sourceReference,
                    symbol: Display(binary.Method),
                    suggestion: "Concatenate string operands directly or use the canonical structural concat function.");
            }

            return Expr.Call(
                ExprFunctionNames.Concat,
                Translate(binary.Left, scope, sourceReference, expressionPath + "/left"),
                Translate(binary.Right, scope, sourceReference, expressionPath + "/right"));
        }

        if (TryTranslateNonNullStringNullComparison(binary, out var nullComparison))
            return Expr.Const(nullComparison);

        if (binary.Method is not null && !IsCanonicalFrameworkOperator(binary))
        {
            throw Fail(
                RelationQueryExpressionDiagnosticCodes.OperatorUnsupported,
                "User-defined binary operators are not executed or inferred by expression authoring.",
                expressionPath,
                sourceReference,
                symbol: Display(binary.Method),
                suggestion: "Express the operation using a canonical built-in operator or function.");
        }

        var operation = binary.NodeType switch
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
            _ => throw Fail(
                RelationQueryExpressionDiagnosticCodes.OperatorUnsupported,
                $"Binary operator '{binary.NodeType}' has no exact canonical lowering.",
                expressionPath,
                sourceReference,
                symbol: binary.NodeType.ToString(),
                suggestion: "Use a supported comparison, short-circuit Boolean operator, or arithmetic operator.")
        };

        var left = Translate(binary.Left, scope, sourceReference, expressionPath + "/left");
        var rightScope = binary.NodeType switch
        {
            ExpressionType.AndAlso => ApplyConditionFacts(binary.Left, whenTrue: true, scope),
            ExpressionType.OrElse => ApplyConditionFacts(binary.Left, whenTrue: false, scope),
            _ => scope
        };
        var right = Translate(binary.Right, rightScope, sourceReference, expressionPath + "/right");
        ValidateExactBinaryDomain(binary, expressionPath, sourceReference);

        return new BinaryExpr(
            operation,
            left,
            right);
    }

    bool TryTranslateExactEnumComparison(
        BinaryExpression binary,
        RootScope scope,
        string sourceReference,
        string expressionPath,
        out Expr expression)
    {
        expression = null!;
        if (binary.NodeType is not (ExpressionType.Equal or ExpressionType.NotEqual)
            || binary.Method is not null
            || binary.IsLifted
            || binary.IsLiftedToNull)
        {
            return false;
        }

        var leftIsEnum = TryGetConvertedEnumOperand(binary.Left, out var leftOperand, out var leftEnumType);
        var rightIsEnum = TryGetConvertedEnumOperand(binary.Right, out var rightOperand, out var rightEnumType);
        if (!leftIsEnum && !rightIsEnum)
            return false;

        var enumType = leftIsEnum ? leftEnumType : rightEnumType;
        if (leftIsEnum && rightIsEnum && leftEnumType != rightEnumType)
            return false;

        if ((!leftIsEnum && !IsExactEnumConstant(binary.Left, enumType))
            || (!rightIsEnum && !IsExactEnumConstant(binary.Right, enumType)))
        {
            return false;
        }

        var left = leftIsEnum
            ? Translate(leftOperand, scope, sourceReference, expressionPath + "/left")
            : TranslateExactEnumConstant(binary.Left, enumType, sourceReference, expressionPath + "/left");
        var right = rightIsEnum
            ? Translate(rightOperand, scope, sourceReference, expressionPath + "/right")
            : TranslateExactEnumConstant(binary.Right, enumType, sourceReference, expressionPath + "/right");
        expression = new BinaryExpr(
            binary.NodeType == ExpressionType.Equal ? BinaryOperator.Eq : BinaryOperator.Ne,
            left,
            right);
        return true;
    }

    static bool TryGetConvertedEnumOperand(
        Expression expression,
        out Expression operand,
        out Type enumType)
    {
        if (expression is UnaryExpression
            {
                NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked,
                Method: null,
                IsLifted: false,
                IsLiftedToNull: false
            } conversion
            && conversion.Operand.Type.IsEnum
            && conversion.Type == GetEnumComparisonCarrierType(conversion.Operand.Type)
            && (conversion.Operand is not MemberExpression member
                || !IsParameterMarkerMember(member, out _)))
        {
            operand = conversion.Operand;
            enumType = conversion.Operand.Type;
            return true;
        }

        operand = null!;
        enumType = null!;
        return false;
    }

    static bool IsExactEnumConstant(Expression expression, Type enumType) =>
        expression is ConstantExpression { Value: not null } constant
        && constant.Type == GetEnumComparisonCarrierType(enumType);

    static Type GetEnumComparisonCarrierType(Type enumType)
    {
        var underlying = Enum.GetUnderlyingType(enumType);
        return underlying == typeof(byte)
               || underlying == typeof(sbyte)
               || underlying == typeof(short)
               || underlying == typeof(ushort)
            ? typeof(int)
            : underlying;
    }

    Expr TranslateExactEnumConstant(
        Expression expression,
        Type enumType,
        string sourceReference,
        string expressionPath)
    {
        var constant = (ConstantExpression)expression;
        var value = (Enum)Enum.ToObject(enumType, constant.Value!);
        if (!TryGetUnambiguousEnumMember(value, out var member))
        {
            throw Fail(
                RelationQueryExpressionDiagnosticCodes.LiteralUnsupported,
                $"Enum value '{constant.Value}' is not one exact, unambiguous named member of '{Display(enumType)}'.",
                expressionPath,
                sourceReference,
                symbol: $"{Display(enumType)}:{constant.Value}",
                suggestion: "Compare against one uniquely named enum member or author the intended numeric/flags semantics structurally.");
        }

        return CreateTypedLiteralOrConstant(enumType, ObservationValue.FromString(member));
    }

    Expr TranslateExactCoalesce(
        BinaryExpression binary,
        RootScope scope,
        string sourceReference,
        string expressionPath)
    {
        var left = StripExactConversions(binary.Left, sourceReference, expressionPath + "/left");
        var right = StripExactConversions(binary.Right, sourceReference, expressionPath + "/right");
        if (left is ConstantExpression { Value: null })
            return Translate(right, scope, sourceReference, expressionPath + "/right");
        if (right is ConstantExpression { Value: null } && binary.Conversion is null)
            return Translate(left, scope, sourceReference, expressionPath + "/left");

        if (binary.Conversion is null
            && left is MemberExpression nullableMember
            && TryGetGuardableMemberAccess(nullableMember, scope, out var access))
        {
            var leftValue = TranslateMember(
                nullableMember,
                scope,
                sourceReference,
                expressionPath + "/left");
            return new ConditionalExpr(
                Expr.Ne(leftValue, Expr.Null()),
                TranslateMember(
                    nullableMember,
                    scope.WithKnownNonNull(access),
                    sourceReference,
                    expressionPath + "/left"),
                Translate(right, scope, sourceReference, expressionPath + "/right"));
        }

        throw Fail(
            RelationQueryExpressionDiagnosticCodes.OperatorUnsupported,
            "General null-coalescing has no exact canonical lowering because the shared expression IR does not currently expose a presence/null test.",
            expressionPath,
            sourceReference,
            symbol: "??",
            suggestion: "Use an explicit parameter default or add an attributable canonical null/presence semantic before authoring this expression.");
    }

    Expr TranslateCall(
        MethodCallExpression call,
        RootScope scope,
        string sourceReference,
        string expressionPath)
    {
        if (IsDateTimeOffsetEqualsExact(call))
        {
            return Expr.Eq(
                Translate(call.Object!, scope, sourceReference, expressionPath + "/left"),
                Translate(call.Arguments[0], scope, sourceReference, expressionPath + "/right"));
        }

        if (IsOrdinalEndsWith(call))
        {
            return Expr.EndsWith(
                Translate(call.Object!, scope, sourceReference, expressionPath + "/value"),
                Translate(call.Arguments[0], scope, sourceReference, expressionPath + "/suffix"));
        }

        if (IsStringEndsWith(call))
        {
            throw Fail(
                RelationQueryExpressionDiagnosticCodes.MethodUnsupported,
                "Only the ordinal, case-sensitive EndsWith overload has the same semantics as the canonical endsWith function.",
                expressionPath,
                sourceReference,
                symbol: Display(call.Method),
                suggestion: "Call EndsWith(suffix, StringComparison.Ordinal).");
        }

        if (IsOrdinalStartsWith(call))
        {
            return Expr.StartsWith(
                Translate(call.Object!, scope, sourceReference, expressionPath + "/value"),
                Translate(call.Arguments[0], scope, sourceReference, expressionPath + "/prefix"));
        }

        if (IsStringStartsWith(call))
        {
            throw Fail(
                RelationQueryExpressionDiagnosticCodes.MethodUnsupported,
                "Only the ordinal, case-sensitive StartsWith overload has the same semantics as the canonical startsWith function.",
                expressionPath,
                sourceReference,
                symbol: Display(call.Method),
                suggestion: "Call StartsWith(prefix, StringComparison.Ordinal).");
        }

        if (IsOrdinalStringContains(call))
        {
            return Expr.TextContains(
                Translate(call.Object!, scope, sourceReference, expressionPath + "/value"),
                Translate(call.Arguments[0], scope, sourceReference, expressionPath + "/substring"));
        }

        if (IsStringContains(call))
        {
            throw Fail(
                RelationQueryExpressionDiagnosticCodes.MethodUnsupported,
                "Only the ordinal, case-sensitive string Contains overload has the same semantics as the canonical textContains function.",
                expressionPath,
                sourceReference,
                symbol: Display(call.Method),
                suggestion: "Call Contains(substring, StringComparison.Ordinal).");
        }

        if (IsStringConcat(call))
        {
            if (call.Arguments.Any(static argument => !IsProvablyNonNullString(argument)))
            {
                throw Fail(
                    RelationQueryExpressionDiagnosticCodes.MethodUnsupported,
                    "C# string concatenation treats null as empty, while canonical concat requires present strings.",
                    expressionPath,
                    sourceReference,
                    symbol: Display(call.Method),
                    suggestion: "Concatenate non-nullable required string fields or author explicit missing/null semantics structurally.");
            }

            return new CallExpr(
                ExprFunctionNames.Concat,
                [.. call.Arguments.Select((argument, index) =>
                    Translate(argument, scope, sourceReference, $"{expressionPath}/arguments/{index}"))]);
        }

        if (TryGetContainsOperands(call, out var collection, out var candidate))
        {
            if (!HasGuaranteedDefaultMembershipEquality(collection.Type))
            {
                throw Fail(
                    RelationQueryExpressionDiagnosticCodes.MethodUnsupported,
                    "Collection membership requires an array or List<T> whose Contains operation uses default element equality.",
                    expressionPath,
                    sourceReference,
                    symbol: Display(call.Method),
                    suggestion: "Use an array/List<T> value or author the intended comparer explicitly through structural semantics.");
            }

            if (!TryGetSequenceElementType(collection.Type, out var elementType)
                || !HasExactCanonicalMembershipEquality(elementType))
            {
                throw Fail(
                    RelationQueryExpressionDiagnosticCodes.MethodUnsupported,
                    "Canonical collection membership is currently authored only for scalar element types with portable value equality.",
                    expressionPath,
                    sourceReference,
                    symbol: Display(call.Method),
                    suggestion: "Compare a scalar collection, or express complex-element matching with Any and a correlated predicate.");
            }

            return Expr.Contains(
                Translate(collection, scope, sourceReference, expressionPath + "/collection"),
                Translate(candidate, scope, sourceReference, expressionPath + "/candidate"));
        }

        if (TryGetEagerSelectMaterialization(call, out var select))
        {
            return TranslateScopedSequenceFunction(
                select,
                ExprFunctionNames.Select,
                requireSelector: true,
                scope,
                sourceReference,
                expressionPath + "/select");
        }

        if (IsSequenceMethod(call, nameof(Enumerable.Any)))
            return TranslateScopedSequenceFunction(call, ExprFunctionNames.Any, requireSelector: true, scope, sourceReference, expressionPath);
        if (IsSequenceMethod(call, nameof(Enumerable.All)))
            return TranslateScopedSequenceFunction(call, ExprFunctionNames.All, requireSelector: true, scope, sourceReference, expressionPath);
        if (IsSequenceMethod(call, nameof(Enumerable.Select)))
        {
            throw Fail(
                RelationQueryExpressionDiagnosticCodes.MethodUnsupported,
                "Enumerable.Select is lazy, while the canonical select function materializes every selector result eagerly.",
                expressionPath,
                sourceReference,
                symbol: Display(call.Method),
                suggestion: "Project the required value structurally, or add an explicit canonical lazy/fused sequence semantic.");
        }
        if (IsSequenceMethod(call, nameof(Enumerable.Sum)))
            return TranslateScopedSequenceFunction(call, ExprFunctionNames.Sum, requireSelector: false, scope, sourceReference, expressionPath);
        if (IsSequenceMethod(call, nameof(Enumerable.Min)))
            throw InexactSequenceAggregate(call, sourceReference, expressionPath);
        if (IsSequenceMethod(call, nameof(Enumerable.Max)))
            throw InexactSequenceAggregate(call, sourceReference, expressionPath);
        if (IsSequenceMethod(call, nameof(Enumerable.Average)))
            throw InexactSequenceAggregate(call, sourceReference, expressionPath);
        if (IsSequenceMethod(call, nameof(Enumerable.Count)) && call.Arguments.Count == 1)
        {
            throw Fail(
                RelationQueryExpressionDiagnosticCodes.MethodUnsupported,
                "Enumerable.Count returns Int32 and can overflow where canonical count returns Int64.",
                expressionPath,
                sourceReference,
                symbol: Display(call.Method),
                suggestion: "Author canonical count through the structural builder or an aggregate Count target typed as Int64.");
        }
        if (IsSequenceMethod(call, nameof(Enumerable.LongCount)))
        {
            if (call.Arguments.Count != 1)
            {
                throw Fail(
                    RelationQueryExpressionDiagnosticCodes.MethodUnsupported,
                    "Only predicate-free LongCount has the exact semantics of canonical count.",
                    expressionPath,
                    sourceReference,
                    symbol: Display(call.Method),
                    suggestion: "Filter the semantic sequence first, then call LongCount() without a predicate.");
            }

            return TranslateScopedSequenceFunction(
                call,
                ExprFunctionNames.Count,
                requireSelector: false,
                scope,
                sourceReference,
                expressionPath);
        }

        throw Fail(
            RelationQueryExpressionDiagnosticCodes.MethodUnsupported,
            $"Method '{Display(call.Method)}' is not in the exact relation/query expression allowlist.",
            expressionPath,
            sourceReference,
            symbol: Display(call.Method),
            suggestion: "Use a supported canonical string or sequence operation, or register an explicit compiler extension outside the canonical model.");
    }

    static LoweringFailure InexactSequenceAggregate(
        MethodCallExpression call,
        string sourceReference,
        string expressionPath) =>
        Fail(
            RelationQueryExpressionDiagnosticCodes.MethodUnsupported,
            $"Enumerable.{call.Method.Name} throws for an empty non-nullable sequence, while the canonical function returns undefined.",
            expressionPath,
            sourceReference,
            symbol: Display(call.Method),
            suggestion: "Prove non-emptiness structurally before applying the canonical aggregate, or author an explicit empty-sequence policy.");

    Expr TranslateScopedSequenceFunction(
        MethodCallExpression call,
        string function,
        bool requireSelector,
        RootScope scope,
        string sourceReference,
        string expressionPath)
    {
        if (call.Arguments.Count is < 1 or > 2
            || (requireSelector && call.Arguments.Count != 2))
        {
            throw Fail(
                RelationQueryExpressionDiagnosticCodes.MethodUnsupported,
                $"The selected '{call.Method.Name}' overload does not match canonical function '{function}'.",
                expressionPath,
                sourceReference,
                symbol: Display(call.Method),
                suggestion: requireSelector
                    ? "Use the overload with one source collection and one single-parameter predicate or selector."
                    : "Use the overload with a source collection and, optionally, one single-parameter selector.");
        }

        if (!TryGetSequenceElementType(call.Arguments[0].Type, out var elementType))
        {
            throw Fail(
                RelationQueryExpressionDiagnosticCodes.MethodUnsupported,
                "The sequence source is not a supported semantic collection. String and byte-array scalars are not relation/query collections.",
                expressionPath + "/collection",
                sourceReference,
                symbol: Display(call.Arguments[0].Type),
                suggestion: "Apply the operation to a statically typed semantic collection field.");
        }

        var source = Translate(call.Arguments[0], scope, sourceReference, expressionPath + "/collection");
        if (call.Arguments.Count == 1)
        {
            ValidateSequenceFunctionDomain(call, function, selector: null, sourceReference, expressionPath);
            return Expr.Call(function, source);
        }

        var selectorExpression = StripQuote(call.Arguments[1]);
        if (selectorExpression is not LambdaExpression { Parameters.Count: 1 } selector)
        {
            throw Fail(
                RelationQueryExpressionDiagnosticCodes.MethodUnsupported,
                "The sequence predicate or selector must be represented by one inline single-parameter lambda.",
                expressionPath + "/selector",
                sourceReference,
                symbol: Display(call.Method),
                suggestion: "Inline the sequence lambda instead of invoking or capturing a delegate.");
        }

        ValidateSequenceFunctionDomain(call, function, selector, sourceReference, expressionPath);

        var itemIsProvablyNonNull = IsSequenceElementProvablyNonNull(
            call.Arguments[0],
            elementType);
        var itemMemberPathResolver = CreateCurrentItemMemberPathResolver(
            call.Arguments[0],
            scope,
            sourceReference,
            expressionPath + "/collection");

        var nestedScope = scope.WithCurrentItem(
            selector.Parameters[0],
            itemIsProvablyNonNull,
            itemMemberPathResolver,
            ResolveSequenceProvenance(UnwrapSequenceView(call.Arguments[0]), scope)
                .Target?.UsesImportedMapping == true);
        var loweredSelector = Translate(
            selector.Body,
            nestedScope,
            sourceReference,
            expressionPath + "/selector/body");
        return Expr.Call(function, source, loweredSelector);
    }

    static void ValidateSequenceFunctionDomain(
        MethodCallExpression call,
        string function,
        LambdaExpression? selector,
        string sourceReference,
        string expressionPath)
    {
        if (function is not (ExprFunctionNames.Sum
            or ExprFunctionNames.Min
            or ExprFunctionNames.Max
            or ExprFunctionNames.Avg))
        {
            return;
        }

        if (!TryGetSequenceElementType(call.Arguments[0].Type, out var elementType))
        {
            throw Fail(
                RelationQueryExpressionDiagnosticCodes.MethodUnsupported,
                "The sequence element type cannot be established for exact aggregate lowering.",
                expressionPath,
                sourceReference,
                symbol: Display(call.Method),
                suggestion: "Use a statically typed generic sequence.");
        }

        var valueType = selector?.ReturnType ?? elementType;
        var valid = function switch
        {
            ExprFunctionNames.Sum or ExprFunctionNames.Avg => valueType == typeof(decimal),
            ExprFunctionNames.Min or ExprFunctionNames.Max =>
                valueType == typeof(byte)
                || valueType == typeof(short)
                || valueType == typeof(int)
                || valueType == typeof(long)
                || valueType == typeof(decimal),
            _ => true
        };
        if (valid)
            return;

        throw Fail(
            RelationQueryExpressionDiagnosticCodes.MethodUnsupported,
            $"Sequence function '{call.Method.Name}' over CLR value type '{Display(valueType)}' does not "
            + "have the exact numeric, overflow, empty-sequence, or ordering contract of the canonical function.",
            expressionPath,
            sourceReference,
            symbol: Display(call.Method),
            suggestion: "Use decimal Sum or author the intended aggregate and empty-sequence policy structurally.");
    }

    Expr TranslateObject(
        Expression expression,
        RootScope scope,
        string sourceReference,
        string expressionPath)
    {
        List<(PropertyInfo Member, Expression Value, string Path)> entries = [];
        CollectDirectObjectEntries(expression, sourceReference, expressionPath, entries);

        List<Expr> arguments = new(entries.Count * 2);
        HashSet<string> keys = new(StringComparer.Ordinal);
        foreach (var (member, value, path) in entries)
        {
            var target = ResolveMemberPath(expression.Type, [member], path + "/member", sourceReference);
            if (target.Segments.Length != 1
                || !target.Segments[0].TryGetFieldIdentity(out var key))
            {
                throw Fail(
                    RelationQueryExpressionDiagnosticCodes.ProjectionInvalid,
                    "An inline object member must resolve to one direct canonical field.",
                    path + "/member",
                    sourceReference,
                    symbol: Display(member),
                    suggestion: "Use a direct DTO member or lower the object through a structural projection assignment.");
            }
            if (!keys.Add(key))
            {
                throw Fail(
                    RelationQueryExpressionDiagnosticCodes.ProjectionInvalid,
                    $"Inline object construction produces duplicate canonical field '{key}'.",
                    path + "/member",
                    sourceReference,
                    symbol: key,
                    suggestion: "Assign each canonical output field exactly once.");
            }

            arguments.Add(Expr.Const(key));
            arguments.Add(Translate(value, scope, sourceReference, path + "/expression"));
        }

        return Expr.Call(ExprFunctionNames.Object, [.. arguments]);
    }

    Expr TranslateArray(
        NewArrayExpression array,
        RootScope scope,
        string sourceReference,
        string expressionPath)
    {
        if (array.NodeType != ExpressionType.NewArrayInit)
        {
            throw Fail(
                RelationQueryExpressionDiagnosticCodes.NodeUnsupported,
                "An array-bounds expression allocates default elements and is not an array literal.",
                expressionPath,
                sourceReference,
                symbol: array.NodeType.ToString(),
                suggestion: "Use an initialized portable literal array or declare the collection as a query parameter.");
        }

        ObservationValue[] values = new ObservationValue[array.Expressions.Count];
        for (var index = 0; index < array.Expressions.Count; index++)
        {
            var lowered = Translate(
                array.Expressions[index],
                scope,
                sourceReference,
                $"{expressionPath}/elements/{index}");
            if (lowered is not ConstantExpr constant)
            {
                throw Fail(
                    RelationQueryExpressionDiagnosticCodes.NodeUnsupported,
                    "Canonical array literals may contain only portable literal values.",
                    $"{expressionPath}/elements/{index}",
                    sourceReference,
                    symbol: array.NodeType.ToString(),
                    suggestion: "Declare a runtime collection as an explicit query parameter.");
            }

            values[index] = constant.Value;
        }

        return new ConstantExpr(ObservationValue.FromImmutableArray(
            ImmutableCollectionsMarshal.AsImmutableArray(values)));
    }

    void LowerProjectionObject(
        Expression expression,
        Type outputRootType,
        ImmutableArray<PropertyInfo> memberPrefix,
        RootScope scope,
        string sourceReference,
        string expressionPath,
        List<RelationQueryProjectionAssignment> assignments,
        HashSet<FieldPath> targets,
        bool topLevel)
    {
        var current = StripExactConversions(expression, sourceReference, expressionPath);
        switch (current)
        {
            case MemberInitExpression initializer:
                RequireSafeMemberInitializerConstruction(initializer, sourceReference, expressionPath);
                LowerProjectionNew(
                    initializer.NewExpression,
                    outputRootType,
                    memberPrefix,
                    scope,
                    sourceReference,
                    expressionPath + "/new",
                    assignments,
                    targets);
                LowerProjectionBindings(
                    initializer.Bindings,
                    outputRootType,
                    memberPrefix,
                    scope,
                    sourceReference,
                    expressionPath + "/bindings",
                    assignments,
                    targets);
                return;
            case NewExpression created:
                if (created.Arguments.Count == 0)
                    RequireDirectConstructorProjection(created, [], sourceReference, expressionPath);
                LowerProjectionNew(
                    created,
                    outputRootType,
                    memberPrefix,
                    scope,
                    sourceReference,
                    expressionPath,
                    assignments,
                    targets);
                return;
            case var _ when !topLevel && !memberPrefix.IsDefaultOrEmpty:
                AddProjectionAssignment(
                    outputRootType,
                    memberPrefix,
                    current,
                    scope,
                    sourceReference,
                    expressionPath,
                    assignments,
                    targets);
                return;
            default:
                throw Fail(
                    RelationQueryExpressionDiagnosticCodes.ProjectionInvalid,
                    $"A top-level projection must construct an object or record, but found '{current.NodeType}'.",
                    expressionPath,
                    sourceReference,
                    symbol: current.NodeType.ToString(),
                    suggestion: "Construct the result DTO with an object initializer or an unambiguous constructor.");
        }
    }

    void LowerProjectionNew(
        NewExpression created,
        Type outputRootType,
        ImmutableArray<PropertyInfo> memberPrefix,
        RootScope scope,
        string sourceReference,
        string expressionPath,
        List<RelationQueryProjectionAssignment> assignments,
        HashSet<FieldPath> targets)
    {
        var members = ResolveConstructorMembers(created, sourceReference, expressionPath);
        for (var index = 0; index < created.Arguments.Count; index++)
        {
            var argumentPath = $"{expressionPath}/arguments/{index}";
            var prefix = memberPrefix.Add(members[index]);
            var argument = StripExactConversions(created.Arguments[index], sourceReference, argumentPath);
            if (argument is NewExpression or MemberInitExpression)
            {
                LowerProjectionObject(
                    argument,
                    outputRootType,
                    prefix,
                    scope,
                    sourceReference,
                    argumentPath,
                    assignments,
                    targets,
                    topLevel: false);
            }
            else
            {
                AddProjectionAssignment(
                    outputRootType,
                    prefix,
                    argument,
                    scope,
                    sourceReference,
                    argumentPath,
                    assignments,
                    targets);
            }
        }
    }

    void LowerProjectionBindings(
        IReadOnlyList<MemberBinding> bindings,
        Type outputRootType,
        ImmutableArray<PropertyInfo> memberPrefix,
        RootScope scope,
        string sourceReference,
        string expressionPath,
        List<RelationQueryProjectionAssignment> assignments,
        HashSet<FieldPath> targets)
    {
        for (var index = 0; index < bindings.Count; index++)
        {
            var binding = bindings[index];
            var bindingPath = $"{expressionPath}/{index}";
            var property = RequireProjectionAssignmentProperty(binding.Member, bindingPath, sourceReference);
            var prefix = memberPrefix.Add(property);
            switch (binding)
            {
                case MemberAssignment assignment:
                    {
                        var value = StripExactConversions(assignment.Expression, sourceReference, bindingPath + "/expression");
                        if (value is NewExpression or MemberInitExpression)
                        {
                            LowerProjectionObject(
                                value,
                                outputRootType,
                                prefix,
                                scope,
                                sourceReference,
                                bindingPath + "/expression",
                                assignments,
                                targets,
                                topLevel: false);
                        }
                        else
                        {
                            AddProjectionAssignment(
                                outputRootType,
                                prefix,
                                value,
                                scope,
                                sourceReference,
                                bindingPath,
                                assignments,
                                targets);
                        }
                        break;
                    }
                case MemberMemberBinding:
                    throw Fail(
                        RelationQueryExpressionDiagnosticCodes.ProjectionInvalid,
                        "Nested member mutation invokes the containing CLR getter and has no exact declarative projection lowering.",
                        bindingPath,
                        sourceReference,
                        symbol: Display(binding.Member),
                        suggestion: "Assign a newly constructed nested DTO, for example Nested = new NestedDto { Value = ... }.");
                default:
                    throw Fail(
                        RelationQueryExpressionDiagnosticCodes.ProjectionInvalid,
                        $"Projection member binding '{binding.BindingType}' has no canonical structural lowering.",
                        bindingPath,
                        sourceReference,
                        symbol: Display(binding.Member),
                        suggestion: "Use a direct member assignment or nested object initializer.");
            }
        }
    }

    void AddProjectionAssignment(
        Type outputRootType,
        ImmutableArray<PropertyInfo> members,
        Expression value,
        RootScope scope,
        string sourceReference,
        string expressionPath,
        List<RelationQueryProjectionAssignment> assignments,
        HashSet<FieldPath> targets)
    {
        var target = ResolveMemberPath(outputRootType, members, expressionPath + "/member", sourceReference);
        if (!targets.Add(target))
        {
            throw Fail(
                RelationQueryExpressionDiagnosticCodes.ProjectionInvalid,
                $"Projection target '{target}' is assigned more than once.",
                expressionPath + "/member",
                sourceReference,
                symbol: target.ToString(),
                suggestion: "Assign each canonical output field exactly once.");
        }

        var valuePath = expressionPath.EndsWith("/expression", StringComparison.Ordinal)
            ? expressionPath
            : expressionPath + "/expression";
        assignments.Add(
            new RelationQueryProjectionAssignment(
                target,
                Translate(value, scope, sourceReference, valuePath),
                assignmentSource: Source(
                    sourceReference + "/" + expressionPath,
                    $"Projection assignment to '{target}'."),
                valueSource: Source(
                    sourceReference + "/" + valuePath,
                    $"Projection value for '{target}'.")));
    }

    void CollectDirectObjectEntries(
        Expression expression,
        string sourceReference,
        string expressionPath,
        List<(PropertyInfo Member, Expression Value, string Path)> entries)
    {
        switch (expression)
        {
            case NewExpression created:
                {
                    if (created.Arguments.Count == 0)
                        RequireDirectConstructorProjection(created, [], sourceReference, expressionPath);
                    var members = ResolveConstructorMembers(created, sourceReference, expressionPath);
                    for (var index = 0; index < created.Arguments.Count; index++)
                    {
                        entries.Add((members[index], created.Arguments[index], $"{expressionPath}/arguments/{index}"));
                    }
                    return;
                }
            case MemberInitExpression initializer:
                RequireSafeMemberInitializerConstruction(initializer, sourceReference, expressionPath);
                var constructorMembers = ResolveConstructorMembers(
                    initializer.NewExpression,
                    sourceReference,
                    expressionPath + "/new");
                for (var index = 0; index < initializer.NewExpression.Arguments.Count; index++)
                {
                    entries.Add((
                        constructorMembers[index],
                        initializer.NewExpression.Arguments[index],
                        $"{expressionPath}/new/arguments/{index}"));
                }
                for (var index = 0; index < initializer.Bindings.Count; index++)
                {
                    if (initializer.Bindings[index] is not MemberAssignment assignment)
                    {
                        throw Fail(
                            RelationQueryExpressionDiagnosticCodes.ProjectionInvalid,
                            "Inline object expressions support only direct member assignments.",
                            $"{expressionPath}/bindings/{index}",
                            sourceReference,
                            symbol: initializer.Bindings[index].BindingType.ToString(),
                            suggestion: "Use a structural projection for nested member bindings.");
                    }
                    entries.Add((
                        RequireProjectionAssignmentProperty(
                            assignment.Member,
                            $"{expressionPath}/bindings/{index}",
                            sourceReference),
                        assignment.Expression,
                        $"{expressionPath}/bindings/{index}"));
                }
                return;
            default:
                throw new UnreachableException();
        }
    }

    ImmutableArray<PropertyInfo> ResolveConstructorMembers(
        NewExpression created,
        string sourceReference,
        string expressionPath)
    {
        if (created.Arguments.Count == 0)
            return [];

        if (created.Members is { Count: > 0 } explicitMembers)
        {
            if (explicitMembers.Count != created.Arguments.Count)
            {
                throw Fail(
                    RelationQueryExpressionDiagnosticCodes.ProjectionInvalid,
                    "The expression tree exposes a constructor/member mapping with inconsistent arity.",
                    expressionPath,
                    sourceReference,
                    symbol: Display(created.Constructor),
                    suggestion: "Use a conventional object or record constructor.");
            }
            PropertyInfo[] explicitResult = new PropertyInfo[explicitMembers.Count];
            for (var index = 0; index < explicitMembers.Count; index++)
            {
                explicitResult[index] = RequireProjectionProperty(
                    explicitMembers[index],
                    $"{expressionPath}/arguments/{index}",
                    sourceReference);
            }
            ImmutableArray<PropertyInfo> resolved = [.. explicitResult];
            RequireDirectConstructorProjection(created, resolved, sourceReference, expressionPath);
            return resolved;
        }

        if (created.Constructor is null)
        {
            throw Fail(
                RelationQueryExpressionDiagnosticCodes.ProjectionInvalid,
                "Constructor projection metadata is unavailable.",
                expressionPath,
                sourceReference,
                symbol: Display(created.Type),
                suggestion: "Use an object initializer with explicit member assignments.");
        }

        var parameters = created.Constructor.GetParameters();
        if (parameters.Length != created.Arguments.Count)
        {
            throw Fail(
                RelationQueryExpressionDiagnosticCodes.ProjectionInvalid,
                "Constructor parameter metadata does not match the projected argument count.",
                expressionPath,
                sourceReference,
                symbol: Display(created.Constructor),
                suggestion: "Use an object initializer with explicit member assignments.");
        }

        var candidates = created.Type
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(static property =>
                property.GetMethod is not null
                && property.GetIndexParameters().Length == 0)
            .ToArray();
        PropertyInfo[] result = new PropertyInfo[parameters.Length];
        for (var index = 0; index < parameters.Length; index++)
        {
            var parameter = parameters[index];
            var name = parameter.Name;
            if (string.IsNullOrWhiteSpace(name))
            {
                throw Fail(
                    RelationQueryExpressionDiagnosticCodes.ProjectionMemberAmbiguous,
                    $"Constructor parameter {index} has no stable name for member matching.",
                    $"{expressionPath}/arguments/{index}",
                    sourceReference,
                    symbol: Display(created.Constructor),
                    suggestion: "Use an object initializer or a constructor with named parameters matching result members.");
            }

            var exact = candidates.Where(candidate => string.Equals(candidate.Name, name, StringComparison.Ordinal)).ToArray();
            var matching = exact.Length > 0
                ? exact
                : candidates.Where(candidate => string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matching.Length != 1)
            {
                throw Fail(
                    RelationQueryExpressionDiagnosticCodes.ProjectionMemberAmbiguous,
                    matching.Length == 0
                        ? $"Constructor parameter '{name}' does not match a public result member."
                        : $"Constructor parameter '{name}' matches more than one public result member.",
                    $"{expressionPath}/arguments/{index}",
                    sourceReference,
                    symbol: name,
                    suggestion: "Use an object initializer with an explicit target member.");
            }

            result[index] = matching[0];
        }

        ImmutableArray<PropertyInfo> resolvedResult = [.. result];
        RequireDirectConstructorProjection(created, resolvedResult, sourceReference, expressionPath);
        return resolvedResult;
    }

    static void RequireDirectConstructorProjection(
        NewExpression created,
        ImmutableArray<PropertyInfo> members,
        string sourceReference,
        string expressionPath)
    {
        var reason = "Constructor metadata is unavailable.";
        if (created.Constructor is { } constructor
            && TryVerifyDirectConstructorProjection(constructor, members, out reason))
        {
            return;
        }

        throw Fail(
            RelationQueryExpressionDiagnosticCodes.ProjectionInvalid,
            "A constructor projection is supported only when metadata proves that every argument is "
            + $"assigned directly to its matched auto-property. {reason}",
            expressionPath,
            sourceReference,
            symbol: Display(created.Constructor),
            suggestion: "Use a compiler-generated positional record constructor or an object initializer with explicit assignments.");
    }

    void RequireSafeMemberInitializerConstruction(
        MemberInitExpression initializer,
        string sourceReference,
        string expressionPath)
    {
        if (initializer.NewExpression.Arguments.Count > 0)
        {
            _ = ResolveConstructorMembers(
                initializer.NewExpression,
                sourceReference,
                expressionPath + "/new");
            return;
        }

        HashSet<FieldInfo> overwrittenFields = [];
        for (var index = 0; index < initializer.Bindings.Count; index++)
        {
            if (initializer.Bindings[index] is not MemberAssignment assignment)
                continue;

            var property = RequireProjectionAssignmentProperty(
                assignment.Member,
                $"{expressionPath}/bindings/{index}",
                sourceReference);
            var field = property.DeclaringType!.GetField(
                $"<{property.Name}>k__BackingField",
                BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new UnreachableException();
            overwrittenFields.Add(field);
        }

        if (initializer.Type.IsValueType && initializer.NewExpression.Constructor is null)
        {
            var instanceFields = initializer.Type.GetFields(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (instanceFields.Length > 0 && instanceFields.All(overwrittenFields.Contains))
                return;
        }

        var reason = "Constructor metadata is unavailable.";
        if (initializer.NewExpression.Constructor is { } constructor
            && TryVerifySafeInitializerConstructor(constructor, overwrittenFields, out reason))
        {
            return;
        }

        throw Fail(
            RelationQueryExpressionDiagnosticCodes.ProjectionInvalid,
            "An object-initializer projection requires a behavior-free parameterless constructor; "
            + $"inert defaults are allowed only when the initializer overwrites their auto-properties. {reason}",
            expressionPath + "/new",
            sourceReference,
            symbol: Display(initializer.NewExpression.Constructor),
            suggestion: "Use an inert DTO constructor and explicitly assign every property with a CLR field initializer.");
    }

    static bool TryVerifySafeInitializerConstructor(
        ConstructorInfo constructor,
        ISet<FieldInfo> overwrittenFields,
        out string reason)
    {
        if (constructor.GetParameters().Length != 0)
        {
            reason = "Constructor is not parameterless.";
            return false;
        }

        var il = constructor.GetMethodBody()?.GetILAsByteArray();
        if (il is null)
        {
            reason = "Constructor IL is unavailable for verification.";
            return false;
        }

        var offset = 0;
        var returned = false;
        try
        {
            while (offset < il.Length)
            {
                var opcode = ReadOpcode(il, ref offset);
                if (opcode == 0x00)
                    continue;
                if (opcode == 0x2A)
                {
                    returned = offset == il.Length;
                    break;
                }
                if (opcode != 0x02)
                {
                    reason = "Constructor executes behavior other than inert auto-property initialization.";
                    return false;
                }

                var valueOpcode = ReadOpcode(il, ref offset);
                if (valueOpcode == 0x28)
                {
                    var token = ReadInt32(il, ref offset);
                    var called = constructor.Module.ResolveMethod(
                        token,
                        constructor.DeclaringType?.GetGenericArguments(),
                        Type.EmptyTypes);
                    if (called is ConstructorInfo { DeclaringType: { } declaringType } baseConstructor
                        && declaringType == typeof(object)
                        && baseConstructor.GetParameters().Length == 0)
                    {
                        continue;
                    }

                    if (called is not MethodInfo
                        {
                            DeclaringType: { } methodType,
                            Name: nameof(Array.Empty),
                            IsGenericMethod: true
                        } method
                        || methodType != typeof(Array)
                        || method.GetParameters().Length != 0)
                    {
                        reason = "Constructor invokes a method other than object construction or Array.Empty<T>().";
                        return false;
                    }
                }
                else if (!SkipSafeInitializerValue(constructor, valueOpcode, il, ref offset))
                {
                    reason = "Constructor computes a field initializer instead of loading an inert literal default.";
                    return false;
                }

                if (ReadOpcode(il, ref offset) != 0x7D)
                {
                    reason = "Constructor initializer does not directly store an auto-property backing field.";
                    return false;
                }

                var fieldToken = ReadInt32(il, ref offset);
                var field = constructor.Module.ResolveField(
                    fieldToken,
                    constructor.DeclaringType?.GetGenericArguments(),
                    Type.EmptyTypes);
                if (field is null || !overwrittenFields.Contains(field))
                {
                    reason = "Constructor initializes a field that the object initializer does not overwrite.";
                    return false;
                }
            }
        }
        catch (Exception exception) when (
            exception is ArgumentException
            or BadImageFormatException
            or IndexOutOfRangeException
            or InvalidOperationException)
        {
            reason = $"Constructor metadata could not be verified: {exception.Message}";
            return false;
        }

        reason = returned ? string.Empty : "Constructor does not end in one final return.";
        return returned;
    }

    static bool SkipSafeInitializerValue(
        ConstructorInfo constructor,
        int opcode,
        byte[] il,
        ref int offset)
    {
        switch (opcode)
        {
            case 0x14:
            case >= 0x15 and <= 0x1E:
                return true;
            case 0x1F:
                offset += 1;
                return true;
            case 0x20:
            case 0x22:
            case 0x72:
                offset += 4;
                return true;
            case 0x21:
            case 0x23:
                offset += 8;
                return true;
            case 0x7E:
                var token = ReadInt32(il, ref offset);
                var field = constructor.Module.ResolveField(token);
                return field?.DeclaringType == typeof(string)
                       && string.Equals(field.Name, nameof(string.Empty), StringComparison.Ordinal);
            default:
                return false;
        }
    }

    static bool TryVerifyDirectConstructorProjection(
        ConstructorInfo constructor,
        ImmutableArray<PropertyInfo> members,
        out string reason)
    {
        var parameters = constructor.GetParameters();
        if (parameters.Length != members.Length)
        {
            reason = "Constructor and member arity differ.";
            return false;
        }

        FieldInfo[] fields = new FieldInfo[members.Length];
        for (var index = 0; index < members.Length; index++)
        {
            var member = members[index];
            var field = member.DeclaringType?.GetField(
                $"<{member.Name}>k__BackingField",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (field is null
                || field.GetCustomAttribute<CompilerGeneratedAttribute>(inherit: false) is null
                || field.FieldType != parameters[index].ParameterType)
            {
                reason = $"Member '{member.Name}' is not a directly type-compatible auto-property.";
                return false;
            }

            fields[index] = field;
        }

        var body = constructor.GetMethodBody();
        var il = body?.GetILAsByteArray();
        if (il is null)
        {
            reason = "Constructor IL is unavailable for verification.";
            return false;
        }

        bool[] assigned = new bool[members.Length];
        var offset = 0;
        var returned = false;
        try
        {
            while (offset < il.Length)
            {
                var opcode = ReadOpcode(il, ref offset);
                if (opcode == 0x00)
                    continue;
                if (opcode == 0x2A)
                {
                    returned = offset == il.Length;
                    break;
                }
                if (!TryReadArgumentLoad(opcode, il, ref offset, out var targetArgument)
                    || targetArgument != 0)
                {
                    reason = "Constructor contains operations other than direct property initialization.";
                    return false;
                }

                var next = ReadOpcode(il, ref offset);
                if (next == 0x28)
                {
                    var token = ReadInt32(il, ref offset);
                    var called = constructor.Module.ResolveMethod(
                        token,
                        constructor.DeclaringType?.GetGenericArguments(),
                        Type.EmptyTypes);
                    if (called is not ConstructorInfo
                        {
                            DeclaringType: { } declaringType
                        } baseConstructor
                        || declaringType != typeof(object)
                        || baseConstructor.GetParameters().Length != 0)
                    {
                        reason = "Constructor invokes behavior other than the parameterless object constructor.";
                        return false;
                    }

                    continue;
                }

                if (!TryReadArgumentLoad(next, il, ref offset, out var valueArgument)
                    || valueArgument <= 0
                    || valueArgument > fields.Length
                    || ReadOpcode(il, ref offset) != 0x7D)
                {
                    reason = "Constructor arguments are not assigned directly to matched auto-property fields.";
                    return false;
                }

                var fieldToken = ReadInt32(il, ref offset);
                var assignedField = constructor.Module.ResolveField(
                    fieldToken,
                    constructor.DeclaringType?.GetGenericArguments(),
                    Type.EmptyTypes);
                var expectedField = fields[valueArgument - 1];
                if (assignedField is null
                    || assignedField.Module != expectedField.Module
                    || assignedField.MetadataToken != expectedField.MetadataToken
                    || assigned[valueArgument - 1])
                {
                    reason = "A constructor argument does not map one-to-one to its matched auto-property.";
                    return false;
                }

                assigned[valueArgument - 1] = true;
            }
        }
        catch (Exception exception) when (
            exception is ArgumentException
            or BadImageFormatException
            or IndexOutOfRangeException
            or InvalidOperationException)
        {
            reason = $"Constructor metadata could not be verified: {exception.Message}";
            return false;
        }

        if (!returned || assigned.Any(static value => !value))
        {
            reason = "Constructor does not directly assign every matched auto-property exactly once.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    static int ReadOpcode(byte[] il, ref int offset)
    {
        var first = il[offset++];
        return first == 0xFE
            ? 0xFE00 | il[offset++]
            : first;
    }

    static bool TryReadArgumentLoad(int opcode, byte[] il, ref int offset, out int argument)
    {
        switch (opcode)
        {
            case 0x02:
            case 0x03:
            case 0x04:
            case 0x05:
                argument = opcode - 0x02;
                return true;
            case 0x0E:
                argument = il[offset++];
                return true;
            case 0xFE09:
                argument = il[offset] | il[offset + 1] << 8;
                offset += 2;
                return true;
            default:
                argument = -1;
                return false;
        }
    }

    static int ReadInt32(byte[] il, ref int offset)
    {
        var value = il[offset]
                    | il[offset + 1] << 8
                    | il[offset + 2] << 16
                    | il[offset + 3] << 24;
        offset += 4;
        return value;
    }

    static PropertyInfo RequireProjectionProperty(
        MemberInfo member,
        string expressionPath,
        string sourceReference)
    {
        if (member is PropertyInfo property
            && property.GetIndexParameters().Length == 0)
        {
            return property;
        }

        throw Fail(
            RelationQueryExpressionDiagnosticCodes.ProjectionInvalid,
            "CLR reflection shape authoring supports projected properties, not fields, indexers, or other member kinds.",
            expressionPath,
            sourceReference,
            symbol: Display(member),
            suggestion: "Expose the projected value as a readable CLR property represented by the selected shape metadata profile.");
    }

    static PropertyInfo RequireProjectionAssignmentProperty(
        MemberInfo member,
        string expressionPath,
        string sourceReference)
    {
        var property = RequireProjectionProperty(member, expressionPath, sourceReference);
        var setter = property.SetMethod;
        var backingField = property.DeclaringType?.GetField(
            $"<{property.Name}>k__BackingField",
            BindingFlags.Instance | BindingFlags.NonPublic);
        if (setter is not null
            && setter.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false)
            && backingField is not null
            && backingField.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false))
        {
            return property;
        }

        throw Fail(
            RelationQueryExpressionDiagnosticCodes.ProjectionInvalid,
            "An object-initializer projection target must be a compiler-generated auto-property so assigning it cannot run transforming user code.",
            expressionPath,
            sourceReference,
            symbol: Display(member),
            suggestion: "Project into an auto/init property, or author the canonical target assignment structurally.");
    }

    FieldPath ResolveMemberPath(
        Type rootType,
        IReadOnlyList<PropertyInfo> members,
        string expressionPath,
        string sourceReference,
        RelationQueryExpressionMemberPathResolver? scopedResolver = null)
    {
        try
        {
            var path = scopedResolver is null
                ? memberPathResolver(rootType, members)
                : scopedResolver(rootType, members);
            if (path.Segments.IsDefaultOrEmpty)
                throw new InvalidOperationException("The member-path resolver returned an empty path.");
            return path;
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or NotSupportedException or KeyNotFoundException)
        {
            var symbol = members.Count == 0
                ? Display(rootType)
                : string.Join(".", members.Select(static member => member.Name));
            throw Fail(
                RelationQueryExpressionDiagnosticCodes.MemberPathUnavailable,
                $"The selected CLR metadata profile could not resolve member chain '{symbol}': {exception.Message}",
                expressionPath,
                sourceReference,
                symbol,
                "Register the CLR shape and use the same deterministic metadata profile for shapes, relationships, and expressions.");
        }
    }

    Expr TranslateConstant(
        ConstantExpression constant,
        string sourceReference,
        string expressionPath)
    {
        if (constant.Value is null)
            return CreateTypedLiteralOrConstant(constant.Type, ObservationValue.Null);

        if (!TryConvertPortableLiteral(constant.Value, out var value))
        {
            throw Fail(
                RelationQueryExpressionDiagnosticCodes.LiteralUnsupported,
                $"Literal CLR type '{Display(constant.Value.GetType())}' cannot be represented without executing user code or losing value semantics.",
                expressionPath,
                sourceReference,
                symbol: Display(constant.Value.GetType()),
                suggestion: "Use a portable scalar literal or declare the runtime value as an explicit query parameter.");
        }

        return CreateTypedLiteralOrConstant(constant.Type, value);
    }

    Expr CreateTypedLiteralOrConstant(Type clrType, ObservationValue value)
    {
        TypeRef literalType;
        try
        {
            var effectiveClrType = Nullable.GetUnderlyingType(clrType) ?? clrType;
            literalType = effectiveClrType.IsEnum
                ? DefaultLiteralTypeMapper.Map(effectiveClrType, null)
                : literalTypeResolver(effectiveClrType);
        }
        catch (InvalidOperationException)
        {
            return new ConstantExpr(value);
        }

        if (literalType is not (ScalarTypeRef or EnumTypeRef)
            || value.Kind is not (ObservationValueKind.Null or ObservationValueKind.Undefined)
                && !new ValueContract(literalType).IsSatisfiedByConstant(value))
        {
            return new ConstantExpr(value);
        }

        return new LiteralExpr(literalType, value);
    }

    static bool TryConvertPortableLiteral(object value, out ObservationValue observed)
    {
        switch (value)
        {
            case ObservationValue observation
                when !RelationQueryPortableObservationValueSemantics.TryGetCanonicalJsonIssue(
                    observation,
                    out _,
                    out _):
                observed = observation;
                return true;
            case ObservationValue:
                observed = default;
                return false;
            case bool boolean:
                observed = ObservationValue.FromBool(boolean);
                return true;
            case byte number:
                observed = ObservationValue.FromInt64(number);
                return true;
            case sbyte number:
                observed = ObservationValue.FromInt64(number);
                return true;
            case short number:
                observed = ObservationValue.FromInt64(number);
                return true;
            case ushort number:
                observed = ObservationValue.FromInt64(number);
                return true;
            case int number:
                observed = ObservationValue.FromInt64(number);
                return true;
            case uint number:
                observed = ObservationValue.FromInt64(number);
                return true;
            case long number:
                observed = ObservationValue.FromInt64(number);
                return true;
            case ulong number when number <= long.MaxValue:
                observed = ObservationValue.FromInt64((long)number);
                return true;
            case ulong:
                observed = default;
                return false;
            case float number when float.IsFinite(number):
                observed = ObservationValue.FromDouble(number);
                return true;
            case double number when double.IsFinite(number):
                observed = ObservationValue.FromDouble(number);
                return true;
            case decimal number:
                observed = ObservationValue.FromDecimal(number);
                return true;
            case char character:
                observed = ObservationValue.FromString(character.ToString());
                return true;
            case string text:
                observed = ObservationValue.FromString(text);
                return true;
            case Guid guid:
                observed = ObservationValue.FromString(guid.ToString());
                return true;
            case DateTime instant:
                observed = ObservationValue.FromString(instant.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
                return true;
            case Uri uri when value.GetType() == typeof(Uri):
                observed = ObservationValue.FromString(uri.ToString());
                return true;
            case Enum enumeration when TryGetUnambiguousEnumMember(enumeration, out var member):
                observed = ObservationValue.FromString(member);
                return true;
            case Enum:
                observed = default;
                return false;
            default:
                observed = default;
                return false;
        }
    }

    static bool TryGetUnambiguousEnumMember(Enum value, out string member)
    {
        var enumType = value.GetType();
        var names = Enum.GetNames(enumType);
        var values = Enum.GetValues(enumType);
        string? selected = null;
        for (var index = 0; index < values.Length; index++)
        {
            if (!value.Equals(values.GetValue(index)))
                continue;
            if (selected is not null)
            {
                member = string.Empty;
                return false;
            }

            selected = names[index];
        }

        member = selected ?? string.Empty;
        return selected is not null;
    }

    bool TryTranslateParameterMarker(
        MemberExpression member,
        string sourceReference,
        string expressionPath,
        out Expr expression)
    {
        expression = null!;
        if (!IsParameterMarkerMember(member, out _))
            return false;
        if (!TryGetParameterMarker(member, out var markerField, out var marker))
        {
            throw Fail(
                RelationQueryExpressionDiagnosticCodes.ParameterMarkerInvalid,
                "A query-parameter marker must be captured directly by the compiler-generated lambda closure.",
                expressionPath,
                sourceReference,
                symbol: Display(member.Member),
                suggestion: "Assign the declared parameter handle to a local variable and reference parameter.Value directly inside the lambda.");
        }
        if (marker is null || string.IsNullOrWhiteSpace(marker.ParameterId.Value))
        {
            throw Fail(
                RelationQueryExpressionDiagnosticCodes.ParameterMarkerInvalid,
                "The captured query-parameter marker is null or has no canonical parameter identity.",
                expressionPath,
                sourceReference,
                symbol: markerField.Name,
                suggestion: "Use a parameter handle returned by the active relation/query authoring session.");
        }

        if (expectedParameterOwner is not null
            && !ReferenceEquals(marker.Owner, expectedParameterOwner))
        {
            throw Fail(
                RelationQueryExpressionDiagnosticCodes.ParameterMarkerInvalid,
                "The query-parameter marker belongs to another expression-authoring session.",
                expressionPath,
                sourceReference,
                symbol: marker.ParameterId.Value,
                suggestion: "Declare and reference the parameter through the same relation/query authoring session as this expression.");
        }

        expression = Expr.Param(marker.ParameterId.Value);
        return true;
    }

    static bool TryGetParameterMarker(
        MemberExpression member,
        out FieldInfo markerField,
        out IRelationQueryExpressionParameterMarker? marker)
    {
        markerField = null!;
        marker = null;
        if (!IsParameterMarkerMember(member, out var markerAccess)
            || markerAccess.Member is not FieldInfo field
            || markerAccess.Expression is not ConstantExpression { Value: not null } closure
            || !IsCompilerGeneratedClosure(closure.Value.GetType()))
        {
            return false;
        }

        markerField = field;
        marker = field.GetValue(closure.Value) as IRelationQueryExpressionParameterMarker;
        return true;
    }

    static bool IsParameterMarkerMember(
        MemberExpression member,
        out MemberExpression markerAccess)
    {
        if (string.Equals(member.Member.Name, "Value", StringComparison.Ordinal)
            && member.Expression is MemberExpression access
            && access.Member is FieldInfo field
            && typeof(IRelationQueryExpressionParameterMarker).IsAssignableFrom(field.FieldType))
        {
            markerAccess = access;
            return true;
        }

        markerAccess = null!;
        return false;
    }

    static bool IsCompilerGeneratedClosure(Type type) =>
        type.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false)
        && (type.Name.Contains("DisplayClass", StringComparison.Ordinal)
            || type.Name.StartsWith("<>", StringComparison.Ordinal));

    bool TryTranslateGuardableNullComparison(
        BinaryExpression binary,
        RootScope scope,
        string sourceReference,
        string expressionPath,
        out Expr expression)
    {
        expression = null!;
        if (binary.NodeType is not (ExpressionType.Equal or ExpressionType.NotEqual))
            return false;
        if (binary.Method is not null && binary.Method.DeclaringType != typeof(string))
            return false;

        var left = StripExactConversions(binary.Left, sourceReference, expressionPath + "/left");
        var right = StripExactConversions(binary.Right, sourceReference, expressionPath + "/right");
        var member = left is ConstantExpression { Value: null } ? right as MemberExpression
            : right is ConstantExpression { Value: null } ? left as MemberExpression
            : null;
        if (member is null || !TryGetGuardableMemberAccess(member, scope, out _))
            return false;

        var value = TranslateMember(member, scope, sourceReference, expressionPath + "/member");
        expression = binary.NodeType == ExpressionType.Equal
            ? Expr.Eq(value, Expr.Null())
            : Expr.Ne(value, Expr.Null());
        return true;
    }

    static Expression StripExactConversions(
        Expression expression,
        string sourceReference,
        string expressionPath)
    {
        var current = expression;
        while (current is UnaryExpression unary
               && unary.NodeType is ExpressionType.Convert or ExpressionType.ConvertChecked or ExpressionType.TypeAs)
        {
            if (!IsExactErasableConversion(unary.Operand.Type, unary.Type))
            {
                throw Fail(
                    RelationQueryExpressionDiagnosticCodes.ConversionUnsupported,
                    $"Conversion from '{Display(unary.Operand.Type)}' to '{Display(unary.Type)}' cannot be erased while preserving exact semantics.",
                    expressionPath,
                    sourceReference,
                    symbol: $"{Display(unary.Operand.Type)} -> {Display(unary.Type)}",
                    suggestion: "Align the CLR member and expression types, or introduce an explicit canonical conversion semantic.");
            }
            current = unary.Operand;
        }
        return current;
    }

    static bool IsExactErasableConversion(Type source, Type target)
    {
        if (source == target || target.IsAssignableFrom(source))
            return true;

        var targetUnderlying = Nullable.GetUnderlyingType(target);
        return targetUnderlying == source;
    }

    static void ValidateExactBinaryDomain(
        BinaryExpression binary,
        string expressionPath,
        string sourceReference)
    {
        var supported = binary.NodeType switch
        {
            ExpressionType.AndAlso or ExpressionType.OrElse =>
                binary.Left.Type == typeof(bool)
                && binary.Right.Type == typeof(bool)
                && binary.Type == typeof(bool),
            ExpressionType.Equal or ExpressionType.NotEqual =>
                binary.Left.Type == binary.Right.Type
                && IsExactEqualityType(binary.Left.Type)
                && (binary.Left.Type != typeof(string)
                    || IsProvablyNonNullString(binary.Left) || IsProvablyNonNullString(binary.Right)),
            ExpressionType.GreaterThan
                or ExpressionType.GreaterThanOrEqual
                or ExpressionType.LessThan
                or ExpressionType.LessThanOrEqual =>
                binary.Left.Type == binary.Right.Type
                && IsExactOrderingType(binary.Left.Type),
            ExpressionType.Add
                or ExpressionType.Subtract
                or ExpressionType.Multiply
                or ExpressionType.Divide =>
                binary.Left.Type == typeof(decimal)
                && binary.Right.Type == typeof(decimal)
                && binary.Type == typeof(decimal),
            _ => true
        };
        if (supported)
            return;

        throw Fail(
            RelationQueryExpressionDiagnosticCodes.OperatorUnsupported,
            $"C# operator '{binary.NodeType}' over '{Display(binary.Left.Type)}' and "
            + $"'{Display(binary.Right.Type)}' does not have the exact value, overflow, or null contract "
            + "of the canonical operator.",
            expressionPath,
            sourceReference,
            symbol: binary.NodeType.ToString(),
            suggestion: "Use exact canonical scalar operands, decimal arithmetic, or author the intended semantic operation structurally.");
    }

    static bool IsExactEqualityType(Type type)
    {
        var normalized = Nullable.GetUnderlyingType(type) ?? type;
        return type == normalized
               && (normalized == typeof(string)
                   || normalized == typeof(bool)
                   || normalized == typeof(byte)
                   || normalized == typeof(short)
                   || normalized == typeof(int)
                   || normalized == typeof(long)
                   || normalized == typeof(decimal));
    }

    static bool TryTranslateNonNullStringNullComparison(
        BinaryExpression binary,
        out bool result)
    {
        result = false;
        if (binary.NodeType is not (ExpressionType.Equal or ExpressionType.NotEqual)
            || binary.Left.Type != typeof(string)
            || binary.Right.Type != typeof(string))
        {
            return false;
        }

        var leftIsNull = binary.Left is ConstantExpression { Value: null };
        var rightIsNull = binary.Right is ConstantExpression { Value: null };
        if (leftIsNull == rightIsNull)
            return false;

        var nonNull = leftIsNull ? binary.Right : binary.Left;
        if (!IsProvablyNonNullString(nonNull))
            return false;

        result = binary.NodeType == ExpressionType.NotEqual;
        return true;
    }

    static bool IsExactOrderingType(Type type)
    {
        var normalized = Nullable.GetUnderlyingType(type) ?? type;
        return type == normalized
               && (normalized == typeof(byte)
                   || normalized == typeof(short)
                   || normalized == typeof(int)
                   || normalized == typeof(long)
                   || normalized == typeof(decimal));
    }

    static bool IsExactStringConcatOperator(MethodInfo method)
    {
        var parameters = method.GetParameters();
        return method.DeclaringType == typeof(string)
               && string.Equals(method.Name, nameof(string.Concat), StringComparison.Ordinal)
               && method.ReturnType == typeof(string)
               && parameters.Length == 2
               && parameters.All(static parameter => parameter.ParameterType == typeof(string));
    }

    static bool IsProvablyNonNullString(Expression expression)
    {
        var current = expression;
        while (current is UnaryExpression unary
               && unary.NodeType is ExpressionType.Convert or ExpressionType.ConvertChecked
               && IsExactErasableConversion(unary.Operand.Type, unary.Type))
        {
            current = unary.Operand;
        }

        return current switch
        {
            ConstantExpression { Value: string } => true,
            MemberExpression marker when TryGetParameterMarker(marker, out _, out var parameter) =>
                parameter?.IsProvablyNonNull == true,
            MemberExpression member when member.Type == typeof(string) =>
                IsProvablyNonNullPropertyChain(member),
            BinaryExpression { NodeType: ExpressionType.Add, Type: var type } binary when type == typeof(string) =>
                IsProvablyNonNullString(binary.Left) && IsProvablyNonNullString(binary.Right),
            ConditionalExpression conditional when conditional.Type == typeof(string) =>
                IsProvablyNonNullString(conditional.IfTrue) && IsProvablyNonNullString(conditional.IfFalse),
            MethodCallExpression call when IsStringConcat(call) =>
                call.Arguments.All(static argument => IsProvablyNonNullString(argument)),
            _ => false
        };
    }

    static bool IsProvablyNonNullPropertyChain(MemberExpression member)
    {
        Expression? current = member;
        while (current is MemberExpression access)
        {
            if (access.Member is not PropertyInfo property)
                return false;
            if (Nullable.GetUnderlyingType(property.PropertyType) is not null)
                return false;
            if (!property.PropertyType.IsValueType
                && !NonNullProperties.GetOrAdd(
                    property,
                    static candidate => new NullabilityInfoContext().Create(candidate).ReadState
                        == NullabilityState.NotNull))
            {
                return false;
            }

            current = access.Expression;
            while (current is UnaryExpression unary
                   && unary.NodeType is ExpressionType.Convert or ExpressionType.ConvertChecked
                   && IsExactErasableConversion(unary.Operand.Type, unary.Type))
            {
                current = unary.Operand;
            }
        }

        return current is ParameterExpression;
    }

    static bool IsSequenceElementProvablyNonNull(
        Expression sequence,
        Type elementType)
    {
        if (elementType.IsValueType)
            return Nullable.GetUnderlyingType(elementType) is null;

        var current = sequence;
        while (current is UnaryExpression unary
               && unary.NodeType is ExpressionType.Convert or ExpressionType.ConvertChecked
               && IsExactErasableConversion(unary.Operand.Type, unary.Type))
        {
            current = unary.Operand;
        }

        if (current is ConditionalExpression conditional)
        {
            return IsSequenceElementProvablyNonNull(conditional.IfTrue, elementType)
                   && IsSequenceElementProvablyNonNull(conditional.IfFalse, elementType);
        }

        if (current is BinaryExpression { NodeType: ExpressionType.Coalesce } coalesce)
        {
            return IsSequenceElementProvablyNonNull(coalesce.Left, elementType)
                   && IsSequenceElementProvablyNonNull(coalesce.Right, elementType);
        }

        return current is MemberExpression { Member: PropertyInfo property }
               && NonNullSequenceElementProperties.GetOrAdd(
                   property,
                   static candidate => IsSequenceElementDeclaredNonNull(candidate));
    }

    static bool IsSequenceElementDeclaredNonNull(PropertyInfo property)
    {
        var nullability = new NullabilityInfoContext().Create(property);
        if (property.PropertyType.IsArray)
        {
            return nullability.ElementType?.ReadState == NullabilityState.NotNull;
        }

        if (!TryGetSequenceElementType(property.PropertyType, out var elementType)
            || !property.PropertyType.IsGenericType)
        {
            return false;
        }

        var arguments = property.PropertyType.GetGenericArguments();
        var nullabilityArguments = nullability.GenericTypeArguments;
        if (arguments.Length != nullabilityArguments.Length)
            return false;

        for (var index = 0; index < arguments.Length; index++)
        {
            if (arguments[index] == elementType)
                return nullabilityArguments[index].ReadState == NullabilityState.NotNull;
        }

        return false;
    }

    static RelationQueryExpressionMemberPathResolver? CreateCurrentItemMemberPathResolver(
        Expression sequence,
        RootScope scope,
        string sourceReference,
        string expressionPath)
    {
        var provenance = ResolveSequenceProvenance(UnwrapSequenceView(sequence), scope);
        if (provenance.IsAmbiguous)
        {
            throw Fail(
                RelationQueryExpressionDiagnosticCodes.MemberPathUnavailable,
                "The collection expression combines items from different semantic bindings, so one authoritative current-item member mapping cannot be proven.",
                expressionPath,
                sourceReference,
                symbol: Display(sequence.Type),
                suggestion: "Project the collection branches to one canonical item shape before applying the scoped sequence operation.");
        }

        return provenance.Target?.MemberPathResolver;
    }

    static SequenceProvenance ResolveSequenceProvenance(
        Expression sequence,
        RootScope scope)
    {
        var current = sequence;
        while (current is UnaryExpression unary
               && unary.NodeType is ExpressionType.Convert or ExpressionType.ConvertChecked
               && IsExactErasableConversion(unary.Operand.Type, unary.Type))
        {
            current = unary.Operand;
        }

        if (current is MemberExpression member
            && TryReadMemberChain(member, out var root, out _)
            && root is ParameterExpression parameter
            && scope.Parameters.TryGetValue(parameter, out var target))
        {
            return new(target, IsAmbiguous: false);
        }

        if (current is ConditionalExpression conditional)
        {
            return CombineSequenceProvenance(
                ResolveSequenceProvenance(conditional.IfTrue, scope),
                ResolveSequenceProvenance(conditional.IfFalse, scope));
        }

        if (current is BinaryExpression { NodeType: ExpressionType.Coalesce } coalesce)
        {
            return CombineSequenceProvenance(
                ResolveSequenceProvenance(coalesce.Left, scope),
                ResolveSequenceProvenance(coalesce.Right, scope));
        }

        return default;
    }

    static SequenceProvenance CombineSequenceProvenance(
        SequenceProvenance left,
        SequenceProvenance right)
    {
        if (left.IsAmbiguous || right.IsAmbiguous)
            return new(null, IsAmbiguous: true);
        if (left.Target is null && right.Target is null)
            return default;
        if (left.Target is null || right.Target is null)
            return new(null, IsAmbiguous: true);
        return left.Target.Value == right.Target.Value
            ? left
            : new(null, IsAmbiguous: true);
    }

    ImmutableArray<PropertyInfo> NormalizeGuardedNullableValueMembers(
        ParameterExpression root,
        ParameterTarget target,
        ImmutableArray<PropertyInfo> members,
        RootScope scope,
        string expressionPath,
        string sourceReference)
    {
        var normalized = ImmutableArray.CreateBuilder<PropertyInfo>(members.Length);
        for (var index = 0; index < members.Length; index++)
        {
            var property = members[index];
            if (!IsNullableValueProperty(property))
            {
                normalized.Add(property);
                continue;
            }

            var guardedPath = normalized.ToImmutable();
            if (guardedPath.IsDefaultOrEmpty
                || target.UsesImportedMapping
                || !scope.IsKnownNonNull(root, guardedPath))
            {
                throw Fail(
                    RelationQueryExpressionDiagnosticCodes.NodeUnsupported,
                    "Nullable Value is safe only within control flow guarded by HasValue or an exact non-null test.",
                    $"{expressionPath}/members/{index}",
                    sourceReference,
                    symbol: Display(property),
                    suggestion: "Guard the required CLR-backed nullable field with HasValue before reading Value.");
            }
        }

        return normalized.ToImmutable();
    }

    RootScope ApplyConditionFacts(
        Expression condition,
        bool whenTrue,
        RootScope scope)
    {
        var current = StripConditionConversions(condition);
        if (current is UnaryExpression { NodeType: ExpressionType.Not } unary)
            return ApplyConditionFacts(unary.Operand, !whenTrue, scope);

        if (current is MemberExpression hasValue
            && TryGetNullableHasValueOperand(hasValue, out var nullableOperand)
            && whenTrue
            && TryGetGuardableMemberAccess(nullableOperand, scope, out var nullableAccess))
        {
            return scope.WithKnownNonNull(nullableAccess);
        }

        if (current is BinaryExpression binary)
        {
            if (binary.NodeType == ExpressionType.AndAlso && whenTrue)
            {
                var withLeft = ApplyConditionFacts(binary.Left, whenTrue: true, scope);
                return ApplyConditionFacts(binary.Right, whenTrue: true, withLeft);
            }
            if (binary.NodeType == ExpressionType.OrElse && !whenTrue)
            {
                var withLeft = ApplyConditionFacts(binary.Left, whenTrue: false, scope);
                return ApplyConditionFacts(binary.Right, whenTrue: false, withLeft);
            }
            if (TryGetNullComparedMember(binary, out var member)
                && TryGetGuardableMemberAccess(member, scope, out var access))
            {
                var provesNonNull = binary.NodeType switch
                {
                    ExpressionType.NotEqual => whenTrue,
                    ExpressionType.Equal => !whenTrue,
                    _ => false
                };
                if (provesNonNull)
                    return scope.WithKnownNonNull(access);
            }
        }

        return scope;
    }

    static bool TryGetNullableHasValueOperand(
        MemberExpression member,
        out MemberExpression operand)
    {
        if (member.Expression is MemberExpression candidate
            && string.Equals(member.Member.Name, nameof(Nullable<int>.HasValue), StringComparison.Ordinal)
            && member.Member.DeclaringType is { IsGenericType: true } declaringType
            && declaringType.GetGenericTypeDefinition() == typeof(Nullable<>))
        {
            operand = candidate;
            return true;
        }

        operand = null!;
        return false;
    }

    static bool IsNullableValueProperty(PropertyInfo property) =>
        string.Equals(property.Name, nameof(Nullable<int>.Value), StringComparison.Ordinal)
        && property.DeclaringType is { IsGenericType: true } declaringType
        && declaringType.GetGenericTypeDefinition() == typeof(Nullable<>);

    bool TryGetGuardableMemberAccess(
        MemberExpression member,
        RootScope scope,
        out GuardedMemberAccess access)
    {
        access = default;
        // Convention-inferred CLR shapes make property presence required. An imported mapping may make the
        // same CLR path optional, and the current resolver does not expose enough per-path evidence to prove
        // that a CLR null test is equivalent to a canonical null test, so imported paths remain fail-closed.
        if (!TryReadMemberChain(member, out var root, out var members)
            || root is not ParameterExpression parameter
            || !scope.Parameters.TryGetValue(parameter, out var target)
            || target.UsesImportedMapping
            || members.Any(IsNullableValueProperty)
            || !IsDeclaredNullable(members[^1]))
        {
            return false;
        }

        for (var index = 0; index < members.Length - 1; index++)
        {
            if (IsDeclaredNullable(members[index]))
                return false;
        }

        access = new(parameter, members);
        return true;
    }

    static bool TryGetNullComparedMember(
        BinaryExpression binary,
        out MemberExpression member)
    {
        member = null!;
        if (binary.NodeType is not (ExpressionType.Equal or ExpressionType.NotEqual))
            return false;
        if (binary.Method is not null && binary.Method.DeclaringType != typeof(string))
            return false;

        var left = StripConditionConversions(binary.Left);
        var right = StripConditionConversions(binary.Right);
        var candidate = left is ConstantExpression { Value: null } ? right as MemberExpression
            : right is ConstantExpression { Value: null } ? left as MemberExpression
            : null;
        if (candidate is null)
            return false;

        member = candidate;
        return true;
    }

    static Expression StripConditionConversions(Expression expression)
    {
        var current = expression;
        while (current is UnaryExpression unary
               && unary.NodeType is ExpressionType.Convert or ExpressionType.ConvertChecked or ExpressionType.TypeAs
               && IsExactErasableConversion(unary.Operand.Type, unary.Type))
        {
            current = unary.Operand;
        }
        return current;
    }

    static bool IsDeclaredNullable(PropertyInfo property) =>
        Nullable.GetUnderlyingType(property.PropertyType) is not null
        || !property.PropertyType.IsValueType
        && !NonNullProperties.GetOrAdd(
            property,
            static candidate => new NullabilityInfoContext().Create(candidate).ReadState
                == NullabilityState.NotNull);

    static void ValidateMemberNavigation(
        ParameterExpression root,
        ImmutableArray<PropertyInfo> members,
        RootScope scope,
        string expressionPath,
        string sourceReference)
    {
        for (var index = 0; index < members.Length - 1; index++)
        {
            var property = members[index];
            var nullableValue = Nullable.GetUnderlyingType(property.PropertyType) is not null;
            var nullableReference = !property.PropertyType.IsValueType
                                    && !NonNullProperties.GetOrAdd(
                                        property,
                                        static candidate => new NullabilityInfoContext().Create(candidate).ReadState
                                            == NullabilityState.NotNull);
            if (!nullableValue && !nullableReference)
                continue;

            if (scope.IsKnownNonNull(root, members[..(index + 1)]))
                continue;

            throw Fail(
                RelationQueryExpressionDiagnosticCodes.NodeUnsupported,
                $"Member path navigation crosses nullable receiver '{Display(property)}'; C# throws when it is null while canonical field navigation preserves null/missing.",
                $"{expressionPath}/members/{index}",
                sourceReference,
                symbol: Display(property),
                suggestion: "Make the receiver required/non-null in the semantic shape, or author explicit null/missing behavior structurally.");
        }
    }

    static bool IsCanonicalFrameworkOperator(BinaryExpression binary)
    {
        var declaringType = binary.Method?.DeclaringType;
        if (declaringType is null)
            return true;

        var methodNameMatches = binary.NodeType switch
        {
            ExpressionType.Equal => binary.Method!.Name == "op_Equality",
            ExpressionType.NotEqual => binary.Method!.Name == "op_Inequality",
            ExpressionType.GreaterThan => binary.Method!.Name == "op_GreaterThan",
            ExpressionType.GreaterThanOrEqual => binary.Method!.Name == "op_GreaterThanOrEqual",
            ExpressionType.LessThan => binary.Method!.Name == "op_LessThan",
            ExpressionType.LessThanOrEqual => binary.Method!.Name == "op_LessThanOrEqual",
            ExpressionType.Add => binary.Method!.Name == "op_Addition",
            ExpressionType.Subtract => binary.Method!.Name == "op_Subtraction",
            ExpressionType.Multiply => binary.Method!.Name == "op_Multiply",
            ExpressionType.Divide => binary.Method!.Name == "op_Division",
            _ => false
        };
        if (!methodNameMatches)
            return false;

        return binary.NodeType switch
        {
            ExpressionType.Equal or ExpressionType.NotEqual =>
                declaringType == typeof(string)
                || declaringType == typeof(decimal)
                || declaringType == typeof(Guid)
                || declaringType == typeof(DateTime)
                || declaringType == typeof(DateTimeOffset)
                || declaringType == typeof(DateOnly)
                || declaringType == typeof(TimeOnly)
                || declaringType == typeof(TimeSpan),
            ExpressionType.GreaterThan
                or ExpressionType.GreaterThanOrEqual
                or ExpressionType.LessThan
                or ExpressionType.LessThanOrEqual =>
                declaringType == typeof(decimal)
                || declaringType == typeof(DateTime)
                || declaringType == typeof(DateTimeOffset)
                || declaringType == typeof(DateOnly)
                || declaringType == typeof(TimeOnly)
                || declaringType == typeof(TimeSpan),
            ExpressionType.Add
                or ExpressionType.Subtract
                or ExpressionType.Multiply
                or ExpressionType.Divide => declaringType == typeof(decimal),
            _ => false
        };
    }

    static Expression StripQuote(Expression expression)
    {
        var current = expression;
        while (current is UnaryExpression { NodeType: ExpressionType.Quote } quote)
            current = quote.Operand;
        return current;
    }

    static bool TryReadMemberChain(
        MemberExpression member,
        out Expression root,
        out ImmutableArray<PropertyInfo> members)
    {
        List<PropertyInfo> reversed = [];
        Expression? current = member;
        while (current is MemberExpression currentMember && currentMember.Expression is not null)
        {
            if (currentMember.Member is not PropertyInfo property)
            {
                root = member;
                members = [];
                return false;
            }
            reversed.Add(property);
            current = currentMember.Expression;
            while (current is UnaryExpression unary
                   && unary.NodeType is ExpressionType.Convert or ExpressionType.ConvertChecked or ExpressionType.TypeAs
                   && IsExactErasableConversion(unary.Operand.Type, unary.Type))
            {
                current = unary.Operand;
            }
        }

        if (current is null || reversed.Count == 0)
        {
            root = member;
            members = [];
            return false;
        }

        reversed.Reverse();
        root = current;
        members = [.. reversed];
        return true;
    }

    static bool IsCollectionCountMember(MemberExpression member)
    {
        if (member.Expression is null || member.Expression.Type == typeof(string))
            return false;
        if (string.Equals(member.Member.Name, "Length", StringComparison.Ordinal))
        {
            return member.Expression.Type.IsArray
                   && member.Expression.Type != typeof(byte[]);
        }
        if (!string.Equals(member.Member.Name, "Count", StringComparison.Ordinal))
            return false;

        if (member.Member is not PropertyInfo property)
            return false;
        var type = member.Expression.Type;
        return type != typeof(byte[]) && ImplementsCollectionCount(type, property);
    }

    static bool ImplementsCollectionCount(Type type, PropertyInfo property)
    {
        List<Type> contracts = [];
        if (type == typeof(ICollection)
            || type.IsGenericType
            && type.GetGenericTypeDefinition() is var definition
            && (definition == typeof(ICollection<>) || definition == typeof(IReadOnlyCollection<>)))
        {
            contracts.Add(type);
        }
        contracts.AddRange(type.GetInterfaces().Where(static candidate =>
            candidate == typeof(ICollection)
            || candidate.IsGenericType
            && candidate.GetGenericTypeDefinition() is var definition
            && (definition == typeof(ICollection<>) || definition == typeof(IReadOnlyCollection<>))));

        foreach (var contract in contracts)
        {
            var contractProperty = contract.GetProperty(nameof(ICollection.Count));
            if (contractProperty?.GetMethod is not { } contractGetter)
                continue;
            if (ShapeTypeInspector.IsSameProperty(property, contractProperty))
            {
                return true;
            }
            if (type.IsInterface || property.GetMethod is null)
                continue;

            var map = type.GetInterfaceMap(contract);
            for (var index = 0; index < map.InterfaceMethods.Length; index++)
            {
                if (map.InterfaceMethods[index] == contractGetter
                    && map.TargetMethods[index] == property.GetMethod)
                {
                    return true;
                }
            }
        }

        return false;
    }

    static bool IsStringEndsWith(MethodCallExpression call) =>
        call.Object?.Type == typeof(string)
        && string.Equals(call.Method.Name, nameof(string.EndsWith), StringComparison.Ordinal);

    static bool IsDateTimeOffsetEqualsExact(MethodCallExpression call) =>
        call.Object?.Type == typeof(DateTimeOffset)
        && call.Method.DeclaringType == typeof(DateTimeOffset)
        && string.Equals(call.Method.Name, nameof(DateTimeOffset.EqualsExact), StringComparison.Ordinal)
        && call.Method.ReturnType == typeof(bool)
        && call.Arguments is [{ Type: var argumentType }]
        && argumentType == typeof(DateTimeOffset);

    static bool IsOrdinalEndsWith(MethodCallExpression call)
        => IsOrdinalStringPredicate(call, nameof(string.EndsWith));

    static bool IsStringStartsWith(MethodCallExpression call) =>
        call.Object?.Type == typeof(string)
        && string.Equals(call.Method.Name, nameof(string.StartsWith), StringComparison.Ordinal);

    static bool IsOrdinalStartsWith(MethodCallExpression call) =>
        IsOrdinalStringPredicate(call, nameof(string.StartsWith));

    static bool IsStringContains(MethodCallExpression call) =>
        call.Object?.Type == typeof(string)
        && string.Equals(call.Method.Name, nameof(string.Contains), StringComparison.Ordinal);

    static bool IsOrdinalStringContains(MethodCallExpression call) =>
        IsOrdinalStringPredicate(call, nameof(string.Contains));

    static bool IsOrdinalStringPredicate(MethodCallExpression call, string methodName)
    {
        if (call.Object?.Type != typeof(string)
            || !string.Equals(call.Method.Name, methodName, StringComparison.Ordinal)
            || call.Arguments.Count != 2
            || call.Arguments[0].Type != typeof(string))
        {
            return false;
        }

        var comparison = call.Arguments[1];
        while (comparison is UnaryExpression unary
               && unary.NodeType is ExpressionType.Convert or ExpressionType.ConvertChecked
               && IsExactErasableConversion(unary.Operand.Type, unary.Type))
        {
            comparison = unary.Operand;
        }
        return comparison is ConstantExpression { Value: StringComparison.Ordinal };
    }

    static bool IsStringConcat(MethodCallExpression call) =>
        call.Method.DeclaringType == typeof(string)
        && string.Equals(call.Method.Name, nameof(string.Concat), StringComparison.Ordinal)
        && call.Arguments.Count > 0
        && call.Arguments.All(static argument => argument.Type == typeof(string));

    static bool TryGetContainsOperands(
        MethodCallExpression call,
        out Expression collection,
        out Expression candidate)
    {
        if ((string.Equals(call.Method.Name, nameof(Enumerable.Contains), StringComparison.Ordinal)
             && call.Method.DeclaringType == typeof(Enumerable)
             || call.Method.DeclaringType == typeof(MemoryExtensions)
                && string.Equals(call.Method.Name, nameof(MemoryExtensions.Contains), StringComparison.Ordinal))
            && call.Arguments.Count == 2)
        {
            collection = UnwrapSequenceView(call.Arguments[0]);
            candidate = call.Arguments[1];
            return true;
        }

        if (call.Object is not null
            && call.Method.DeclaringType is { IsGenericType: true } declaringType
            && declaringType.GetGenericTypeDefinition() == typeof(List<>)
            && string.Equals(call.Method.Name, nameof(List<object>.Contains), StringComparison.Ordinal)
            && call.Arguments.Count == 1)
        {
            collection = call.Object;
            candidate = call.Arguments[0];
            return true;
        }

        collection = null!;
        candidate = null!;
        return false;
    }

    static Expression UnwrapSequenceView(Expression expression)
    {
        if (expression is UnaryExpression
            {
                NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked,
                Method.Name: "op_Implicit"
            } conversion
            && IsExactArraySpanView(conversion.Operand.Type, conversion.Type))
        {
            return conversion.Operand;
        }

        if (expression is MethodCallExpression
            {
                Method.Name: "op_Implicit",
                Arguments.Count: 1
            } conversionCall
            && IsExactArraySpanView(conversionCall.Arguments[0].Type, conversionCall.Type))
        {
            return conversionCall.Arguments[0];
        }

        return expression;
    }

    static bool IsExactArraySpanView(Type source, Type target)
    {
        if (!source.IsArray || !target.IsGenericType)
            return false;

        var definition = target.GetGenericTypeDefinition();
        return (definition == typeof(ReadOnlySpan<>) || definition == typeof(Span<>))
               && source.GetElementType() == target.GetGenericArguments()[0];
    }

    static bool HasGuaranteedDefaultMembershipEquality(Type sequenceType) =>
        sequenceType.IsArray
        || sequenceType.IsGenericType
        && sequenceType.GetGenericTypeDefinition() == typeof(List<>);

    static bool IsSequenceMethod(MethodCallExpression call, string name) =>
        string.Equals(call.Method.Name, name, StringComparison.Ordinal)
        && call.Method.DeclaringType is { } declaringType
        && (declaringType == typeof(Enumerable) || declaringType == typeof(Queryable));

    static bool TryGetEagerSelectMaterialization(
        MethodCallExpression call,
        out MethodCallExpression select)
    {
        if (call.Method.DeclaringType == typeof(Enumerable)
            && string.Equals(call.Method.Name, nameof(Enumerable.ToArray), StringComparison.Ordinal)
            && call.Arguments.Count == 1
            && call.Arguments[0] is MethodCallExpression candidate
            && IsSequenceMethod(candidate, nameof(Enumerable.Select)))
        {
            select = candidate;
            return true;
        }

        select = null!;
        return false;
    }

    static bool TryGetSequenceElementType(Type sequenceType, out Type elementType)
    {
        if (sequenceType == typeof(string) || sequenceType == typeof(byte[]))
        {
            elementType = null!;
            return false;
        }

        if (sequenceType.IsArray)
        {
            elementType = sequenceType.GetElementType()!;
            return true;
        }

        if (sequenceType.IsGenericType
            && sequenceType.GetGenericTypeDefinition() is var genericDefinition
            && (genericDefinition == typeof(ReadOnlySpan<>) || genericDefinition == typeof(Span<>)))
        {
            elementType = sequenceType.GetGenericArguments()[0];
            return true;
        }

        if (sequenceType.IsGenericType
            && sequenceType.GetGenericTypeDefinition() == typeof(IEnumerable<>))
        {
            elementType = sequenceType.GetGenericArguments()[0];
            return true;
        }

        Type? selectedElementType = null;
        foreach (var enumerable in sequenceType.GetInterfaces())
        {
            if (!enumerable.IsGenericType
                || enumerable.GetGenericTypeDefinition() != typeof(IEnumerable<>))
            {
                continue;
            }

            var candidate = enumerable.GetGenericArguments()[0];
            if (selectedElementType is not null && selectedElementType != candidate)
            {
                elementType = null!;
                return false;
            }

            selectedElementType = candidate;
        }

        elementType = selectedElementType!;
        return selectedElementType is not null;
    }

    static bool HasExactCanonicalMembershipEquality(Type type)
    {
        var value = Nullable.GetUnderlyingType(type) ?? type;
        return value == typeof(string)
               || value == typeof(char)
               || value == typeof(bool)
               || value == typeof(byte)
               || value == typeof(sbyte)
               || value == typeof(short)
               || value == typeof(ushort)
               || value == typeof(int)
               || value == typeof(uint)
               || value == typeof(long)
               || value == typeof(decimal);
    }

    static LoweringFailure CapturedOrUnsupportedMember(
        MemberExpression member,
        string expressionPath,
        string sourceReference)
    {
        var captured = member.Expression is null or ConstantExpression
                       || member.Expression is MemberExpression;
        return Fail(
            captured
                ? RelationQueryExpressionDiagnosticCodes.CapturedValueUnsupported
                : RelationQueryExpressionDiagnosticCodes.NodeUnsupported,
            captured
                ? "Captured CLR state is not evaluated by relation/query expression authoring."
                : "The member access is not rooted at a visible semantic binding or current-item parameter.",
            expressionPath,
            sourceReference,
            symbol: Display(member.Member),
            suggestion: captured
                ? "Declare the runtime value as an explicit query parameter and reference its Value marker."
                : "Select a member rooted at a top-level source binding or nested sequence item.");
    }

    static RelationQueryAuthoringSource Source(string reference, string description) =>
        new(Producer, reference, description);

    static RelationQueryExpressionLoweringResult<T> Success<T>(T value)
        where T : class =>
        new(value, []);

    static RelationQueryExpressionLoweringResult<T> Failure<T>(RelationQueryExpressionDiagnostic diagnostic)
        where T : class =>
        new(value: null, [diagnostic]);

    static LoweringFailure Fail(
        string code,
        string message,
        string expressionPath,
        string sourceReference,
        string? symbol = null,
        string? suggestion = null) =>
        new(
            new RelationQueryExpressionDiagnostic(
                code,
                DiagnosticSeverity.Error,
                message,
                expressionPath,
                sourceReference,
                symbol,
                suggestion));

    static string Display(MemberInfo? member) => member switch
    {
        null => "unknown",
        MethodBase method => $"{Display(method.DeclaringType!)}.{method.Name}({string.Join(", ", method.GetParameters().Select(static parameter => Display(parameter.ParameterType)))})",
        _ => $"{Display(member.DeclaringType!)}.{member.Name}"
    };

    static string Display(Type type) => type.FullName ?? type.Name;

    enum ParameterTargetKind
    {
        Binding,
        CurrentItem
    }

    readonly record struct ParameterTarget(
        ParameterTargetKind Kind,
        ValueBindingId Binding,
        Type RootType,
        bool IsProvablyNonNull,
        RelationQueryExpressionMemberPathResolver? MemberPathResolver,
        bool UsesImportedMapping)
    {
        public static ParameterTarget ForBinding(
            ValueBindingId binding,
            Type rootType,
            RelationQueryExpressionMemberPathResolver? memberPathResolver,
            bool usesImportedMapping) =>
            new(
                ParameterTargetKind.Binding,
                binding,
                rootType,
                IsProvablyNonNull: true,
                MemberPathResolver: memberPathResolver,
                UsesImportedMapping: usesImportedMapping);

        public static ParameterTarget ForCurrentItem(
            Type rootType,
            bool isProvablyNonNull,
            RelationQueryExpressionMemberPathResolver? memberPathResolver,
            bool usesImportedMapping) =>
            new(
                ParameterTargetKind.CurrentItem,
                default,
                rootType,
                isProvablyNonNull,
                memberPathResolver,
                usesImportedMapping);
    }

    readonly record struct GuardedMemberAccess(
        ParameterExpression Root,
        ImmutableArray<PropertyInfo> Members);

    readonly record struct SequenceProvenance(
        ParameterTarget? Target,
        bool IsAmbiguous);

    sealed class RootScope
    {
        public RootScope(
            Dictionary<ParameterExpression, ParameterTarget> parameters,
            ParameterExpression? activeCurrentItem,
            ImmutableArray<GuardedMemberAccess> knownNonNull = default)
        {
            Parameters = parameters;
            ActiveCurrentItem = activeCurrentItem;
            KnownNonNull = knownNonNull.IsDefault ? [] : knownNonNull;
        }

        public Dictionary<ParameterExpression, ParameterTarget> Parameters { get; }

        public ParameterExpression? ActiveCurrentItem { get; }

        ImmutableArray<GuardedMemberAccess> KnownNonNull { get; }

        public bool IsKnownNonNull(
            ParameterExpression root,
            IReadOnlyList<PropertyInfo> members) =>
            KnownNonNull.Any(access =>
                ReferenceEquals(access.Root, root)
                && access.Members.SequenceEqual(members));

        public RootScope WithKnownNonNull(GuardedMemberAccess access) =>
            IsKnownNonNull(access.Root, access.Members)
                ? this
                : new(Parameters, ActiveCurrentItem, KnownNonNull.Add(access));

        public RootScope WithCurrentItem(
            ParameterExpression parameter,
            bool isProvablyNonNull,
            RelationQueryExpressionMemberPathResolver? memberPathResolver,
            bool usesImportedMapping)
        {
            Dictionary<ParameterExpression, ParameterTarget> nested =
                new(Parameters, ReferenceEqualityComparer.Instance)
                {
                    [parameter] = ParameterTarget.ForCurrentItem(
                        parameter.Type,
                        isProvablyNonNull,
                        memberPathResolver,
                        usesImportedMapping)
                };
            return new(nested, parameter, KnownNonNull);
        }
    }

    sealed class LoweringFailure : Exception
    {
        public LoweringFailure(RelationQueryExpressionDiagnostic diagnostic)
            : base(diagnostic.Message)
        {
            Diagnostic = diagnostic;
        }

        public RelationQueryExpressionDiagnostic Diagnostic { get; }
    }
}
