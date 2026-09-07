using System.Collections.Immutable;
using System.Globalization;
using Cohesive.Model.Serialization;

namespace Cohesive.Model.Expressions;

/// <summary>
/// Deterministic compiler-front-end analysis for canonical <see cref="Expr"/> values.
/// </summary>
public static class ExprAnalyzer
{
    /// <summary>
    /// Analyzes an expression against its declared site, scope, expectation, and selected capability profile.
    /// </summary>
    /// <param name="site">Expression site to analyze.</param>
    /// <param name="semantics">Function and operator semantics; defaults to <see cref="ExprSemanticsCatalog.Default"/>.</param>
    /// <returns>Immutable requirements, known result information, and structured diagnostics.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="site"/> is <see langword="null"/>.</exception>
    public static ExprAnalysisResult Analyze(
        ExprSite site,
        ExprSemanticsCatalog? semantics = null)
    {
        ArgumentNullException.ThrowIfNull(site);
        Context context = new(site, semantics ?? ExprSemanticsCatalog.Default);
        return context.Analyze();
    }

    sealed class Context(
        ExprSite site,
        ExprSemanticsCatalog semantics)
    {
        readonly List<ExprFieldRequirement> fields = [];
        readonly List<ExprFieldUse> fieldUses = [];
        readonly HashSet<ValueBindingId> bindings = [];
        readonly List<ExprBindingUse> bindingUses = [];
        readonly HashSet<string> parameters = new(StringComparer.Ordinal);
        readonly HashSet<ExprCapabilityRequirement> capabilities = [];
        readonly List<ExprCapabilityUse> capabilityUses = [];
        readonly List<DocumentValidationDiagnostic> diagnostics = [];
        bool requiresCurrentItem;
        Dictionary<(ValueBindingId Binding, FieldPath Path), ValueContract> guardedFields = [];
        HashSet<ValueBindingId> presentBindings = [];

        public ExprAnalysisResult Analyze()
        {
            var result = AnalyzeNode(
                site.Expression,
                site.Scope,
                site.Expectation,
                expressionPath: "/");
            var requirements = new ExprRequirements(
                fields,
                bindings,
                parameters,
                requiresCurrentItem,
                capabilities);

            ValidateAllowedDependencies(requirements);
            return new(
                site,
                semantics,
                result.Category,
                result.Value,
                requirements,
                capabilityUses,
                DocumentValidationResult.FromDiagnostics(SortDiagnostics(diagnostics)),
                knownConstant: result.ConstantValue,
                fieldUses: fieldUses,
                bindingUses: bindingUses);
        }

        NodeResult AnalyzeNode(
            Expr? expression,
            ExprScope scope,
            ExprExpectation expectation,
            string expressionPath)
        {
            if (expression is null)
            {
                Add(
                    ExprAnalysisDiagnosticCodes.ExpressionMissing,
                    "A required expression node is missing.",
                    expressionPath);
                return NodeResult.Unknown;
            }

            var result = expression switch
            {
                BindingExpr binding => AnalyzeBinding(binding, scope, expressionPath),
                FieldExpr field => AnalyzeField(field.Path, field.Binding, wasUnqualified: field.Binding is null, scope, expressionPath),
                FieldRefExpr field => AnalyzeTypedField(field, scope, expressionPath),
                CurrentItemExpr => AnalyzeCurrentItem(scope, expressionPath),
                ParameterExpr parameter => AnalyzeParameter(parameter, scope, expressionPath),
                ConstantExpr constant => AnalyzeConstant(constant, expressionPath),
                LiteralExpr literal => AnalyzeLiteral(literal, expressionPath),
                UnaryExpr unary => AnalyzeUnary(unary, scope, expressionPath),
                BinaryExpr binary => AnalyzeBinary(binary, scope, expressionPath),
                ConditionalExpr conditional => AnalyzeConditional(conditional, scope, expressionPath),
                CallExpr call => AnalyzeCall(call, scope, expressionPath),
                AggregateExpr aggregate => AnalyzeAggregate(aggregate, scope, expressionPath),
                _ => AnalyzeUnsupported(expression, expressionPath)
            };

            ValidateResultExpectation(result, expectation, expressionPath);
            return result;
        }

        NodeResult AnalyzeBinding(
            BindingExpr expression,
            ExprScope scope,
            string expressionPath)
        {
            RequireOperation(ExprCapabilities.Binding, expressionPath);
            if (string.IsNullOrWhiteSpace(expression.Binding.Value))
            {
                Add(
                    ExprAnalysisDiagnosticCodes.BindingInvalid,
                    "A binding expression requires a non-empty identifier.",
                    expressionPath);
                return NodeResult.Unknown;
            }

            bindingUses.Add(new(expression.Binding, expressionPath));
            bindings.Add(expression.Binding);
            if (!scope.TryGetBinding(expression.Binding, out var binding))
            {
                Add(
                    ExprAnalysisDiagnosticCodes.BindingNotVisible,
                    $"Expression references binding '{expression.Binding.Value}' that is not visible at this site.",
                    expressionPath);
                return NodeResult.Unknown;
            }

            var value = binding.Availability == ExprBindingAvailability.MayBeAbsent && !presentBindings.Contains(binding.Id)
                ? WithPresence(binding.Value, FieldPresence.Optional)
                : binding.Value;
            return NodeResult.FromValue(value);
        }

        NodeResult AnalyzeField(
            FieldPath path,
            ValueBindingId? explicitBinding,
            bool wasUnqualified,
            ExprScope scope,
            string expressionPath)
        {
            RequireOperation(ExprCapabilities.Field, expressionPath);
            if (!path.Segments.IsDefaultOrEmpty && path.Segments.Length > 1)
                RequireOperation(ExprCapabilities.NestedFieldPath, expressionPath);
            var pathIsValid = ValidateFieldPath(path, expressionPath);
            if (explicitBinding is { } authoredBinding
                && string.IsNullOrWhiteSpace(authoredBinding.Value))
            {
                Add(
                    ExprAnalysisDiagnosticCodes.BindingInvalid,
                    "An explicit field binding must have a non-empty identifier.",
                    expressionPath);
                return NodeResult.Unknown;
            }
            if (pathIsValid
                && explicitBinding is null
                && ExprFieldRequirement.IsCurrentItemPath(path))
            {
                AddFieldUse(new(
                    path,
                    ExprFieldRootKind.CurrentItem,
                    wasUnqualified: true),
                    expressionPath);
                return AnalyzeCurrentItemPath(path, scope, expressionPath);
            }

            var resolvedBinding = explicitBinding ?? scope.ImplicitBinding;
            if (pathIsValid)
            {
                AddFieldUse(new(
                    path,
                    resolvedBinding is null
                        ? ExprFieldRootKind.Unresolved
                        : ExprFieldRootKind.Binding,
                    resolvedBinding,
                    wasUnqualified),
                    expressionPath);
            }

            if (resolvedBinding is null)
            {
                Add(
                    ExprAnalysisDiagnosticCodes.ImplicitBindingUnavailable,
                    "An unqualified field requires an explicit, visible implicit binding.",
                    expressionPath);
                return NodeResult.Unknown;
            }

            bindings.Add(resolvedBinding.Value);
            if (!scope.TryGetBinding(resolvedBinding.Value, out var binding))
            {
                Add(
                    ExprAnalysisDiagnosticCodes.BindingNotVisible,
                    $"Expression references binding '{resolvedBinding.Value.Value}' that is not visible at this site.",
                    expressionPath);
                return NodeResult.Unknown;
            }

            if (!pathIsValid)
                return NodeResult.Unknown;

            if (TryResolvePath(binding.Value, path, out var value, out var definitelyMissing))
            {
                var resolvedValue = binding.Availability == ExprBindingAvailability.MayBeAbsent && !presentBindings.Contains(binding.Id)
                    ? WithPresence(value!, FieldPresence.Optional)
                    : value!;
                return NodeResult.FromValue(guardedFields.GetValueOrDefault((binding.Id, path), resolvedValue));
            }

            if (definitelyMissing)
            {
                Add(
                    ExprAnalysisDiagnosticCodes.FieldPathUnknown,
                    $"Field path '{path}' does not exist on binding '{resolvedBinding.Value.Value}'.",
                    expressionPath);
            }

            if (!definitelyMissing)
            {
                var unresolvedValue = value;
                if (binding.Availability == ExprBindingAvailability.MayBeAbsent && !presentBindings.Contains(binding.Id))
                {
                    unresolvedValue = WithPresence(
                        unresolvedValue ?? new ValueContract(),
                        FieldPresence.Optional);
                }

                if (unresolvedValue is not null)
                    return NodeResult.FromValue(guardedFields.GetValueOrDefault((binding.Id, path), unresolvedValue));
            }

            return NodeResult.Unknown;
        }

        void AddFieldUse(ExprFieldRequirement requirement, string expressionPath)
        {
            fields.Add(requirement);
            fieldUses.Add(new(requirement, expressionPath));
        }

        NodeResult AnalyzeCurrentItemPath(
            FieldPath authoredPath,
            ExprScope scope,
            string expressionPath)
        {
            requiresCurrentItem = true;
            RequireOperation(ExprCapabilities.CurrentItem, expressionPath);
            if (scope.CurrentItem is null)
            {
                Add(
                    ExprAnalysisDiagnosticCodes.CurrentItemUnavailable,
                    "Current-item access is not available at this expression site.",
                    expressionPath);
                return NodeResult.Unknown;
            }

            if (authoredPath.Segments.Length == 1)
                return NodeResult.FromValue(scope.CurrentItem);

            FieldPath relativePath = new([.. authoredPath.Segments.Skip(1)]);
            if (TryResolvePath(scope.CurrentItem, relativePath, out var value, out var definitelyMissing))
                return NodeResult.FromValue(value!);

            if (definitelyMissing)
            {
                Add(
                    ExprAnalysisDiagnosticCodes.FieldPathUnknown,
                    $"Field path '{authoredPath}' does not exist on the current item.",
                    expressionPath);
            }
            else if (value is not null)
            {
                return NodeResult.FromValue(value);
            }

            return NodeResult.Unknown;
        }

        NodeResult AnalyzeTypedField(
            FieldRefExpr field,
            ExprScope scope,
            string expressionPath)
        {
            RequireOperation(ExprCapabilities.TypedField, expressionPath);
            var resolved = AnalyzeField(
                field.Path,
                explicitBinding: null,
                wasUnqualified: true,
                scope,
                expressionPath);

            if (field.Type is null)
            {
                Add(
                    ExprAnalysisDiagnosticCodes.ResultTypeMismatch,
                    "Typed field reference has no declared result type.",
                    Child(expressionPath, "type"));
                return NodeResult.Unknown;
            }

            return ReconcileDeclaredResult(
                resolved,
                NodeResult.FromValue(new(field.Type)),
                "Typed field reference",
                Child(expressionPath, "type"));
        }

        NodeResult AnalyzeCurrentItem(ExprScope scope, string expressionPath)
        {
            requiresCurrentItem = true;
            RequireOperation(ExprCapabilities.CurrentItem, expressionPath);
            if (scope.CurrentItem is null)
            {
                Add(
                    ExprAnalysisDiagnosticCodes.CurrentItemUnavailable,
                    "Current-item access is not available at this expression site.",
                    expressionPath);
                return NodeResult.Unknown;
            }

            return NodeResult.FromValue(scope.CurrentItem);
        }

        NodeResult AnalyzeParameter(
            ParameterExpr parameter,
            ExprScope scope,
            string expressionPath)
        {
            RequireOperation(ExprCapabilities.Parameter, expressionPath);
            if (string.IsNullOrWhiteSpace(parameter.Parameter))
            {
                Add(
                    ExprAnalysisDiagnosticCodes.ParameterInvalid,
                    "A parameter expression must reference a non-empty parameter name.",
                    expressionPath);
                return NodeResult.Unknown;
            }

            parameters.Add(parameter.Parameter);
            if (!scope.TryGetParameter(parameter.Parameter, out var declaration))
            {
                Add(
                    ExprAnalysisDiagnosticCodes.ParameterNotDeclared,
                    $"Expression references undeclared parameter '{parameter.Parameter}'.",
                    expressionPath);
                return NodeResult.Unknown;
            }

            return NodeResult.FromValue(declaration.Value);
        }

        NodeResult AnalyzeConstant(ConstantExpr constant, string expressionPath)
        {
            RequireOperation(ExprCapabilities.Constant, expressionPath);
            return InferConstant(constant.Value);
        }

        static NodeResult InferConstant(ObservationValue value)
        {
            return value.Kind switch
            {
                ObservationValueKind.Undefined => NodeResult.FromValue(
                    new(presence: FieldPresence.Optional),
                    value),
                ObservationValueKind.Null => NodeResult.FromValue(
                    new(nullability: FieldNullability.Nullable),
                    value),
                ObservationValueKind.Bool => NodeResult.FromValue(new(new ScalarTypeRef(ScalarTypeKind.Bool)), value),
                ObservationValueKind.Int64 => NodeResult.FromValue(new(new ScalarTypeRef(ScalarTypeKind.Int64)), value),
                ObservationValueKind.Double => NodeResult.FromValue(new(new ScalarTypeRef(ScalarTypeKind.Decimal)), value),
                ObservationValueKind.Decimal => NodeResult.FromValue(new(new ScalarTypeRef(ScalarTypeKind.Decimal)), value),
                ObservationValueKind.String => NodeResult.FromValue(new(new ScalarTypeRef(ScalarTypeKind.String)), value),
                ObservationValueKind.Bytes => NodeResult.FromValue(new(new ScalarTypeRef(ScalarTypeKind.Bytes)), value),
                ObservationValueKind.DateTimeOffset => NodeResult.FromValue(new(new ScalarTypeRef(ScalarTypeKind.Instant)), value),
                ObservationValueKind.DateOnly => NodeResult.FromValue(new(new ScalarTypeRef(ScalarTypeKind.Date)), value),
                ObservationValueKind.Object => NodeResult.FromValue(new(new JsonTypeRef(JsonTypeKind.Object)), value),
                ObservationValueKind.Array => NodeResult.FromValue(new(new JsonTypeRef(JsonTypeKind.Array)), value),
                _ => new(ExprResultCategory.Any, null, value)
            };
        }

        NodeResult AnalyzeLiteral(LiteralExpr literal, string expressionPath)
        {
            RequireOperation(ExprCapabilities.TypedLiteral, expressionPath);
            if (literal.Type is null)
            {
                Add(
                    ExprAnalysisDiagnosticCodes.ResultTypeMismatch,
                    "Typed literal has no declared result type.",
                    Child(expressionPath, "type"));
                return NodeResult.Unknown;
            }

            return ReconcileDeclaredResult(
                InferConstant(literal.Value),
                NodeResult.FromValue(new(literal.Type)),
                "Typed literal",
                Child(expressionPath, "type"),
                allowDeclaredRefinement: true);
        }

        NodeResult AnalyzeUnary(
            UnaryExpr unary,
            ExprScope scope,
            string expressionPath)
        {
            var capability = ExprCapabilities.ForUnary(unary.Operator);
            RequireOperation(capability, expressionPath);
            if (!semantics.TryGetUnary(unary.Operator, out var definition))
            {
                Add(
                    ExprAnalysisDiagnosticCodes.OperationUnknown,
                    $"Unary operator value '{((int)unary.Operator).ToString(CultureInfo.InvariantCulture)}' has no semantic definition.",
                    expressionPath);
                _ = AnalyzeNode(unary.Operand, scope, ExprExpectation.Any, Child(expressionPath, "operand"));
                return NodeResult.Unknown;
            }

            _ = AnalyzeNode(
                unary.Operand,
                scope,
                new(definition.OperandCategory),
                Child(expressionPath, "operand"));
            return new(definition.ResultCategory, definition.FixedResult);
        }

        NodeResult AnalyzeBinary(
            BinaryExpr binary,
            ExprScope scope,
            string expressionPath)
        {
            var capability = ExprCapabilities.ForBinary(binary.Operator);
            RequireOperation(capability, expressionPath);
            if (!semantics.TryGetBinary(binary.Operator, out var definition))
            {
                Add(
                    ExprAnalysisDiagnosticCodes.OperationUnknown,
                    $"Binary operator value '{((int)binary.Operator).ToString(CultureInfo.InvariantCulture)}' has no semantic definition.",
                    expressionPath);
                _ = AnalyzeNode(binary.Left, scope, ExprExpectation.Any, Child(expressionPath, "left"));
                _ = AnalyzeNode(binary.Right, scope, ExprExpectation.Any, Child(expressionPath, "right"));
                return NodeResult.Unknown;
            }

            _ = AnalyzeNode(
                binary.Left,
                scope,
                new(definition.LeftCategory),
                Child(expressionPath, "left"));
            if (binary.Operator is BinaryOperator.And or BinaryOperator.Or)
                _ = AnalyzeGuarded(binary.Right, scope, new(definition.RightCategory), Child(expressionPath, "right"),
                    binary.Left, whenTrue: binary.Operator == BinaryOperator.And);
            else
                _ = AnalyzeNode(binary.Right, scope, new(definition.RightCategory), Child(expressionPath, "right"));
            return new(definition.ResultCategory, definition.FixedResult);
        }

        NodeResult AnalyzeGuarded(Expr expression, ExprScope scope, ExprExpectation expectation, string path,
            Expr guard, bool whenTrue)
        {
            var previousFields = guardedFields;
            var previousBindings = presentBindings;
            guardedFields = new(previousFields);
            presentBindings = new(previousBindings);
            try
            {
                ExprGuardRefinement.Apply(guard, whenTrue, Resolve, (field, contract) =>
                {
                    if (FieldIdentity(field) is not { } identity) return;
                    guardedFields[identity] = contract;
                    if (contract.Presence == FieldPresence.Required) presentBindings.Add(identity.Binding);
                });
                return AnalyzeNode(expression, scope, expectation, path);
            }
            finally
            {
                guardedFields = previousFields;
                presentBindings = previousBindings;
            }

            (ValueBindingId Binding, FieldPath Path)? FieldIdentity(Expr field)
            {
                var binding = field is FieldExpr untyped ? untyped.Binding ?? scope.ImplicitBinding : scope.ImplicitBinding;
                var fieldPath = field switch { FieldExpr reference => reference.Path, FieldRefExpr typed => typed.Path, _ => (FieldPath?)null };
                return binding is { } id && fieldPath is { } selected ? (id, selected) : null;
            }

            ValueContract? Resolve(Expr value)
            {
                if (value is ParameterExpr parameter)
                    return scope.TryGetParameter(parameter.Parameter, out var contract) ? contract.Value : null;
                if (FieldIdentity(value) is not { } identity || !scope.TryGetBinding(identity.Binding, out var binding)) return null;
                if (guardedFields.TryGetValue(identity, out var refined)) return refined;
                _ = TryResolvePath(binding.Value, identity.Path, out var resolved, out var missing);
                if (missing) return null;
                resolved ??= new ValueContract();
                return binding.Availability == ExprBindingAvailability.MayBeAbsent && !presentBindings.Contains(binding.Id)
                    ? WithPresence(resolved, FieldPresence.Optional) : resolved;
            }
        }

        NodeResult AnalyzeConditional(
            ConditionalExpr conditional,
            ExprScope scope,
            string expressionPath)
        {
            RequireOperation(ExprCapabilities.Conditional, expressionPath);
            if (conditional.ReturnType is null)
            {
                Add(
                    ExprAnalysisDiagnosticCodes.ResultTypeMismatch,
                    "Conditional expression has no declared result-type metadata.",
                    Child(expressionPath, "returnType"));
            }
            _ = AnalyzeNode(
                conditional.Test,
                scope,
                ExprExpectation.Boolean,
                Child(expressionPath, "test"));
            var hasDeclaredResult = TryGetDeclaredType(conditional.ReturnType, out var declared);
            var branchExpectation = hasDeclaredResult
                ? new ExprExpectation(value: new(
                    declared,
                    presence: FieldPresence.Optional,
                    nullability: FieldNullability.Nullable))
                : ExprExpectation.Any;
            var ifTruePath = Child(expressionPath, "ifTrue");
            var ifTrueDiagnosticStart = diagnostics.Count;
            var ifTrue = AnalyzeNode(
                conditional.IfTrue,
                scope,
                branchExpectation,
                ifTruePath);
            var ifTrueSatisfiesDeclaredResult = !HasDirectResultMismatch(
                diagnostics,
                ifTrueDiagnosticStart,
                ifTruePath);
            var ifFalsePath = Child(expressionPath, "ifFalse");
            var ifFalseDiagnosticStart = diagnostics.Count;
            var ifFalse = AnalyzeNode(
                conditional.IfFalse,
                scope,
                branchExpectation,
                ifFalsePath);
            var ifFalseSatisfiesDeclaredResult = !HasDirectResultMismatch(
                diagnostics,
                ifFalseDiagnosticStart,
                ifFalsePath);

            if (hasDeclaredResult)
            {
                var declaredResult = NodeResult.FromValue(new(declared));
                if (ifTrueSatisfiesDeclaredResult)
                    ifTrue = ApplyValidatedDeclaredResult(ifTrue, declaredResult);
                if (ifFalseSatisfiesDeclaredResult)
                    ifFalse = ApplyValidatedDeclaredResult(ifFalse, declaredResult);
            }

            return JoinConditionalResults(ifTrue, ifFalse);
        }

        static bool HasDirectResultMismatch(
            IReadOnlyList<DocumentValidationDiagnostic> diagnostics,
            int startIndex,
            string expressionPath)
        {
            for (var index = startIndex; index < diagnostics.Count; index++)
            {
                var diagnostic = diagnostics[index];
                if (string.Equals(diagnostic.SchemaLocation, expressionPath, StringComparison.Ordinal)
                    && diagnostic.Code is ExprAnalysisDiagnosticCodes.ResultCategoryMismatch
                        or ExprAnalysisDiagnosticCodes.ResultTypeMismatch)
                {
                    return true;
                }
            }

            return false;
        }

        NodeResult AnalyzeCall(
            CallExpr call,
            ExprScope scope,
            string expressionPath)
        {
            var arguments = call.Arguments.IsDefault ? ImmutableArray<Expr>.Empty : call.Arguments;
            if (call.ReturnType is null)
            {
                Add(
                    ExprAnalysisDiagnosticCodes.ResultTypeMismatch,
                    "Function call has no declared result-type metadata.",
                    Child(expressionPath, "returnType"));
            }
            if (string.IsNullOrWhiteSpace(call.Function))
            {
                Add(
                    ExprAnalysisDiagnosticCodes.FunctionUnknown,
                    "A function call must have a non-empty semantic function identifier.",
                    Child(expressionPath, "function"));
                AnalyzeArgumentsWithoutDefinition(arguments, scope, expressionPath);
                return ResultFromDeclaredType(call.ReturnType);
            }

            var operation = ExprCapabilities.ForFunction(call.Function);
            RequireOperation(operation, expressionPath);
            if (!semantics.TryGetFunction(call.Function, out var definition))
            {
                Add(
                    ExprAnalysisDiagnosticCodes.FunctionUnknown,
                    $"Function '{call.Function}' has no semantic definition in the selected catalog.",
                    Child(expressionPath, "function"));
                AnalyzeArgumentsWithoutDefinition(arguments, scope, expressionPath);
                return ResultFromDeclaredType(call.ReturnType);
            }

            if (!definition.Arity.Accepts(arguments.Length))
            {
                Add(
                    ExprAnalysisDiagnosticCodes.FunctionArityInvalid,
                    $"Function '{call.Function}' expects {definition.Arity.Describe()} arguments but received {arguments.Length.ToString(CultureInfo.InvariantCulture)}.",
                    Child(expressionPath, "arguments"));
            }

            foreach (var ambient in definition.AmbientCapabilities)
                RequireAmbient(ambient, scope, expressionPath);

            NodeResult[] argumentResults = new NodeResult[arguments.Length];
            var scopedByIndex = definition.ScopedArguments.ToDictionary(static item => item.ArgumentIndex);
            for (var index = 0; index < arguments.Length; index++)
            {
                if (scopedByIndex.ContainsKey(index))
                    continue;

                argumentResults[index] = AnalyzeNode(
                    arguments[index],
                    scope,
                    new(definition.GetArgumentCategory(index)),
                    Child(expressionPath, $"arguments/{index.ToString(CultureInfo.InvariantCulture)}"));
            }

            foreach (var scopedArgument in definition.ScopedArguments)
            {
                if (scopedArgument.ArgumentIndex >= arguments.Length)
                    continue;

                var currentItem = scopedArgument.SourceArgumentIndex < argumentResults.Length
                    ? GetCollectionElement(argumentResults[scopedArgument.SourceArgumentIndex].Value)
                    : null;
                var scoped = scope.WithCurrentItem(currentItem ?? new ValueContract());
                argumentResults[scopedArgument.ArgumentIndex] = AnalyzeNode(
                    arguments[scopedArgument.ArgumentIndex],
                    scoped,
                    new(definition.GetArgumentCategory(scopedArgument.ArgumentIndex)),
                    Child(expressionPath, $"arguments/{scopedArgument.ArgumentIndex.ToString(CultureInfo.InvariantCulture)}"));
            }

            var semanticResult = definition.ResultRule switch
            {
                ExprFunctionResultRule.Fixed or ExprFunctionResultRule.DeclaredOrFixed =>
                    new(definition.ResultCategory, definition.FixedResult),
                ExprFunctionResultRule.FirstArgument
                    when argumentResults.Length > 0 && argumentResults[0].Value is { } firstArgument =>
                    NodeResult.FromValue(firstArgument),
                ExprFunctionResultRule.FirstArgument =>
                    new(definition.ResultCategory, null),
                ExprFunctionResultRule.CollectionOfSelector =>
                    CollectionResult(argumentResults, definition),
                _ => new(definition.ResultCategory, null)
            };
            return ReconcileDeclaredResult(
                semanticResult,
                ResultFromDeclaredType(call.ReturnType),
                $"Function '{call.Function}'",
                Child(expressionPath, "returnType"));
        }

        NodeResult AnalyzeAggregate(
            AggregateExpr aggregate,
            ExprScope scope,
            string expressionPath)
        {
            var operation = ExprCapabilities.ForAggregate(aggregate.Operator);
            RequireOperation(operation, expressionPath);
            if (aggregate.ReturnType is null)
            {
                Add(
                    ExprAnalysisDiagnosticCodes.ResultTypeMismatch,
                    "Aggregate expression has no declared result-type metadata.",
                    Child(expressionPath, "returnType"));
            }
            if (!semantics.TryGetAggregate(aggregate.Operator, out var definition))
            {
                Add(
                    ExprAnalysisDiagnosticCodes.OperationUnknown,
                    $"Aggregate operator value '{((int)aggregate.Operator).ToString(CultureInfo.InvariantCulture)}' has no semantic definition.",
                    expressionPath);
                _ = AnalyzeNode(aggregate.Source, scope, ExprExpectation.Any, Child(expressionPath, "source"));
                AnalyzeAggregateGroups(aggregate, scope, expressionPath);
                return ResultFromDeclaredType(aggregate.ReturnType);
            }

            _ = AnalyzeNode(
                aggregate.Source,
                scope,
                new(definition.SourceCategory),
                Child(expressionPath, "source"));
            AnalyzeAggregateGroups(aggregate, scope, expressionPath);
            return ReconcileDeclaredResult(
                new(definition.ResultCategory, definition.FixedResult),
                ResultFromDeclaredType(aggregate.ReturnType),
                $"Aggregate '{aggregate.Operator}'",
                Child(expressionPath, "returnType"));
        }

        NodeResult ReconcileDeclaredResult(
            NodeResult semanticResult,
            NodeResult declaredResult,
            string subject,
            string expressionPath,
            bool allowDeclaredRefinement = false)
        {
            if (declaredResult.Value is null)
                return semanticResult;

            var semanticType = semanticResult.Value?.GetEffectiveType();
            var declaredType = declaredResult.Value.GetEffectiveType();
            var constantCompatibility = semanticResult.ConstantValue is { } constant
                && declaredType is not null
                    ? ValueContractSemantics.Evaluate(declaredType, constant)
                    : ValueConstantCompatibility.Unknown;
            var constantMatches = constantCompatibility == ValueConstantCompatibility.Compatible;
            var constantMustMatch = allowDeclaredRefinement
                && semanticResult.ConstantValue is { Kind: not ObservationValueKind.Null and not ObservationValueKind.Undefined }
                && declaredType is not null
                && constantCompatibility != ValueConstantCompatibility.Unknown;
            var categoryMatches = constantMatches
                || ExprResultCategorySemantics.Satisfies(declaredResult.Category, semanticResult.Category);
            var typeMatches = allowDeclaredRefinement
                ? !constantMustMatch || constantMatches
                : semanticType is null
                    || declaredType is null
                    || semanticType == declaredType;
            var shapeMatches = semanticResult.Value?.Shape is not { } semanticShape
                || declaredResult.Value.Shape is not { } declaredShape
                || semanticShape == declaredShape;
            if (!categoryMatches || !typeMatches || !shapeMatches)
            {
                Add(
                    ExprAnalysisDiagnosticCodes.ResultTypeMismatch,
                    $"{subject} declares result type '{Describe(declaredType)}' that does not match its semantic result.",
                    expressionPath);
                return semanticResult;
            }

            var reconciledCategory = declaredResult.Category == ExprResultCategory.Any
                ? semanticResult.Category
                : semanticResult.Category == ExprResultCategory.Any
                    || constantMatches
                    || ExprResultCategorySemantics.Satisfies(
                        declaredResult.Category,
                        semanticResult.Category)
                        ? declaredResult.Category
                        : semanticResult.Category;

            if (allowDeclaredRefinement)
            {
                var semanticValue = semanticResult.Value;
                var declaredValue = declaredResult.Value;
                return new(
                    reconciledCategory,
                    new ValueContract(
                        declaredValue.Type,
                        declaredValue.Shape,
                        declaredValue.Cardinality,
                        semanticValue?.Presence == FieldPresence.Optional
                            ? FieldPresence.Optional
                            : declaredValue.Presence,
                        semanticValue?.Nullability == FieldNullability.Nullable
                            ? FieldNullability.Nullable
                            : declaredValue.Nullability),
                    semanticResult.ConstantValue);
            }

            if (semanticResult.Value is null)
            {
                return new(
                    reconciledCategory,
                    declaredResult.Value,
                    semanticResult.ConstantValue);
            }
            if (semanticType is null && declaredResult.Value is { } declaredValueWithType)
            {
                // Declared metadata supplies the missing type; the semantic rule remains authoritative for
                // occurrence guarantees such as object construction being present and non-null.
                return new(
                    reconciledCategory,
                    new ValueContract(
                        declaredValueWithType.Type,
                        declaredValueWithType.Shape,
                        declaredValueWithType.Cardinality,
                        semanticResult.Value.Presence,
                        semanticResult.Value.Nullability),
                    semanticResult.ConstantValue);
            }

            return semanticResult;
        }

        static NodeResult ApplyValidatedDeclaredResult(
            NodeResult semanticResult,
            NodeResult declaredResult)
        {
            var declaredValue = declaredResult.Value!;
            return new(
                declaredResult.Category == ExprResultCategory.Any
                    ? semanticResult.Category
                    : declaredResult.Category,
                new ValueContract(
                    declaredValue.Type,
                    declaredValue.Shape ?? semanticResult.Value?.Shape,
                    declaredValue.Cardinality,
                    semanticResult.Value?.Presence == FieldPresence.Optional
                        ? FieldPresence.Optional
                        : declaredValue.Presence,
                    semanticResult.Value?.Nullability == FieldNullability.Nullable
                        ? FieldNullability.Nullable
                        : declaredValue.Nullability),
                semanticResult.ConstantValue);
        }

        void AnalyzeAggregateGroups(
            AggregateExpr aggregate,
            ExprScope scope,
            string expressionPath)
        {
            var groups = aggregate.GroupBy.IsDefault ? ImmutableArray<Expr>.Empty : aggregate.GroupBy;
            for (var index = 0; index < groups.Length; index++)
            {
                _ = AnalyzeNode(
                    groups[index],
                    scope,
                    ExprExpectation.Any,
                    Child(expressionPath, $"groupBy/{index.ToString(CultureInfo.InvariantCulture)}"));
            }
        }

        NodeResult AnalyzeUnsupported(Expr expression, string expressionPath)
        {
            Add(
                ExprAnalysisDiagnosticCodes.NodeUnsupported,
                $"Expression node '{expression.GetType().Name}' is not supported by this analyzer.",
                expressionPath);
            return NodeResult.Unknown;
        }

        void AnalyzeArgumentsWithoutDefinition(
            ImmutableArray<Expr> arguments,
            ExprScope scope,
            string expressionPath)
        {
            for (var index = 0; index < arguments.Length; index++)
            {
                _ = AnalyzeNode(
                    arguments[index],
                    scope,
                    ExprExpectation.Any,
                    Child(expressionPath, $"arguments/{index.ToString(CultureInfo.InvariantCulture)}"));
            }
        }

        void RequireOperation(ExprCapabilityId capability, string expressionPath)
        {
            ExprCapabilityRequirement requirement = new(
                capability,
                ExprCapabilityRequirementKind.Operation);
            capabilities.Add(requirement);
            var isSatisfied = site.CapabilityProfile.Supports(capability);
            capabilityUses.Add(new(requirement, expressionPath, isSatisfied));
            if (!isSatisfied)
            {
                Add(
                    ExprAnalysisDiagnosticCodes.CapabilityUnsupported,
                    $"The selected expression capability profile does not allow or support '{capability.Value}'.",
                    expressionPath);
            }
        }

        void RequireAmbient(
            ExprCapabilityId capability,
            ExprScope scope,
            string expressionPath)
        {
            ExprCapabilityRequirement requirement = new(
                capability,
                ExprCapabilityRequirementKind.Ambient);
            capabilities.Add(requirement);
            var isSatisfied = scope.HasAmbientCapability(capability);
            capabilityUses.Add(new(requirement, expressionPath, isSatisfied));
            if (!isSatisfied)
            {
                Add(
                    ExprAnalysisDiagnosticCodes.AmbientCapabilityUnavailable,
                    $"Expression site does not provide ambient capability '{capability.Value}'.",
                    expressionPath);
            }
        }

        void ValidateResultExpectation(
            NodeResult result,
            ExprExpectation expectation,
            string expressionPath)
        {
            var expectedType = expectation.Value?.GetEffectiveType();
            var actualType = result.Value?.GetEffectiveType();
            var constantCompatibility = expectedType is not null
                && result.ConstantValue is { } constant
                    ? ValueContractSemantics.Evaluate(expectedType, constant)
                    : (ValueConstantCompatibility?)null;
            var constantSatisfiesExpectedType =
                constantCompatibility == ValueConstantCompatibility.Compatible;
            var constantCouldSatisfyExpectedType = constantCompatibility is
                ValueConstantCompatibility.Compatible or ValueConstantCompatibility.Unknown;
            if (!constantSatisfiesExpectedType
                && !ExprResultCategorySemantics.Satisfies(result.Category, expectation.Category))
            {
                Add(
                    ExprAnalysisDiagnosticCodes.ResultCategoryMismatch,
                    $"Expression result category '{result.Category}' does not satisfy expected category '{expectation.Category}'.",
                    expressionPath);
            }
            if (expectedType is not null
                && actualType is not null
                && expectedType != actualType
                && !constantCouldSatisfyExpectedType)
            {
                Add(
                    ExprAnalysisDiagnosticCodes.ResultTypeMismatch,
                    $"Expression result type '{Describe(actualType)}' does not satisfy expected type '{Describe(expectedType)}'.",
                    expressionPath);
            }

            if (expectation.Value?.Shape is { } expectedShape
                && result.Value?.Shape is { } actualShape
                && expectedShape != actualShape)
            {
                Add(
                    ExprAnalysisDiagnosticCodes.ResultTypeMismatch,
                    $"Expression result shape '{actualShape}' does not satisfy expected shape '{expectedShape}'.",
                    expressionPath);
            }

            if (result.Value is { } actualValue)
            {
                var expectedValue = expectation.Value;
                var effectiveCardinalityIsEquivalent = expectedType is not null
                    && actualType is not null
                    && expectedType == actualType
                    || constantCouldSatisfyExpectedType
                    || result.ConstantValue is { Kind: ObservationValueKind.Null or ObservationValueKind.Undefined };
                if (expectedValue is not null
                    && expectedValue.Cardinality != actualValue.Cardinality
                    && !effectiveCardinalityIsEquivalent)
                {
                    Add(
                        ExprAnalysisDiagnosticCodes.ResultTypeMismatch,
                        $"Expression result cardinality '{actualValue.Cardinality}' does not satisfy expected cardinality '{expectedValue.Cardinality}'.",
                        expressionPath);
                }
                var constrainedCategoryRequiresValue = expectation.Category != ExprResultCategory.Any
                    && expectedValue is null;
                if ((expectedValue?.Presence == FieldPresence.Required
                        || constrainedCategoryRequiresValue)
                    && actualValue.Presence == FieldPresence.Optional)
                {
                    Add(
                        ExprAnalysisDiagnosticCodes.ResultTypeMismatch,
                        "Expression result may be absent but the site requires a present value.",
                        expressionPath);
                }
                if ((expectedValue?.Nullability == FieldNullability.NonNullable
                        || constrainedCategoryRequiresValue)
                    && actualValue.Nullability == FieldNullability.Nullable)
                {
                    Add(
                        ExprAnalysisDiagnosticCodes.ResultTypeMismatch,
                        "Expression result may be null but the site requires a non-null value.",
                        expressionPath);
                }
            }
        }

        void ValidateAllowedDependencies(ExprRequirements requirements)
        {
            var disallowed = requirements.Dependencies & ~site.Expectation.AllowedDependencies;
            foreach (var dependency in DependencyFlags(disallowed))
            {
                Add(
                    ExprAnalysisDiagnosticCodes.DependencyNotAllowed,
                    $"Expression requires '{dependency}' context, which is not allowed at this site.",
                    expressionPath: "/");
            }
        }

        bool ValidateFieldPath(FieldPath path, string expressionPath)
        {
            if (path.Segments.IsDefaultOrEmpty
                || path.Segments.Any(static segment => segment.Kind switch
                {
                    SegmentKind.Field => string.IsNullOrWhiteSpace(segment.Segment),
                    SegmentKind.Element => segment.Segment is not null,
                    _ => true
                }))
            {
                Add(
                    ExprAnalysisDiagnosticCodes.FieldPathInvalid,
                    "A field expression must contain a valid, non-empty field path.",
                    expressionPath);
                return false;
            }

            return true;
        }

        void Add(string code, string message, string expressionPath) => diagnostics.Add(new(
            Code: code,
            Severity: DiagnosticSeverity.Error,
            Message: message,
            Location: site.DiagnosticLocation,
            SchemaLocation: expressionPath));

        static ImmutableArray<DocumentValidationDiagnostic> SortDiagnostics(
            IEnumerable<DocumentValidationDiagnostic> values) =>
        [
            .. values
                .OrderBy(static diagnostic => diagnostic.Location, StringComparer.Ordinal)
                .ThenBy(static diagnostic => diagnostic.SchemaLocation, StringComparer.Ordinal)
                .ThenBy(static diagnostic => diagnostic.Code, StringComparer.Ordinal)
                .ThenBy(static diagnostic => diagnostic.Message, StringComparer.Ordinal)
        ];

        static bool TryResolvePath(
            ValueContract root,
            FieldPath path,
            out ValueContract? value,
            out bool definitelyMissing)
        {
            value = root;
            definitelyMissing = false;
            if (path.Segments.IsDefaultOrEmpty)
                return false;

            foreach (var segment in path.Segments)
            {
                var type = value?.GetEffectiveType();
                switch (segment.Kind)
                {
                    case SegmentKind.Field when type is ObjectTypeRef objectType:
                    {
                        var field = objectType.Fields.FirstOrDefault(candidate =>
                            string.Equals(candidate.Name, segment.Segment, StringComparison.Ordinal));
                        if (field is null)
                        {
                            value = null;
                            definitelyMissing = true;
                            return false;
                        }

                        ValueContract next = new(
                            field.Type,
                            cardinality: field.Cardinality,
                            presence: field.Presence,
                            nullability: field.Nullability);
                        value = ComposePathValue(value!, next);
                        break;
                    }
                    case SegmentKind.Element when type is ArrayTypeRef array:
                        value = ComposePathValue(value!, new(array.ElementType));
                        break;
                    case SegmentKind.Field when type is JsonTypeRef { Kind: JsonTypeKind.Any or JsonTypeKind.Object }:
                    case SegmentKind.Element when type is JsonTypeRef { Kind: JsonTypeKind.Any or JsonTypeKind.Array }:
                    case SegmentKind.Field when type is NamedTypeRef or OpaqueRuntimeTypeRef:
                        value = PreserveWeakGuarantees(value);
                        return false;
                    case SegmentKind.Field:
                    case SegmentKind.Element:
                        value = null;
                        definitelyMissing = type is not null;
                        return false;
                    default:
                        value = null;
                        definitelyMissing = true;
                        return false;
                }
            }

            return value is not null;
        }

        static ValueContract? PreserveWeakGuarantees(ValueContract? value)
        {
            if (value is null
                || value.Presence == FieldPresence.Required
                    && value.Nullability == FieldNullability.NonNullable)
            {
                return null;
            }

            return new(
                presence: value.Presence,
                nullability: value.Nullability);
        }

        static ValueContract? GetCollectionElement(ValueContract? collection)
        {
            if (collection is null)
                return null;
            if (collection.Cardinality == FieldCardinality.Many)
            {
                return new(
                    collection.Type,
                    collection.Shape);
            }
            return collection.GetEffectiveType() is ArrayTypeRef array
                ? new(
                    array.ElementType,
                    collection.Shape)
                : null;
        }

        static NodeResult JoinConditionalResults(NodeResult ifTrue, NodeResult ifFalse)
        {
            if (ifTrue.Value is null || ifFalse.Value is null)
            {
                return ifTrue.Category == ifFalse.Category
                    ? new(ifTrue.Category, null)
                    : NodeResult.Unknown;
            }

            var trueType = ifTrue.Value.GetEffectiveType();
            var falseType = ifFalse.Value.GetEffectiveType();
            ValueContract basis;
            if (trueType == falseType)
            {
                basis = ifTrue.Value;
            }
            else if (trueType is null && IsNullishConstant(ifTrue.ConstantValue))
            {
                basis = ifFalse.Value;
            }
            else if (falseType is null && IsNullishConstant(ifFalse.ConstantValue))
            {
                basis = ifTrue.Value;
            }
            else
            {
                var unknown = new ValueContract(
                    presence: ifTrue.Value.Presence == FieldPresence.Optional
                        || ifFalse.Value.Presence == FieldPresence.Optional
                            ? FieldPresence.Optional
                            : FieldPresence.Required,
                    nullability: ifTrue.Value.Nullability == FieldNullability.Nullable
                        || ifFalse.Value.Nullability == FieldNullability.Nullable
                            ? FieldNullability.Nullable
                            : FieldNullability.NonNullable);
                return NodeResult.FromValue(unknown);
            }

            var trueIsNullish = IsNullishConstant(ifTrue.ConstantValue);
            var falseIsNullish = IsNullishConstant(ifFalse.ConstantValue);
            var shape = trueIsNullish && !falseIsNullish
                ? ifFalse.Value.Shape
                : falseIsNullish && !trueIsNullish
                    ? ifTrue.Value.Shape
                    : ifTrue.Value.Shape == ifFalse.Value.Shape
                        ? ifTrue.Value.Shape
                        : null;
            var value = new ValueContract(
                basis.Type,
                shape,
                basis.Cardinality,
                ifTrue.Value.Presence == FieldPresence.Optional
                    || ifFalse.Value.Presence == FieldPresence.Optional
                        ? FieldPresence.Optional
                        : FieldPresence.Required,
                ifTrue.Value.Nullability == FieldNullability.Nullable
                    || ifFalse.Value.Nullability == FieldNullability.Nullable
                        ? FieldNullability.Nullable
                        : FieldNullability.NonNullable);
            var constant = ifTrue.ConstantValue is { } trueConstant
                && ifFalse.ConstantValue is { } falseConstant
                && trueConstant.Equals(falseConstant)
                    ? trueConstant
                    : (ObservationValue?)null;
            return new(ExprResultCategorySemantics.Classify(value), value, constant);

            static bool IsNullishConstant(ObservationValue? value) => value is
            {
                Kind: ObservationValueKind.Null or ObservationValueKind.Undefined
            };
        }

        static ValueContract WithPresence(ValueContract value, FieldPresence presence) => new(
            value.Type,
            value.Shape,
            value.Cardinality,
            presence,
            value.Nullability);

        static ValueContract ComposePathValue(
            ValueContract parent,
            ValueContract child) => new(
            child.Type,
            child.Shape,
            child.Cardinality,
            parent.Presence == FieldPresence.Optional || child.Presence == FieldPresence.Optional
                ? FieldPresence.Optional
                : FieldPresence.Required,
            parent.Nullability == FieldNullability.Nullable || child.Nullability == FieldNullability.Nullable
                ? FieldNullability.Nullable
                : FieldNullability.NonNullable);

        static NodeResult CollectionResult(
            IReadOnlyList<NodeResult> argumentResults,
            ExprFunctionDefinition definition)
        {
            if (definition.ScopedArguments.IsDefaultOrEmpty)
                return new(ExprResultCategory.Collection, null);

            var selectorIndex = definition.ScopedArguments.FirstOrDefault().ArgumentIndex;
            if (selectorIndex >= 0
                && selectorIndex < argumentResults.Count
                && argumentResults[selectorIndex].Value is { } selector)
            {
                var selectorType = selector.GetEffectiveType();
                if (selectorType is null
                    && selector.Shape is null)
                {
                    return new(ExprResultCategory.Collection, null);
                }

                return new(
                    ExprResultCategory.Collection,
                    new(
                        selectorType,
                        selector.Shape,
                        cardinality: FieldCardinality.Many));
            }

            return new(ExprResultCategory.Collection, null);
        }

        static NodeResult ResultFromDeclaredType(TypeRef? type) =>
            TryGetDeclaredType(type, out var declared)
                ? NodeResult.FromValue(new(
                    declared,
                    presence: FieldPresence.Optional,
                    nullability: FieldNullability.Nullable))
                : NodeResult.Unknown;

        static bool TryGetDeclaredType(TypeRef? type, out TypeRef declared)
        {
            if (type is not null
                && type is not OpaqueRuntimeTypeRef { RuntimeType: "unknown" })
            {
                declared = type;
                return true;
            }

            declared = null!;
            return false;
        }

        static IEnumerable<ExprDependencyKind> DependencyFlags(ExprDependencyKind value)
        {
            if ((value & ExprDependencyKind.Binding) != 0)
                yield return ExprDependencyKind.Binding;
            if ((value & ExprDependencyKind.Parameter) != 0)
                yield return ExprDependencyKind.Parameter;
            if ((value & ExprDependencyKind.CurrentItem) != 0)
                yield return ExprDependencyKind.CurrentItem;
            if ((value & ExprDependencyKind.Ambient) != 0)
                yield return ExprDependencyKind.Ambient;
        }

        static string Child(string parent, string child) =>
            parent == "/" ? $"/{child}" : $"{parent}/{child}";

        static string Describe(TypeRef? type) => type switch
        {
            null => "unknown",
            ScalarTypeRef scalar => scalar.Kind.ToString(),
            ArrayTypeRef array => $"array<{Describe(array.ElementType)}>",
            ObjectTypeRef => "object",
            NamedTypeRef named => $"named:{named.TypeId.Value}",
            EnumTypeRef @enum => $"enum:{@enum.Name}",
            EntityReferenceTypeRef entity => $"entity:{entity.Entity.Value}",
            JsonTypeRef json => $"json:{json.Kind}",
            OpaqueRuntimeTypeRef opaque => $"opaque:{opaque.RuntimeType}",
            _ => type.GetType().Name
        };

        readonly record struct NodeResult(
            ExprResultCategory Category,
            ValueContract? Value,
            ObservationValue? ConstantValue = null)
        {
            public static NodeResult Unknown { get; } = new(ExprResultCategory.Any, null);

            public static NodeResult FromValue(
                ValueContract value,
                ObservationValue? constantValue = null) =>
                new(ExprResultCategorySemantics.Classify(value), value, constantValue);
        }
    }
}
