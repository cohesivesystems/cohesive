using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;

namespace Cohesive.Analyzers;

/// <summary>
/// Lowers async-style process authoring methods into <c>ProcessDefinition</c> factories.
/// </summary>
[Generator]
public sealed class ProcessFlowSourceGenerator : IIncrementalGenerator
{
    static readonly SymbolDisplayFormat FullyQualifiedNullableFormat =
        SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(
            SymbolDisplayFormat.FullyQualifiedFormat.MiscellaneousOptions
            | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    static readonly DiagnosticDescriptor ContainingTypeMustBePartial = new(
        id: "COHPF001",
        title: "Process authoring container must be partial",
        messageFormat: "Type '{0}' is marked with [GenerateProcessDefinition] and must be declared partial.",
        category: "Cohesive.Processes",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "The process-definition source generator emits the generated definition method into the containing type, which requires the type declaration to be partial.");

    static readonly DiagnosticDescriptor UnsupportedAuthoringMethodShape = new(
        id: "COHPF002",
        title: "Unsupported process authoring method shape",
        messageFormat: "Type '{0}' references authoring method '{1}', but that method does not match a supported authoring shape: {2}.",
        category: "Cohesive.Processes",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Generated process definitions currently support only async instance methods that return ProcessTask<TResult>, take a leading ProcessAuthoringContext<TInput, TResult> parameter, may optionally take a following TInput runtime input parameter, and consist of supported await-bind statements, nested block and if/else control flow, and flow.Return(...) terminal paths.");

    static readonly DiagnosticDescriptor UnsupportedAuthoringStatement = new(
        id: "COHPF003",
        title: "Unsupported process authoring statement",
        messageFormat: "Method '{0}' contains an unsupported process authoring statement: {1}.",
        category: "Cohesive.Processes",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Generated process definitions currently support local declarations of the form 'var value = await flow.Input(...)', 'var value = await flow.Parameter<T>(...)', 'var value = await flow.Request(...)', 'var value = await flow.Read(...)', 'var value = await flow.Create(...)', 'var value = await flow.Query(...)', 'var value = await flow.Compute(...)', 'var value = await flow.Transition(...)', 'var value = await flow.TransitionMany(...)', 'var value = await flow.Timer(...)', or 'var value = await flow.Poll(...)', plus nested blocks, if/else control flow, and 'return flow.Return(...)' terminals.");

    static readonly DiagnosticDescriptor UnsupportedContinuationCapture = new(
        id: "COHPF004",
        title: "Continuation expressions cannot capture bound process values",
        messageFormat: "Method '{0}' captures a previously bound process value inside 'continuationEntityExpression', which is not supported by source generation.",
        category: "Cohesive.Processes",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Continuation expressions execute directly against ProcessExecutionContext and cannot close over generated process bindings. Compute the continuation entity from the context or from ordinary method parameters instead.");

    static readonly DiagnosticDescriptor ReferencedAuthoringMethodNotFound = new(
        id: "COHPF005",
        title: "Referenced process authoring method not found",
        messageFormat: "Type '{0}' is marked with [GenerateProcessDefinition(\"{1}\")], but no unique source-declared method with that name was found.",
        category: "Cohesive.Processes",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Class-level process-definition generation must point to exactly one source-declared authoring method on the same type.");

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var candidates = context.SyntaxProvider.ForAttributeWithMetadataName(
                fullyQualifiedMetadataName: "Cohesive.Processes.Model.GenerateProcessDefinitionAttribute",
                predicate: static (node, _) => node is ClassDeclarationSyntax,
                transform: static (generatorContext, _) => new AuthoringTypeCandidate(
                    Syntax: (ClassDeclarationSyntax)generatorContext.TargetNode,
                    Symbol: (INamedTypeSymbol)generatorContext.TargetSymbol))
            .Collect();

        context.RegisterSourceOutput(
            context.CompilationProvider.Combine(candidates),
            static (productionContext, pair) =>
            {
                var compilation = pair.Left;
                var candidates = pair.Right;
                var seen = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
                foreach (var candidate in candidates)
                {
                    if (!seen.Add(candidate.Symbol))
                        continue;

                    GenerateForType(compilation, productionContext, candidate);
                }
            });
    }

    static void GenerateForType(
        Compilation compilation,
        SourceProductionContext productionContext,
        AuthoringTypeCandidate candidate)
    {
        if (!TryBuildModel(compilation, productionContext, candidate, out var model))
            return;

        productionContext.AddSource(
            hintName: $"{model.ContainingType.Name}.{model.GeneratedMethodName}.ProcessDefinition.g.cs",
            sourceText: SourceText.From(text: Emit(model), encoding: Encoding.UTF8));
    }

    static bool TryBuildModel(
        Compilation compilation,
        SourceProductionContext productionContext,
        AuthoringTypeCandidate candidate,
        out ProcessAuthoringModel model)
    {
        model = null!;

        var typeSyntax = candidate.Syntax;
        var containingType = candidate.Symbol;

        if (!TryValidateContainingType(productionContext, containingType, typeSyntax))
            return false;

        if (!TryResolveAuthoringMethod(
                productionContext,
                containingType,
                out var methodSymbol,
                out var methodSyntax))
        {
            return false;
        }

        var semanticModel = compilation.GetSemanticModel(methodSyntax.SyntaxTree);

        if (!TryValidateMethodShape(productionContext, containingType, methodSymbol, methodSyntax))
            return false;

        if (!TryResolveFlowShape(productionContext, methodSymbol, out var flowParameter, out var inputType, out var resultType))
            return false;

        if (!TryValidateAdditionalParameters(productionContext, methodSymbol, methodSyntax))
            return false;

        if (methodSyntax.Body is null || methodSyntax.ExpressionBody is not null)
        {
            Report(productionContext, UnsupportedAuthoringMethodShape, methodSymbol, containingType.Name, methodSymbol.Name, "authoring methods must use a block body.");
            return false;
        }

        var statements = methodSyntax.Body.Statements;
        if (statements.Count == 0)
        {
            Report(productionContext, UnsupportedAuthoringMethodShape, methodSymbol, containingType.Name, methodSymbol.Name, "authoring methods must contain at least one return statement.");
            return false;
        }

        var implicitInputParameter = GetImplicitInputParameter(methodSymbol, inputType);
        Dictionary<ISymbol, BoundBinding> boundBindings = new(SymbolEqualityComparer.Default);
        if (implicitInputParameter is not null)
        {
            boundBindings[implicitInputParameter] = new(
                Kind: BoundBindingKind.Parameter,
                TypeName: FormatType(implicitInputParameter.Type),
                NameExpressionText: Literal(implicitInputParameter.Name),
                NeedsNullSuppression: NeedsNullSuppression(implicitInputParameter.Type)
                );
        }

        Dictionary<LocalDeclarationStatementSyntax, AwaitStepModel?> awaitStepsByStatement = [];
        if (!TryCollectFlowBindings(
                productionContext: productionContext,
                methodSymbol: methodSymbol,
                semanticModel: semanticModel,
                flowParameter: flowParameter,
                statements: statements,
                boundBindings: boundBindings,
                awaitStepsByStatement: awaitStepsByStatement))
        {
            return false;
        }

        if (!TryLowerStatements(
                productionContext: productionContext,
                methodSymbol: methodSymbol,
                semanticModel: semanticModel,
                flowParameter: flowParameter,
                statements: statements,
                boundBindings: boundBindings,
                awaitStepsByStatement: awaitStepsByStatement,
                successorNodeText: null,
                loweringState: new(),
                isTopLevel: true,
                loweredBlock: out var loweredBlock))
        {
            return false;
        }

        if (loweredBlock.EntryNodeText is null)
            return false;

        var generatedLines = BuildGeneratedLines(loweredBlock.Nodes, loweredBlock.EntryNodeText, FormatType(resultType));

        var generatedMethodName = $"{methodSymbol.Name}Definition";
        var shouldGenerateTypedDefineMethod = implicitInputParameter is not null
            && ShouldGenerateTypedDefineMethod(methodSymbol);
        var definitionParameterStartIndex = GetDefinitionParameterStartIndex(implicitInputParameter);
        var hasDefinitionParameters = methodSyntax.ParameterList.Parameters.Count > definitionParameterStartIndex;

        model = new ProcessAuthoringModel(
            UsingDirectives: [..methodSyntax.SyntaxTree.GetCompilationUnitRoot().Usings.Select(static usingDirective => usingDirective.NormalizeWhitespace().ToFullString()).Distinct(StringComparer.Ordinal)],
            NamespaceSymbol: methodSymbol.ContainingNamespace,
            ContainingType: containingType,
            ContainingTypeDeclaration: BuildContainingTypeDeclaration(containingType, typeSyntax),
            GeneratedMethodAccessibility: "private",
            GeneratedMethodName: generatedMethodName,
            GeneratedMethodParameters: BuildGeneratedMethodParameterList(methodSyntax, definitionParameterStartIndex),
            DefaultProcessNameLiteral: Literal(GetDefaultProcessName(containingType)),
            GenerateTypedDefineMethod: shouldGenerateTypedDefineMethod,
            TypedDefineMethodParameters: BuildPublicDefineMethodParameterList(methodSyntax, definitionParameterStartIndex),
            TypedDefineInvocationArguments: BuildGeneratedMethodInvocationArguments(methodSyntax, definitionParameterStartIndex, "__normalizedProcessName"),
            InputTypeName: FormatType(inputType),
            ResultTypeName: FormatType(resultType),
            TypedInputParameterName: implicitInputParameter?.Name,
            CacheDefaultDefinition: shouldGenerateTypedDefineMethod && !hasDefinitionParameters,
            DefaultDefinitionFieldName: $"__{generatedMethodName}DefaultDefinition",
            GeneratedLines: generatedLines);
        return true;
    }

    static bool TryValidateContainingType(
        SourceProductionContext productionContext,
        INamedTypeSymbol typeSymbol,
        ClassDeclarationSyntax classDeclaration)
    {
        if (typeSymbol.ContainingType is not null
            || typeSymbol.TypeParameters.Length != 0
            || typeSymbol.TypeKind != TypeKind.Class)
        {
            Report(productionContext, UnsupportedAuthoringMethodShape, typeSymbol, typeSymbol.Name, "<unknown>", "authoring types must be top-level non-generic classes.");
            return false;
        }

        if (!classDeclaration.Modifiers.Any(static modifier => modifier.IsKind(SyntaxKind.PartialKeyword)))
        {
            Report(productionContext, ContainingTypeMustBePartial, typeSymbol, typeSymbol.ToDisplayString());
            return false;
        }

        return true;
    }

    static bool TryResolveAuthoringMethod(
        SourceProductionContext productionContext,
        INamedTypeSymbol containingType,
        out IMethodSymbol methodSymbol,
        out MethodDeclarationSyntax methodSyntax)
    {
        methodSymbol = null!;
        methodSyntax = null!;

        var processAttribute = containingType.GetAttributes()
            .First(attributeData => IsAttribute(attributeData.AttributeClass, "Cohesive.Processes.Model.GenerateProcessDefinitionAttribute"));
        var methodName = processAttribute.ConstructorArguments.Length > 0
            ? processAttribute.ConstructorArguments[0].Value as string
            : null;

        if (string.IsNullOrWhiteSpace(methodName))
        {
            Report(productionContext, ReferencedAuthoringMethodNotFound, containingType, containingType.Name, string.Empty);
            return false;
        }

        var methodNameValue = methodName!;

        var matches = containingType
            .GetMembers(methodNameValue)
            .OfType<IMethodSymbol>()
            .Where(static member => member.DeclaringSyntaxReferences.Length > 0)
            .ToArray();

        if (matches.Length != 1
            || matches[0].DeclaringSyntaxReferences[0].GetSyntax() is not MethodDeclarationSyntax declaration)
        {
            Report(productionContext, ReferencedAuthoringMethodNotFound, containingType, containingType.Name, methodNameValue);
            return false;
        }

        methodSymbol = matches[0];
        methodSyntax = declaration;
        return true;
    }

    static bool ShouldGenerateTypedDefineMethod(IMethodSymbol methodSymbol)
    {
        var containingType = methodSymbol.ContainingType;
        return !containingType
            .GetMembers("Define")
            .OfType<IMethodSymbol>()
            .Where(static member => member.DeclaringSyntaxReferences.Length > 0)
            .Where(member => !SymbolEqualityComparer.Default.Equals(member, methodSymbol))
            .Any(static member =>
                member.Parameters.Length == 0
                || member.Parameters[0].Type is not INamedTypeSymbol firstParameterType
                || !IsNamedType(firstParameterType, "Cohesive.Processes.Model.ProcessAuthoringContext`2"));
    }

    static string GetDefaultProcessName(INamedTypeSymbol containingType)
    {
        const string suffix = "Process";
        return containingType.Name.EndsWith(suffix, StringComparison.Ordinal) && containingType.Name.Length > suffix.Length
            ? containingType.Name.Substring(0, containingType.Name.Length - suffix.Length)
            : containingType.Name;
    }

    static bool TryValidateMethodShape(
        SourceProductionContext productionContext,
        INamedTypeSymbol containingType,
        IMethodSymbol methodSymbol,
        MethodDeclarationSyntax methodSyntax)
    {
        if (methodSymbol.IsStatic
            || methodSymbol.IsGenericMethod
            || !methodSyntax.Modifiers.Any(static modifier => modifier.IsKind(SyntaxKind.AsyncKeyword)))
        {
            Report(productionContext, UnsupportedAuthoringMethodShape, methodSymbol, containingType.Name, methodSymbol.Name, "authoring methods must be instance, async, and non-generic.");
            return false;
        }

        return true;
    }

    static bool TryResolveFlowShape(
        SourceProductionContext productionContext,
        IMethodSymbol methodSymbol,
        out IParameterSymbol flowParameter,
        out ITypeSymbol inputType,
        out ITypeSymbol resultType)
    {
        flowParameter = null!;
        inputType = null!;
        resultType = null!;

        if (methodSymbol.ReturnType is not INamedTypeSymbol returnType
            || !IsNamedType(returnType, "Cohesive.Processes.Model.ProcessTask`1"))
        {
            Report(productionContext, UnsupportedAuthoringMethodShape, methodSymbol, methodSymbol.ContainingType.Name, methodSymbol.Name, "authoring methods must return ProcessTask<TResult>.");
            return false;
        }

        if (methodSymbol.Parameters.Length == 0
            || methodSymbol.Parameters[0].Type is not INamedTypeSymbol flowType
            || !IsNamedType(flowType, "Cohesive.Processes.Model.ProcessAuthoringContext`2"))
        {
            Report(productionContext, UnsupportedAuthoringMethodShape, methodSymbol, methodSymbol.ContainingType.Name, methodSymbol.Name, "authoring methods must take a leading ProcessAuthoringContext<TInput, TResult> parameter.");
            return false;
        }

        if (!SymbolEqualityComparer.Default.Equals(returnType.TypeArguments[0], flowType.TypeArguments[1]))
        {
            Report(productionContext, UnsupportedAuthoringMethodShape, methodSymbol, methodSymbol.ContainingType.Name, methodSymbol.Name, "ProcessTask<TResult> must use the same TResult as ProcessAuthoringContext<TInput, TResult>.");
            return false;
        }

        flowParameter = methodSymbol.Parameters[0];
        inputType = flowType.TypeArguments[0];
        resultType = returnType.TypeArguments[0];
        return true;
    }

    static IParameterSymbol? GetImplicitInputParameter(IMethodSymbol methodSymbol, ITypeSymbol inputType)
    {
        if (methodSymbol.Parameters.Length <= 1)
            return null;

        var candidate = methodSymbol.Parameters[1];
        return SymbolEqualityComparer.Default.Equals(candidate.Type, inputType)
            ? candidate
            : null;
    }

    static int GetDefinitionParameterStartIndex(IParameterSymbol? implicitInputParameter) =>
        implicitInputParameter is null ? 1 : 2;

    static bool TryValidateAdditionalParameters(
        SourceProductionContext productionContext,
        IMethodSymbol methodSymbol,
        MethodDeclarationSyntax methodSyntax)
    {
        foreach (var parameter in methodSyntax.ParameterList.Parameters.Skip(1))
        {
            if (parameter.Modifiers.Count != 0)
            {
                Report(productionContext, UnsupportedAuthoringMethodShape, methodSymbol, methodSymbol.ContainingType.Name, methodSymbol.Name, "additional parameters cannot use ref, in, out, or params modifiers.");
                return false;
            }
        }

        return true;
    }

    static bool TryProcessBindingStatement(
        SourceProductionContext productionContext,
        IMethodSymbol methodSymbol,
        SemanticModel semanticModel,
        IParameterSymbol flowParameter,
        StatementSyntax statement,
        Dictionary<ISymbol, BoundBinding> boundBindings,
        out AwaitStepModel? awaitStep)
    {
        awaitStep = null;

        if (statement is not LocalDeclarationStatementSyntax localDeclaration
            || localDeclaration.Declaration.Variables.Count != 1)
        {
            Report(productionContext, UnsupportedAuthoringStatement, methodSymbol, methodSymbol.Name, statement.NormalizeWhitespace().ToFullString());
            return false;
        }

        var variable = localDeclaration.Declaration.Variables[0];
        if (variable.Initializer?.Value is not AwaitExpressionSyntax awaitExpression
            || awaitExpression.Expression is not InvocationExpressionSyntax invocationSyntax
            || semanticModel.GetDeclaredSymbol(variable) is not ILocalSymbol localSymbol)
        {
            Report(productionContext, UnsupportedAuthoringStatement, methodSymbol, methodSymbol.Name, statement.NormalizeWhitespace().ToFullString());
            return false;
        }

        if (semanticModel.GetOperation(invocationSyntax) is not IInvocationOperation invocation
            || invocation.Instance is not IParameterReferenceOperation instance
            || !SymbolEqualityComparer.Default.Equals(instance.Parameter, flowParameter))
        {
            Report(productionContext, UnsupportedAuthoringStatement, methodSymbol, methodSymbol.Name, statement.NormalizeWhitespace().ToFullString());
            return false;
        }

        var localBindingName = variable.Identifier.ValueText;
        switch (invocation.TargetMethod.Name)
        {
            case "Input":
                boundBindings[localSymbol] = new(
                    Kind: BoundBindingKind.Parameter,
                    TypeName: FormatType(localSymbol.Type),
                    NameExpressionText: GetNameArgument(invocation, defaultName: localBindingName),
                    NeedsNullSuppression: NeedsNullSuppression(localSymbol.Type));
                return true;

            case "Parameter":
                boundBindings[localSymbol] = new(
                    Kind: BoundBindingKind.Parameter,
                    TypeName: FormatType(localSymbol.Type),
                    NameExpressionText: GetNameArgument(invocation, defaultName: localBindingName),
                    NeedsNullSuppression: NeedsNullSuppression(localSymbol.Type));
                return true;

            case "Request":
                var requestArgument = GetArgument(invocation, "request", position: 0);
                if (requestArgument?.Expression is not { } requestExpression)
                {
                    Report(productionContext, UnsupportedAuthoringStatement, methodSymbol, methodSymbol.Name, statement.NormalizeWhitespace().ToFullString());
                    return false;
                }

                var continuationArgument = GetArgument(invocation, "continuationEntityExpression", position: 3);
                if (continuationArgument?.Expression is { } continuationExpression
                    && ContainsBoundSymbolReference(continuationExpression, semanticModel, boundBindings.Keys))
                {
                    Report(productionContext, UnsupportedContinuationCapture, methodSymbol, methodSymbol.Name);
                    return false;
                }

                var nodeNameText = GetOptionalArgument(invocation, "nodeName", position: 1) ?? Literal(localBindingName);
                var resultNameText = GetOptionalArgument(invocation, "resultName", position: 2) ?? Literal(localBindingName);
                var requestExpressionText = RewriteExpression(requestExpression, semanticModel, boundBindings);
                var continuationExpressionText = continuationArgument?.Expression?.NormalizeWhitespace().ToFullString();

                boundBindings[localSymbol] = new(
                    Kind: BoundBindingKind.Variable,
                    TypeName: FormatType(localSymbol.Type),
                    NameExpressionText: resultNameText,
                    NeedsNullSuppression: NeedsNullSuppression(localSymbol.Type));

                awaitStep = new(
                    Kind: AwaitStepKind.Request,
                    ResultTypeName: FormatType(localSymbol.Type),
                    NodeNameText: nodeNameText,
                    ResultNameText: resultNameText,
                    ExpressionText: requestExpressionText,
                    ContinuationExpressionText: continuationExpressionText);
                return true;

            case "Read":
                if (!TryLowerReadInvocation(invocation, semanticModel, boundBindings, out var loweredReadExpressionText))
                {
                    Report(productionContext, UnsupportedAuthoringStatement, methodSymbol, methodSymbol.Name, statement.NormalizeWhitespace().ToFullString());
                    return false;
                }

                var loweredReadNodeNameText = GetOptionalArgument(invocation, "nodeName", position: 1) ?? Literal(localBindingName);
                var loweredReadResultNameText = GetOptionalArgument(invocation, "resultName", position: 2) ?? Literal(localBindingName);

                boundBindings[localSymbol] = new(
                    Kind: BoundBindingKind.Variable,
                    TypeName: FormatType(localSymbol.Type),
                    NameExpressionText: loweredReadResultNameText,
                    NeedsNullSuppression: NeedsNullSuppression(localSymbol.Type));

                awaitStep = new(
                    Kind: AwaitStepKind.Read,
                    ResultTypeName: FormatType(localSymbol.Type),
                    NodeNameText: loweredReadNodeNameText,
                    ResultNameText: loweredReadResultNameText,
                    ExpressionText: loweredReadExpressionText,
                    ContinuationExpressionText: null);
                return true;

            case "Create":
                if (!TryLowerCreateInvocation(invocation, semanticModel, boundBindings, out var loweredCreateExpressionText))
                {
                    Report(productionContext, UnsupportedAuthoringStatement, methodSymbol, methodSymbol.Name, statement.NormalizeWhitespace().ToFullString());
                    return false;
                }

                var loweredCreateNodeNameText = GetOptionalArgument(invocation, "nodeName", position: 1) ?? Literal(localBindingName);
                var loweredCreateResultNameText = GetOptionalArgument(invocation, "resultName", position: 2) ?? Literal(localBindingName);

                boundBindings[localSymbol] = new(
                    Kind: BoundBindingKind.Variable,
                    TypeName: FormatType(localSymbol.Type),
                    NameExpressionText: loweredCreateResultNameText,
                    NeedsNullSuppression: NeedsNullSuppression(localSymbol.Type));

                awaitStep = new(
                    Kind: AwaitStepKind.Create,
                    ResultTypeName: FormatType(localSymbol.Type),
                    NodeNameText: loweredCreateNodeNameText,
                    ResultNameText: loweredCreateResultNameText,
                    ExpressionText: loweredCreateExpressionText,
                    ContinuationExpressionText: null);
                return true;

            case "Transition":
                if (!TryLowerTransitionInvocation(invocation, semanticModel, boundBindings, out var loweredTransitionExpressionText))
                {
                    Report(productionContext, UnsupportedAuthoringStatement, methodSymbol, methodSymbol.Name, statement.NormalizeWhitespace().ToFullString());
                    return false;
                }

                var loweredTransitionNodeNameText = GetOptionalArgument(invocation, "nodeName", position: GetParameterPosition(invocation, "nodeName")) ?? Literal(localBindingName);
                var loweredTransitionResultNameText = GetOptionalArgument(invocation, "resultName", position: GetParameterPosition(invocation, "resultName")) ?? Literal(localBindingName);

                boundBindings[localSymbol] = new(
                    Kind: BoundBindingKind.Variable,
                    TypeName: FormatType(localSymbol.Type),
                    NameExpressionText: loweredTransitionResultNameText,
                    NeedsNullSuppression: NeedsNullSuppression(localSymbol.Type));

                awaitStep = new(
                    Kind: AwaitStepKind.Transition,
                    ResultTypeName: FormatType(localSymbol.Type),
                    NodeNameText: loweredTransitionNodeNameText,
                    ResultNameText: loweredTransitionResultNameText,
                    ExpressionText: loweredTransitionExpressionText,
                    ContinuationExpressionText: null);
                return true;

            case "Timer":
                if (!TryLowerTimerInvocation(invocation, semanticModel, boundBindings, out var loweredTimerKeyExpressionText, out var loweredTimerTimeoutExpressionText))
                {
                    Report(productionContext, UnsupportedAuthoringStatement, methodSymbol, methodSymbol.Name, statement.NormalizeWhitespace().ToFullString());
                    return false;
                }

                var loweredTimerNodeNameText = GetOptionalArgument(invocation, "nodeName", position: GetParameterPosition(invocation, "nodeName")) ?? Literal(localBindingName);
                var loweredTimerResultNameText = GetOptionalArgument(invocation, "resultName", position: GetParameterPosition(invocation, "resultName")) ?? Literal(localBindingName);

                boundBindings[localSymbol] = new(
                    Kind: BoundBindingKind.Variable,
                    TypeName: FormatType(localSymbol.Type),
                    NameExpressionText: loweredTimerResultNameText,
                    NeedsNullSuppression: NeedsNullSuppression(localSymbol.Type));

                awaitStep = new(
                    Kind: AwaitStepKind.Timer,
                    ResultTypeName: FormatType(localSymbol.Type),
                    NodeNameText: loweredTimerNodeNameText,
                    ResultNameText: loweredTimerResultNameText,
                    ExpressionText: string.Empty,
                    ContinuationExpressionText: null,
                    KeyExpressionText: loweredTimerKeyExpressionText,
                    TimeoutExpressionText: loweredTimerTimeoutExpressionText);
                return true;

            case "Poll":
                if (!TryLowerPollInvocation(
                        invocation,
                        semanticModel,
                        boundBindings,
                        out var loweredPollRequestExpressionText,
                        out var loweredPollPredicateExpressionText,
                        out var loweredPollIntervalExpressionText,
                        out var loweredPollTimeoutExpressionText,
                        out var loweredPollTimeoutResultExpressionText))
                {
                    Report(productionContext, UnsupportedAuthoringStatement, methodSymbol, methodSymbol.Name, statement.NormalizeWhitespace().ToFullString());
                    return false;
                }

                var loweredPollNodeNameText = GetOptionalArgument(invocation, "nodeName", position: GetParameterPosition(invocation, "nodeName")) ?? Literal(localBindingName);
                var loweredPollResultNameText = GetOptionalArgument(invocation, "resultName", position: GetParameterPosition(invocation, "resultName")) ?? Literal(localBindingName);

                boundBindings[localSymbol] = new(
                    Kind: BoundBindingKind.Variable,
                    TypeName: FormatType(localSymbol.Type),
                    NameExpressionText: loweredPollResultNameText,
                    NeedsNullSuppression: NeedsNullSuppression(localSymbol.Type));

                awaitStep = new(
                    Kind: AwaitStepKind.Poll,
                    ResultTypeName: FormatType(localSymbol.Type),
                    NodeNameText: loweredPollNodeNameText,
                    ResultNameText: loweredPollResultNameText,
                    ExpressionText: loweredPollRequestExpressionText,
                    ContinuationExpressionText: null,
                    PredicateExpressionText: loweredPollPredicateExpressionText,
                    IntervalExpressionText: loweredPollIntervalExpressionText,
                    TimeoutExpressionText: loweredPollTimeoutExpressionText,
                    TimeoutResultExpressionText: loweredPollTimeoutResultExpressionText
                    );
                return true;

            case "Query":
            case "Compute":
            case "TransitionMany":
                var valueArgument = GetArgument(invocation, invocation.TargetMethod.Parameters[0].Name ?? "value", position: 0);
                if (valueArgument?.Expression is not { } valueExpression)
                {
                    Report(productionContext, UnsupportedAuthoringStatement, methodSymbol, methodSymbol.Name, statement.NormalizeWhitespace().ToFullString());
                    return false;
                }

                var loweredExpressionText = RewriteExpression(valueExpression, semanticModel, boundBindings);
                var loweredNodeNameText = GetOptionalArgument(invocation, "nodeName", position: 1) ?? Literal(localBindingName);
                var loweredResultNameText = GetOptionalArgument(invocation, "resultName", position: 2) ?? Literal(localBindingName);

                boundBindings[localSymbol] = new(
                    Kind: BoundBindingKind.Variable,
                    TypeName: FormatType(localSymbol.Type),
                    NameExpressionText: loweredResultNameText,
                    NeedsNullSuppression: NeedsNullSuppression(localSymbol.Type)
                    );

                awaitStep = new(
                    Kind: invocation.TargetMethod.Name switch
                    {
                        "Query" => AwaitStepKind.Query,
                        "Compute" => AwaitStepKind.Compute,
                        "TransitionMany" => AwaitStepKind.TransitionMany,
                        _ => throw new InvalidOperationException($"Unsupported await step '{invocation.TargetMethod.Name}'.")
                    },
                    ResultTypeName: FormatType(localSymbol.Type),
                    NodeNameText: loweredNodeNameText,
                    ResultNameText: loweredResultNameText,
                    ExpressionText: loweredExpressionText,
                    ContinuationExpressionText: null);
                return true;

            default:
                Report(productionContext, UnsupportedAuthoringStatement, methodSymbol, methodSymbol.Name, statement.NormalizeWhitespace().ToFullString());
                return false;
        }
    }

    static bool TryCollectFlowBindings(
        SourceProductionContext productionContext,
        IMethodSymbol methodSymbol,
        SemanticModel semanticModel,
        IParameterSymbol flowParameter,
        SyntaxList<StatementSyntax> statements,
        Dictionary<ISymbol, BoundBinding> boundBindings,
        Dictionary<LocalDeclarationStatementSyntax, AwaitStepModel?> awaitStepsByStatement)
    {
        foreach (var statement in statements)
        {
            if (!TryCollectFlowBindingsFromStatement(
                    productionContext: productionContext,
                    methodSymbol: methodSymbol,
                    semanticModel: semanticModel,
                    flowParameter: flowParameter,
                    statement: statement,
                    boundBindings: boundBindings,
                    awaitStepsByStatement: awaitStepsByStatement))
            {
                return false;
            }
        }

        return true;
    }

    static bool TryCollectFlowBindingsFromStatement(
        SourceProductionContext productionContext,
        IMethodSymbol methodSymbol,
        SemanticModel semanticModel,
        IParameterSymbol flowParameter,
        StatementSyntax statement,
        Dictionary<ISymbol, BoundBinding> boundBindings,
        Dictionary<LocalDeclarationStatementSyntax, AwaitStepModel?> awaitStepsByStatement)
    {
        switch (statement)
        {
            case LocalDeclarationStatementSyntax localDeclaration:
                if (!TryProcessBindingStatement(
                        productionContext: productionContext,
                        methodSymbol: methodSymbol,
                        semanticModel: semanticModel,
                        flowParameter: flowParameter,
                        statement: localDeclaration,
                        boundBindings: boundBindings,
                        awaitStep: out var awaitStep))
                {
                    return false;
                }

                awaitStepsByStatement[localDeclaration] = awaitStep;
                return true;

            case ReturnStatementSyntax:
                return true;

            case IfStatementSyntax ifStatement:
                if (!TryCollectFlowBindingsFromStatement(
                        productionContext: productionContext,
                        methodSymbol: methodSymbol,
                        semanticModel: semanticModel,
                        flowParameter: flowParameter,
                        statement: ifStatement.Statement,
                        boundBindings: boundBindings,
                        awaitStepsByStatement: awaitStepsByStatement))
                {
                    return false;
                }

                if (ifStatement.Else is null)
                    return true;

                return TryCollectFlowBindingsFromStatement(
                    productionContext: productionContext,
                    methodSymbol: methodSymbol,
                    semanticModel: semanticModel,
                    flowParameter: flowParameter,
                    statement: ifStatement.Else.Statement,
                    boundBindings: boundBindings,
                    awaitStepsByStatement: awaitStepsByStatement);

            case BlockSyntax block:
                return TryCollectFlowBindings(
                    productionContext: productionContext,
                    methodSymbol: methodSymbol,
                    semanticModel: semanticModel,
                    flowParameter: flowParameter,
                    statements: block.Statements,
                    boundBindings: boundBindings,
                    awaitStepsByStatement: awaitStepsByStatement);

            default:
                Report(productionContext, UnsupportedAuthoringStatement, methodSymbol, methodSymbol.Name, statement.NormalizeWhitespace().ToFullString());
                return false;
        }
    }

    static bool TryLowerReturnStatement(
        SourceProductionContext productionContext,
        IMethodSymbol methodSymbol,
        SemanticModel semanticModel,
        IParameterSymbol flowParameter,
        StatementSyntax statement,
        Dictionary<ISymbol, BoundBinding> boundBindings,
        out ReturnStepModel returnStep,
        string defaultNodeNameText)
    {
        returnStep = null!;

        if (statement is not ReturnStatementSyntax returnStatement
            || returnStatement.Expression is not InvocationExpressionSyntax invocationSyntax
            || semanticModel.GetOperation(invocationSyntax) is not IInvocationOperation invocation
            || invocation.Instance is not IParameterReferenceOperation instance
            || !SymbolEqualityComparer.Default.Equals(instance.Parameter, flowParameter)
            || invocation.TargetMethod.Name != "Return")
        {
            Report(productionContext, UnsupportedAuthoringStatement, methodSymbol, methodSymbol.Name, statement.NormalizeWhitespace().ToFullString());
            return false;
        }

        var resultArgument = GetArgument(invocation, "result", position: 0);
        if (resultArgument?.Expression is not { } resultExpression)
        {
            Report(productionContext, UnsupportedAuthoringStatement, methodSymbol, methodSymbol.Name, statement.NormalizeWhitespace().ToFullString());
            return false;
        }

        var nodeNameText = GetOptionalArgument(invocation, "nodeName", position: 1) ?? defaultNodeNameText;
        returnStep = new(
            NodeNameText: nodeNameText,
            ResultExpressionText: RewriteExpression(resultExpression, semanticModel, boundBindings));
        return true;
    }

    static bool TryLowerStatements(
        SourceProductionContext productionContext,
        IMethodSymbol methodSymbol,
        SemanticModel semanticModel,
        IParameterSymbol flowParameter,
        SyntaxList<StatementSyntax> statements,
        Dictionary<ISymbol, BoundBinding> boundBindings,
        Dictionary<LocalDeclarationStatementSyntax, AwaitStepModel?> awaitStepsByStatement,
        string? successorNodeText,
        LoweringState loweringState,
        bool isTopLevel,
        out LoweredBlockModel loweredBlock)
    {
        var currentEntryNodeText = successorNodeText;
        List<ImmutableArray<FlowNodeModel>> nodeSegments = [];

        for (var i = statements.Count - 1; i >= 0; i--)
        {
            if (!TryLowerStatement(
                    productionContext: productionContext,
                    methodSymbol: methodSymbol,
                    semanticModel: semanticModel,
                    flowParameter: flowParameter,
                    statement: statements[i],
                    boundBindings: boundBindings,
                    awaitStepsByStatement: awaitStepsByStatement,
                    successorNodeText: currentEntryNodeText,
                    loweringState: loweringState,
                    useTerminalEndNodeName: isTopLevel && successorNodeText is null && i == statements.Count - 1,
                    loweredStatement: out var loweredStatement))
            {
                loweredBlock = null!;
                return false;
            }

            currentEntryNodeText = loweredStatement.EntryNodeText;
            if (!loweredStatement.Nodes.IsDefaultOrEmpty)
                nodeSegments.Add(loweredStatement.Nodes);
        }

        if (currentEntryNodeText is null)
        {
            ReportMissingTerminalReturn(productionContext, methodSymbol);
            loweredBlock = null!;
            return false;
        }

        var nodes = ImmutableArray.CreateBuilder<FlowNodeModel>();
        foreach (var segment in nodeSegments)
        {
            nodes.AddRange(segment);
        }

        loweredBlock = new(
            EntryNodeText: currentEntryNodeText,
            Nodes: nodes.ToImmutable());
        return true;
    }

    static bool TryLowerStatement(
        SourceProductionContext productionContext,
        IMethodSymbol methodSymbol,
        SemanticModel semanticModel,
        IParameterSymbol flowParameter,
        StatementSyntax statement,
        Dictionary<ISymbol, BoundBinding> boundBindings,
        Dictionary<LocalDeclarationStatementSyntax, AwaitStepModel?> awaitStepsByStatement,
        string? successorNodeText,
        LoweringState loweringState,
        bool useTerminalEndNodeName,
        out LoweredBlockModel loweredStatement)
    {
        switch (statement)
        {
            case BlockSyntax block:
                return TryLowerStatements(
                    productionContext: productionContext,
                    methodSymbol: methodSymbol,
                    semanticModel: semanticModel,
                    flowParameter: flowParameter,
                    statements: block.Statements,
                    boundBindings: boundBindings,
                    awaitStepsByStatement: awaitStepsByStatement,
                    successorNodeText: successorNodeText,
                    loweringState: loweringState,
                    isTopLevel: false,
                    loweredBlock: out loweredStatement);

            case LocalDeclarationStatementSyntax localDeclaration:
                if (!awaitStepsByStatement.TryGetValue(localDeclaration, out var awaitStep))
                {
                    Report(productionContext, UnsupportedAuthoringStatement, methodSymbol, methodSymbol.Name, statement.NormalizeWhitespace().ToFullString());
                    loweredStatement = null!;
                    return false;
                }

                if (awaitStep is null)
                {
                    loweredStatement = new(
                        EntryNodeText: successorNodeText,
                        Nodes: []);
                    return true;
                }

                if (successorNodeText is null)
                {
                    ReportMissingTerminalReturn(productionContext, methodSymbol);
                    loweredStatement = null!;
                    return false;
                }

                loweredStatement = new(
                    EntryNodeText: awaitStep.NodeNameText,
                    Nodes: [new AwaitFlowNodeModel(awaitStep, successorNodeText)]);
                return true;

            case ReturnStatementSyntax:
                if (!TryLowerReturnStatement(
                        productionContext: productionContext,
                        methodSymbol: methodSymbol,
                        semanticModel: semanticModel,
                        flowParameter: flowParameter,
                        statement: statement,
                        boundBindings: boundBindings,
                        returnStep: out var returnStep,
                        defaultNodeNameText: useTerminalEndNodeName
                            ? Literal("end")
                            : Literal($"__return_{loweringState.NextReturnIndex++}")))
                {
                    loweredStatement = null!;
                    return false;
                }

                loweredStatement = new(
                    EntryNodeText: returnStep.NodeNameText,
                    Nodes: [new ReturnFlowNodeModel(returnStep)]);
                return true;

            case IfStatementSyntax ifStatement:
                return TryLowerIfStatement(
                    productionContext: productionContext,
                    methodSymbol: methodSymbol,
                    semanticModel: semanticModel,
                    flowParameter: flowParameter,
                    statement: ifStatement,
                    boundBindings: boundBindings,
                    awaitStepsByStatement: awaitStepsByStatement,
                    successorNodeText: successorNodeText,
                    loweringState: loweringState,
                    loweredStatement: out loweredStatement);

            default:
                Report(productionContext, UnsupportedAuthoringStatement, methodSymbol, methodSymbol.Name, statement.NormalizeWhitespace().ToFullString());
                loweredStatement = null!;
                return false;
        }
    }

    static bool TryLowerIfStatement(
        SourceProductionContext productionContext,
        IMethodSymbol methodSymbol,
        SemanticModel semanticModel,
        IParameterSymbol flowParameter,
        IfStatementSyntax statement,
        Dictionary<ISymbol, BoundBinding> boundBindings,
        Dictionary<LocalDeclarationStatementSyntax, AwaitStepModel?> awaitStepsByStatement,
        string? successorNodeText,
        LoweringState loweringState,
        out LoweredBlockModel loweredStatement)
    {
        if (!TryLowerStatement(
                productionContext: productionContext,
                methodSymbol: methodSymbol,
                semanticModel: semanticModel,
                flowParameter: flowParameter,
                statement: statement.Statement,
                boundBindings: boundBindings,
                awaitStepsByStatement: awaitStepsByStatement,
                successorNodeText: successorNodeText,
                loweringState: loweringState,
                useTerminalEndNodeName: false,
                loweredStatement: out var thenBlock))
        {
            loweredStatement = null!;
            return false;
        }

        LoweredBlockModel elseBlock;
        if (statement.Else is null)
        {
            elseBlock = new(
                EntryNodeText: successorNodeText,
                Nodes: []);
        }
        else if (!TryLowerStatement(
                     productionContext: productionContext,
                     methodSymbol: methodSymbol,
                     semanticModel: semanticModel,
                     flowParameter: flowParameter,
                     statement: statement.Else.Statement,
                     boundBindings: boundBindings,
                     awaitStepsByStatement: awaitStepsByStatement,
                     successorNodeText: successorNodeText,
                     loweringState: loweringState,
                     useTerminalEndNodeName: false,
                     loweredStatement: out elseBlock))
        {
            loweredStatement = null!;
            return false;
        }

        if (thenBlock.EntryNodeText is null || elseBlock.EntryNodeText is null)
        {
            ReportMissingTerminalReturn(productionContext, methodSymbol);
            loweredStatement = null!;
            return false;
        }

        var branchNodeNameText = Literal($"__branch_{loweringState.NextBranchIndex++}");
        var nodes = ImmutableArray.CreateBuilder<FlowNodeModel>();
        nodes.Add(new BranchFlowNodeModel(
            NodeNameText: branchNodeNameText,
            ConditionExpressionText: RewriteExpression(statement.Condition, semanticModel, boundBindings),
            TrueNodeText: thenBlock.EntryNodeText,
            FalseNodeText: elseBlock.EntryNodeText));
        nodes.AddRange(thenBlock.Nodes);
        nodes.AddRange(elseBlock.Nodes);

        loweredStatement = new(
            EntryNodeText: branchNodeNameText,
            Nodes: nodes.ToImmutable());
        return true;
    }

    static void ReportMissingTerminalReturn(
        SourceProductionContext productionContext,
        IMethodSymbol methodSymbol) =>
        Report(
            productionContext,
            UnsupportedAuthoringMethodShape,
            methodSymbol,
            methodSymbol.ContainingType.Name,
            methodSymbol.Name,
            "all control-flow paths must terminate with flow.Return(...).");

    static ImmutableArray<string> BuildGeneratedLines(
        ImmutableArray<FlowNodeModel> flowNodes,
        string entryNodeText,
        string resultTypeName)
    {
        var lines = ImmutableArray.CreateBuilder<string>();
        lines.Add("var __builder = new global::Cohesive.Processes.Model.ProcessDefinitionBuilder(processNameOverride ?? DEFAULT_PROCESS_NAME);");

        static string Suffix(string baseExpressionText, string suffix) => $"({baseExpressionText}) + {Literal(suffix)}";
        static string RemainingExpression(AwaitStepModel step) =>
            $"(context.ContainsVariable({Suffix(step.NodeNameText, "__remaining")}) ? context.RequireVariable<global::System.TimeSpan>({Suffix(step.NodeNameText, "__remaining")}) : {step.TimeoutExpressionText})";

        foreach (var flowNode in flowNodes)
        {
            if (flowNode is BranchFlowNodeModel branch)
            {
                lines.Add($"__builder.AddBranchingNode(name: {branch.NodeNameText}, branches: new global::Cohesive.Processes.Model.BranchNodeBranch[] {{ new(context => {branch.ConditionExpressionText}, {branch.TrueNodeText}) }}, elseNode: {branch.FalseNodeText});");
                continue;
            }

            if (flowNode is ReturnFlowNodeModel returnNode)
            {
                lines.Add($"__builder.AddEndNode<{resultTypeName}>(name: {returnNode.ReturnStep.NodeNameText}, resultExpression: context => {returnNode.ReturnStep.ResultExpressionText});");
                continue;
            }

            var awaitFlowNode = (AwaitFlowNodeModel)flowNode;
            var step = awaitFlowNode.AwaitStep;
            var nextNodeText = awaitFlowNode.NextNodeText;
            var builder = new StringBuilder();
            switch (step.Kind)
            {
                case AwaitStepKind.Request:
                    builder.Append("__builder.AddEffectRequestNode<");
                    builder.Append(step.ResultTypeName);
                    builder.Append(">(name: ");
                    builder.Append(step.NodeNameText);
                    builder.Append(", requestExpression: context => ");
                    builder.Append(step.ExpressionText);
                    builder.Append(", resultVariable: ");
                    builder.Append(step.ResultNameText);
                    if (step.ContinuationExpressionText is not null)
                    {
                        builder.Append(", continuationEntityExpression: ");
                        builder.Append(step.ContinuationExpressionText);
                    }

                    builder.Append(", nextNode: ");
                    builder.Append(nextNodeText);
                    builder.Append(");");
                    break;

                case AwaitStepKind.Read:
                    builder.Append("__builder.AddEntityReadNode(name: ");
                    builder.Append(step.NodeNameText);
                    builder.Append(", readExpression: context => ");
                    builder.Append(step.ExpressionText);
                    builder.Append(", resultVariable: ");
                    builder.Append(step.ResultNameText);
                    builder.Append(", nextNode: ");
                    builder.Append(nextNodeText);
                    builder.Append(");");
                    break;

                case AwaitStepKind.Create:
                    builder.Append("__builder.AddEntityCreateNode(name: ");
                    builder.Append(step.NodeNameText);
                    builder.Append(", createExpression: context => ");
                    builder.Append(step.ExpressionText);
                    builder.Append(", resultVariable: ");
                    builder.Append(step.ResultNameText);
                    builder.Append(", nextNode: ");
                    builder.Append(nextNodeText);
                    builder.Append(");");
                    break;

                case AwaitStepKind.Query:
                    builder.Append("__builder.AddEntityQueryNode(name: ");
                    builder.Append(step.NodeNameText);
                    builder.Append(", queryExpression: context => ");
                    builder.Append(step.ExpressionText);
                    builder.Append(", resultVariable: ");
                    builder.Append(step.ResultNameText);
                    builder.Append(", nextNode: ");
                    builder.Append(nextNodeText);
                    builder.Append(");");
                    break;

                case AwaitStepKind.Compute:
                    builder.Append("__builder.AddComputeNode(name: ");
                    builder.Append(step.NodeNameText);
                    builder.Append(", valueExpression: context => ");
                    builder.Append(step.ExpressionText);
                    builder.Append(", resultVariable: ");
                    builder.Append(step.ResultNameText);
                    builder.Append(", nextNode: ");
                    builder.Append(nextNodeText);
                    builder.Append(");");
                    break;

                case AwaitStepKind.Transition:
                case AwaitStepKind.TransitionMany:
                    builder.Append("__builder.AddEntityTransitionNode(name: ");
                    builder.Append(step.NodeNameText);
                    builder.Append(", transitionExpression: context => ");
                    builder.Append(step.ExpressionText);
                    builder.Append(", resultVariable: ");
                    builder.Append(step.ResultNameText);
                    builder.Append(", nextNode: ");
                    builder.Append(nextNodeText);
                    builder.Append(");");
                    break;

                case AwaitStepKind.Timer:
                    builder.Append("__builder.AddWaitNode(name: ");
                    builder.Append(step.NodeNameText);
                    builder.Append(", waitType: global::Cohesive.Processes.Model.ProcessWaitType.Timer, keyExpression: context => ");
                    builder.Append(step.KeyExpressionText ?? step.NodeNameText);
                    builder.Append(", timeoutExpression: context => ");
                    builder.Append(step.TimeoutExpressionText);
                    builder.Append(", captureVar: ");
                    builder.Append(step.ResultNameText);
                    builder.Append(", nextNode: ");
                    builder.Append(nextNodeText);
                    builder.Append(");");
                    break;

                case AwaitStepKind.Poll:
                    var currentVar = Suffix(step.NodeNameText, "__current");
                    var remainingVar = Suffix(step.NodeNameText, "__remaining");
                    var delayVar = Suffix(step.NodeNameText, "__delay");
                    var terminalNode = Suffix(step.NodeNameText, "__terminal");
                    var completeNode = Suffix(step.NodeNameText, "__complete");
                    var timeoutCheckNode = Suffix(step.NodeNameText, "__timeoutCheck");
                    var delayNode = Suffix(step.NodeNameText, "__delayCompute");
                    var waitNode = Suffix(step.NodeNameText, "__wait");
                    var remainingNode = Suffix(step.NodeNameText, "__remainingNext");
                    var timeoutNode = Suffix(step.NodeNameText, "__timeout");
                    var remainingExpression = RemainingExpression(step);
                    var completedPredicate = $"new global::System.Func<{step.ResultTypeName}, bool>({step.PredicateExpressionText})(context.RequireVariable<{step.ResultTypeName}>({currentVar}))";
                    var delayExpression = $"({remainingExpression} <= {step.IntervalExpressionText} ? {remainingExpression} : {step.IntervalExpressionText})";
                    var nextRemainingExpression = $"{remainingExpression} - context.RequireVariable<global::System.TimeSpan>({delayVar})";

                    lines.Add($"__builder.AddEffectRequestNode<{step.ResultTypeName}>(name: {step.NodeNameText}, requestExpression: context => {step.ExpressionText}, resultVariable: {currentVar}, nextNode: {terminalNode});");
                    lines.Add($"__builder.AddBranchingNode(name: {terminalNode}, branches: new global::Cohesive.Processes.Model.BranchNodeBranch[] {{ new(context => {completedPredicate}, {completeNode}) }}, elseNode: {timeoutCheckNode});");
                    lines.Add($"__builder.AddComputeNode(name: {completeNode}, valueExpression: context => context.RequireVariable<{step.ResultTypeName}>({currentVar}), resultVariable: {step.ResultNameText}, nextNode: {nextNodeText});");
                    lines.Add($"__builder.AddBranchingNode(name: {timeoutCheckNode}, branches: new global::Cohesive.Processes.Model.BranchNodeBranch[] {{ new(context => {remainingExpression} > global::System.TimeSpan.Zero, {delayNode}) }}, elseNode: {timeoutNode});");
                    lines.Add($"__builder.AddComputeNode(name: {delayNode}, valueExpression: context => {delayExpression}, resultVariable: {delayVar}, nextNode: {waitNode});");
                    lines.Add($"__builder.AddWaitNode(name: {waitNode}, waitType: global::Cohesive.Processes.Model.ProcessWaitType.Timer, keyExpression: context => {waitNode}, timeoutExpression: context => context.RequireVariable<global::System.TimeSpan>({delayVar}), captureVar: null, nextNode: {remainingNode});");
                    lines.Add($"__builder.AddComputeNode(name: {remainingNode}, valueExpression: context => {nextRemainingExpression}, resultVariable: {remainingVar}, nextNode: {step.NodeNameText});");
                    lines.Add($"__builder.AddComputeNode(name: {timeoutNode}, valueExpression: context => {step.TimeoutResultExpressionText}, resultVariable: {step.ResultNameText}, nextNode: {nextNodeText});");
                    builder.Append("// poll emitted via composite nodes");
                    break;

                default:
                    throw new InvalidOperationException($"Unsupported await step kind '{step.Kind}'.");
            }

            lines.Add(builder.ToString());
        }

        lines.Add($"__builder.SetEntryNode({entryNodeText});");
        lines.Add("return __builder.Build();");
        return lines.ToImmutable();
    }

    static string Emit(ProcessAuthoringModel model)
    {
        var builder = new StringBuilder();
        builder.AppendLine("// <auto-generated />");
        builder.AppendLine("#nullable enable");

        foreach (var usingDirective in model.UsingDirectives)
        {
            builder.AppendLine(usingDirective);
        }

        if (!model.NamespaceSymbol.IsGlobalNamespace)
        {
            builder.Append("namespace ");
            builder.Append(model.NamespaceSymbol.ToDisplayString());
            builder.AppendLine(";");
            builder.AppendLine();
        }

        builder.Append(model.ContainingTypeDeclaration);
        builder.AppendLine();
        builder.AppendLine("{");
        if (model.CacheDefaultDefinition)
        {
            builder.Append("    global::Cohesive.Processes.Model.ProcessDefinition? ");
            builder.Append(model.DefaultDefinitionFieldName);
            builder.AppendLine(";");
            builder.AppendLine();
        }

        builder.Append("    ");
        builder.Append(model.GeneratedMethodAccessibility);
        builder.Append(" global::Cohesive.Processes.Model.ProcessDefinition ");
        builder.Append(model.GeneratedMethodName);
        builder.Append('(');
        builder.Append(model.GeneratedMethodParameters);
        builder.AppendLine(")");
        builder.AppendLine("    {");
        builder.Append("        const string DEFAULT_PROCESS_NAME = ");
        builder.Append(model.DefaultProcessNameLiteral);
        builder.AppendLine(";");

        foreach (var line in model.GeneratedLines)
        {
            builder.Append("        ");
            builder.AppendLine(line);
        }

        builder.AppendLine("    }");

        if (model.GenerateTypedDefineMethod)
        {
            builder.AppendLine();
            builder.AppendLine("    /// <summary>");
            builder.AppendLine("    /// Returns the generated strongly typed process definition.");
            builder.AppendLine("    /// </summary>");
            builder.Append("    public global::Cohesive.Processes.Model.TypedProcessDefinition<");
            builder.Append(model.InputTypeName);
            builder.Append(", ");
            builder.Append(model.ResultTypeName);
            builder.Append("> Define");
            builder.Append('(');
            builder.Append(model.TypedDefineMethodParameters);
            builder.AppendLine(")");
            builder.AppendLine("    {");
            builder.AppendLine("        var __normalizedProcessName = string.IsNullOrWhiteSpace(processName) ? null : processName;");
            if (model.CacheDefaultDefinition)
            {
                builder.AppendLine("        if (__normalizedProcessName is null)");
                builder.AppendLine("        {");
                builder.Append("            global::Cohesive.Processes.Model.ProcessDefinition __definition = ");
                builder.Append(model.DefaultDefinitionFieldName);
                builder.Append(" ??= ");
                builder.Append(model.GeneratedMethodName);
                builder.AppendLine("();");
                builder.Append("            return new global::Cohesive.Processes.Model.TypedProcessDefinition<");
                builder.Append(model.InputTypeName);
                builder.Append(", ");
                builder.Append(model.ResultTypeName);
                builder.Append(">(__definition, ");
                builder.Append(Literal(model.TypedInputParameterName!));
                builder.AppendLine(");");
                builder.AppendLine("        }");
            }

            builder.Append("        return new global::Cohesive.Processes.Model.TypedProcessDefinition<");
            builder.Append(model.InputTypeName);
            builder.Append(", ");
            builder.Append(model.ResultTypeName);
            builder.Append(">(");
            builder.Append(model.GeneratedMethodName);
            builder.Append('(');
            builder.Append(model.TypedDefineInvocationArguments);
            builder.Append("), ");
            builder.Append(Literal(model.TypedInputParameterName!));
            builder.AppendLine(");");
            builder.AppendLine("    }");
        }

        builder.AppendLine("}");
        return builder.ToString();
    }

    static bool IsNamedType(INamedTypeSymbol typeSymbol, string fullyQualifiedMetadataName) =>
        typeSymbol.GetFullyQualifiedMetadataName() == fullyQualifiedMetadataName;

    static bool IsAttribute(INamedTypeSymbol? typeSymbol, string fullyQualifiedMetadataName) =>
        typeSymbol is not null && typeSymbol.GetFullyQualifiedMetadataName() == fullyQualifiedMetadataName;

    static string BuildContainingTypeDeclaration(INamedTypeSymbol containingType, ClassDeclarationSyntax declaration)
    {
        var modifiers = new List<string>();
        switch (containingType.DeclaredAccessibility)
        {
            case Accessibility.Public:
                modifiers.Add("public");
                break;
            case Accessibility.Internal:
                modifiers.Add("internal");
                break;
            case Accessibility.Private:
                modifiers.Add("private");
                break;
            case Accessibility.Protected:
                modifiers.Add("protected");
                break;
            case Accessibility.ProtectedOrInternal:
                modifiers.Add("protected internal");
                break;
            case Accessibility.ProtectedAndInternal:
                modifiers.Add("private protected");
                break;
        }

        if (containingType.IsStatic)
            modifiers.Add("static");
        else
        {
            if (containingType.IsAbstract)
                modifiers.Add("abstract");
            if (containingType.IsSealed)
                modifiers.Add("sealed");
        }

        modifiers.Add("partial");
        modifiers.Add("class");
        modifiers.Add(declaration.Identifier.Text);
        return string.Join(" ", modifiers);
    }

    static string BuildGeneratedMethodParameterList(MethodDeclarationSyntax methodSyntax, int definitionParameterStartIndex)
    {
        var parameters = methodSyntax.ParameterList.Parameters.Skip(definitionParameterStartIndex)
            .Select(static parameter => parameter.NormalizeWhitespace().ToFullString())
            .ToList();
        parameters.Add("string? processNameOverride = null");
        return string.Join(", ", parameters);
    }

    static string BuildPublicDefineMethodParameterList(MethodDeclarationSyntax methodSyntax, int definitionParameterStartIndex)
    {
        var parameters = methodSyntax.ParameterList.Parameters.Skip(definitionParameterStartIndex)
            .Select(static parameter => parameter.NormalizeWhitespace().ToFullString())
            .ToList();
        parameters.Add("string? processName = null");
        return string.Join(", ", parameters);
    }

    static string BuildGeneratedMethodInvocationArguments(MethodDeclarationSyntax methodSyntax, int definitionParameterStartIndex, string processNameExpression)
    {
        var arguments = methodSyntax.ParameterList.Parameters.Skip(definitionParameterStartIndex)
            .Select(static parameter => parameter.Identifier.ValueText)
            .ToList();
        arguments.Add($"processNameOverride: {processNameExpression}");
        return string.Join(", ", arguments);
    }

    static string FormatType(ITypeSymbol typeSymbol) => typeSymbol.ToDisplayString(FullyQualifiedNullableFormat);

    static bool NeedsNullSuppression(ITypeSymbol typeSymbol) =>
        typeSymbol.IsReferenceType || typeSymbol.NullableAnnotation == NullableAnnotation.Annotated;

    static string Literal(string value) => SymbolDisplay.FormatLiteral(value, quote: true);

    static string GetNameArgument(IInvocationOperation invocation, string defaultName) =>
        GetOptionalArgument(invocation, "name", position: 0) ?? Literal(defaultName);

    static int GetParameterPosition(IInvocationOperation invocation, string parameterName)
    {
        for (var i = 0; i < invocation.TargetMethod.Parameters.Length; i++)
        {
            if (string.Equals(invocation.TargetMethod.Parameters[i].Name, parameterName, StringComparison.Ordinal))
                return i;
        }

        return -1;
    }

    static ArgumentSyntax? GetArgument(IInvocationOperation invocation, string name, int position)
    {
        var byName = invocation.Arguments.FirstOrDefault(argument => argument.Parameter?.Name == name);
        if (byName is not null)
            return byName.Syntax as ArgumentSyntax;

        if (position >= 0
            && invocation.Arguments.Length > position
            && invocation.Arguments[position].Parameter is null)
        {
            return invocation.Arguments[position].Syntax as ArgumentSyntax;
        }

        return null;
    }

    static string? GetOptionalArgument(IInvocationOperation invocation, string name, int position) =>
        GetArgument(invocation, name, position)?.Expression.NormalizeWhitespace().ToFullString();

    static string RewriteExpression(
        ExpressionSyntax expression,
        SemanticModel semanticModel,
        Dictionary<ISymbol, BoundBinding> boundBindings,
        IEnumerable<ISymbol>? nullSuppressedSymbols = null)
    {
        var rewriter = new BoundSymbolAccessRewriter(
            semanticModel,
            boundBindings,
            nullSuppressedSymbols is null
                ? null
                : new HashSet<ISymbol>(nullSuppressedSymbols, SymbolEqualityComparer.Default));
        return rewriter.Rewrite(expression).NormalizeWhitespace().ToFullString();
    }

    static bool TryLowerReadInvocation(
        IInvocationOperation invocation,
        SemanticModel semanticModel,
        Dictionary<ISymbol, BoundBinding> boundBindings,
        out string expressionText)
    {
        expressionText = null!;

        if (invocation.TargetMethod.Parameters.Length == 0)
            return false;

        if (invocation.TargetMethod.Parameters[0].Type is INamedTypeSymbol firstParameterType
            && firstParameterType.Name == "ProcessEntityRead")
        {
            var readArgument = GetArgument(invocation, invocation.TargetMethod.Parameters[0].Name ?? "read", position: 0);
            if (readArgument?.Expression is not { } readExpression)
                return false;

            expressionText = RewriteExpression(readExpression, semanticModel, boundBindings);
            return true;
        }

        var entityArgument = GetArgument(invocation, "entity", position: 0);
        var entityIdArgument = GetArgument(invocation, "entityId", position: 1);
        if (entityArgument?.Expression is not { } entityExpression
            || entityIdArgument?.Expression is not { } entityIdExpression)
        {
            return false;
        }

        var loweredEntityText = RewriteExpression(entityExpression, semanticModel, boundBindings);
        var loweredEntityIdText = RewriteExpression(entityIdExpression, semanticModel, boundBindings);
        var loweredPartitionKeyText = GetArgument(invocation, "partitionKey", position: 2)?.Expression is { } partitionKeyExpression
            ? RewriteExpression(partitionKeyExpression, semanticModel, boundBindings)
            : "null";
        var loweredReadRequestText = GetArgument(invocation, "read", position: 3)?.Expression is { } readRequestExpression
            ? RewriteExpression(readRequestExpression, semanticModel, boundBindings)
            : "null";

        if (GetArgument(invocation, "project", position: 2)?.Expression is { } projectExpression)
        {
            expressionText = $"{loweredEntityText}.ReadById(entityId: {loweredEntityIdText}, project: {RewriteExpression(projectExpression, semanticModel, boundBindings)}, partitionKey: {loweredPartitionKeyText}, read: {loweredReadRequestText})";
            return true;
        }

        expressionText = $"global::Cohesive.Processes.Model.ProcessEntityRead.ReadById(entity: {loweredEntityText}, entityId: {loweredEntityIdText}, partitionKey: {loweredPartitionKeyText}, read: {loweredReadRequestText})";
        return true;
    }

    static bool TryLowerCreateInvocation(
        IInvocationOperation invocation,
        SemanticModel semanticModel,
        Dictionary<ISymbol, BoundBinding> boundBindings,
        out string expressionText)
    {
        expressionText = null!;

        if (invocation.TargetMethod.Parameters.Length == 0)
            return false;

        if (invocation.TargetMethod.Parameters[0].Type is INamedTypeSymbol firstParameterType
            && firstParameterType.Name == "ProcessEntityCreate")
        {
            var createArgument = GetArgument(invocation, invocation.TargetMethod.Parameters[0].Name ?? "create", position: 0);
            if (createArgument?.Expression is not { } createExpression)
                return false;

            expressionText = RewriteExpression(createExpression, semanticModel, boundBindings);
            return true;
        }

        var entityArgument = GetArgument(invocation, "entity", position: 0);
        var entityIdArgument = GetArgument(invocation, "entityId", position: 1);
        if (entityArgument?.Expression is not { } entityExpression
            || entityIdArgument?.Expression is not { } entityIdExpression)
        {
            return false;
        }

        var loweredEntityText = RewriteExpression(entityExpression, semanticModel, boundBindings);
        var loweredEntityIdText = RewriteExpression(entityIdExpression, semanticModel, boundBindings);
        var loweredStateObjectText = GetArgument(invocation, "stateObject", position: 2)?.Expression is { } stateObjectExpression
            ? RewriteExpression(stateObjectExpression, semanticModel, boundBindings)
            : "null";
        var loweredPartitionKeyText = GetArgument(invocation, "partitionKey", position: 3)?.Expression is { } partitionKeyExpression
            ? RewriteExpression(partitionKeyExpression, semanticModel, boundBindings)
            : "null";
        var loweredVersionText = GetArgument(invocation, "version", position: 4)?.Expression is { } versionExpression
            ? RewriteExpression(versionExpression, semanticModel, boundBindings)
            : "0";

        if (GetArgument(invocation, "project", position: 3)?.Expression is { } projectExpression)
        {
            expressionText = $"{loweredEntityText}.Create(entityId: {loweredEntityIdText}, stateObject: {loweredStateObjectText}, project: {RewriteExpression(projectExpression, semanticModel, boundBindings)}, partitionKey: {loweredPartitionKeyText}, version: {loweredVersionText})";
            return true;
        }

        expressionText = $"global::Cohesive.Processes.Model.ProcessEntityCreate.Create(entity: {loweredEntityText}, entityId: {loweredEntityIdText}, stateObject: {loweredStateObjectText}, partitionKey: {loweredPartitionKeyText}, version: {loweredVersionText})";
        return true;
    }

    static bool TryLowerTransitionInvocation(
        IInvocationOperation invocation,
        SemanticModel semanticModel,
        Dictionary<ISymbol, BoundBinding> boundBindings,
        out string expressionText)
    {
        expressionText = null!;

        if (invocation.TargetMethod.Parameters.Length == 0)
            return false;

        if (invocation.TargetMethod.Parameters[0].Type is INamedTypeSymbol firstParameterType
            && firstParameterType.Name == "ProcessEntityTransitionInvocation")
        {
            var transitionArgument = GetArgument(invocation, invocation.TargetMethod.Parameters[0].Name ?? "transition", position: 0);
            if (transitionArgument?.Expression is not { } transitionExpression)
                return false;

            expressionText = RewriteExpression(transitionExpression, semanticModel, boundBindings);
            return true;
        }

        var entityIdArgument = GetArgument(invocation, "entityId", position: 0);
        var transitionArgumentDirect = GetArgument(invocation, "transition", position: 1);
        var inputArgument = GetArgument(invocation, "input", position: 2);
        if (entityIdArgument?.Expression is not { } entityIdExpression
            || transitionArgumentDirect?.Expression is not { } transitionExpressionDirect
            || inputArgument?.Expression is not { } inputExpression)
        {
            return false;
        }

        var loweredEntityIdText = RewriteExpression(entityIdExpression, semanticModel, boundBindings);
        var loweredTransitionText = RewriteExpression(transitionExpressionDirect, semanticModel, boundBindings);
        var loweredInputText = RewriteExpression(inputExpression, semanticModel, boundBindings);
        var loweredPartitionKeyText = GetArgument(invocation, "partitionKey", position: GetParameterPosition(invocation, "partitionKey"))?.Expression is { } partitionKeyExpression
            ? RewriteExpression(partitionKeyExpression, semanticModel, boundBindings)
            : "null";
        var loweredEffectSchedulingText = GetArgument(invocation, "effectScheduling", position: GetParameterPosition(invocation, "effectScheduling"))?.Expression is { } effectSchedulingExpression
            ? RewriteExpression(effectSchedulingExpression, semanticModel, boundBindings)
            : "global::Cohesive.Processes.Model.ProcessEffectSchedulingMode.AutoDispatch";

        expressionText =
            $"global::Cohesive.Processes.Model.ProcessEntityTransition.For(entityId: {loweredEntityIdText}, transition: {loweredTransitionText}, input: {loweredInputText}, partitionKey: {loweredPartitionKeyText}, effectScheduling: {loweredEffectSchedulingText})";
        return true;
    }

    static bool TryLowerTimerInvocation(
        IInvocationOperation invocation,
        SemanticModel semanticModel,
        Dictionary<ISymbol, BoundBinding> boundBindings,
        out string keyExpressionText,
        out string timeoutExpressionText)
    {
        keyExpressionText = null!;
        timeoutExpressionText = null!;

        var delayArgument = GetArgument(invocation, "delay", position: 0);
        if (delayArgument?.Expression is not { } delayExpression)
            return false;

        timeoutExpressionText = RewriteExpression(delayExpression, semanticModel, boundBindings);
        keyExpressionText = GetArgument(invocation, "key", position: 1)?.Expression is { } keyExpression
            ? RewriteExpression(keyExpression, semanticModel, boundBindings)
            : null!;
        return true;
    }

    static bool TryLowerPollInvocation(
        IInvocationOperation invocation,
        SemanticModel semanticModel,
        Dictionary<ISymbol, BoundBinding> boundBindings,
        out string requestExpressionText,
        out string predicateExpressionText,
        out string intervalExpressionText,
        out string timeoutExpressionText,
        out string timeoutResultExpressionText)
    {
        requestExpressionText = null!;
        predicateExpressionText = null!;
        intervalExpressionText = null!;
        timeoutExpressionText = null!;
        timeoutResultExpressionText = null!;

        var requestArgument = GetArgument(invocation, "request", position: 0);
        var predicateArgument = GetArgument(invocation, "isCompleted", position: 1);
        var intervalArgument = GetArgument(invocation, "interval", position: 2);
        var timeoutArgument = GetArgument(invocation, "timeout", position: 3);
        var timeoutResultArgument = GetArgument(invocation, "timeoutResult", position: 4);
        if (requestArgument?.Expression is not { } requestExpression
            || predicateArgument?.Expression is not { } predicateExpression
            || intervalArgument?.Expression is not { } intervalExpression
            || timeoutArgument?.Expression is not { } timeoutExpression
            || timeoutResultArgument?.Expression is not { } timeoutResultExpression)
        {
            return false;
        }

        requestExpressionText = RewriteExpression(requestExpression, semanticModel, boundBindings);
        predicateExpressionText = RewritePollPredicateExpression(predicateExpression, semanticModel, boundBindings);
        intervalExpressionText = RewriteExpression(intervalExpression, semanticModel, boundBindings);
        timeoutExpressionText = RewriteExpression(timeoutExpression, semanticModel, boundBindings);
        timeoutResultExpressionText = RewriteExpression(timeoutResultExpression, semanticModel, boundBindings);
        return true;
    }

    static string RewritePollPredicateExpression(
        ExpressionSyntax expression,
        SemanticModel semanticModel,
        Dictionary<ISymbol, BoundBinding> boundBindings)
    {
        var nullSuppressedSymbols = expression.DescendantNodesAndSelf()
            .OfType<ParameterSyntax>()
            .Select(parameter => semanticModel.GetDeclaredSymbol(parameter))
            .OfType<ISymbol>();

        return RewriteExpression(expression, semanticModel, boundBindings, nullSuppressedSymbols);
    }

    static bool ContainsBoundSymbolReference(
        SyntaxNode syntaxNode,
        SemanticModel semanticModel,
        IEnumerable<ISymbol> boundSymbols)
    {
        var boundSymbolSet = new HashSet<ISymbol>(boundSymbols, SymbolEqualityComparer.Default);
        return syntaxNode.DescendantNodesAndSelf()
            .OfType<IdentifierNameSyntax>()
            .Select(identifier => semanticModel.GetSymbolInfo(identifier).Symbol)
            .Any(symbol => symbol is not null && boundSymbolSet.Contains(symbol));
    }

    // static string GetAccessibilitySource(Accessibility accessibility) => accessibility switch
    // {
    //     Accessibility.Public => "public",
    //     Accessibility.Internal => "internal",
    //     Accessibility.Private => "private",
    //     Accessibility.Protected => "protected",
    //     Accessibility.ProtectedOrInternal => "protected internal",
    //     Accessibility.ProtectedAndInternal => "private protected",
    //     _ => "private"
    // };

    static void Report(
        SourceProductionContext productionContext,
        DiagnosticDescriptor descriptor,
        ISymbol symbol,
        params object[] args)
    {
        productionContext.ReportDiagnostic(Diagnostic.Create(
            descriptor: descriptor,
            location: symbol.Locations.FirstOrDefault(),
            messageArgs: args));
    }

    sealed record AuthoringTypeCandidate(ClassDeclarationSyntax Syntax, INamedTypeSymbol Symbol);

    sealed record ProcessAuthoringModel(
        ImmutableArray<string> UsingDirectives,
        INamespaceSymbol NamespaceSymbol,
        INamedTypeSymbol ContainingType,
        string ContainingTypeDeclaration,
        string GeneratedMethodAccessibility,
        string GeneratedMethodName,
        string GeneratedMethodParameters,
        string DefaultProcessNameLiteral,
        bool GenerateTypedDefineMethod,
        string TypedDefineMethodParameters,
        string TypedDefineInvocationArguments,
        string InputTypeName,
        string ResultTypeName,
        string? TypedInputParameterName,
        bool CacheDefaultDefinition,
        string DefaultDefinitionFieldName,
        ImmutableArray<string> GeneratedLines);

    sealed record LoweredBlockModel(
        string? EntryNodeText,
        ImmutableArray<FlowNodeModel> Nodes);

    abstract record FlowNodeModel(string NodeNameText);

    sealed record AwaitFlowNodeModel(
        AwaitStepModel AwaitStep,
        string NextNodeText) : FlowNodeModel(AwaitStep.NodeNameText);

    sealed record BranchFlowNodeModel(
        string NodeNameText,
        string ConditionExpressionText,
        string TrueNodeText,
        string FalseNodeText) : FlowNodeModel(NodeNameText);

    sealed record ReturnFlowNodeModel(
        ReturnStepModel ReturnStep) : FlowNodeModel(ReturnStep.NodeNameText);

    sealed record AwaitStepModel(
        AwaitStepKind Kind,
        string ResultTypeName,
        string NodeNameText,
        string ResultNameText,
        string ExpressionText,
        string? ContinuationExpressionText,
        string? KeyExpressionText = null,
        string? TimeoutExpressionText = null,
        string? PredicateExpressionText = null,
        string? IntervalExpressionText = null,
        string? TimeoutResultExpressionText = null
        );

    sealed record ReturnStepModel(
        string NodeNameText,
        string ResultExpressionText);

    sealed class LoweringState
    {
        public int NextBranchIndex { get; set; }
        public int NextReturnIndex { get; set; }
    }

    enum AwaitStepKind
    {
        Request = 0,
        Read = 1,
        Create = 2,
        Query = 3,
        Compute = 4,
        Transition = 5,
        TransitionMany = 6,
        Timer = 7,
        Poll = 8
    }

    enum BoundBindingKind
    {
        Parameter,
        Variable
    }

    sealed record BoundBinding(
        BoundBindingKind Kind,
        string TypeName,
        string NameExpressionText,
        bool NeedsNullSuppression
        )
    {
        public string BuildAccessExpression(string contextIdentifier) => Kind switch
        {
            BoundBindingKind.Parameter => AppendNullSuppression($"{contextIdentifier}.RequireParameter<{TypeName}>({NameExpressionText})"),
            BoundBindingKind.Variable => AppendNullSuppression($"{contextIdentifier}.RequireVariable<{TypeName}>({NameExpressionText})"),
            _ => throw new InvalidOperationException($"Unsupported binding kind '{Kind}'.")
        };

        string AppendNullSuppression(string expression) => NeedsNullSuppression
            ? $"{expression}!"
            : expression;
    }

    sealed class BoundSymbolAccessRewriter(
        SemanticModel semanticModel,
        Dictionary<ISymbol, BoundBinding> boundBindings,
        HashSet<ISymbol>? nullSuppressedSymbols = null
        ) : CSharpSyntaxRewriter
    {
        public ExpressionSyntax Rewrite(ExpressionSyntax expression) =>
            (ExpressionSyntax)Visit(expression)!;

        public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
        {
            if (IsInsideNameOf(node))
                return base.VisitIdentifierName(node);

            if (semanticModel.GetSymbolInfo(node).Symbol is not { } symbol)
                return base.VisitIdentifierName(node);

            if (boundBindings.TryGetValue(symbol, out var binding))
            {
                return SyntaxFactory.ParseExpression(binding.BuildAccessExpression("context"))
                    .WithTriviaFrom(node);
            }

            if (nullSuppressedSymbols?.Contains(symbol) == true)
            {
                return SyntaxFactory.PostfixUnaryExpression(
                        SyntaxKind.SuppressNullableWarningExpression,
                        node.WithoutTrivia())
                    .WithTriviaFrom(node);
            }

            return base.VisitIdentifierName(node);
        }

        static bool IsInsideNameOf(SyntaxNode node)
        {
            for (var current = node.Parent; current is not null; current = current.Parent)
            {
                if (current is InvocationExpressionSyntax invocation
                    && invocation.Expression is IdentifierNameSyntax identifier
                    && identifier.Identifier.ValueText == "nameof")
                {
                    return true;
                }

                if (current is AnonymousFunctionExpressionSyntax)
                    return false;
            }

            return false;
        }
    }
}

static class SymbolExtensions
{
    public static string GetFullyQualifiedMetadataName(this ISymbol symbol)
    {
        if (symbol is null)
            throw new ArgumentNullException(nameof(symbol));

        var parts = new Stack<string>();
        var current = symbol;
        while (current is not null && !IsRootNamespace(current))
        {
            parts.Push(current.MetadataName);
            current = current.ContainingSymbol;
        }

        return string.Join(".", parts);
    }

    static bool IsRootNamespace(ISymbol symbol) =>
        symbol is INamespaceSymbol namespaceSymbol && namespaceSymbol.IsGlobalNamespace;
}
