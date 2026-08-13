using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;

namespace Cohesive.Analyzers;

/// <summary>Lowers syntax-only C# Process computations into canonical Process builder calls.</summary>
[Generator]
public sealed class ProcessComputationSourceGenerator : IIncrementalGenerator
{
    const string AttributeName = "Cohesive.Processes.Authoring.GenerateProcessDefinitionAttribute";
    const string ContextName = "Cohesive.Processes.Authoring.ProcessContext";
    const string TaskName = "Cohesive.Processes.Authoring.ProcessTask<TResult>";
    const string BranchTaskName = "Cohesive.Processes.Authoring.ProcessTask";

    static readonly SymbolDisplayFormat FullyQualifiedNullableFormat =
        SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(
            SymbolDisplayFormat.FullyQualifiedFormat.MiscellaneousOptions
            | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    static readonly DiagnosticDescriptor ContainingTypeMustBePartial = new(
        id: "COHPC001",
        title: "Process computation container must be partial",
        messageFormat: "Type '{0}' is marked with [GenerateProcessDefinition] and must be declared partial.",
        category: "Cohesive.Processes",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    static readonly DiagnosticDescriptor UnsupportedMethodShape = new(
        id: "COHPC002",
        title: "Unsupported Process computation method shape",
        messageFormat: "Process computation method '{0}' has an unsupported shape: {1}.",
        category: "Cohesive.Processes",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    static readonly DiagnosticDescriptor UnsupportedStatement = new(
        id: "COHPC003",
        title: "Unsupported Process computation statement",
        messageFormat: "Process computation method '{0}' contains an unsupported statement: {1}.",
        category: "Cohesive.Processes",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    static readonly DiagnosticDescriptor UnsupportedPureExpression = new(
        id: "COHPC004",
        title: "Expression is outside the portable Process subset",
        messageFormat: "Process computation method '{0}' contains a pure expression that cannot be fused into canonical IR: {1}.",
        category: "Cohesive.Processes",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    static readonly DiagnosticDescriptor AuthoringMethodNotFound = new(
        id: "COHPC005",
        title: "Process computation method was not found",
        messageFormat: "Type '{0}' references Process computation method '{1}', but no unique source-declared method with that name exists.",
        category: "Cohesive.Processes",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    static readonly DiagnosticDescriptor GeneratedMemberConflict = new(
        id: "COHPC006",
        title: "Generated Process definition member conflicts",
        messageFormat: "Type '{0}' already declares a member named 'Define'; the Process computation generator cannot emit its factory.",
        category: "Cohesive.Processes",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    static readonly DiagnosticDescriptor DuplicateExactIdentity = new(
        id: "COHPC007",
        title: "Process computation identity is duplicated",
        messageFormat: "Process computation method '{0}' declares exact node identity '{1}' more than once.",
        category: "Cohesive.Processes",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var candidates = context.SyntaxProvider.ForAttributeWithMetadataName(
                fullyQualifiedMetadataName: AttributeName,
                predicate: static (node, _) => node is ClassDeclarationSyntax,
                transform: static (attributeContext, _) => new Candidate(
                    (ClassDeclarationSyntax)attributeContext.TargetNode,
                    (INamedTypeSymbol)attributeContext.TargetSymbol))
            .Collect();

        context.RegisterSourceOutput(
            context.CompilationProvider.Combine(candidates),
            static (productionContext, pair) => Generate(pair.Left, pair.Right, productionContext));
    }

    static void Generate(
        Compilation compilation,
        ImmutableArray<Candidate> candidates,
        SourceProductionContext productionContext)
    {
        HashSet<INamedTypeSymbol> seen = new(SymbolEqualityComparer.Default);
        foreach (var candidate in candidates)
        {
            if (!seen.Add(candidate.Symbol))
            {
                continue;
            }

            GenerateCandidate(compilation, candidate, productionContext);
        }
    }

    static void GenerateCandidate(
        Compilation compilation,
        Candidate candidate,
        SourceProductionContext productionContext)
    {
        if (!candidate.Syntax.Modifiers.Any(static modifier => modifier.IsKind(SyntaxKind.PartialKeyword)))
        {
            Report(
                productionContext,
                ContainingTypeMustBePartial,
                candidate.Syntax.Identifier.GetLocation(),
                candidate.Symbol.ToDisplayString());
            return;
        }

        if (candidate.Symbol.ContainingType is not null || candidate.Symbol.TypeParameters.Length != 0)
        {
            Report(
                productionContext,
                UnsupportedMethodShape,
                candidate.Syntax.Identifier.GetLocation(),
                candidate.Symbol.Name,
                "the annotated type must be top-level and non-generic");
            return;
        }

        if (candidate.Symbol.GetMembers("Define").Length != 0)
        {
            Report(
                productionContext,
                GeneratedMemberConflict,
                candidate.Syntax.Identifier.GetLocation(),
                candidate.Symbol.ToDisplayString());
            return;
        }

        var attribute = candidate.Symbol.GetAttributes()
            .Single(static item => item.AttributeClass?.ToDisplayString() == AttributeName);
        var methodName = attribute.ConstructorArguments.Length == 1
            ? attribute.ConstructorArguments[0].Value as string
            : null;
        var methods = string.IsNullOrWhiteSpace(methodName)
            ? []
            : candidate.Symbol.GetMembers(methodName!).OfType<IMethodSymbol>()
                .Where(static method => method.DeclaringSyntaxReferences.Length == 1)
                .ToArray();
        if (methods.Length != 1
            || methods[0].DeclaringSyntaxReferences[0].GetSyntax() is not MethodDeclarationSyntax methodSyntax)
        {
            Report(
                productionContext,
                AuthoringMethodNotFound,
                candidate.Syntax.Identifier.GetLocation(),
                candidate.Symbol.ToDisplayString(),
                methodName ?? "<unknown>");
            return;
        }

        var method = methods[0];
        if (!TryValidateMethod(method, methodSyntax, productionContext, out var inputParameter, out var resultType))
        {
            return;
        }

        var semanticModel = compilation.GetSemanticModel(methodSyntax.SyntaxTree);
        var parser = new FlowParser(
            productionContext,
            semanticModel,
            method,
            method.Parameters[0],
            inputParameter);
        if (!parser.TryParse(methodSyntax.Body!.Statements, out var body))
        {
            return;
        }

        var emitter = new DefinitionEmitter(
            productionContext,
            method,
            inputParameter,
            resultType,
            parser.PureLocals,
            parser.ForkResultTuples,
            parser.Awaits,
            parser.AuthoredOutputs,
            parser.PatternOutputs,
            parser.RequestObligations);
        if (!emitter.TryEmit(body, out var generatedBody))
        {
            return;
        }

        var source = EmitSource(candidate, methodSyntax, inputParameter.Type, resultType, generatedBody);
        productionContext.AddSource(
            hintName: $"{candidate.Symbol.Name}.Define.ProcessComputation.g.cs",
            sourceText: SourceText.From(source, Encoding.UTF8));
    }

    static bool TryValidateMethod(
        IMethodSymbol method,
        MethodDeclarationSyntax syntax,
        SourceProductionContext productionContext,
        out IParameterSymbol inputParameter,
        out ITypeSymbol resultType)
    {
        inputParameter = null!;
        resultType = null!;
        string? failure = null;
        if (!method.IsStatic)
        {
            failure = "the method must be static";
        }
        else if (!syntax.Modifiers.Any(static modifier => modifier.IsKind(SyntaxKind.AsyncKeyword)))
        {
            failure = "the method must be async";
        }
        else if (syntax.Body is null || syntax.ExpressionBody is not null)
        {
            failure = "the method must have a block body";
        }
        else if (method.Parameters.Length != 2
                 || method.Parameters[0].Type.ToDisplayString() != ContextName)
        {
            failure = "the parameters must be '(ProcessContext process, TInput input)'";
        }
        else if (method.ReturnType is not INamedTypeSymbol { IsGenericType: true } task
                 || task.ConstructedFrom.ToDisplayString() != TaskName)
        {
            failure = "the return type must be ProcessTask<TResult>";
        }
        else
        {
            inputParameter = method.Parameters[1];
            resultType = ((INamedTypeSymbol)method.ReturnType).TypeArguments[0];
        }

        if (failure is null)
        {
            return true;
        }

        Report(
            productionContext,
            UnsupportedMethodShape,
            syntax.Identifier.GetLocation(),
            method.Name,
            failure);
        return false;
    }

    static string EmitSource(
        Candidate candidate,
        MethodDeclarationSyntax method,
        ITypeSymbol inputType,
        ITypeSymbol resultType,
        GeneratedDefinition body)
    {
        var builder = new StringBuilder();
        builder.AppendLine("// <auto-generated />");
        builder.AppendLine("#nullable enable");
        foreach (var usingDirective in method.SyntaxTree.GetCompilationUnitRoot().Usings)
        {
            builder.AppendLine(usingDirective.NormalizeWhitespace().ToFullString());
        }

        if (!candidate.Symbol.ContainingNamespace.IsGlobalNamespace)
        {
            builder.Append("namespace ")
                .Append(candidate.Symbol.ContainingNamespace.ToDisplayString())
                .AppendLine(";")
                .AppendLine();
        }

        if (candidate.Symbol.IsStatic)
        {
            builder.Append("static ");
        }

        builder.Append("partial class ").Append(candidate.Symbol.Name).AppendLine()
            .AppendLine("{")
            .Append("    public static global::Cohesive.Processes.Authoring.Process<")
            .Append(FormatType(inputType)).Append(", ").Append(FormatType(resultType))
            .AppendLine("> Define(global::Cohesive.Processes.Authoring.ProcessAuthoringMetadata metadata)")
            .AppendLine("    {")
            .AppendLine("        global::System.ArgumentNullException.ThrowIfNull(metadata);");

        foreach (var identity in body.IdentityDeclarations)
        {
            builder.Append("        ").AppendLine(identity);
        }

        builder.Append("        var __metadata = metadata.WithEntry(")
            .Append(body.EntryIdentity).AppendLine(");")
            .Append("        return global::Cohesive.Processes.Authoring.ProcessAuthoring.Create<")
            .Append(FormatType(inputType)).Append(", ").Append(FormatType(resultType)).AppendLine(">(")
            .AppendLine("            metadata: __metadata,")
            .AppendLine("            configure: __builder =>")
            .AppendLine("            {");

        foreach (var output in body.OutputDeclarations)
        {
            builder.Append("                ").AppendLine(output);
        }

        if (body.OutputDeclarations.Length != 0)
        {
            builder.AppendLine();
        }

        builder.AppendLine("                var __typeMapper = new global::Cohesive.Model.Authoring.DefaultClrTypeRefMapper();");
        builder.AppendLine();
        foreach (var line in body.BuilderStatements)
        {
            builder.Append("                ").AppendLine(line);
        }

        var location = SourceLocation(method);
        builder.AppendLine("            },")
            .Append("            sourceFile: ").Append(Literal(location.File)).AppendLine(",")
            .Append("            sourceLine: ").Append(location.Line.ToString(CultureInfo.InvariantCulture)).AppendLine(",")
            .Append("            sourceMember: ").Append(Literal(method.Identifier.ValueText)).AppendLine(");")
            .AppendLine("    }")
            .AppendLine("}");
        return builder.ToString();
    }

    static string FormatType(ITypeSymbol type) => type.ToDisplayString(FullyQualifiedNullableFormat);

    static string Literal(string value) => SymbolDisplay.FormatLiteral(value, quote: true);

    static string? SerializedName(ISymbol? member)
    {
        if (member is null)
        {
            return null;
        }

        var attribute = member.GetAttributes().FirstOrDefault(static item =>
            item.AttributeClass?.ToDisplayString() == "System.Text.Json.Serialization.JsonPropertyNameAttribute");
        return attribute?.ConstructorArguments.Length == 1
            ? attribute.ConstructorArguments[0].Value as string
            : member.Name;
    }

    static SourceReference SourceLocation(SyntaxNode syntax)
    {
        var span = syntax.GetLocation().GetLineSpan();
        return new(
            span.Path ?? string.Empty,
            span.StartLinePosition.Line + 1);
    }

    static void Report(
        SourceProductionContext context,
        DiagnosticDescriptor descriptor,
        Location location,
        params object[] arguments) =>
        context.ReportDiagnostic(Diagnostic.Create(descriptor, location, arguments));

    readonly record struct Candidate(ClassDeclarationSyntax Syntax, INamedTypeSymbol Symbol);

    readonly record struct SourceReference(string File, int Line);

    sealed class FlowParser
    {
        readonly SourceProductionContext productionContext;
        readonly SemanticModel semanticModel;
        readonly IMethodSymbol method;
        readonly IParameterSymbol contextParameter;
        readonly IParameterSymbol inputParameter;
        readonly Dictionary<ILocalSymbol, IOperation> pureLocals = new(SymbolEqualityComparer.Default);
        readonly Dictionary<ILocalSymbol, ImmutableArray<IOperation>> forkResultTuples = new(SymbolEqualityComparer.Default);
        readonly Dictionary<IMethodSymbol, LocalFunctionStatementSyntax> localFunctions = new(SymbolEqualityComparer.Default);
        readonly List<AwaitFlow> awaits = [];
        readonly List<AuthoredOutput> authoredOutputs = [];
        readonly List<PatternOutput> patternOutputs = [];
        readonly List<BranchObligation> requestObligations = [];
        readonly Dictionary<string, int> semanticRoleOrdinals = new(StringComparer.Ordinal);
        int variableOrdinal;

        public FlowParser(
            SourceProductionContext productionContext,
            SemanticModel semanticModel,
            IMethodSymbol method,
            IParameterSymbol contextParameter,
            IParameterSymbol inputParameter)
        {
            this.productionContext = productionContext;
            this.semanticModel = semanticModel;
            this.method = method;
            this.contextParameter = contextParameter;
            this.inputParameter = inputParameter;
        }

        public IReadOnlyDictionary<ILocalSymbol, IOperation> PureLocals => pureLocals;

        public IReadOnlyDictionary<ILocalSymbol, ImmutableArray<IOperation>> ForkResultTuples => forkResultTuples;

        public IReadOnlyList<AwaitFlow> Awaits => awaits;

        public IReadOnlyList<AuthoredOutput> AuthoredOutputs => authoredOutputs;

        public IReadOnlyList<PatternOutput> PatternOutputs => patternOutputs;

        public IReadOnlyList<BranchObligation> RequestObligations => requestObligations;

        public bool TryParse(SyntaxList<StatementSyntax> statements, out FlowBlock block)
        {
            foreach (var localFunction in statements.SelectMany(static statement =>
                         statement.DescendantNodesAndSelf().OfType<LocalFunctionStatementSyntax>()))
            {
                if (semanticModel.GetDeclaredSymbol(localFunction) is IMethodSymbol symbol)
                {
                    localFunctions[symbol] = localFunction;
                }
            }

            return TryParse(statements, ImmutableArray<string>.Empty, out block);
        }

        bool TryParse(
            SyntaxList<StatementSyntax> statements,
            ImmutableArray<string> structuralPath,
            out FlowBlock block,
            bool ignoreTerminalBreak = false,
            bool allowTrailingBareReturn = false)
        {
            List<FlowStatement> flows = [];
            var terminalObserved = false;
            var statementCount = ignoreTerminalBreak
                                 && statements.Count != 0
                                 && statements[statements.Count - 1] is BreakStatementSyntax
                ? statements.Count - 1
                : statements.Count;
            for (var statementIndex = 0; statementIndex < statementCount; statementIndex++)
            {
                var statement = statements[statementIndex];
                if (statement is LocalFunctionStatementSyntax)
                {
                    continue;
                }

                if (terminalObserved)
                {
                    return Failure(
                        statement,
                        "statements after an unconditional return are not supported",
                        out block);
                }

                switch (statement)
                {
                    case LocalDeclarationStatementSyntax localDeclaration:
                        if (TryGetTypedAwaitMatchInvocation(
                                localDeclaration,
                                out var awaitResult,
                                out var typedAwaitInvocation))
                        {
                            var selectionIndex = statementIndex + 1;
                            while (selectionIndex < statements.Count
                                   && statements[selectionIndex] is LocalFunctionStatementSyntax)
                            {
                                selectionIndex++;
                            }
                            if (selectionIndex >= statements.Count
                                || statements[selectionIndex] is not SwitchStatementSyntax selection)
                            {
                                return Failure(
                                    localDeclaration,
                                    "a typed AwaitMatch result must be consumed by an immediately following exhaustive type switch",
                                    out block);
                            }
                            if (!TryParseTypedAwaitMatch(
                                    localDeclaration,
                                    awaitResult,
                                    selection,
                                    structuralPath,
                                    typedAwaitInvocation,
                                    out var typedAwaitMatch))
                            {
                                block = null!;
                                return false;
                            }

                            flows.Add(typedAwaitMatch);
                            statementIndex = selectionIndex;
                            break;
                        }

                        if (TryGetForkJoinInvocation(localDeclaration, out _))
                        {
                            if (!TryParseForkJoin(localDeclaration, structuralPath, out var localForkJoin))
                            {
                                block = null!;
                                return false;
                            }
                            flows.Add(localForkJoin);
                            break;
                        }

                        if (!TryParseLocal(localDeclaration, structuralPath, out var flow))
                        {
                            block = null!;
                            return false;
                        }
                        if (flow is not null)
                        {
                            flows.Add(flow);
                        }

                        break;

                    case ReturnStatementSyntax returned when returned.Expression is not null:
                        var returnedOperation = semanticModel.GetOperation(returned.Expression);
                        if (returnedOperation is null)
                        {
                            return Failure(returned, "return expression could not be analyzed", out block);
                        }

                        var terminalKind = TerminalAuthoringKind.Return;
                        IInvocationOperation? terminalInvocation = null;
                        var terminalResult = returnedOperation;
                        var terminalRole = "return";
                        var strippedReturn = Strip(returnedOperation);
                        if (strippedReturn is IInvocationOperation unreachable
                            && unreachable.TargetMethod.Name == "Unreachable"
                            && SymbolEqualityComparer.Default.Equals(unreachable.TargetMethod.ContainingType, contextParameter.Type)
                            && unreachable.Instance is not null
                            && Strip(unreachable.Instance) is IParameterReferenceOperation unreachableContext
                            && SymbolEqualityComparer.Default.Equals(unreachableContext.Parameter, contextParameter))
                        {
                            terminalObserved = true;
                            break;
                        }
                        if (strippedReturn is IInvocationOperation candidate
                            && candidate.TargetMethod.Name is "Complete" or "Fail"
                            && SymbolEqualityComparer.Default.Equals(candidate.TargetMethod.ContainingType, contextParameter.Type)
                            && candidate.Instance is not null
                            && Strip(candidate.Instance) is IParameterReferenceOperation contextReference
                            && SymbolEqualityComparer.Default.Equals(contextReference.Parameter, contextParameter))
                        {
                            var result = Argument(candidate, "result");
                            if (result is null)
                            {
                                return Failure(returned, "an explicit Process terminal requires one portable result", out block);
                            }

                            terminalKind = candidate.TargetMethod.Name == "Fail"
                                ? TerminalAuthoringKind.Fail
                                : TerminalAuthoringKind.Return;
                            terminalInvocation = candidate;
                            terminalResult = result.Value;
                            terminalRole = candidate.TargetMethod.Name.ToLowerInvariant();
                        }

                        flows.Add(new ReturnFlow(
                            NextIdentity(terminalRole, structuralPath),
                            terminalResult,
                            terminalKind,
                            terminalInvocation,
                            SourceLocation(returned),
                            returned));
                        terminalObserved = true;
                        break;

                    case ReturnStatementSyntax returned:
                        if (!allowTrailingBareReturn)
                        {
                            return Failure(
                                returned,
                                "a bare return is supported only as the trailing statement of an untyped local ProcessTask branch",
                                out block);
                        }

                        // Falling through and an explicit trailing return are the same untyped branch completion.
                        // Retain no IR node so syntax normalization cannot change the canonical Process definition.
                        terminalObserved = true;
                        break;

                    case IfStatementSyntax conditional:
                        if (!TryParseIf(conditional, structuralPath, out var branch))
                        {
                            block = null!;
                            return false;
                        }
                        flows.Add(branch);
                        break;

                    case SwitchStatementSyntax match:
                        if (!TryParseSwitch(match, structuralPath, out var switchFlow))
                        {
                            block = null!;
                            return false;
                        }
                        flows.Add(switchFlow);
                        break;

                    case ExpressionStatementSyntax expressionStatement:
                        if (!TryParseExpression(expressionStatement, structuralPath, out var expressionFlow))
                        {
                            block = null!;
                            return false;
                        }
                        flows.Add(expressionFlow);
                        terminalObserved = expressionFlow is ActionFlow
                        {
                            Kind: ActionKind.Succeed or ActionKind.Terminate or ActionKind.ContinueAt
                        };
                        break;

                    case BlockSyntax nested:
                        if (!TryParse(nested.Statements, structuralPath.Add("block"), out var nestedBlock))
                        {
                            block = null!;
                            return false;
                        }
                        flows.AddRange(nestedBlock.Statements);
                        break;

                    case EmptyStatementSyntax:
                        break;

                    case WhileStatementSyntax:
                    case DoStatementSyntax:
                    case ForStatementSyntax:
                    case ForEachStatementSyntax:
                    case ForEachVariableStatementSyntax:
                        return Failure(
                            statement,
                            "host loops are not supported; use RepeatAcrossActivation with explicit finite occurrence and unchanged-progress limits",
                            out block);

                    default:
                        return Failure(
                            statement,
                            "only pure local declarations, awaited Process operations, fork/join, if/else, exact switch, nested blocks, and supported Process return forms are supported",
                            out block);
                }
            }

            block = new([.. flows]);
            return true;
        }

        bool TryGetTypedAwaitMatchInvocation(
            LocalDeclarationStatementSyntax declaration,
            out ILocalSymbol result,
            out IInvocationOperation invocation)
        {
            result = null!;
            invocation = null!;
            if (declaration.Declaration.Variables.Count != 1)
            {
                return false;
            }

            var declarator = declaration.Declaration.Variables[0];
            if (declarator.Initializer is not { Value: { } initializer }
                || semanticModel.GetDeclaredSymbol(declarator) is not ILocalSymbol local
                || semanticModel.GetOperation(initializer) is not IAwaitOperation awaited
                || Strip(awaited.Operation) is not IInvocationOperation candidate
                || candidate.TargetMethod.Name != "AwaitMatch"
                || !candidate.TargetMethod.IsGenericMethod
                || !SymbolEqualityComparer.Default.Equals(candidate.TargetMethod.ContainingType, contextParameter.Type)
                || candidate.Instance is null
                || Strip(candidate.Instance) is not IParameterReferenceOperation contextReference
                || !SymbolEqualityComparer.Default.Equals(contextReference.Parameter, contextParameter))
            {
                return false;
            }

            result = local;
            invocation = candidate;
            return true;
        }

        bool TryParseLocal(
            LocalDeclarationStatementSyntax declaration,
            ImmutableArray<string> structuralPath,
            out FlowStatement? flow)
        {
            flow = null;
            if (declaration.Declaration.Variables.Count != 1)
            {
                return StatementFailure(declaration, "a local declaration must declare exactly one value");
            }

            var declarator = declaration.Declaration.Variables[0];
            if (declarator.Initializer is null
                || semanticModel.GetDeclaredSymbol(declarator) is not ILocalSymbol local
                || semanticModel.GetOperation(declarator.Initializer.Value) is not { } operation)
            {
                return StatementFailure(declaration, "a local declaration requires an analyzable initializer");
            }

            operation = Strip(operation);
            if (operation is not IAwaitOperation awaited)
            {
                pureLocals.Add(local, operation);
                return true;
            }

            var awaitedOperation = Strip(awaited.Operation);
            if (awaitedOperation is not IInvocationOperation invocation
                || !SymbolEqualityComparer.Default.Equals(invocation.TargetMethod.ContainingType, contextParameter.Type)
                || invocation.Instance is null
                || Strip(invocation.Instance) is not IParameterReferenceOperation contextReference
                || !SymbolEqualityComparer.Default.Equals(contextReference.Parameter, contextParameter))
            {
                return StatementFailure(
                    declaration,
                    "await is reserved for Query, Read, Transition, and Effect operations on the ProcessContext parameter");
            }

            if (invocation.TargetMethod.Name == "RepeatAcrossActivation")
            {
                if (!TryParseRecurrence(declaration, local, structuralPath, invocation, out var recurrence))
                {
                    return false;
                }

                flow = recurrence;
                return true;
            }

            var kind = invocation.TargetMethod.Name switch
            {
                "Query" => AwaitKind.Query,
                "Read" => AwaitKind.Read,
                "Transition" => AwaitKind.Transition,
                "Effect" => AwaitKind.Effect,
                _ => AwaitKind.Unsupported
            };
            if (kind == AwaitKind.Unsupported)
            {
                return StatementFailure(declaration, $"awaited Process operation '{invocation.TargetMethod.Name}' is not supported");
            }

            var authored = new AwaitFlow(
                NextIdentity(kind == AwaitKind.Read ? "read" : invocation.TargetMethod.Name.ToLowerInvariant(), structuralPath, local.Name),
                local,
                kind,
                invocation,
                SourceLocation(declaration),
                declaration,
                $"__output_{awaits.Count.ToString(CultureInfo.InvariantCulture)}");
            awaits.Add(authored);
            flow = authored;
            return true;
        }

        bool TryParseExpression(
            ExpressionStatementSyntax statement,
            ImmutableArray<string> structuralPath,
            out FlowStatement flow)
        {
            flow = null!;
            if (TryGetForkJoinInvocation(statement, out _))
            {
                if (!TryParseForkJoin(statement, structuralPath, out var forkJoin))
                {
                    return false;
                }

                flow = forkJoin;
                return true;
            }
            if (statement.Expression is AssignmentExpressionSyntax)
            {
                return StatementFailure(
                    statement,
                    "mutable Process-local state is not supported; recurrence state must be represented by canonical outputs and progress evidence");
            }
            if (!TryGetAwaitedContextInvocation(statement, out var invocation))
            {
                if (statement.Expression is AwaitExpressionSyntax awaitedSyntax
                    && semanticModel.GetOperation(awaitedSyntax) is IAwaitOperation awaited
                    && Strip(awaited.Operation) is IInvocationOperation localInvocation
                    && localFunctions.ContainsKey(localInvocation.TargetMethod))
                {
                    return StatementFailure(
                        statement,
                        "direct or recursive local Process calls are not supported; pass the local function to a bounded Fork or RepeatAcrossActivation construct");
                }
                return StatementFailure(
                    statement,
                    "an expression statement must await a supported operation on the ProcessContext parameter");
            }

            switch (invocation.TargetMethod.Name)
            {
                case "Choice":
                    if (!TryParseExplicitChoice(statement, structuralPath, invocation, out var choice))
                    {
                        return false;
                    }

                    flow = choice;
                    return true;

                case "Match":
                    if (!TryParseExplicitMatch(statement, structuralPath, invocation, out var match))
                    {
                        return false;
                    }

                    flow = match;
                    return true;

                case "Effect" when Argument(invocation, "outcomes") is not null:
                case "InvokeProcess":
                    var requestKind = invocation.TargetMethod.Name == "InvokeProcess"
                        ? RequestAuthoringKind.ChildProcess
                        : RequestAuthoringKind.Request;
                    if (!TryParseRequest(statement, structuralPath, invocation, requestKind, out var request))
                    {
                        return false;
                    }

                    flow = request;
                    return true;

                case "AwaitMatch":
                    if (!TryParseAwaitMatch(statement, structuralPath, invocation, out var awaitMatch))
                    {
                        return false;
                    }

                    flow = awaitMatch;
                    return true;

                case "ForEachPartition":
                    if (!TryParsePartition(statement, structuralPath, invocation, out var partition))
                    {
                        return false;
                    }

                    flow = partition;
                    return true;

                case "Timer":
                case "Reply":
                case "Transition":
                case "ContinueAt":
                case "Succeed":
                case "Terminate":
                    var kind = invocation.TargetMethod.Name switch
                    {
                        "Timer" => ActionKind.Timer,
                        "Reply" => ActionKind.Reply,
                        "Transition" => ActionKind.Transition,
                        "ContinueAt" => ActionKind.ContinueAt,
                        "Succeed" => ActionKind.Succeed,
                        _ => ActionKind.Terminate
                    };
                    flow = new ActionFlow(
                        NextIdentity(invocation.TargetMethod.Name.ToLowerInvariant(), structuralPath),
                        kind,
                        invocation,
                        SourceLocation(statement),
                        statement);
                    return true;

                default:
                    return StatementFailure(
                        statement,
                        $"awaited Process operation '{invocation.TargetMethod.Name}' is not supported as a statement");
            }
        }

        bool TryParseRequest(
            StatementSyntax statement,
            ImmutableArray<string> structuralPath,
            IInvocationOperation invocation,
            RequestAuthoringKind kind,
            out RequestFlow flow)
        {
            flow = null!;
            var requestIdentity = NextIdentity(
                kind == RequestAuthoringKind.ChildProcess ? "invoke-process" : "request",
                structuralPath);
            List<(
                IArgumentOperation Branch,
                IInvocationOperation? Declaration,
                string? ChildTerminalMember,
                SyntaxNode Syntax)> declarations = [];
            var protocol = Argument(invocation, "protocol");
            if (kind == RequestAuthoringKind.ChildProcess && protocol is not null)
            {
                foreach (var (parameter, member) in new[]
                {
                    ("completed", "Completed"),
                    ("failed", "Failed"),
                    ("cancelled", "Cancelled"),
                    ("terminated", "Terminated")
                })
                {
                    var branch = Argument(invocation, parameter);
                    if (branch is null)
                    {
                        return StatementFailure(
                            statement,
                            $"typed InvokeProcess outcomes require the {parameter} branch");
                    }
                    declarations.Add((branch, null, member, branch.Syntax));
                }
            }
            else
            {
                var authoredOutcomes = CollectionArguments(invocation, "outcomes");
                if (authoredOutcomes.IsEmpty)
                {
                    return StatementFailure(statement, "a multi-outcome Request requires at least one terminal outcome");
                }
                if (kind == RequestAuthoringKind.ChildProcess && authoredOutcomes.Length != 4)
                {
                    return StatementFailure(
                        statement,
                        "InvokeProcess requires exactly one branch for each completed, failed, cancelled, and terminated child outcome");
                }

                foreach (var authoredOutcome in authoredOutcomes)
                {
                    var declaration = Strip(authoredOutcome);
                    if (declaration is not IInvocationOperation outcomeDeclaration
                        || outcomeDeclaration.TargetMethod.Name != "Outcome"
                        || !SymbolEqualityComparer.Default.Equals(
                            outcomeDeclaration.TargetMethod.ContainingType,
                            contextParameter.Type))
                    {
                        return StatementFailure(
                            authoredOutcome.Syntax,
                            "every Request outcome must be declared with process.Outcome");
                    }
                    var branch = Argument(outcomeDeclaration, "branch");
                    if (branch is null)
                    {
                        return StatementFailure(
                            outcomeDeclaration.Syntax,
                            "a Request outcome requires one named local branch");
                    }
                    declarations.Add((branch, outcomeDeclaration, null, outcomeDeclaration.Syntax));
                }
            }

            if (declarations.Count == 0)
            {
                return StatementFailure(statement, "a multi-outcome Request requires at least one terminal outcome");
            }

            List<RequestOutcomeFlow> outcomes = [];
            HashSet<IMethodSymbol> observed = new(SymbolEqualityComparer.Default);
            foreach (var declaration in declarations)
            {
                var branch = declaration.Branch;
                if (!TryGetNamedLocalBranch(
                        branch.Value,
                        observed,
                        parameterCount: 1,
                        out var branchMethod,
                        out var localFunction)
                    && !TryGetNamedLocalBranch(
                        branch.Value,
                        observed,
                        parameterCount: 0,
                        out branchMethod,
                        out localFunction))
                {
                    return StatementFailure(
                        branch.Syntax,
                        "a Request outcome branch must name a unique local async ProcessTask function with zero or one typed outcome parameter");
                }

                var outcomeIdentity = NextIdentity(
                    "outcome",
                    structuralPath.Add(requestIdentity.PathSegment),
                    branchMethod.Name);
                if (!TryParse(
                        localFunction.Body!.Statements,
                        structuralPath.Add(requestIdentity.PathSegment).Add(outcomeIdentity.PathSegment),
                        out var body,
                        allowTrailingBareReturn: true))
                {
                    return false;
                }

                IParameterSymbol? parameter = null;
                if (branchMethod.Parameters.Length == 1)
                {
                    parameter = branchMethod.Parameters[0];
                    var output = new AuthoredOutput(
                        parameter,
                        parameter.Type,
                        outcomeIdentity,
                        "result",
                        $"__authored_output_{authoredOutputs.Count.ToString(CultureInfo.InvariantCulture)}",
                        declaration.Declaration,
                        SourceLocation(localFunction));
                    authoredOutputs.Add(output);
                }
                outcomes.Add(new(
                    outcomeIdentity,
                    branchMethod.Name,
                    body,
                    parameter,
                    declaration.Declaration,
                    declaration.ChildTerminalMember,
                    SourceLocation(declaration.Syntax),
                    declaration.Syntax));
            }

            flow = new(
                requestIdentity,
                kind,
                [.. outcomes],
                invocation,
                SourceLocation(statement),
                statement);
            return true;
        }

        bool TryParsePartition(
            StatementSyntax statement,
            ImmutableArray<string> structuralPath,
            IInvocationOperation invocation,
            out PartitionFlow flow)
        {
            flow = null!;
            var identity = NextIdentity("for-each-partition", structuralPath);
            if (!TryGetInlineProjection(
                    Argument(invocation, "progressIdentity"),
                    statement,
                    "ForEachPartition",
                    "progressIdentity",
                    optional: false,
                    out var progressIdentity)
                || !TryGetInlineProjection(
                    Argument(invocation, "childInput"),
                    statement,
                    "ForEachPartition",
                    "childInput",
                    optional: false,
                    out var childInput)
                || !TryGetInlineProjection(
                    Argument(invocation, "capacityIdentity"),
                    statement,
                    "ForEachPartition",
                    "capacityIdentity",
                    optional: true,
                    out var capacityIdentity))
            {
                return false;
            }

            var failedArgument = Argument(invocation, "failed");
            HashSet<IMethodSymbol> observed = new(SymbolEqualityComparer.Default);
            if (failedArgument is null
                || !TryGetNamedLocalBranch(
                    failedArgument.Value,
                    observed,
                    parameterCount: 0,
                    out var failedMethod,
                    out var failedFunction))
            {
                return StatementFailure(
                    failedArgument?.Syntax ?? statement,
                    "ForEachPartition failed must name one parameterless local async ProcessTask function");
            }

            if (!TryParse(
                    failedFunction.Body!.Statements,
                    structuralPath.Add(identity.PathSegment).Add($"failed-{failedMethod.Name}"),
                    out var failedBody,
                    allowTrailingBareReturn: true))
            {
                return false;
            }

            var partition = new AuthoredOutput(
                null,
                progressIdentity!.Parameter.Type,
                identity,
                "partition",
                $"__authored_output_{authoredOutputs.Count.ToString(CultureInfo.InvariantCulture)}",
                null,
                SourceLocation(statement));
            authoredOutputs.Add(partition);
            flow = new(
                identity,
                partition,
                progressIdentity,
                childInput!,
                capacityIdentity,
                failedMethod.Name,
                failedBody,
                invocation,
                SourceLocation(statement),
                statement);
            return true;
        }

        bool TryParseRecurrence(
            LocalDeclarationStatementSyntax statement,
            ILocalSymbol resultLocal,
            ImmutableArray<string> structuralPath,
            IInvocationOperation invocation,
            out RecurrenceFlow flow)
        {
            flow = null!;
            var identity = NextIdentity("repeat-across-activation", structuralPath, resultLocal.Name);
            if (!TryGetInlineProjection(
                    Argument(invocation, "continueWhen"),
                    statement,
                    "RepeatAcrossActivation",
                    "continueWhen",
                    optional: false,
                    out var continueWhen)
                || !TryGetInlineProjection(
                    Argument(invocation, "progress"),
                    statement,
                    "RepeatAcrossActivation",
                    "progress",
                    optional: false,
                    out var progress))
            {
                return false;
            }

            var occurrenceArgument = Argument(invocation, "occurrence");
            var occurrenceOperation = occurrenceArgument is null ? null : Strip(occurrenceArgument.Value);
            if (occurrenceOperation is not IInvocationOperation occurrenceInvocation
                || occurrenceInvocation.Instance is not null
                || occurrenceInvocation.Arguments.Length != 0
                || !localFunctions.TryGetValue(occurrenceInvocation.TargetMethod, out var occurrenceFunction)
                || occurrenceInvocation.TargetMethod.ReturnType is not INamedTypeSymbol
                {
                    IsGenericType: true
                } occurrenceTask
                || occurrenceTask.ConstructedFrom.ToDisplayString() != TaskName
                || !occurrenceFunction.Modifiers.Any(static modifier => modifier.IsKind(SyntaxKind.AsyncKeyword))
                || occurrenceFunction.Body is null
                || occurrenceFunction.ExpressionBody is not null)
            {
                return StatementFailure(
                    occurrenceArgument?.Syntax ?? statement,
                    "RepeatAcrossActivation occurrence must invoke one parameterless local async ProcessTask<T> function");
            }

            if (!TryParse(
                    occurrenceFunction.Body.Statements,
                    structuralPath.Add(identity.PathSegment).Add($"occurrence-{occurrenceInvocation.TargetMethod.Name}"),
                    out var occurrenceBody))
            {
                return false;
            }

            var occurrenceReturns = occurrenceBody.Descendants().OfType<ReturnFlow>().ToArray();
            if (occurrenceReturns.Length != 1
                || occurrenceBody.Statements.IsEmpty
                || !ReferenceEquals(
                    occurrenceBody.Statements[occurrenceBody.Statements.Length - 1],
                    occurrenceReturns[0]))
            {
                return StatementFailure(
                    occurrenceFunction,
                    $"RepeatAcrossActivation occurrence '{occurrenceInvocation.TargetMethod.Name}' must end in one path-total portable result expression");
            }

            var occurrenceResult = occurrenceReturns[0].Result;
            occurrenceBody = new(occurrenceBody.Statements.RemoveAt(occurrenceBody.Statements.Length - 1));
            if (occurrenceBody.Statements.IsEmpty)
            {
                return StatementFailure(
                    occurrenceFunction,
                    "RepeatAcrossActivation occurrence must contain at least one Process operation before its result");
            }

            var exhaustedArgument = Argument(invocation, "exhausted");
            var stalledArgument = Argument(invocation, "stalled");
            HashSet<IMethodSymbol> observed = new(SymbolEqualityComparer.Default);
            if (exhaustedArgument is null
                || !TryGetNamedLocalBranch(
                    exhaustedArgument.Value,
                    observed,
                    parameterCount: 0,
                    out var exhaustedMethod,
                    out var exhaustedFunction))
            {
                return StatementFailure(
                    exhaustedArgument?.Syntax ?? statement,
                    "RepeatAcrossActivation exhausted must name one parameterless local async ProcessTask function");
            }
            if (stalledArgument is null
                || !TryGetNamedLocalBranch(
                    stalledArgument.Value,
                    observed,
                    parameterCount: 0,
                    out var stalledMethod,
                    out var stalledFunction))
            {
                return StatementFailure(
                    stalledArgument?.Syntax ?? statement,
                    "RepeatAcrossActivation stalled must name a different parameterless local async ProcessTask function");
            }
            if (!TryParse(
                    exhaustedFunction.Body!.Statements,
                    structuralPath.Add(identity.PathSegment).Add($"exhausted-{exhaustedMethod.Name}"),
                    out var exhaustedBody,
                    allowTrailingBareReturn: true)
                || !TryParse(
                    stalledFunction.Body!.Statements,
                    structuralPath.Add(identity.PathSegment).Add($"stalled-{stalledMethod.Name}"),
                    out var stalledBody,
                    allowTrailingBareReturn: true))
            {
                return false;
            }

            pureLocals.Add(resultLocal, occurrenceResult);
            flow = new(
                identity,
                occurrenceInvocation.TargetMethod.Name,
                occurrenceBody,
                occurrenceResult,
                continueWhen!,
                progress!,
                exhaustedMethod.Name,
                exhaustedBody,
                stalledMethod.Name,
                stalledBody,
                invocation,
                SourceLocation(statement),
                statement);
            return true;
        }

        bool TryGetInlineProjection(
            IArgumentOperation? argument,
            SyntaxNode fallbackSyntax,
            string construct,
            string name,
            bool optional,
            out ProjectionFlow? projection)
        {
            projection = null;
            if (argument is null
                || argument.IsImplicit
                || Strip(argument.Value).ConstantValue is { HasValue: true, Value: null })
            {
                return optional
                    || StatementFailure(
                        argument?.Syntax ?? fallbackSyntax,
                        $"{construct} {name} requires one inline pure projection lambda");
            }

            var operation = Strip(argument.Value);
            if (operation is IDelegateCreationOperation delegateCreation)
            {
                operation = Strip(delegateCreation.Target);
            }
            if (operation is not IAnonymousFunctionOperation anonymous
                || anonymous.Symbol.Parameters.Length != 1)
            {
                return StatementFailure(
                    argument.Syntax,
                    $"{construct} {name} must be one inline pure lambda; runtime delegates and callbacks are not supported");
            }

            var returns = SelfAndDescendants(anonymous.Body)
                .OfType<IReturnOperation>()
                .Where(static returned => returned.ReturnedValue is not null)
                .ToArray();
            if (returns.Length != 1 || returns[0].ReturnedValue is not { } returnedValue)
            {
                return StatementFailure(
                    argument.Syntax,
                    $"{construct} {name} must contain one pure projection expression");
            }

            projection = new(
                anonymous.Symbol.Parameters[0],
                returnedValue,
                SourceLocation(argument.Syntax),
                argument.Syntax);
            return true;
        }

        bool TryParseAwaitMatch(
            StatementSyntax statement,
            ImmutableArray<string> structuralPath,
            IInvocationOperation invocation,
            out AwaitMatchFlow flow)
        {
            flow = null!;
            var awaitIdentity = NextIdentity("await-match", structuralPath);
            var declarations = CollectionArguments(invocation, "clauses");
            if (declarations.IsEmpty)
            {
                return StatementFailure(statement, "AwaitMatch requires at least one interaction or timer clause");
            }

            List<AwaitClauseFlow> clauses = [];
            HashSet<IMethodSymbol> observed = new(SymbolEqualityComparer.Default);
            for (var index = 0; index < declarations.Length; index++)
            {
                var declaration = Strip(declarations[index]);
                if (declaration is not IInvocationOperation clauseDeclaration
                    || !SymbolEqualityComparer.Default.Equals(clauseDeclaration.TargetMethod.ContainingType, contextParameter.Type))
                {
                    return StatementFailure(
                        declarations[index].Syntax,
                        "every AwaitMatch clause must be declared on the ProcessContext parameter");
                }

                var kind = clauseDeclaration.TargetMethod.Name switch
                {
                    "Event" => AwaitClauseKind.Event,
                    "Signal" => AwaitClauseKind.Signal,
                    "Request" => AwaitClauseKind.Request,
                    "Deadline" => AwaitClauseKind.Timer,
                    _ => AwaitClauseKind.Unsupported
                };
                if (kind == AwaitClauseKind.Unsupported)
                {
                    return StatementFailure(
                        clauseDeclaration.Syntax,
                        $"AwaitMatch clause '{clauseDeclaration.TargetMethod.Name}' is not supported");
                }

                var branch = Argument(clauseDeclaration, "branch");
                var parameterCount = kind == AwaitClauseKind.Request ? 2 : kind == AwaitClauseKind.Timer ? 0 : 1;
                if (branch is null
                    || !TryGetNamedLocalBranch(branch.Value, observed, parameterCount, out var branchMethod, out var localFunction))
                {
                    return StatementFailure(
                        branch?.Syntax ?? clauseDeclaration.Syntax,
                        $"an AwaitMatch {kind} branch must name a unique local async ProcessTask function with {parameterCount.ToString(CultureInfo.InvariantCulture)} parameter(s)");
                }

                var clauseIdentity = NextIdentity(
                    kind == AwaitClauseKind.Timer ? "timer-clause" : "interaction-clause",
                    structuralPath.Add(awaitIdentity.PathSegment),
                    branchMethod.Name);
                if (!TryParse(
                        localFunction.Body!.Statements,
                        structuralPath.Add(awaitIdentity.PathSegment).Add(clauseIdentity.PathSegment),
                        out var body,
                        allowTrailingBareReturn: true))
                {
                    return false;
                }

                AuthoredOutput? input = null;
                IParameterSymbol? obligation = null;
                if (kind != AwaitClauseKind.Timer)
                {
                    var inputParameter = branchMethod.Parameters[0];
                    input = new(
                        inputParameter,
                        inputParameter.Type,
                        clauseIdentity,
                        "input",
                        $"__authored_output_{authoredOutputs.Count.ToString(CultureInfo.InvariantCulture)}",
                        clauseDeclaration,
                        SourceLocation(localFunction));
                    authoredOutputs.Add(input);
                }
                if (kind == AwaitClauseKind.Request)
                {
                    obligation = branchMethod.Parameters[1];
                    requestObligations.Add(new(
                        obligation,
                        clauseIdentity,
                        $"__request_obligation_{requestObligations.Count.ToString(CultureInfo.InvariantCulture)}",
                        SourceLocation(localFunction)));
                }

                clauses.Add(new(
                    clauseIdentity,
                    branchMethod.Name,
                    kind,
                    body,
                    input,
                    obligation,
                    clauseDeclaration,
                    SourceLocation(clauseDeclaration.Syntax),
                    clauseDeclaration.Syntax));
            }

            flow = new(
                awaitIdentity,
                [.. clauses],
                invocation,
                SourceLocation(statement),
                statement);
            return true;
        }

        bool TryParseTypedAwaitMatch(
            LocalDeclarationStatementSyntax statement,
            ILocalSymbol resultLocal,
            SwitchStatementSyntax selection,
            ImmutableArray<string> structuralPath,
            IInvocationOperation invocation,
            out AwaitMatchFlow flow)
        {
            flow = null!;
            if (semanticModel.GetOperation(selection.Expression) is not { } selected
                || Strip(selected) is not ILocalReferenceOperation selectedLocal
                || !SymbolEqualityComparer.Default.Equals(selectedLocal.Local, resultLocal))
            {
                return StatementFailure(
                    selection.Expression,
                    $"the type switch following AwaitMatch must select its bound result '{resultLocal.Name}'");
            }

            var awaitIdentity = NextIdentity("await-match", structuralPath);
            var declarations = CollectionArguments(invocation, "clauses");
            if (declarations.IsEmpty)
            {
                return StatementFailure(statement, "AwaitMatch requires at least one interaction or timer clause");
            }

            var resultType = invocation.TargetMethod.TypeArguments[0];
            List<TypedAwaitClauseDeclaration> typedClauses = [];
            HashSet<ITypeSymbol> declaredCases = new(SymbolEqualityComparer.Default);
            for (var index = 0; index < declarations.Length; index++)
            {
                var declared = Strip(declarations[index]);
                if (declared is not IInvocationOperation clause
                    || !SymbolEqualityComparer.Default.Equals(clause.TargetMethod.ContainingType, contextParameter.Type))
                {
                    return StatementFailure(
                        declarations[index].Syntax,
                        "every typed AwaitMatch clause must be declared on the ProcessContext parameter");
                }

                var kind = clause.TargetMethod.Name switch
                {
                    "Event" => AwaitClauseKind.Event,
                    "Signal" => AwaitClauseKind.Signal,
                    "Deadline" => AwaitClauseKind.Timer,
                    _ => AwaitClauseKind.Unsupported
                };
                if (kind == AwaitClauseKind.Unsupported
                    || !clause.TargetMethod.IsGenericMethod
                    || clause.TargetMethod.TypeArguments.Length != 1
                    || Argument(clause, "branch") is not null)
                {
                    return StatementFailure(
                        clause.Syntax,
                        $"typed AwaitMatch clause '{clause.TargetMethod.Name}' must be a branch-free typed Event, Signal, or Deadline alternative");
                }

                var caseType = clause.TargetMethod.TypeArguments[0];
                if (!caseType.IsReferenceType || !IsAssignableTo(caseType, resultType))
                {
                    return StatementFailure(
                        clause.Syntax,
                        $"typed AwaitMatch case '{caseType.ToDisplayString()}' must be a reference type assignable to result '{resultType.ToDisplayString()}'");
                }
                if (!declaredCases.Add(caseType))
                {
                    return StatementFailure(
                        clause.Syntax,
                        $"typed AwaitMatch result case '{caseType.ToDisplayString()}' is declared more than once");
                }

                var identity = NextIdentity(
                    kind == AwaitClauseKind.Timer ? "timer-clause" : "interaction-clause",
                    structuralPath.Add(awaitIdentity.PathSegment),
                    caseType.Name);
                typedClauses.Add(new(
                    identity,
                    kind,
                    caseType,
                    clause,
                    SourceLocation(clause.Syntax),
                    clause.Syntax));
            }

            Dictionary<ITypeSymbol, TypedAwaitSelection> selections = new(SymbolEqualityComparer.Default);
            foreach (var section in selection.Sections)
            {
                if (section.Labels.Count != 1
                    || section.Labels[0] is not CasePatternSwitchLabelSyntax label
                    || label.WhenClause is not null
                    || !TryGetTypedAwaitCase(
                        label,
                        out var caseType,
                        out var patternLocal,
                        out var propertyBindings))
                {
                    return StatementFailure(
                        section,
                        "typed AwaitMatch switch sections require one unguarded type or portable property-pattern case");
                }
                if (!declaredCases.Contains(caseType))
                {
                    return StatementFailure(
                        label,
                        $"switch case '{caseType.ToDisplayString()}' is not a declared AwaitMatch alternative");
                }
                if (selections.ContainsKey(caseType))
                {
                    return StatementFailure(
                        label,
                        $"switch case '{caseType.ToDisplayString()}' is handled more than once");
                }
                selections.Add(
                    caseType,
                    new(
                        section,
                        patternLocal,
                        propertyBindings,
                        SourceLocation(label),
                        label));
            }

            var missing = typedClauses
                .Where(clause => !selections.ContainsKey(clause.CaseType))
                .Select(clause => clause.CaseType.ToDisplayString())
                .ToArray();
            if (missing.Length != 0)
            {
                return StatementFailure(
                    selection,
                    $"typed AwaitMatch switch is not exhaustive; add case(s): {string.Join(", ", missing)}");
            }

            List<AwaitClauseFlow> clauses = [];
            foreach (var declared in typedClauses)
            {
                var selectedCase = selections[declared.CaseType];
                AuthoredOutput? input = null;
                if (declared.Kind == AwaitClauseKind.Timer)
                {
                    if (selectedCase.PatternLocal is not null
                        || !selectedCase.PropertyBindings.IsEmpty)
                    {
                        return StatementFailure(
                            selectedCase.Syntax,
                            "a typed AwaitMatch timer case cannot bind a CLR value; use the lexical due-time expression in its branch");
                    }
                }
                else
                {
                    input = new(
                        selectedCase.PatternLocal,
                        declared.CaseType,
                        declared.Identity,
                        "input",
                        $"__authored_output_{authoredOutputs.Count.ToString(CultureInfo.InvariantCulture)}",
                        declared.Declaration,
                        selectedCase.Source);
                    authoredOutputs.Add(input);
                    foreach (var propertyBinding in selectedCase.PropertyBindings)
                    {
                        patternOutputs.Add(new(
                            propertyBinding.Symbol,
                            input,
                            propertyBinding.Path));
                    }
                }

                if (!TryParse(
                        selectedCase.Section.Statements,
                        structuralPath.Add(awaitIdentity.PathSegment).Add(declared.Identity.PathSegment),
                        out var body,
                        ignoreTerminalBreak: true))
                {
                    return false;
                }

                clauses.Add(new(
                    declared.Identity,
                    declared.CaseType.Name,
                    declared.Kind,
                    body,
                    input,
                    null,
                    declared.Declaration,
                    declared.Source,
                    declared.Syntax));
            }

            flow = new(
                awaitIdentity,
                [.. clauses],
                invocation,
                SourceLocation(statement),
                statement);
            return true;
        }

        bool TryGetTypedAwaitCase(
            CasePatternSwitchLabelSyntax label,
            out ITypeSymbol caseType,
            out ILocalSymbol? patternLocal,
            out ImmutableArray<TypedAwaitPropertyBinding> propertyBindings)
        {
            caseType = null!;
            patternLocal = null;
            propertyBindings = [];
            TypeSyntax? typeSyntax;
            SingleVariableDesignationSyntax? designation;
            PropertyPatternClauseSyntax? properties = null;
            switch (label.Pattern)
            {
                case DeclarationPatternSyntax declaration:
                    typeSyntax = declaration.Type;
                    designation = declaration.Designation as SingleVariableDesignationSyntax;
                    break;
                case TypePatternSyntax type:
                    typeSyntax = type.Type;
                    designation = null;
                    break;
                case RecursivePatternSyntax recursive
                    when recursive.Type is not null
                         && recursive.PositionalPatternClause is null:
                    typeSyntax = recursive.Type;
                    designation = recursive.Designation as SingleVariableDesignationSyntax;
                    properties = recursive.PropertyPatternClause;
                    break;
                default:
                    return false;
            }

            if (semanticModel.GetTypeInfo(typeSyntax).Type is not INamedTypeSymbol resolved)
            {
                return false;
            }
            if (designation is not null
                && semanticModel.GetDeclaredSymbol(designation) is not ILocalSymbol local)
            {
                return false;
            }

            caseType = resolved;
            patternLocal = designation is null
                ? null
                : (ILocalSymbol)semanticModel.GetDeclaredSymbol(designation)!;
            if (properties is null)
            {
                return true;
            }

            var bindings = ImmutableArray.CreateBuilder<TypedAwaitPropertyBinding>();
            if (!TryGetTypedAwaitPropertyBindings(resolved, properties, [], bindings))
            {
                return false;
            }
            propertyBindings = bindings.ToImmutable();
            return true;
        }

        bool TryGetTypedAwaitPropertyBindings(
            INamedTypeSymbol containingType,
            PropertyPatternClauseSyntax properties,
            ImmutableArray<string> prefix,
            ImmutableArray<TypedAwaitPropertyBinding>.Builder bindings)
        {
            foreach (var subpattern in properties.Subpatterns)
            {
                var memberName = subpattern.NameColon?.Name.Identifier.ValueText
                                 ?? subpattern.ExpressionColon?.Expression.ToString();
                if (memberName is not { Length: > 0 }
                    || containingType.GetMembers(memberName).OfType<IPropertySymbol>().SingleOrDefault() is not { } property)
                {
                    return false;
                }

                var path = prefix.Add(SerializedName(property) ?? property.Name);
                SingleVariableDesignationSyntax? designation = subpattern.Pattern switch
                {
                    VarPatternSyntax variable => variable.Designation as SingleVariableDesignationSyntax,
                    DeclarationPatternSyntax declaration => declaration.Designation as SingleVariableDesignationSyntax,
                    _ => null
                };
                if (designation is not null)
                {
                    if (semanticModel.GetDeclaredSymbol(designation) is not ILocalSymbol local)
                    {
                        return false;
                    }
                    bindings.Add(new(local, path));
                    continue;
                }

                if (subpattern.Pattern is not RecursivePatternSyntax
                    {
                        Type: null,
                        PositionalPatternClause: null,
                        PropertyPatternClause: { } nested
                    }
                    || property.Type is not INamedTypeSymbol nestedType
                    || !TryGetTypedAwaitPropertyBindings(nestedType, nested, path, bindings))
                {
                    return false;
                }
            }
            return true;
        }

        static bool IsAssignableTo(ITypeSymbol candidate, ITypeSymbol target)
        {
            if (SymbolEqualityComparer.Default.Equals(candidate, target))
            {
                return true;
            }
            if (candidate is not INamedTypeSymbol named)
            {
                return false;
            }
            if (named.AllInterfaces.Any(@interface => SymbolEqualityComparer.Default.Equals(@interface, target)))
            {
                return true;
            }

            for (var current = named.BaseType; current is not null; current = current.BaseType)
            {
                if (SymbolEqualityComparer.Default.Equals(current, target))
                {
                    return true;
                }
            }
            return false;
        }

        bool TryGetNamedLocalBranch(
            IOperation branch,
            ISet<IMethodSymbol> observed,
            int parameterCount,
            out IMethodSymbol methodSymbol,
            out LocalFunctionStatementSyntax localFunction)
        {
            methodSymbol = null!;
            localFunction = null!;
            branch = Strip(branch);
            if (branch is IDelegateCreationOperation delegateCreation)
            {
                branch = Strip(delegateCreation.Target);
            }

            if (branch is not IMethodReferenceOperation methodReference
                || !localFunctions.TryGetValue(methodReference.Method, out localFunction)
                || methodReference.Method.Parameters.Length != parameterCount
                || methodReference.Method.ReturnType.ToDisplayString() != BranchTaskName
                || !localFunction.Modifiers.Any(static modifier => modifier.IsKind(SyntaxKind.AsyncKeyword))
                || localFunction.Body is null
                || localFunction.ExpressionBody is not null
                || !observed.Add(methodReference.Method))
            {
                return false;
            }

            methodSymbol = methodReference.Method;
            return true;
        }

        bool TryGetAwaitedContextInvocation(StatementSyntax statement, out IInvocationOperation invocation)
        {
            invocation = null!;
            if (statement is not ExpressionStatementSyntax { Expression: AwaitExpressionSyntax awaitedSyntax }
                || semanticModel.GetOperation(awaitedSyntax) is not IAwaitOperation awaited
                || Strip(awaited.Operation) is not IInvocationOperation candidate
                || !SymbolEqualityComparer.Default.Equals(candidate.TargetMethod.ContainingType, contextParameter.Type)
                || candidate.Instance is null
                || Strip(candidate.Instance) is not IParameterReferenceOperation contextReference
                || !SymbolEqualityComparer.Default.Equals(contextReference.Parameter, contextParameter))
            {
                return false;
            }

            invocation = candidate;
            return true;
        }

        static ImmutableArray<IOperation> CollectionArguments(IInvocationOperation invocation, string parameterName)
        {
            var argument = Argument(invocation, parameterName);
            if (argument is null)
            {
                return [];
            }

            var value = Strip(argument.Value);
            return value switch
            {
                IArrayCreationOperation { Initializer: { } initializer } => initializer.ElementValues,
                ICollectionExpressionOperation collection => collection.Elements,
                _ => [value]
            };
        }

        bool TryParseForkJoin(
            StatementSyntax statement,
            ImmutableArray<string> structuralPath,
            out ForkJoinFlow flow)
        {
            flow = null!;
            if (!TryGetForkJoinInvocation(statement, out var invocation))
            {
                return StatementFailure(
                    statement,
                    "a ForkJoin statement must await ForkJoin on the ProcessContext parameter");
            }

            var mode = invocation.TargetMethod.Name switch
            {
                "ForkJoin" => ForkAuthoringMode.All,
                "ForkAny" => ForkAuthoringMode.Any,
                "ForkRequired" => ForkAuthoringMode.RequiredCount,
                _ => ForkAuthoringMode.Unsupported
            };
            if (mode == ForkAuthoringMode.Unsupported)
            {
                return StatementFailure(statement, $"unsupported Fork operation '{invocation.TargetMethod.Name}'");
            }

            var branchOperations = BranchArguments(invocation);
            if (branchOperations.Length < 2)
            {
                return StatementFailure(statement, "ForkJoin requires at least two local-function branches");
            }

            var forkIdentity = NextIdentity("fork", structuralPath);
            var joinIdentity = NextIdentity("join", structuralPath.Add(forkIdentity.PathSegment));
            List<ForkBranchFlow> branches = [];
            HashSet<IMethodSymbol> observed = new(SymbolEqualityComparer.Default);
            for (var index = 0; index < branchOperations.Length; index++)
            {
                var branchOperation = Strip(branchOperations[index]);
                IArgumentOperation? capacityDomain = null;
                IArgumentOperation? explicitBranchId = null;
                IArgumentOperation? explicitBranchRole = null;
                IArgumentOperation? explicitBranchEdgeOwner = null;
                if (branchOperation is IInvocationOperation annotation
                    && annotation.TargetMethod.Name == "Branch"
                    && SymbolEqualityComparer.Default.Equals(annotation.TargetMethod.ContainingType, contextParameter.Type)
                    && annotation.Instance is not null
                    && Strip(annotation.Instance) is IParameterReferenceOperation annotationContext
                    && SymbolEqualityComparer.Default.Equals(annotationContext.Parameter, contextParameter))
                {
                    var annotatedBranch = Argument(annotation, "branch");
                    capacityDomain = Argument(annotation, "capacityDomain");
                    explicitBranchId = Argument(annotation, "id");
                    explicitBranchRole = Argument(annotation, "role");
                    explicitBranchEdgeOwner = Argument(annotation, "edgeOwner");
                    if (capacityDomain is { IsImplicit: true }
                        || capacityDomain is not null
                        && Strip(capacityDomain.Value).ConstantValue is { HasValue: true, Value: null })
                    {
                        capacityDomain = null;
                    }
                    if (explicitBranchId is { IsImplicit: true })
                    {
                        explicitBranchId = null;
                    }
                    if (explicitBranchRole is { IsImplicit: true }
                        || explicitBranchRole is not null
                        && Strip(explicitBranchRole.Value).ConstantValue is { HasValue: true, Value: null })
                    {
                        explicitBranchRole = null;
                    }
                    if (explicitBranchEdgeOwner is { IsImplicit: true }
                        || explicitBranchEdgeOwner is not null
                        && Strip(explicitBranchEdgeOwner.Value).ConstantValue is { HasValue: true, Value: null })
                    {
                        explicitBranchEdgeOwner = null;
                    }
                    if (annotatedBranch is null || capacityDomain is null && explicitBranchId is null)
                    {
                        return StatementFailure(
                            branchOperations[index].Syntax,
                            "a ForkJoin branch annotation requires a branch identity or capacity domain");
                    }
                    branchOperation = Strip(annotatedBranch.Value);
                }
                if (branchOperation is not IInvocationOperation branchInvocation
                    || branchInvocation.Instance is not null
                    || branchInvocation.Arguments.Length != 0
                    || !localFunctions.TryGetValue(branchInvocation.TargetMethod, out var localFunction))
                {
                    return StatementFailure(
                        branchOperations[index].Syntax,
                        "each ForkJoin branch must invoke a parameterless local function declared in the Process method");
                }
                if (!observed.Add(branchInvocation.TargetMethod))
                {
                    return StatementFailure(
                        branchOperations[index].Syntax,
                        $"ForkJoin branch function '{branchInvocation.TargetMethod.Name}' is invoked more than once");
                }
                var branchReturn = branchInvocation.TargetMethod.ReturnType;
                var branchResultType = branchReturn is INamedTypeSymbol { IsGenericType: true } typedTask
                                       && typedTask.ConstructedFrom.ToDisplayString() == TaskName
                    ? typedTask.TypeArguments[0]
                    : null;
                if (!localFunction.Modifiers.Any(static modifier => modifier.IsKind(SyntaxKind.AsyncKeyword))
                    || branchResultType is null && branchReturn.ToDisplayString() != BranchTaskName
                    || localFunction.Body is null
                    || localFunction.ExpressionBody is not null)
                {
                    return StatementFailure(
                        localFunction,
                        $"ForkJoin branch '{branchInvocation.TargetMethod.Name}' must be an async, block-bodied, parameterless ProcessTask or ProcessTask<T> local function");
                }

                var branchIdentity = NextIdentity(
                    "branch",
                    structuralPath.Add(forkIdentity.PathSegment),
                    branchInvocation.TargetMethod.Name);
                if (!TryParse(
                        localFunction.Body.Statements,
                        structuralPath.Add(forkIdentity.PathSegment).Add(branchIdentity.PathSegment),
                        out var branchBody,
                        allowTrailingBareReturn: branchResultType is null))
                {
                    return false;
                }

                IOperation? branchResult = null;
                if (branchResultType is not null)
                {
                    var returns = branchBody.Descendants().OfType<ReturnFlow>().ToArray();
                    if (returns.Length == 0)
                    {
                        return StatementFailure(
                            localFunction,
                            $"typed ForkJoin branch '{branchInvocation.TargetMethod.Name}' has no portable result expression");
                    }
                    if (returns.Length > 1)
                    {
                        return StatementFailure(
                            localFunction,
                            $"typed ForkJoin branch '{branchInvocation.TargetMethod.Name}' has ambiguous result paths; bind branch flow to one pure value and use one final return expression");
                    }
                    if (branchBody.Statements.IsEmpty
                        || !ReferenceEquals(branchBody.Statements[branchBody.Statements.Length - 1], returns[0]))
                    {
                        return StatementFailure(
                            returns[0].Syntax,
                            $"typed ForkJoin branch '{branchInvocation.TargetMethod.Name}' has a path-partial result; every successful path must reach one final return expression");
                    }

                    branchResult = returns[0].Result;
                    branchBody = new(branchBody.Statements.RemoveAt(branchBody.Statements.Length - 1));
                }
                if (branchBody.Statements.IsEmpty)
                {
                    return StatementFailure(localFunction, "ForkJoin branches must contain at least one Process operation");
                }

                branches.Add(new(
                    branchIdentity,
                    branchInvocation.TargetMethod.Name,
                    branchBody,
                    branchResult,
                    explicitBranchId,
                    capacityDomain,
                    explicitBranchRole,
                    explicitBranchEdgeOwner,
                    SourceLocation(localFunction),
                    localFunction));
            }

            var typedBranches = branches.Count(static branch => branch.Result is not null);
            if (typedBranches != 0 && typedBranches != branches.Count)
            {
                return StatementFailure(statement, "a typed ForkJoin cannot mix result-producing and result-less branches");
            }

            AuthoredOutput? partialResult = null;
            if (typedBranches != 0 && mode == ForkAuthoringMode.All && !TryBindForkResults(statement, branches))
            {
                return false;
            }

            if (mode != ForkAuthoringMode.All)
            {
                if (typedBranches != branches.Count)
                {
                    return StatementFailure(statement, "a partial Fork requires a portable result from every branch");
                }

                if (!TryBindPartialForkResult(statement, joinIdentity, out partialResult))
                {
                    return false;
                }
            }

            flow = new(
                forkIdentity,
                joinIdentity,
                [.. branches],
                mode,
                partialResult,
                invocation,
                SourceLocation(statement),
                statement);
            return true;
        }

        bool TryGetForkJoinInvocation(StatementSyntax statement, out IInvocationOperation invocation)
        {
            invocation = null!;
            var matches = statement.DescendantNodesAndSelf()
                .OfType<AwaitExpressionSyntax>()
                .Select(awaited => semanticModel.GetOperation(awaited))
                .Where(static operation => operation is not null)
                .Select(static operation => Strip(operation!))
                .OfType<IAwaitOperation>()
                .Select(static awaited => Strip(awaited.Operation))
                .OfType<IInvocationOperation>()
                .Where(candidate => candidate.TargetMethod.Name is "ForkJoin" or "ForkAny" or "ForkRequired"
                    && SymbolEqualityComparer.Default.Equals(candidate.TargetMethod.ContainingType, contextParameter.Type)
                    && candidate.Instance is not null
                    && Strip(candidate.Instance) is IParameterReferenceOperation contextReference
                    && SymbolEqualityComparer.Default.Equals(contextReference.Parameter, contextParameter))
                .ToArray();
            if (matches.Length != 1)
            {
                return false;
            }

            invocation = matches[0];
            return true;
        }

        bool TryParseExplicitChoice(
            StatementSyntax statement,
            ImmutableArray<string> structuralPath,
            IInvocationOperation invocation,
            out ExplicitDecisionFlow flow)
        {
            flow = null!;
            var identity = NextIdentity("choice", structuralPath);
            HashSet<IMethodSymbol> observed = new(SymbolEqualityComparer.Default);
            if (!TryParseDecisionArms(
                    statement,
                    structuralPath,
                    identity,
                    invocation,
                    declarationMethod: "When",
                    selectorParameter: "predicate",
                    operation: "Choice",
                    observed: observed,
                    arms: out var arms)
                || !TryParseDecisionFallback(
                    statement,
                    structuralPath.Add(identity.PathSegment),
                    invocation,
                    observed,
                    out var fallback))
            {
                return false;
            }
            if (!ValidateDecisionPolicies(statement, invocation, fallback is not null, "Choice"))
            {
                return false;
            }

            flow = new(
                identity,
                DecisionAuthoringKind.Choice,
                null,
                arms,
                fallback,
                invocation,
                SourceLocation(statement),
                statement);
            return true;
        }

        bool TryParseExplicitMatch(
            StatementSyntax statement,
            ImmutableArray<string> structuralPath,
            IInvocationOperation invocation,
            out ExplicitDecisionFlow flow)
        {
            flow = null!;
            var value = Argument(invocation, "value");
            if (value is null)
            {
                return StatementFailure(statement, "Match requires one portable typed value");
            }

            var identity = NextIdentity("match", structuralPath);
            HashSet<IMethodSymbol> observed = new(SymbolEqualityComparer.Default);
            if (!TryParseDecisionArms(
                    statement,
                    structuralPath,
                    identity,
                    invocation,
                    declarationMethod: "Case",
                    selectorParameter: "pattern",
                    operation: "Match",
                    observed: observed,
                    arms: out var arms)
                || !TryParseDecisionFallback(
                    statement,
                    structuralPath.Add(identity.PathSegment),
                    invocation,
                    observed,
                    out var fallback))
            {
                return false;
            }
            if (!ValidateDecisionPolicies(statement, invocation, fallback is not null, "Match"))
            {
                return false;
            }

            flow = new(
                identity,
                DecisionAuthoringKind.Match,
                value.Value,
                arms,
                fallback,
                invocation,
                SourceLocation(statement),
                statement);
            return true;
        }

        bool TryParseDecisionArms(
            StatementSyntax statement,
            ImmutableArray<string> structuralPath,
            FlowIdentity identity,
            IInvocationOperation invocation,
            string declarationMethod,
            string selectorParameter,
            string operation,
            HashSet<IMethodSymbol> observed,
            out ImmutableArray<DecisionArmFlow> arms)
        {
            arms = [];
            var declarations = CollectionArguments(invocation, "cases");
            if (declarations.IsEmpty)
            {
                return StatementFailure(
                    statement,
                    $"{operation} requires at least one process.{declarationMethod} arm");
            }

            var builder = ImmutableArray.CreateBuilder<DecisionArmFlow>(declarations.Length);
            foreach (var candidate in declarations)
            {
                var declaration = Strip(candidate);
                if (declaration is not IInvocationOperation armDeclaration
                    || armDeclaration.TargetMethod.Name != declarationMethod
                    || !SymbolEqualityComparer.Default.Equals(armDeclaration.TargetMethod.ContainingType, contextParameter.Type))
                {
                    return StatementFailure(
                        candidate.Syntax,
                        $"every {operation} arm must be declared with process.{declarationMethod}");
                }

                var selector = Argument(armDeclaration, selectorParameter);
                var branch = Argument(armDeclaration, "branch");
                if (selector is null
                    || branch is null
                    || !TryGetNamedLocalBranch(
                        branch.Value,
                        observed,
                        parameterCount: 0,
                        out var branchMethod,
                        out var localFunction))
                {
                    return StatementFailure(
                        branch?.Syntax ?? armDeclaration.Syntax,
                        $"a {operation} arm must name a unique parameterless async ProcessTask local function");
                }

                var armIdentity = NextIdentity(
                    "case",
                    structuralPath.Add(identity.PathSegment),
                    branchMethod.Name);
                if (!TryParse(
                        localFunction.Body!.Statements,
                        structuralPath.Add(identity.PathSegment).Add(armIdentity.PathSegment),
                        out var body,
                        allowTrailingBareReturn: true))
                {
                    return false;
                }

                builder.Add(new(
                    armIdentity,
                    branchMethod.Name,
                    selector,
                    body,
                    armDeclaration,
                    SourceLocation(armDeclaration.Syntax),
                    armDeclaration.Syntax));
            }

            arms = builder.MoveToImmutable();
            return true;
        }

        bool TryParseDecisionFallback(
            StatementSyntax statement,
            ImmutableArray<string> structuralPath,
            IInvocationOperation invocation,
            HashSet<IMethodSymbol> observed,
            out DecisionFallbackFlow? fallback)
        {
            fallback = null;
            var argument = Argument(invocation, "fallback");
            if (argument is null
                || argument.IsImplicit
                || Strip(argument.Value).ConstantValue is { HasValue: true, Value: null })
            {
                return true;
            }
            if (!TryGetNamedLocalBranch(
                    argument.Value,
                    observed,
                    parameterCount: 0,
                    out var branchMethod,
                    out var localFunction))
            {
                return StatementFailure(
                    argument.Syntax,
                    "a Choice or Match fallback must name a unique parameterless async ProcessTask local function");
            }

            var identity = NextIdentity("fallback", structuralPath, branchMethod.Name);
            if (!TryParse(
                    localFunction.Body!.Statements,
                    structuralPath.Add(identity.PathSegment),
                    out var body,
                    allowTrailingBareReturn: true))
            {
                return false;
            }

            fallback = new(
                identity,
                branchMethod.Name,
                body,
                SourceLocation(argument.Syntax),
                argument.Syntax);
            return true;
        }

        bool ValidateDecisionPolicies(
            StatementSyntax statement,
            IInvocationOperation invocation,
            bool hasFallback,
            string operation)
        {
            var selection = ExactEnumMember(Argument(invocation, "selection"));
            var completeness = ExactEnumMember(Argument(invocation, "completeness"));
            if (selection is null || completeness is null)
            {
                return StatementFailure(
                    statement,
                    $"{operation} requires named exact selection and completeness policies");
            }
            if (selection == "Unspecified" || completeness == "Unspecified")
            {
                return StatementFailure(
                    statement,
                    $"{operation} selection and completeness policies cannot be Unspecified");
            }
            if (completeness == "Fallback" && !hasFallback)
            {
                return StatementFailure(statement, $"{operation} fallback completeness requires a named fallback branch");
            }
            if (completeness == "Exhaustive" && hasFallback)
            {
                return StatementFailure(statement, $"{operation} exhaustive completeness cannot declare a fallback branch");
            }
            return true;
        }

        static string? ExactEnumMember(IArgumentOperation? argument)
        {
            if (argument is null)
            {
                return null;
            }
            var value = Strip(argument.Value);
            return value is IFieldReferenceOperation { Field.ContainingType.TypeKind: TypeKind.Enum } field
                ? field.Field.Name
                : null;
        }

        bool TryBindPartialForkResult(
            StatementSyntax statement,
            FlowIdentity joinIdentity,
            out AuthoredOutput? output)
        {
            output = null;
            if (statement.DescendantNodesAndSelf().OfType<SingleVariableDesignationSyntax>().Any())
            {
                return StatementFailure(
                    statement,
                    "Any and RequiredCount Fork results cannot be deconstructed because only selected branches are guaranteed");
            }
            if (statement is not LocalDeclarationStatementSyntax declaration
                || declaration.Declaration.Variables.Count != 1
                || semanticModel.GetDeclaredSymbol(declaration.Declaration.Variables[0]) is not ILocalSymbol local)
            {
                return StatementFailure(
                    statement,
                    "Any and RequiredCount Fork results must be bound to one winner or winner-collection local");
            }

            output = new(
                local,
                local.Type,
                joinIdentity,
                "result",
                $"__authored_output_{authoredOutputs.Count.ToString(CultureInfo.InvariantCulture)}",
                null,
                SourceLocation(statement));
            authoredOutputs.Add(output);
            return true;
        }

        bool TryBindForkResults(StatementSyntax statement, IReadOnlyList<ForkBranchFlow> branches)
        {
            var designations = statement.DescendantNodesAndSelf()
                .OfType<SingleVariableDesignationSyntax>()
                .Select(designation => semanticModel.GetDeclaredSymbol(designation))
                .OfType<ILocalSymbol>()
                .ToArray();
            if (designations.Length != 0)
            {
                if (designations.Length != branches.Count)
                {
                    return StatementFailure(
                        statement,
                        $"ForkJoin deconstruction requires exactly {branches.Count.ToString(CultureInfo.InvariantCulture)} result variables");
                }

                for (var index = 0; index < designations.Length; index++)
                {
                    pureLocals.Add(designations[index], branches[index].Result!);
                }
                return true;
            }

            if (statement is not LocalDeclarationStatementSyntax declaration
                || declaration.Declaration.Variables.Count != 1
                || semanticModel.GetDeclaredSymbol(declaration.Declaration.Variables[0]) is not ILocalSymbol tuple)
            {
                return true;
            }

            forkResultTuples.Add(tuple, [.. branches.Select(static branch => branch.Result!)]);
            return true;
        }

        static ImmutableArray<IOperation> BranchArguments(IInvocationOperation invocation)
        {
            var arguments = ImmutableArray.CreateBuilder<IOperation>();
            foreach (var argument in invocation.Arguments)
            {
                var name = argument.Parameter?.Name;
                if (!string.Equals(name, "branches", StringComparison.Ordinal)
                    && !(name?.StartsWith("branch", StringComparison.Ordinal) ?? false))
                {
                    continue;
                }

                var value = Strip(argument.Value);
                if (value is IArrayCreationOperation { Initializer: { } initializer })
                {
                    arguments.AddRange(initializer.ElementValues);
                }
                else if (value is ICollectionExpressionOperation collection)
                {
                    arguments.AddRange(collection.Elements);
                }
                else
                {
                    arguments.Add(value);
                }
            }
            return arguments.ToImmutable();
        }

        bool TryParseIf(
            IfStatementSyntax conditional,
            ImmutableArray<string> structuralPath,
            out IfFlow flow)
        {
            flow = null!;
            var condition = semanticModel.GetOperation(conditional.Condition);
            if (condition is null)
            {
                return StatementFailure(conditional, "if condition could not be analyzed");
            }

            var identity = NextIdentity("choice", structuralPath);
            if (!TryParseStatementBody(
                    conditional.Statement,
                    structuralPath.Add(identity.PathSegment).Add("true"),
                    out var whenTrue))
            {
                return false;
            }

            FlowBlock? whenFalse = null;
            if (conditional.Else is not null
                && !TryParseStatementBody(
                    conditional.Else.Statement,
                    structuralPath.Add(identity.PathSegment).Add("false"),
                    out whenFalse))
            {
                return false;
            }

            flow = new(identity, condition, whenTrue, whenFalse, SourceLocation(conditional), conditional);
            return true;
        }

        bool TryParseStatementBody(
            StatementSyntax statement,
            ImmutableArray<string> structuralPath,
            out FlowBlock block)
        {
            if (statement is BlockSyntax blockSyntax)
            {
                return TryParse(blockSyntax.Statements, structuralPath, out block);
            }

            return TryParse(new SyntaxList<StatementSyntax>(statement), structuralPath, out block);
        }

        bool TryParseSwitch(
            SwitchStatementSyntax match,
            ImmutableArray<string> structuralPath,
            out MatchFlow flow)
        {
            flow = null!;
            var value = semanticModel.GetOperation(match.Expression);
            if (value is null)
            {
                return StatementFailure(match, "switch value could not be analyzed");
            }

            var identity = NextIdentity("match", structuralPath);
            List<MatchArm> arms = [];
            FlowBlock? fallback = null;
            for (var sectionIndex = 0; sectionIndex < match.Sections.Count; sectionIndex++)
            {
                var section = match.Sections[sectionIndex];
                var statements = section.Statements;
                if (statements.Count != 0 && statements[statements.Count - 1] is BreakStatementSyntax)
                {
                    statements = SyntaxFactory.List(statements.Take(statements.Count - 1));
                }

                if (statements.Any(static statement => statement is BreakStatementSyntax))
                {
                    return StatementFailure(section, "switch break is supported only as the final statement of a case");
                }

                if (!TryParse(
                        statements,
                        structuralPath.Add(identity.PathSegment).Add($"case-{sectionIndex.ToString(CultureInfo.InvariantCulture)}"),
                        out var sectionBody))
                {
                    return false;
                }

                foreach (var label in section.Labels)
                {
                    switch (label)
                    {
                        case CaseSwitchLabelSyntax @case:
                            if (@case.Value is null || semanticModel.GetConstantValue(@case.Value) is not { HasValue: true })
                            {
                                return StatementFailure(
                                    @case,
                                    "switch cases require exact compile-time constant patterns");
                            }
                            arms.Add(new(@case.Value.ToString(), sectionBody, SourceLocation(@case)));
                            break;
                        case DefaultSwitchLabelSyntax:
                            if (fallback is not null)
                            {
                                return StatementFailure(label, "a Process switch may declare only one default section");
                            }

                            fallback = sectionBody;
                            break;
                        default:
                            return StatementFailure(label, "pattern and guarded switch labels are not yet supported");
                    }
                }
            }
            if (arms.Count == 0)
            {
                return StatementFailure(match, "switch requires at least one exact case");
            }

            flow = new(identity, value, [.. arms], fallback, SourceLocation(match), match);
            return true;
        }

        FlowIdentity NextIdentity(
            string role,
            ImmutableArray<string> structuralPath,
            string? semanticName = null)
        {
            var variable = variableOrdinal++;
            var key = string.Join("\u001f", structuralPath) + "\u001e" + role;
            semanticRoleOrdinals.TryGetValue(key, out var ordinal);
            semanticRoleOrdinals[key] = ordinal + 1;
            var segment = semanticName is null
                ? $"{role}-{ordinal.ToString(CultureInfo.InvariantCulture)}"
                : $"{role}-{semanticName}";
            return new(
                $"__node_{variable.ToString(CultureInfo.InvariantCulture)}",
                ["body", .. structuralPath, segment],
                segment);
        }

        bool StatementFailure(SyntaxNode syntax, string reason)
        {
            Report(
                productionContext,
                UnsupportedStatement,
                syntax.GetLocation(),
                method.Name,
                reason);
            return false;
        }

        bool Failure(SyntaxNode syntax, string reason, out FlowBlock block)
        {
            block = null!;
            return StatementFailure(syntax, reason);
        }
    }

    sealed class DefinitionEmitter
    {
        readonly SourceProductionContext productionContext;
        readonly IMethodSymbol method;
        readonly IParameterSymbol inputParameter;
        readonly ITypeSymbol resultType;
        readonly IReadOnlyDictionary<ILocalSymbol, IOperation> pureLocals;
        readonly IReadOnlyDictionary<ILocalSymbol, ImmutableArray<IOperation>> forkResultTuples;
        readonly IReadOnlyList<AwaitFlow> awaits;
        readonly IReadOnlyList<AuthoredOutput> authoredOutputs;
        readonly IReadOnlyDictionary<ISymbol, PatternOutput> patternOutputs;
        readonly IReadOnlyList<BranchObligation> requestObligations;
        readonly Dictionary<ISymbol, string> outputBySymbol;
        readonly Dictionary<IParameterSymbol, string> obligationByParameter;
        readonly Dictionary<string, SyntaxNode> exactIdentities = new(StringComparer.Ordinal);
        readonly Dictionary<string, string> emittedExactTerminals = new(StringComparer.Ordinal);
        readonly List<string> builderStatements = [];
        readonly HashSet<ILocalSymbol> resolvingPureLocals = new(SymbolEqualityComparer.Default);
        int valueOrdinal;

        public DefinitionEmitter(
            SourceProductionContext productionContext,
            IMethodSymbol method,
            IParameterSymbol inputParameter,
            ITypeSymbol resultType,
            IReadOnlyDictionary<ILocalSymbol, IOperation> pureLocals,
            IReadOnlyDictionary<ILocalSymbol, ImmutableArray<IOperation>> forkResultTuples,
            IReadOnlyList<AwaitFlow> awaits,
            IReadOnlyList<AuthoredOutput> authoredOutputs,
            IReadOnlyList<PatternOutput> patternOutputs,
            IReadOnlyList<BranchObligation> requestObligations)
        {
            this.productionContext = productionContext;
            this.method = method;
            this.inputParameter = inputParameter;
            this.resultType = resultType;
            this.pureLocals = pureLocals;
            this.forkResultTuples = forkResultTuples;
            this.awaits = awaits;
            this.authoredOutputs = authoredOutputs;
            this.patternOutputs = patternOutputs.ToDictionary(
                static output => (ISymbol)output.Symbol,
                SymbolEqualityComparer.Default);
            this.requestObligations = requestObligations;
            outputBySymbol = new(SymbolEqualityComparer.Default);
            obligationByParameter = new(SymbolEqualityComparer.Default);
            foreach (var awaited in awaits)
            {
                outputBySymbol.Add(awaited.Local, awaited.OutputVariable);
            }

            foreach (var output in authoredOutputs)
            {
                if (output.Symbol is not null)
                {
                    outputBySymbol.Add(output.Symbol, output.Variable);
                }
            }

            foreach (var obligation in requestObligations)
            {
                obligationByParameter.Add(obligation.Parameter, obligation.Variable);
            }
        }

        public bool TryEmit(FlowBlock body, out GeneratedDefinition generated)
        {
            generated = null!;
            var identities = new List<string>();
            foreach (var statement in body.Descendants())
            {
                if (statement is ActionFlow { Kind: ActionKind.ContinueAt })
                {
                    continue;
                }
                if (!TryDeclareIdentity(
                        statement.Identity,
                        statement switch
                        {
                            AwaitFlow awaited => Argument(awaited.Invocation, "id"),
                            ForkJoinFlow forkJoin => Argument(forkJoin.Invocation, "id"),
                            ActionFlow action => Argument(action.Invocation, "id"),
                            RequestFlow request => Argument(request.Invocation, "id"),
                            PartitionFlow partition => Argument(partition.Invocation, "id"),
                            RecurrenceFlow recurrence => Argument(recurrence.Invocation, "id"),
                            AwaitMatchFlow awaitMatch => Argument(awaitMatch.Invocation, "id"),
                            ExplicitDecisionFlow decision => Argument(decision.Invocation, "id"),
                            ReturnFlow terminal when terminal.Invocation is not null =>
                                Argument(terminal.Invocation, "id"),
                            _ => null
                        },
                        statement.Syntax,
                        identities))
                {
                    return false;
                }

                if (statement is ForkJoinFlow fork)
                {
                    if (!TryDeclareOwnedIdentity(
                            fork.JoinIdentity,
                            Argument(fork.Invocation, "joinId"),
                            fork.Identity,
                            "join",
                            statement.Syntax,
                            identities))
                    {
                        return false;
                    }

                    foreach (var branch in fork.Branches)
                    {
                        if (!TryDeclareOwnedIdentity(
                                branch.Identity,
                                branch.ExplicitId,
                                fork.Identity,
                                branch.Identity.PathSegment,
                                branch.Syntax,
                                identities))
                        {
                            return false;
                        }
                    }
                }
                else if (statement is RequestFlow request)
                {
                    foreach (var outcome in request.Outcomes)
                    {
                        if (!TryDeclareOwnedIdentity(
                                outcome.Identity,
                                outcome.Declaration is null ? null : Argument(outcome.Declaration, "id"),
                                request.Identity,
                                outcome.Identity.PathSegment,
                                outcome.Syntax,
                                identities))
                        {
                            return false;
                        }
                    }
                }
                else if (statement is AwaitMatchFlow awaitMatch)
                {
                    foreach (var clause in awaitMatch.Clauses)
                    {
                        if (!TryDeclareOwnedIdentity(
                                clause.Identity,
                                Argument(clause.Declaration, "id"),
                                awaitMatch.Identity,
                                clause.Identity.PathSegment,
                                clause.Syntax,
                                identities))
                        {
                            return false;
                        }
                    }
                }
                else if (statement is ExplicitDecisionFlow decision)
                {
                    foreach (var arm in decision.Arms)
                    {
                        if (!TryDeclareOwnedIdentity(
                                arm.Identity,
                                Argument(arm.Declaration, "id"),
                                decision.Identity,
                                arm.Identity.PathSegment,
                                arm.Syntax,
                                identities))
                        {
                            return false;
                        }
                    }
                    if (decision.Fallback is not null
                        && !TryDeclareOwnedIdentity(
                            decision.Fallback.Identity,
                            Argument(decision.Invocation, "fallbackId"),
                            decision.Identity,
                            "otherwise",
                            decision.Fallback.Syntax,
                            identities))
                    {
                        return false;
                    }
                }
            }

            var outputDeclarations = ImmutableArray.CreateBuilder<string>(
                awaits.Count + authoredOutputs.Count + requestObligations.Count);
            foreach (var awaited in awaits)
            {
                if (!TryEmitRole(
                        awaited.Invocation,
                        "outputRole",
                        "result",
                        awaited.Syntax,
                        out var role))
                {
                    return false;
                }
                outputDeclarations.Add(
                    $"var {awaited.OutputVariable} = __builder.Output<{FormatType(awaited.Local.Type)}>(owner: {awaited.Identity.Variable}, role: {role}, {SourceArguments(awaited.Source, method.Name)});");
            }
            foreach (var output in authoredOutputs)
            {
                var owner = output.Owner.Variable;
                var role = Literal(output.Role);
                if (output.Declaration is not null
                    && (!TryEmitOptionalExact(
                            output.Declaration,
                            "outputOwner",
                            owner,
                            output.Declaration.Syntax,
                            out owner)
                        || !TryEmitRole(
                            output.Declaration,
                            "outputRole",
                            output.Role,
                            output.Declaration.Syntax,
                            out role)))
                {
                    return false;
                }
                outputDeclarations.Add(
                    $"var {output.Variable} = __builder.Output<{FormatType(output.Type)}>(owner: {owner}, role: {role}, {SourceArguments(output.Source, method.Name)});");
            }
            foreach (var obligation in requestObligations)
            {
                outputDeclarations.Add(
                    $"var {obligation.Variable} = __builder.RequestObligation(owner: {obligation.Owner.Variable}, role: \"request\", {SourceArguments(obligation.Source, method.Name)});");
            }
            var outputs = outputDeclarations.MoveToImmutable();

            if (!TryValidateForkResults(body))
            {
                return false;
            }

            if (!TryLowerBlock(body, successor: null, out var entry) || entry is null)
            {
                if (entry is null)
                {
                    Report(
                        productionContext,
                        UnsupportedStatement,
                        method.Locations[0],
                        method.Name,
                        "every reachable path must end in a return statement");
                }
                return false;
            }

            generated = new(
                [.. identities],
                outputs,
                [.. builderStatements],
                entry);
            return true;
        }

        bool TryValidateForkResults(FlowBlock body)
        {
            var translator = new PureExpressionEmitter(
                method,
                inputParameter,
                pureLocals,
                forkResultTuples,
                outputBySymbol,
                patternOutputs,
                resolvingPureLocals);
            foreach (var branch in body.Descendants()
                         .OfType<ForkJoinFlow>()
                         .SelectMany(static fork => fork.Branches))
            {
                if (branch.Result is null
                    || translator.TryEmit(branch.Result, out _, out var failure))
                {
                    continue;
                }

                Report(
                    productionContext,
                    UnsupportedPureExpression,
                    branch.Result.Syntax.GetLocation(),
                    method.Name,
                    $"typed ForkJoin branch '{branch.Name}' result is not portable: {failure}");
                return false;
            }

            return true;
        }

        bool TryDeclareIdentity(
            FlowIdentity identity,
            IArgumentOperation? explicitId,
            SyntaxNode syntax,
            ICollection<string> declarations)
        {
            var conventional =
                "global::Cohesive.Processes.Authoring.ProcessAuthoringIdentities.NodeFor(" +
                Path(identity.Path) + ")";
            return TryDeclareIdentity(identity, explicitId, conventional, syntax, declarations);
        }

        bool TryDeclareOwnedIdentity(
            FlowIdentity identity,
            IArgumentOperation? explicitId,
            FlowIdentity owner,
            string role,
            SyntaxNode syntax,
            ICollection<string> declarations)
        {
            var conventional =
                "global::Cohesive.Processes.Authoring.ProcessAuthoringIdentities.NodeFor(" +
                $"owner: {owner.Variable}, role: {Literal(role)})";
            return TryDeclareIdentity(identity, explicitId, conventional, syntax, declarations);
        }

        bool TryDeclareIdentity(
            FlowIdentity identity,
            IArgumentOperation? explicitId,
            string conventional,
            SyntaxNode syntax,
            ICollection<string> declarations)
        {
            string value;
            if (explicitId is null || explicitId.IsImplicit)
            {
                value = conventional;
            }
            else
            {
                if (!TryRegisterExactIdentity(explicitId, syntax))
                {
                    return false;
                }
                if (!TryEmitExactArgument(explicitId, syntax, out var authoredId))
                {
                    return false;
                }

                value = "((global::Cohesive.Execution.ExecutionNodeId?)(" + authoredId + ")) ?? " + conventional;
            }
            declarations.Add($"var {identity.Variable} = {value};");
            return true;
        }

        bool TryRegisterExactIdentity(IArgumentOperation explicitId, SyntaxNode syntax)
        {
            var operation = Strip(explicitId.Value);
            if (operation is not IObjectCreationOperation creation
                || creation.Constructor?.ContainingType.ToDisplayString() != "Cohesive.Execution.ExecutionNodeId"
                || creation.Arguments.Length != 1
                || Strip(creation.Arguments[0].Value).ConstantValue is not { HasValue: true, Value: string value })
            {
                return true;
            }
            if (!exactIdentities.ContainsKey(value))
            {
                exactIdentities.Add(value, syntax);
                return true;
            }

            Report(
                productionContext,
                DuplicateExactIdentity,
                explicitId.Syntax.GetLocation(),
                method.Name,
                value);
            return false;
        }

        bool TryLowerBlock(FlowBlock block, string? successor, out string? entry)
        {
            entry = successor;
            for (var index = block.Statements.Length - 1; index >= 0; index--)
            {
                var statement = block.Statements[index];
                switch (statement)
                {
                    case ReturnFlow returned:
                        if (!TryEmitValue(returned.Result, resultType, returned.Source, out var returnedValue))
                        {
                            return false;
                        }

                        var terminal = returned.Kind == TerminalAuthoringKind.Fail ? "Fail" : "Return";
                        if (!TryEmitTerminal(
                                returned.Identity,
                                returned.Invocation,
                                terminal,
                                returnedValue,
                                returned.Source,
                                returned.Syntax))
                        {
                            return false;
                        }
                        entry = returned.Identity.Variable;
                        break;

                    case AwaitFlow awaited:
                        if (entry is null)
                        {
                            return StatementFailure(awaited.Syntax, "an awaited operation requires a following operation or return");
                        }

                        if (!TryEmitAwait(awaited, entry))
                        {
                            return false;
                        }

                        entry = awaited.Identity.Variable;
                        break;

                    case ActionFlow action:
                        if (action.Kind == ActionKind.ContinueAt)
                        {
                            var target = Argument(action.Invocation, "target");
                            if (target is null
                                || !TryEmitExactArgument(target, action.Syntax, out var targetExpression))
                            {
                                return StatementFailure(action.Syntax, "ContinueAt requires one exact durable target node");
                            }

                            entry = targetExpression;
                            break;
                        }
                        if (action.Kind is ActionKind.Succeed or ActionKind.Terminate)
                        {
                            if (!TryEmitAction(action, successor: null))
                            {
                                return false;
                            }

                            entry = action.Identity.Variable;
                            break;
                        }
                        if (entry is null)
                        {
                            return StatementFailure(action.Syntax, "an awaited Process action requires a following operation or return");
                        }

                        if (!TryEmitAction(action, entry))
                        {
                            return false;
                        }

                        entry = action.Identity.Variable;
                        break;

                    case IfFlow conditional:
                        if (!TryLowerBlock(conditional.WhenTrue, entry, out var trueEntry))
                        {
                            return false;
                        }

                        string? falseEntry = entry;
                        if (conditional.WhenFalse is not null
                            && !TryLowerBlock(conditional.WhenFalse, entry, out falseEntry))
                        {
                            return false;
                        }

                        if (trueEntry is null || falseEntry is null)
                        {
                            return StatementFailure(conditional.Syntax, "both branches must return when no following continuation exists");
                        }

                        if (!TryEmitValue(
                                conditional.Condition,
                                conditional.Condition.Type!,
                                conditional.Source,
                                out var predicate))
                        {
                            return false;
                        }

                        var trueCase = $"__case_{conditional.Identity.Variable.Substring("__node_".Length)}";
                        var fallback = $"__fallback_{conditional.Identity.Variable.Substring("__node_".Length)}";
                        builderStatements.Add(
                            $"var {trueCase} = global::Cohesive.Processes.Authoring.ProcessAuthoringIdentities.NodeFor(owner: {conditional.Identity.Variable}, role: \"when-true\");");
                        builderStatements.Add(
                            $"var {fallback} = global::Cohesive.Processes.Authoring.ProcessAuthoringIdentities.NodeFor(owner: {conditional.Identity.Variable}, role: \"otherwise\");");
                        builderStatements.Add(
                            $"__builder.Choice(id: {conditional.Identity.Variable}, selection: global::Cohesive.Execution.CaseSelection.OrderedFirstMatch, completeness: global::Cohesive.Execution.BranchCompleteness.Fallback, cases: [__builder.ChoiceCase(id: {trueCase}, predicate: {predicate}, next: __builder.Edge(owner: {trueCase}, role: \"next\", target: {trueEntry}, {SourceArguments(conditional.Source, method.Name)}), {SourceArguments(conditional.Source, method.Name)})], fallback: __builder.Fallback(id: {fallback}, next: __builder.Edge(owner: {fallback}, role: \"next\", target: {falseEntry}, {SourceArguments(conditional.Source, method.Name)}), {SourceArguments(conditional.Source, method.Name)}), {SourceArguments(conditional.Source, method.Name)});");
                        entry = conditional.Identity.Variable;
                        break;

                    case MatchFlow match:
                        if (!TryEmitMatch(match, entry, out var matchEntry))
                        {
                            return false;
                        }

                        entry = matchEntry;
                        break;

                    case ExplicitDecisionFlow decision:
                        if (!TryEmitExplicitDecision(decision, entry))
                        {
                            return false;
                        }

                        entry = decision.Identity.Variable;
                        break;

                    case ForkJoinFlow forkJoin:
                        if (entry is null)
                        {
                            return StatementFailure(
                                forkJoin.Syntax,
                                "ForkJoin requires a following operation or return");
                        }
                        if (!TryEmitForkJoin(forkJoin, entry))
                        {
                            return false;
                        }

                        entry = forkJoin.Identity.Variable;
                        break;

                    case RequestFlow request:
                        if (!TryEmitRequest(request, entry))
                        {
                            return false;
                        }

                        entry = request.Identity.Variable;
                        break;

                    case PartitionFlow partition:
                        if (entry is null)
                        {
                            return StatementFailure(
                                partition.Syntax,
                                "ForEachPartition requires a following operation or return");
                        }

                        if (!TryEmitPartition(partition, entry))
                        {
                            return false;
                        }

                        entry = partition.Identity.Variable;
                        break;

                    case RecurrenceFlow recurrence:
                        if (entry is null)
                        {
                            return StatementFailure(
                                recurrence.Syntax,
                                "RepeatAcrossActivation requires a following operation or return");
                        }

                        if (!TryEmitRecurrence(recurrence, entry, out var occurrenceEntry))
                        {
                            return false;
                        }

                        entry = occurrenceEntry;
                        break;

                    case AwaitMatchFlow awaitMatch:
                        if (!TryEmitAwaitMatch(awaitMatch, entry))
                        {
                            return false;
                        }

                        entry = awaitMatch.Identity.Variable;
                        break;
                }
            }
            return true;
        }

        bool TryEmitForkJoin(ForkJoinFlow forkJoin, string successor)
        {
            List<string> branches = [];
            List<string> branchResults = [];
            string? resultContract = null;
            foreach (var branch in forkJoin.Branches)
            {
                if (!TryLowerBlock(branch.Body, forkJoin.JoinIdentity.Variable, out var branchEntry)
                    || branchEntry is null)
                {
                    return StatementFailure(
                        branch.Syntax,
                        $"ForkJoin branch '{branch.Name}' requires a reachable Process operation");
                }

                var branchVariable = $"__fork_branch_{branches.Count.ToString(CultureInfo.InvariantCulture)}_{forkJoin.Identity.Variable.Substring("__node_".Length)}";
                var capacityDomain = "null";
                if (branch.CapacityDomain is not null
                    && !TryEmitExactArgument(
                        branch.CapacityDomain,
                        branch.CapacityDomain.Syntax,
                        out capacityDomain))
                {
                    return false;
                }

                var branchRole = "\"start\"";
                if (branch.Role is not null
                    && !TryEmitExactArgument(branch.Role, branch.Role.Syntax, out branchRole))
                {
                    return false;
                }
                var branchEdgeOwner = branch.Identity.Variable;
                if (branch.EdgeOwner is not null
                    && !TryEmitExactArgument(branch.EdgeOwner, branch.EdgeOwner.Syntax, out branchEdgeOwner))
                {
                    return false;
                }

                builderStatements.Add(
                    $"var {branchVariable} = __builder.ForkBranch(id: {branch.Identity.Variable}, start: __builder.Edge(owner: {branchEdgeOwner}, role: {branchRole}, target: {branchEntry}, {SourceArguments(branch.Source, method.Name)}), capacityDomain: {capacityDomain}, {SourceArguments(branch.Source, method.Name)});");
                branches.Add(branchVariable);

                if (forkJoin.Mode != ForkAuthoringMode.All)
                {
                    if (branch.Result?.Type is null
                        || !TryEmitValue(branch.Result, branch.Result.Type, branch.Source, out var resultValue))
                    {
                        return StatementFailure(
                            branch.Syntax,
                            $"partial Fork branch '{branch.Name}' requires one portable typed result");
                    }
                    resultContract ??= resultValue + ".Contract";
                    var resultVariable = $"__join_branch_result_{branchResults.Count.ToString(CultureInfo.InvariantCulture)}_{forkJoin.Identity.Variable.Substring("__node_".Length)}";
                    builderStatements.Add(
                        $"var {resultVariable} = __builder.JoinBranchResult(branch: {branch.Identity.Variable}, result: {resultValue}, {SourceArguments(branch.Source, method.Name)});");
                    branchResults.Add(resultVariable);
                }
            }

            var admission = Argument(forkJoin.Invocation, "admission");
            var authoredAdmission = admission is not null
                && !admission.IsImplicit
                && !(Strip(admission.Value).ConstantValue is { HasValue: true, Value: null });
            var unboundCapacity = forkJoin.Branches.FirstOrDefault(static branch => branch.CapacityDomain is not null);
            if (!authoredAdmission && unboundCapacity?.CapacityDomain is { } capacity)
            {
                return StatementFailure(
                    capacity.Syntax,
                    "a ForkJoin branch capacity domain requires an explicit admission policy declaring its canonical limit");
            }
            if (authoredAdmission)
            {
                if (!TryEmitExactArgument(admission!, admission!.Value.Syntax, out var admissionExpression))
                {
                    return false;
                }

                var suffix = forkJoin.Identity.Variable.Substring("__node_".Length);
                var admissionVariable = $"__fork_admission_{suffix}";
                builderStatements.Add(
                    $"var {admissionVariable} = ({admissionExpression}) ?? throw new global::System.ArgumentNullException(\"admission\");");
                builderStatements.Add(
                    $"__builder.Fork(id: {forkJoin.Identity.Variable}, branches: [{string.Join(", ", branches)}], join: {forkJoin.JoinIdentity.Variable}, limits: new global::Cohesive.Processes.IR.ProcessWorkLimits(maximumItems: {branches.Count.ToString(CultureInfo.InvariantCulture)}, maximumStartsPerActivation: {admissionVariable}.MaximumStartsPerActivation ?? {branches.Count.ToString(CultureInfo.InvariantCulture)}, maximumParallelism: {admissionVariable}.MaximumParallelism, minimumParallelism: {admissionVariable}.MinimumParallelism), capacityDomains: {admissionVariable}.CapacityDomains, {SourceArguments(forkJoin.Source, method.Name)});");
            }
            else
            {
                builderStatements.Add(
                    $"__builder.Fork(id: {forkJoin.Identity.Variable}, branches: [{string.Join(", ", branches)}], join: {forkJoin.JoinIdentity.Variable}, {SourceArguments(forkJoin.Source, method.Name)});");
            }
            if (forkJoin.Mode == ForkAuthoringMode.All)
            {
                if (!TryEmitRole(
                        forkJoin.Invocation,
                        "nextRole",
                        "next",
                        forkJoin.Syntax,
                        out var nextRole))
                {
                    return false;
                }
                builderStatements.Add(
                    $"__builder.Join(id: {forkJoin.JoinIdentity.Variable}, fork: {forkJoin.Identity.Variable}, policy: new global::Cohesive.Processes.IR.ProcessJoinPolicy(mode: global::Cohesive.Processes.IR.ProcessJoinMode.All, requiredCount: 0, failure: global::Cohesive.Processes.IR.ProcessJoinFailurePolicy.FailFast, cancellation: global::Cohesive.Processes.IR.ProcessJoinCancellationPolicy.AwaitRemaining, completionOrder: global::Cohesive.Processes.IR.ProcessJoinCompletionOrder.Unobservable, tieBreak: global::Cohesive.Processes.IR.ProcessJoinTieBreak.BranchIdentity), next: __builder.Edge(owner: {forkJoin.JoinIdentity.Variable}, role: {nextRole}, target: {successor}, {SourceArguments(forkJoin.Source, method.Name)}), {SourceArguments(forkJoin.Source, method.Name)});");
                return true;
            }

            var policy = Argument(forkJoin.Invocation, "policy");
            if (policy is null || forkJoin.Result is null || resultContract is null)
            {
                return StatementFailure(
                    forkJoin.Syntax,
                    "Any and RequiredCount Forks require an explicit policy and one materialized selection result");
            }
            if (!TryEmitExactArgument(policy, policy.Syntax, out var policyExpression))
            {
                return false;
            }

            var partialSuffix = forkJoin.Identity.Variable.Substring("__node_".Length);
            var policyVariable = $"__join_policy_{partialSuffix}";
            var projectionVariable = $"__join_result_{partialSuffix}";
            builderStatements.Add(
                $"var {policyVariable} = ({policyExpression}) ?? throw new global::System.ArgumentNullException(\"policy\");");
            builderStatements.Add(
                $"var {projectionVariable} = __builder.JoinResult(output: {forkJoin.Result.Variable}, resultContract: {resultContract}, branches: [{string.Join(", ", branchResults)}], {SourceArguments(forkJoin.Source, method.Name)});");
            builderStatements.Add(
                $"__builder.Join(id: {forkJoin.JoinIdentity.Variable}, fork: {forkJoin.Identity.Variable}, policy: {policyVariable}, next: __builder.Edge(owner: {forkJoin.JoinIdentity.Variable}, role: \"next\", target: {successor}, {SourceArguments(forkJoin.Source, method.Name)}), result: {projectionVariable}, {SourceArguments(forkJoin.Source, method.Name)});");
            return true;
        }

        bool TryEmitAction(ActionFlow action, string? successor)
        {
            switch (action.Kind)
            {
                case ActionKind.Timer:
                    if (successor is null)
                    {
                        return StatementFailure(action.Syntax, "Timer requires a following operation or terminal");
                    }
                    var dueAt = Argument(action.Invocation, "dueAt");
                    if (dueAt is null
                        || !TryEmitValue(dueAt.Value, dueAt.Value.Type!, action.Source, out var dueAtValue))
                    {
                        return StatementFailure(action.Syntax, "Timer requires a portable absolute due instant");
                    }

                    builderStatements.Add(
                        $"__builder.Timer(id: {action.Identity.Variable}, dueAt: {dueAtValue}, next: __builder.Edge(owner: {action.Identity.Variable}, role: \"next\", target: {successor}, {SourceArguments(action.Source, method.Name)}), {SourceArguments(action.Source, method.Name)});");
                    return true;

                case ActionKind.Reply:
                    if (successor is null)
                    {
                        return StatementFailure(action.Syntax, "Reply requires a following operation or terminal");
                    }
                    var contract = Argument(action.Invocation, "contract");
                    var request = Argument(action.Invocation, "request");
                    var payload = Argument(action.Invocation, "payload");
                    var requestOperation = request is null ? null : Strip(request.Value);
                    if (contract is null
                        || requestOperation is not IParameterReferenceOperation requestParameter
                        || !obligationByParameter.TryGetValue(requestParameter.Parameter, out var obligation)
                        || payload is null
                        || !TryEmitValue(payload.Value, payload.Value.Type!, action.Source, out var replyPayload)
                        || !TryEmitExactArgument(contract, action.Syntax, out var replyContract))
                    {
                        return StatementFailure(
                            action.Syntax,
                            "Reply requires an exact contract, a Request obligation from the selected AwaitMatch branch, and a portable payload");
                    }
                    builderStatements.Add(
                        $"__builder.Reply(id: {action.Identity.Variable}, contract: {replyContract}, request: {obligation}, payload: {replyPayload}, next: __builder.Edge(owner: {action.Identity.Variable}, role: \"next\", target: {successor}, {SourceArguments(action.Source, method.Name)}), {SourceArguments(action.Source, method.Name)});");
                    return true;

                case ActionKind.Transition:
                    if (successor is null)
                    {
                        return StatementFailure(action.Syntax, "Transition requires a following operation or terminal");
                    }
                    var transition = Argument(action.Invocation, "transition");
                    var subject = Argument(action.Invocation, "subject");
                    var transitionInput = Argument(action.Invocation, "input");
                    if (transition is null
                        || subject is null
                        || transitionInput is null
                        || !TryEmitValue(subject.Value, subject.Value.Type!, action.Source, out var subjectValue)
                        || !TryEmitValue(transitionInput.Value, transitionInput.Value.Type!, action.Source, out var transitionInputValue)
                        || !TryEmitExactArgument(transition, action.Syntax, out var transitionReference)
                        || !TryEmitRole(action.Invocation, "nextRole", "next", action.Syntax, out var transitionRole))
                    {
                        return StatementFailure(
                            action.Syntax,
                            "Transition requires an exact definition, portable subject and input, and a stable continuation role");
                    }
                    builderStatements.Add(
                        $"__builder.InvokeTransition(id: {action.Identity.Variable}, transition: {transitionReference}, subject: {subjectValue}, input: {transitionInputValue}, continuation: __builder.Continuation(edge: __builder.Edge(owner: {action.Identity.Variable}, role: {transitionRole}, target: {successor}, {SourceArguments(action.Source, method.Name)}), {SourceArguments(action.Source, method.Name)}), {SourceArguments(action.Source, method.Name)});");
                    return true;

                case ActionKind.Succeed:
                case ActionKind.Terminate:
                    var result = Argument(action.Invocation, "result");
                    if (result is null
                        || !TryEmitValue(result.Value, resultType, action.Source, out var terminalResult))
                    {
                        return StatementFailure(action.Syntax, "an explicit Process terminal requires one portable root result");
                    }
                    var terminal = action.Kind == ActionKind.Terminate ? "Fail" : "Return";
                    return TryEmitTerminal(
                        action.Identity,
                        action.Invocation,
                        terminal,
                        terminalResult,
                        action.Source,
                        action.Syntax);

                default:
                    return StatementFailure(action.Syntax, "unsupported awaited Process action");
            }
        }

        bool TryEmitTerminal(
            FlowIdentity identity,
            IInvocationOperation? invocation,
            string terminal,
            string result,
            SourceReference source,
            SyntaxNode syntax)
        {
            var id = invocation is null ? null : Argument(invocation, "id");
            if (id is not null
                && !id.IsImplicit
                && Strip(id.Value).ConstantValue is not { HasValue: true, Value: null })
            {
                if (!TryEmitExactArgument(id, syntax, out var exactId))
                {
                    return false;
                }

                var authoredResult = Argument(invocation!, "result");
                var signature = terminal + ":" + (authoredResult?.Value.Syntax.ToString() ?? result);
                if (emittedExactTerminals.TryGetValue(exactId, out var prior))
                {
                    if (prior == signature)
                    {
                        return true;
                    }

                    return StatementFailure(
                        syntax,
                        $"exact terminal identity '{exactId}' is reused with incompatible result semantics");
                }

                emittedExactTerminals.Add(exactId, signature);
            }

            builderStatements.Add(
                $"__builder.{terminal}(id: {identity.Variable}, result: {result}, {SourceArguments(source, method.Name)});");
            return true;
        }

        bool TryEmitRequest(RequestFlow request, string? successor)
        {
            var input = Argument(request.Invocation, "input");
            if (input is null
                || !TryEmitValue(input.Value, input.Value.Type!, request.Source, out var payload))
            {
                return StatementFailure(request.Syntax, "a multi-outcome Request requires a portable payload");
            }

            string requestContract;
            string? protocolExpression = null;
            var protocol = Argument(request.Invocation, "protocol");
            if (request.Kind == RequestAuthoringKind.ChildProcess && protocol is not null)
            {
                if (!TryEmitExactArgument(protocol, request.Syntax, out protocolExpression))
                {
                    return false;
                }
                var protocolVariable =
                    $"__protocol_{request.Identity.Variable.Substring("__node_".Length)}";
                builderStatements.Add($"var {protocolVariable} = {protocolExpression};");
                protocolExpression = protocolVariable;
                requestContract = $"{protocolExpression}.Request";
            }
            else
            {
                var contract = Argument(request.Invocation, "contract");
                if (contract is null
                    || !TryEmitExactArgument(contract, request.Syntax, out requestContract))
                {
                    return StatementFailure(
                        request.Syntax,
                        "a multi-outcome Request requires an exact contract");
                }
            }

            List<string> outcomes = [];
            foreach (var outcome in request.Outcomes)
            {
                if (!TryLowerBlock(outcome.Body, successor, out var branchEntry) || branchEntry is null)
                {
                    return StatementFailure(outcome.Syntax, $"Request outcome '{outcome.Name}' requires a reachable continuation");
                }

                string terminalOutcomeExpression;
                string role;
                string edgeOwner;
                if (outcome.ChildTerminalMember is not null)
                {
                    if (protocolExpression is null)
                    {
                        return StatementFailure(
                            outcome.Syntax,
                            "semantic child outcomes require a typed invocation protocol");
                    }
                    terminalOutcomeExpression =
                        $"{protocolExpression}.OutcomeMapping.{outcome.ChildTerminalMember}";
                    role = "\"next\"";
                    edgeOwner = outcome.Identity.Variable;
                }
                else
                {
                    var terminalOutcome = outcome.Declaration is null
                        ? null
                        : Argument(outcome.Declaration, "outcome");
                    if (terminalOutcome is null
                        || !TryEmitExactArgument(
                            terminalOutcome,
                            outcome.Syntax,
                            out terminalOutcomeExpression)
                        || !TryEmitRole(
                            outcome.Declaration!,
                            "role",
                            "next",
                            outcome.Syntax,
                            out role)
                        || !TryEmitOptionalExact(
                            outcome.Declaration!,
                            "edgeOwner",
                            outcome.Identity.Variable,
                            outcome.Syntax,
                            out edgeOwner))
                    {
                        return false;
                    }
                }

                if (string.IsNullOrWhiteSpace(terminalOutcomeExpression))
                {
                    return false;
                }

                var edge = $"__builder.Edge(owner: {edgeOwner}, role: {role}, target: {branchEntry}, {SourceArguments(outcome.Source, method.Name)})";
                var continuation = outcome.Input is null
                    ? $"__builder.Continuation(edge: {edge}, {SourceArguments(outcome.Source, method.Name)})"
                    : $"__builder.Continuation(edge: {edge}, output: {outputBySymbol[outcome.Input]}, {SourceArguments(outcome.Source, method.Name)})";
                outcomes.Add(
                    $"__builder.RequestOutcome(id: {outcome.Identity.Variable}, outcome: {terminalOutcomeExpression}, continuation: {continuation}, {SourceArguments(outcome.Source, method.Name)})");
            }

            if (request.Kind == RequestAuthoringKind.Request)
            {
                builderStatements.Add(
                    $"__builder.Request(id: {request.Identity.Variable}, contract: {requestContract}, payload: {payload}, outcomes: [{string.Join(", ", outcomes)}], {SourceArguments(request.Source, method.Name)});");
                return true;
            }

            var purpose = Argument(request.Invocation, "purpose");
            var cancellation = Argument(request.Invocation, "cancellation");
            if (purpose is null
                || cancellation is null
                || !TryEmitExactArgument(purpose, request.Syntax, out var purposeExpression)
                || !TryEmitExactArgument(cancellation, request.Syntax, out var cancellationExpression))
            {
                return StatementFailure(
                    request.Syntax,
                    "InvokeProcess requires exact child definition, outcome mapping, purpose, and cancellation policy");
            }

            string processExpression;
            string outcomeMappingExpression;
            if (protocolExpression is not null)
            {
                processExpression = $"{protocolExpression}.Process.Reference";
                outcomeMappingExpression = $"{protocolExpression}.OutcomeMapping";
            }
            else
            {
                var process = Argument(request.Invocation, "process");
                var outcomeMapping = Argument(request.Invocation, "outcomeMapping");
                if (process is null
                    || outcomeMapping is null
                    || !TryEmitExactArgument(process, request.Syntax, out processExpression)
                    || !TryEmitExactArgument(
                        outcomeMapping,
                        request.Syntax,
                        out outcomeMappingExpression))
                {
                    return StatementFailure(
                        request.Syntax,
                        "InvokeProcess requires an exact child definition and outcome mapping");
                }
            }

            builderStatements.Add(
                $"__builder.InvokeProcess(id: {request.Identity.Variable}, process: {processExpression}, contract: {requestContract}, outcomeMapping: {outcomeMappingExpression}, input: {payload}, purpose: {purposeExpression}, cancellation: {cancellationExpression}, outcomes: [{string.Join(", ", outcomes)}], {SourceArguments(request.Source, method.Name)});");
            return true;
        }

        bool TryEmitPartition(PartitionFlow partition, string successor)
        {
            if (!TryLowerBlock(partition.Failed, successor, out var failedEntry) || failedEntry is null)
            {
                return StatementFailure(
                    partition.Syntax,
                    $"ForEachPartition failed branch '{partition.FailedName}' requires a reachable continuation");
            }

            var partitions = Argument(partition.Invocation, "partitions");
            var process = Argument(partition.Invocation, "process");
            var contract = Argument(partition.Invocation, "contract");
            var outcomeMapping = Argument(partition.Invocation, "outcomeMapping");
            var limits = Argument(partition.Invocation, "limits");
            var failure = Argument(partition.Invocation, "failure");
            var capacityDomains = Argument(partition.Invocation, "capacityDomains");
            var cancellation = Argument(partition.Invocation, "cancellation");
            if (partitions is null
                || process is null
                || contract is null
                || outcomeMapping is null
                || limits is null
                || failure is null
                || capacityDomains is null
                || cancellation is null
                || Strip(partitions.Value).Type is not { } partitionsType)
            {
                return StatementFailure(
                    partition.Syntax,
                    "ForEachPartition requires a portable finite collection and exact child, limits, capacity, failure, and cancellation semantics");
            }
            if (!TryEmitValue(partitions.Value, partitionsType, partition.Source, out var partitionsValue)
                || !TryEmitProjection(
                    partition.ProgressIdentity,
                    partition.Partition.Variable,
                    out var progressIdentity)
                || !TryEmitProjection(
                    partition.ChildInput,
                    partition.Partition.Variable,
                    out var childInput)
                || !TryEmitExactArgument(process, process.Syntax, out var processExpression)
                || !TryEmitExactArgument(contract, contract.Syntax, out var contractExpression)
                || !TryEmitExactArgument(outcomeMapping, outcomeMapping.Syntax, out var outcomeMappingExpression)
                || !TryEmitExactArgument(limits, limits.Syntax, out var limitsExpression)
                || !TryEmitExactArgument(failure, failure.Syntax, out var failureExpression)
                || !TryEmitExactArgument(
                    capacityDomains,
                    capacityDomains.Syntax,
                    out var capacityDomainsExpression)
                || !TryEmitExactArgument(cancellation, cancellation.Syntax, out var cancellationExpression))
            {
                return false;
            }

            var capacityIdentity = "null";
            if (partition.CapacityIdentity is not null
                && !TryEmitProjection(
                    partition.CapacityIdentity,
                    partition.Partition.Variable,
                    out capacityIdentity))
            {
                return false;
            }

            builderStatements.Add(
                $"__builder.ForEachPartition(id: {partition.Identity.Variable}, partitions: {partitionsValue}, partition: {partition.Partition.Variable}, progressIdentity: {progressIdentity}, process: {processExpression}, contract: {contractExpression}, outcomeMapping: {outcomeMappingExpression}, childInput: {childInput}, limits: {limitsExpression}, failure: {failureExpression}, capacityIdentity: {capacityIdentity}, capacityDomains: {capacityDomainsExpression}, cancellation: {cancellationExpression}, completed: __builder.Edge(owner: {partition.Identity.Variable}, role: \"completed\", target: {successor}, {SourceArguments(partition.Source, method.Name)}), failed: __builder.Edge(owner: {partition.Identity.Variable}, role: \"failed\", target: {failedEntry}, {SourceArguments(partition.Source, method.Name)}), {SourceArguments(partition.Source, method.Name)});");
            return true;
        }

        bool TryEmitProjection(
            ProjectionFlow projection,
            string input,
            out string value)
        {
            outputBySymbol.Add(projection.Parameter, input);
            try
            {
                return TryEmitValue(
                    projection.Expression,
                    projection.Expression.Type!,
                    projection.Source,
                    out value);
            }
            finally
            {
                outputBySymbol.Remove(projection.Parameter);
            }
        }

        bool TryEmitRecurrence(
            RecurrenceFlow recurrence,
            string successor,
            out string occurrenceEntry)
        {
            occurrenceEntry = string.Empty;
            if (!TryLowerBlock(recurrence.Exhausted, successor, out var exhaustedEntry)
                || exhaustedEntry is null)
            {
                return StatementFailure(
                    recurrence.Syntax,
                    $"RepeatAcrossActivation exhausted branch '{recurrence.ExhaustedName}' requires a reachable continuation");
            }
            if (!TryLowerBlock(recurrence.Stalled, successor, out var stalledEntry)
                || stalledEntry is null)
            {
                return StatementFailure(
                    recurrence.Syntax,
                    $"RepeatAcrossActivation stalled branch '{recurrence.StalledName}' requires a reachable continuation");
            }
            if (!TryLowerBlock(recurrence.Occurrence, recurrence.Identity.Variable, out var loweredOccurrence)
                || loweredOccurrence is null)
            {
                return StatementFailure(
                    recurrence.Syntax,
                    $"RepeatAcrossActivation occurrence '{recurrence.OccurrenceName}' requires a reachable Process operation");
            }

            var policy = Argument(recurrence.Invocation, "policy");
            if (policy is null)
            {
                return StatementFailure(
                    recurrence.Syntax,
                    "RepeatAcrossActivation requires an exact recurrence policy");
            }
            if (!TryEmitProjection(recurrence.ContinueWhen, recurrence.Result, out var continueWhen)
                || !TryEmitProjection(recurrence.Progress, recurrence.Result, out var progress))
            {
                return false;
            }
            if (!TryEmitExactArgument(policy, policy.Syntax, out var policyExpression))
            {
                return false;
            }

            builderStatements.Add(
                $"__builder.RepeatAcrossActivation(id: {recurrence.Identity.Variable}, continueWhen: {continueWhen}, progress: {progress}, policy: {policyExpression}, repeat: __builder.Edge(owner: {recurrence.Identity.Variable}, role: \"repeat\", target: {loweredOccurrence}, {SourceArguments(recurrence.Source, method.Name)}), completed: __builder.Edge(owner: {recurrence.Identity.Variable}, role: \"completed\", target: {successor}, {SourceArguments(recurrence.Source, method.Name)}), exhausted: __builder.Edge(owner: {recurrence.Identity.Variable}, role: \"exhausted\", target: {exhaustedEntry}, {SourceArguments(recurrence.Source, method.Name)}), stalled: __builder.Edge(owner: {recurrence.Identity.Variable}, role: \"stalled\", target: {stalledEntry}, {SourceArguments(recurrence.Source, method.Name)}), {SourceArguments(recurrence.Source, method.Name)});");
            occurrenceEntry = loweredOccurrence;
            return true;
        }

        bool TryEmitProjection(
            ProjectionFlow projection,
            IOperation input,
            out string value)
        {
            Dictionary<IParameterSymbol, IOperation> projectedParameters = new(SymbolEqualityComparer.Default)
            {
                [projection.Parameter] = input
            };
            return TryEmitValue(
                projection.Expression,
                projection.Expression.Type!,
                projection.Source,
                out value,
                projectedParameters);
        }

        bool TryEmitAwaitMatch(AwaitMatchFlow awaitMatch, string? successor)
        {
            List<string> clauses = [];
            foreach (var clause in awaitMatch.Clauses)
            {
                if (!TryLowerBlock(clause.Body, successor, out var branchEntry) || branchEntry is null)
                {
                    return StatementFailure(clause.Syntax, $"AwaitMatch clause '{clause.Name}' requires a reachable continuation");
                }
                var priority = Argument(clause.Declaration, "priority");
                var priorityExpression = priority is { IsImplicit: true }
                    ? "0"
                    : null;
                if (priority is null
                    || priorityExpression is null
                    && !TryEmitExactArgument(priority, clause.Syntax, out priorityExpression))
                {
                    return StatementFailure(clause.Syntax, "AwaitMatch clause priority must be exact");
                }

                if (clause.Kind == AwaitClauseKind.Timer)
                {
                    var dueAt = Argument(clause.Declaration, "dueAt");
                    if (dueAt is null
                        || !TryEmitValue(dueAt.Value, dueAt.Value.Type!, clause.Source, out var dueAtValue)
                        || !TryEmitRole(clause.Declaration, "role", "next", clause.Syntax, out var role)
                        || !TryEmitOptionalExact(
                            clause.Declaration,
                            "edgeOwner",
                            clause.Identity.Variable,
                            clause.Syntax,
                            out var edgeOwner))
                    {
                        return StatementFailure(clause.Syntax, "an AwaitMatch timer clause requires a portable absolute due instant");
                    }

                    clauses.Add(
                        $"__builder.AwaitTimerClause(id: {clause.Identity.Variable}, dueAt: {dueAtValue}, priority: {priorityExpression}, continuation: __builder.Continuation(edge: __builder.Edge(owner: {edgeOwner}, role: {role}, target: {branchEntry}, {SourceArguments(clause.Source, method.Name)}), {SourceArguments(clause.Source, method.Name)}), {SourceArguments(clause.Source, method.Name)})");
                    continue;
                }

                var contract = Argument(clause.Declaration, "contract");
                if (contract is null
                    || clause.Input is null
                    || !TryEmitExactArgument(contract, clause.Syntax, out var interactionContract))
                {
                    return StatementFailure(clause.Syntax, "an AwaitMatch interaction clause requires an exact contract and typed input");
                }

                var output = clause.Input.Variable;
                if (!TryEmitGuard(clause, output, out var guard))
                {
                    return false;
                }
                if (!TryEmitRole(clause.Declaration, "role", "next", clause.Syntax, out var interactionRole))
                {
                    return false;
                }
                if (!TryEmitOptionalExact(
                        clause.Declaration,
                        "edgeOwner",
                        clause.Identity.Variable,
                        clause.Syntax,
                        out var interactionEdgeOwner))
                {
                    return false;
                }

                var obligation = clause.RequestObligation is null
                    ? "null"
                    : obligationByParameter[clause.RequestObligation];
                clauses.Add(
                    $"__builder.AwaitInteractionClause(id: {clause.Identity.Variable}, contract: {interactionContract}, input: {output}, requestObligation: {obligation}, guard: {guard}, priority: {priorityExpression}, continuation: __builder.Continuation(edge: __builder.Edge(owner: {interactionEdgeOwner}, role: {interactionRole}, target: {branchEntry}, {SourceArguments(clause.Source, method.Name)}), {SourceArguments(clause.Source, method.Name)}), {SourceArguments(clause.Source, method.Name)})");
            }

            var arbitration = Argument(awaitMatch.Invocation, "arbitration");
            var lateInput = Argument(awaitMatch.Invocation, "lateInput");
            var staleInput = Argument(awaitMatch.Invocation, "staleInput");
            var duplicateInput = Argument(awaitMatch.Invocation, "duplicateInput");
            var missingTarget = Argument(awaitMatch.Invocation, "missingTarget");
            var retentionHorizon = Argument(awaitMatch.Invocation, "retentionHorizon");
            if (arbitration is null
                || lateInput is null
                || staleInput is null
                || duplicateInput is null
                || missingTarget is null
                || retentionHorizon is null
                || !TryEmitExactArgument(arbitration, awaitMatch.Syntax, out var arbitrationExpression)
                || !TryEmitExactArgument(lateInput, awaitMatch.Syntax, out var lateInputExpression)
                || !TryEmitExactArgument(staleInput, awaitMatch.Syntax, out var staleInputExpression)
                || !TryEmitExactArgument(duplicateInput, awaitMatch.Syntax, out var duplicateInputExpression)
                || !TryEmitExactArgument(missingTarget, awaitMatch.Syntax, out var missingTargetExpression)
                || !TryEmitExactArgument(retentionHorizon, awaitMatch.Syntax, out var retentionExpression))
            {
                return StatementFailure(awaitMatch.Syntax, "AwaitMatch requires exact arbitration, input-disposition, missing-target, and retention policies");
            }

            builderStatements.Add(
                $"__builder.AwaitMatch(id: {awaitMatch.Identity.Variable}, arbitration: {arbitrationExpression}, clauses: [{string.Join(", ", clauses)}], lateInput: {lateInputExpression}, staleInput: {staleInputExpression}, duplicateInput: {duplicateInputExpression}, missingTarget: {missingTargetExpression}, retentionHorizon: {retentionExpression}, {SourceArguments(awaitMatch.Source, method.Name)});");
            return true;
        }

        bool TryEmitGuard(AwaitClauseFlow clause, string output, out string guard)
        {
            guard = "null";
            var argument = Argument(clause.Declaration, "when");
            if (argument is null
                || argument.IsImplicit
                || Strip(argument.Value).ConstantValue is { HasValue: true, Value: null })
            {
                return true;
            }

            var operation = Strip(argument.Value);
            if (operation is IDelegateCreationOperation delegateCreation)
            {
                operation = Strip(delegateCreation.Target);
            }

            if (operation is not IAnonymousFunctionOperation anonymous
                || anonymous.Symbol.Parameters.Length != 1)
            {
                return StatementFailure(
                    argument.Syntax,
                    "an AwaitMatch guard must be one inline portable lambda; runtime delegates and callbacks are not supported");
            }

            var returns = SelfAndDescendants(anonymous.Body)
                .OfType<IReturnOperation>()
                .Where(static returned => returned.ReturnedValue is not null)
                .ToArray();
            if (returns.Length != 1 || returns[0].ReturnedValue is not { } returnedValue)
            {
                return StatementFailure(argument.Syntax, "an AwaitMatch guard must contain one pure Boolean expression");
            }

            var parameter = anonymous.Symbol.Parameters[0];
            outputBySymbol.Add(parameter, output);
            try
            {
                if (!TryEmitValue(
                        returnedValue,
                        returnedValue.Type!,
                        clause.Source,
                        out guard))
                {
                    return false;
                }
            }
            finally
            {
                outputBySymbol.Remove(parameter);
            }
            return true;
        }

        bool TryEmitExplicitDecision(ExplicitDecisionFlow decision, string? successor)
        {
            var operation = decision.Kind.ToString();
            var value = string.Empty;
            if (decision.Kind == DecisionAuthoringKind.Match
                && (decision.Value is null
                    || !TryEmitValue(decision.Value, decision.Value.Type!, decision.Source, out value)))
            {
                return false;
            }

            List<string> cases = [];
            foreach (var arm in decision.Arms)
            {
                if (!TryLowerBlock(arm.Body, successor, out var armEntry) || armEntry is null)
                {
                    return StatementFailure(
                        arm.Syntax,
                        $"{operation} arm '{arm.Name}' requires a reachable continuation");
                }

                string selector;
                if (decision.Kind == DecisionAuthoringKind.Choice
                    ? !TryEmitValue(
                        arm.Selector.Value,
                        arm.Selector.Value.Type!,
                        arm.Source,
                        out selector)
                    : !TryEmitExactArgument(arm.Selector, arm.Syntax, out selector))
                {
                    return false;
                }
                if (!TryEmitRole(arm.Declaration, "role", "next", arm.Syntax, out var role))
                {
                    return false;
                }
                if (!TryEmitOptionalExact(
                        arm.Declaration,
                        "edgeOwner",
                        arm.Identity.Variable,
                        arm.Syntax,
                        out var edgeOwner))
                {
                    return false;
                }

                var declaration = decision.Kind == DecisionAuthoringKind.Choice
                    ? $"__builder.ChoiceCase(id: {arm.Identity.Variable}, predicate: {selector}"
                    : $"__builder.MatchCase(id: {arm.Identity.Variable}, matchedValue: {value}, pattern: {selector}";
                cases.Add(
                    $"{declaration}, next: __builder.Edge(owner: {edgeOwner}, role: {role}, target: {armEntry}, {SourceArguments(arm.Source, method.Name)}), {SourceArguments(arm.Source, method.Name)})");
            }

            var fallback = "null";
            if (decision.Fallback is not null)
            {
                if (!TryLowerBlock(decision.Fallback.Body, successor, out var fallbackEntry)
                    || fallbackEntry is null)
                {
                    return StatementFailure(
                        decision.Fallback.Syntax,
                        $"{operation} fallback '{decision.Fallback.Name}' requires a reachable continuation");
                }
                if (!TryEmitRole(
                        decision.Invocation,
                        "fallbackRole",
                        "next",
                        decision.Fallback.Syntax,
                        out var fallbackRole))
                {
                    return false;
                }
                if (!TryEmitOptionalExact(
                        decision.Invocation,
                        "fallbackEdgeOwner",
                        decision.Fallback.Identity.Variable,
                        decision.Fallback.Syntax,
                        out var fallbackEdgeOwner))
                {
                    return false;
                }
                fallback =
                    $"__builder.Fallback(id: {decision.Fallback.Identity.Variable}, next: __builder.Edge(owner: {fallbackEdgeOwner}, role: {fallbackRole}, target: {fallbackEntry}, {SourceArguments(decision.Fallback.Source, method.Name)}), {SourceArguments(decision.Fallback.Source, method.Name)})";
            }

            var selection = Argument(decision.Invocation, "selection");
            var completeness = Argument(decision.Invocation, "completeness");
            if (selection is null
                || completeness is null
                || !TryEmitExactArgument(selection, decision.Syntax, out var selectionExpression)
                || !TryEmitExactArgument(completeness, decision.Syntax, out var completenessExpression))
            {
                return StatementFailure(
                    decision.Syntax,
                    $"{operation} requires exact selection and completeness policies");
            }

            var valueArgument = decision.Kind == DecisionAuthoringKind.Match ? $", value: {value}" : string.Empty;
            builderStatements.Add(
                $"__builder.{operation}(id: {decision.Identity.Variable}, selection: {selectionExpression}, completeness: {completenessExpression}{valueArgument}, cases: [{string.Join(", ", cases)}], fallback: {fallback}, {SourceArguments(decision.Source, method.Name)});");
            return true;
        }

        bool TryEmitMatch(MatchFlow match, string? successor, out string? entry)
        {
            entry = null;
            List<(MatchArm Arm, string Entry)> loweredArms = [];
            foreach (var arm in match.Arms)
            {
                if (!TryLowerBlock(arm.Body, successor, out var armEntry) || armEntry is null)
                {
                    return StatementFailure(match.Syntax, "every switch case requires a reachable continuation");
                }

                loweredArms.Add((arm, armEntry));
            }

            string? fallbackEntry = successor;
            if (match.Fallback is not null
                && (!TryLowerBlock(match.Fallback, successor, out fallbackEntry) || fallbackEntry is null))
            {
                return StatementFailure(match.Syntax, "the switch default requires a reachable continuation");
            }

            if (fallbackEntry is null)
            {
                return StatementFailure(match.Syntax, "switch without a following continuation requires a default case");
            }

            if (!TryEmitValue(match.Value, match.Value.Type!, match.Source, out var value))
            {
                return false;
            }

            var suffix = match.Identity.Variable.Substring("__node_".Length);
            List<string> cases = [];
            for (var index = 0; index < loweredArms.Count; index++)
            {
                var (arm, target) = loweredArms[index];
                var caseVariable = $"__match_case_{suffix}_{index.ToString(CultureInfo.InvariantCulture)}";
                builderStatements.Add(
                    $"var {caseVariable} = global::Cohesive.Processes.Authoring.ProcessAuthoringIdentities.NodeFor(owner: {match.Identity.Variable}, role: \"case-{index.ToString(CultureInfo.InvariantCulture)}\");");
                cases.Add(
                    $"__builder.MatchCase(id: {caseVariable}, pattern: {arm.Pattern}, next: __builder.Edge(owner: {caseVariable}, role: \"next\", target: {target}, {SourceArguments(arm.Source, method.Name)}), {SourceArguments(arm.Source, method.Name)})");
            }
            var fallbackVariable = $"__match_fallback_{suffix}";
            builderStatements.Add(
                $"var {fallbackVariable} = global::Cohesive.Processes.Authoring.ProcessAuthoringIdentities.NodeFor(owner: {match.Identity.Variable}, role: \"otherwise\");");
            builderStatements.Add(
                $"__builder.Match(id: {match.Identity.Variable}, selection: global::Cohesive.Execution.CaseSelection.OrderedFirstMatch, completeness: global::Cohesive.Execution.BranchCompleteness.Fallback, value: {value}, cases: [{string.Join(", ", cases)}], fallback: __builder.Fallback(id: {fallbackVariable}, next: __builder.Edge(owner: {fallbackVariable}, role: \"next\", target: {fallbackEntry}, {SourceArguments(match.Source, method.Name)}), {SourceArguments(match.Source, method.Name)}), {SourceArguments(match.Source, method.Name)});");
            entry = match.Identity.Variable;
            return true;
        }

        bool TryEmitAwait(AwaitFlow awaited, string successor)
        {
            switch (awaited.Kind)
            {
                case AwaitKind.Query:
                case AwaitKind.Read:
                    return TryEmitRelation(awaited, successor);
                case AwaitKind.Transition:
                    return TryEmitTransition(awaited, successor);
                case AwaitKind.Effect:
                    return TryEmitEffect(awaited, successor);
                default:
                    return StatementFailure(awaited.Syntax, "unsupported awaited Process operation");
            }
        }

        bool TryEmitRelation(AwaitFlow awaited, string successor)
        {
            var relation = Argument(awaited.Invocation, "relation")
                ?? Argument(awaited.Invocation, "query");
            var input = Argument(awaited.Invocation, "input");
            if (relation is null || input is null || !TryEmitValue(input.Value, input.Value.Type!, awaited.Source, out var inputValue))
            {
                return StatementFailure(awaited.Syntax, "Query and Read require an exact relation and a portable input");
            }

            if (!TryEmitExactArgument(relation, awaited.Syntax, out var relationReference)
                || !TryEmitRole(awaited.Invocation, "nextRole", "next", awaited.Syntax, out var nextRole))
            {
                return false;
            }

            if (IsTypedCanonicalRelationQueryHandle(relation.Parameter?.Type))
                relationReference = $"{relationReference}.Reference";

            builderStatements.Add(
                $"__builder.EvaluateRelation(id: {awaited.Identity.Variable}, relation: {relationReference}, input: {inputValue}, continuation: __builder.Continuation(edge: __builder.Edge(owner: {awaited.Identity.Variable}, role: {nextRole}, target: {successor}, {SourceArguments(awaited.Source, method.Name)}), output: {awaited.OutputVariable}, {SourceArguments(awaited.Source, method.Name)}), {SourceArguments(awaited.Source, method.Name)});");
            return true;
        }

        static bool IsTypedCanonicalRelationQueryHandle(ITypeSymbol? type) =>
            type is INamedTypeSymbol
            {
                Name: "Relation" or "HostedQuery",
                Arity: 2
            } named
            && named.ContainingNamespace.ToDisplayString() == "Cohesive.Relations.Authoring";

        bool TryEmitTransition(AwaitFlow awaited, string successor)
        {
            var transition = Argument(awaited.Invocation, "transition");
            var subject = Argument(awaited.Invocation, "subject");
            var input = Argument(awaited.Invocation, "input");
            if (transition is null || subject is null || input is null
                || !TryEmitValue(subject.Value, subject.Value.Type!, awaited.Source, out var subjectValue)
                || !TryEmitValue(input.Value, input.Value.Type!, awaited.Source, out var inputValue))
            {
                return StatementFailure(awaited.Syntax, "Transition requires an exact definition, portable subject, and portable input");
            }

            if (!TryEmitExactArgument(transition, awaited.Syntax, out var transitionReference)
                || !TryEmitRole(awaited.Invocation, "nextRole", "next", awaited.Syntax, out var nextRole))
            {
                return false;
            }

            builderStatements.Add(
                $"__builder.InvokeTransition(id: {awaited.Identity.Variable}, transition: {transitionReference}, subject: {subjectValue}, input: {inputValue}, continuation: __builder.Continuation(edge: __builder.Edge(owner: {awaited.Identity.Variable}, role: {nextRole}, target: {successor}, {SourceArguments(awaited.Source, method.Name)}), output: {awaited.OutputVariable}, {SourceArguments(awaited.Source, method.Name)}), {SourceArguments(awaited.Source, method.Name)});");
            return true;
        }

        bool TryEmitEffect(AwaitFlow awaited, string successor)
        {
            var contract = Argument(awaited.Invocation, "contract");
            var outcome = Argument(awaited.Invocation, "outcome");
            var input = Argument(awaited.Invocation, "input");
            if (contract is null || outcome is null || input is null
                || !TryEmitValue(input.Value, input.Value.Type!, awaited.Source, out var payload))
            {
                return StatementFailure(awaited.Syntax, "Effect requires an exact Request contract, outcome, and portable payload");
            }

            if (!TryEmitExactArgument(contract, awaited.Syntax, out var requestContract)
                || !TryEmitExactArgument(outcome, awaited.Syntax, out var terminalOutcome))
            {
                return false;
            }

            var suffix = awaited.Identity.Variable.Substring("__node_".Length);
            var outcomeVariable = $"__outcome_{suffix}";
            builderStatements.Add(
                $"var {outcomeVariable} = global::Cohesive.Processes.Authoring.ProcessAuthoringIdentities.NodeFor(owner: {awaited.Identity.Variable}, role: \"outcome\");");
            builderStatements.Add(
                $"__builder.Request(id: {awaited.Identity.Variable}, contract: {requestContract}, payload: {payload}, outcomes: [__builder.RequestOutcome(id: {outcomeVariable}, outcome: {terminalOutcome}, continuation: __builder.Continuation(edge: __builder.Edge(owner: {outcomeVariable}, role: \"next\", target: {successor}, {SourceArguments(awaited.Source, method.Name)}), output: {awaited.OutputVariable}, {SourceArguments(awaited.Source, method.Name)}), {SourceArguments(awaited.Source, method.Name)})], {SourceArguments(awaited.Source, method.Name)});");
            return true;
        }

        bool TryEmitExactArgument(
            IArgumentOperation argument,
            SyntaxNode syntax,
            out string expression)
        {
            return TryRewriteExactOperation(
                Strip(argument.Value),
                syntax,
                new HashSet<ILocalSymbol>(SymbolEqualityComparer.Default),
                out expression);
        }

        bool TryRewriteExactOperation(
            IOperation operation,
            SyntaxNode syntax,
            HashSet<ILocalSymbol> resolving,
            out string expression)
        {
            operation = Strip(operation);
            Dictionary<SyntaxNode, SyntaxNode> replacements = [];
            foreach (var candidate in SelfAndDescendants(operation))
            {
                if (candidate is IParameterReferenceOperation)
                {
                    expression = string.Empty;
                    return StatementFailure(
                        syntax,
                        "exact semantic arguments—definitions, contracts, outcomes, node identities, admission policies, and capacity domains—cannot depend on runtime bindings");
                }
                if (candidate is not ILocalReferenceOperation local)
                {
                    continue;
                }
                if (!pureLocals.TryGetValue(local.Local, out var initializer)
                    || !resolving.Add(local.Local))
                {
                    expression = string.Empty;
                    return StatementFailure(
                        syntax,
                        "exact semantic arguments must use acyclic source locals initialized only from exact values");
                }
                if (!TryRewriteExactOperation(initializer, syntax, resolving, out var rewrittenInitializer))
                {
                    expression = string.Empty;
                    return false;
                }
                resolving.Remove(local.Local);
                replacements[local.Syntax] = SyntaxFactory.ParenthesizedExpression(
                    SyntaxFactory.ParseExpression(rewrittenInitializer));
            }

            var rewritten = replacements.Count == 0
                ? operation.Syntax
                : operation.Syntax.ReplaceNodes(
                    replacements.Keys,
                    (original, _) => replacements[original]);
            expression = $"({rewritten})";
            return true;
        }

        bool TryEmitRole(
            IInvocationOperation invocation,
            string parameter,
            string fallback,
            SyntaxNode syntax,
            out string expression)
        {
            var argument = Argument(invocation, parameter);
            if (argument is null || argument.IsImplicit)
            {
                expression = Literal(fallback);
                return true;
            }

            return TryEmitExactArgument(argument, syntax, out expression);
        }

        bool TryEmitOptionalExact(
            IInvocationOperation invocation,
            string parameter,
            string fallback,
            SyntaxNode syntax,
            out string expression)
        {
            var argument = Argument(invocation, parameter);
            if (argument is null
                || argument.IsImplicit
                || Strip(argument.Value).ConstantValue is { HasValue: true, Value: null })
            {
                expression = fallback;
                return true;
            }

            return TryEmitExactArgument(argument, syntax, out expression);
        }

        bool TryEmitValue(
            IOperation operation,
            ITypeSymbol type,
            SourceReference source,
            out string valueVariable,
            IReadOnlyDictionary<IParameterSymbol, IOperation>? projectedParameters = null)
        {
            valueVariable = string.Empty;
            var authoredOperation = Strip(operation);
            if (authoredOperation is ILocalReferenceOperation local
                && pureLocals.TryGetValue(local.Local, out var initializer))
            {
                source = SourceLocation(initializer.Syntax);
            }

            var translator = new PureExpressionEmitter(
                method,
                inputParameter,
                pureLocals,
                forkResultTuples,
                outputBySymbol,
                patternOutputs,
                resolvingPureLocals,
                projectedParameters);
            if (!translator.TryEmit(operation, out var expression, out var failure))
            {
                Report(
                    productionContext,
                    UnsupportedPureExpression,
                    operation.Syntax.GetLocation(),
                    method.Name,
                    failure);
                return false;
            }

            valueVariable = $"__value_{valueOrdinal++.ToString(CultureInfo.InvariantCulture)}";
            var nullability = type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T }
                ? "global::Cohesive.Model.FieldNullability.Nullable"
                : "global::Cohesive.Model.FieldNullability.NonNullable";
            builderStatements.Add(
                $"var {valueVariable} = __builder.CanonicalValue<{FormatType(type)}>(expression: {expression}, contract: new global::Cohesive.Model.ValueContract(type: __typeMapper.Map(clrType: typeof({FormatType(type)}), nullability: null), nullability: {nullability}), {SourceArguments(source, method.Name)});");
            return true;
        }

        bool StatementFailure(SyntaxNode syntax, string reason)
        {
            Report(
                productionContext,
                UnsupportedStatement,
                syntax.GetLocation(),
                method.Name,
                reason);
            return false;
        }

    }

    sealed class PureExpressionEmitter
    {
        readonly IMethodSymbol method;
        readonly IParameterSymbol inputParameter;
        readonly IReadOnlyDictionary<ILocalSymbol, IOperation> pureLocals;
        readonly IReadOnlyDictionary<ILocalSymbol, ImmutableArray<IOperation>> forkResultTuples;
        readonly IReadOnlyDictionary<ISymbol, string> outputs;
        readonly IReadOnlyDictionary<ISymbol, PatternOutput> patternOutputs;
        readonly HashSet<ILocalSymbol> resolving;
        readonly IReadOnlyDictionary<IParameterSymbol, IOperation>? projectedParameters;

        public PureExpressionEmitter(
            IMethodSymbol method,
            IParameterSymbol inputParameter,
            IReadOnlyDictionary<ILocalSymbol, IOperation> pureLocals,
            IReadOnlyDictionary<ILocalSymbol, ImmutableArray<IOperation>> forkResultTuples,
            IReadOnlyDictionary<ISymbol, string> outputs,
            IReadOnlyDictionary<ISymbol, PatternOutput> patternOutputs,
            HashSet<ILocalSymbol> resolving,
            IReadOnlyDictionary<IParameterSymbol, IOperation>? projectedParameters = null)
        {
            this.method = method;
            this.inputParameter = inputParameter;
            this.pureLocals = pureLocals;
            this.forkResultTuples = forkResultTuples;
            this.outputs = outputs;
            this.patternOutputs = patternOutputs;
            this.resolving = resolving;
            this.projectedParameters = projectedParameters;
        }

        public bool TryEmit(IOperation operation, out string expression, out string failure)
        {
            operation = Strip(operation);
            if (TryResolveForkTupleElement(operation, out var tupleElement))
            {
                return TryEmit(tupleElement, out expression, out failure);
            }

            if (operation is IPropertyReferenceOperation pureMember
                && TryResolvePureMember(pureMember, out var projected))
            {
                return TryEmit(projected, out expression, out failure);
            }

            if (TryEmitCount(operation, out expression, out failure))
            {
                return true;
            }

            if (TryEmitBindingPath(operation, out expression))
            {
                failure = string.Empty;
                return true;
            }

            switch (operation)
            {
                case IParameterReferenceOperation parameter
                    when projectedParameters?.TryGetValue(parameter.Parameter, out var parameterProjection) == true:
                    return TryEmit(parameterProjection, out expression, out failure);

                case IParameterReferenceOperation parameter
                    when SymbolEqualityComparer.Default.Equals(parameter.Parameter, inputParameter):
                    expression = "__builder.Input.Expression";
                    failure = string.Empty;
                    return true;

                case IParameterReferenceOperation parameter when outputs.TryGetValue(parameter.Parameter, out var output):
                    expression = output + ".Expression";
                    failure = string.Empty;
                    return true;

                case ILocalReferenceOperation local when outputs.TryGetValue(local.Local, out var output):
                    expression = output + ".Expression";
                    failure = string.Empty;
                    return true;

                case ILocalReferenceOperation local
                    when patternOutputs.TryGetValue(local.Local, out var patternOutput):
                    expression = patternOutput.Path.IsEmpty
                        ? patternOutput.Output.Variable + ".Expression"
                        : BindingField(patternOutput.Output.Variable + ".Binding", patternOutput.Path);
                    failure = string.Empty;
                    return true;

                case ILocalReferenceOperation local when pureLocals.TryGetValue(local.Local, out var initializer):
                    if (!resolving.Add(local.Local))
                    {
                        return Failure("pure local definitions form a cycle", out expression, out failure);
                    }

                    try
                    {
                        return TryEmit(initializer, out expression, out failure);
                    }
                    finally
                    {
                        resolving.Remove(local.Local);
                    }

                case ILiteralOperation literal:
                    return TryEmitConstant(literal.ConstantValue, literal.Type, out expression, out failure);

                case IFieldReferenceOperation field when field.ConstantValue.HasValue:
                    return TryEmitConstant(field.ConstantValue, field.Type, out expression, out failure);

                case IUnaryOperation unary when unary.OperatorKind == UnaryOperatorKind.Not:
                    if (!TryEmit(unary.Operand, out var operand, out failure))
                    {
                        expression = string.Empty;
                        return false;
                    }
                    expression = $"global::Cohesive.Model.Expr.Not({operand})";
                    return true;

                case IBinaryOperation binary:
                    return TryEmitBinary(binary, out expression, out failure);

                case IConditionalOperation conditional:
                    if (conditional.WhenFalse is null)
                    {
                        return Failure("conditional expression requires both alternatives", out expression, out failure);
                    }

                    if (!TryEmit(conditional.Condition, out var condition, out failure)
                        || !TryEmit(conditional.WhenTrue, out var whenTrue, out failure)
                        || !TryEmit(conditional.WhenFalse, out var whenFalse, out failure))
                    {
                        expression = string.Empty;
                        return false;
                    }
                    expression =
                        $"new global::Cohesive.Model.ConditionalExpr(test: {condition}, ifTrue: {whenTrue}, ifFalse: {whenFalse}, returnType: {ReturnType(conditional.Type)})";
                    return true;

                case IObjectCreationOperation creation:
                    return TryEmitObject(creation, out expression, out failure);

                case IInterpolatedStringOperation interpolation:
                    return TryEmitInterpolation(interpolation, out expression, out failure);

                case IInvocationOperation invocation:
                    return TryEmitInvocation(invocation, out expression, out failure);
            }

            return Failure(
                $"'{operation.Kind}' ({operation.Syntax}) is outside the fixed portable expression closure",
                out expression,
                out failure);
        }

        bool TryEmitBinary(IBinaryOperation binary, out string expression, out string failure)
        {
            if (binary.OperatorKind == BinaryOperatorKind.Add
                && binary.Type?.SpecialType == SpecialType.System_String)
            {
                List<IOperation> parts = [];
                CollectConcat(binary, parts);
                List<string> emitted = [];
                foreach (var part in parts)
                {
                    if (!TryEmit(part, out var value, out failure))
                    {
                        expression = string.Empty;
                        return false;
                    }
                    emitted.Add(value);
                }
                expression =
                    $"new global::Cohesive.Model.CallExpr(function: global::Cohesive.Model.ExprFunctionNames.Concat, arguments: [{string.Join(", ", emitted)}], returnType: {ReturnType(binary.Type)})";
                failure = string.Empty;
                return true;
            }
            if (!TryEmit(binary.LeftOperand, out var left, out failure)
                || !TryEmit(binary.RightOperand, out var right, out failure))
            {
                expression = string.Empty;
                return false;
            }

            var operation = binary.OperatorKind switch
            {
                BinaryOperatorKind.Equals => "Eq",
                BinaryOperatorKind.NotEquals => "Ne",
                BinaryOperatorKind.GreaterThan => "Gt",
                BinaryOperatorKind.GreaterThanOrEqual => "Ge",
                BinaryOperatorKind.LessThan => "Lt",
                BinaryOperatorKind.LessThanOrEqual => "Le",
                BinaryOperatorKind.ConditionalAnd => "And",
                BinaryOperatorKind.ConditionalOr => "Or",
                BinaryOperatorKind.Add when binary.Type?.SpecialType != SpecialType.System_String => "Add",
                BinaryOperatorKind.Subtract => "Sub",
                BinaryOperatorKind.Multiply => "Mul",
                BinaryOperatorKind.Divide => "Div",
                _ => null
            };
            if (operation is null)
            {
                return Failure($"binary operator '{binary.OperatorKind}' is not portable", out expression, out failure);
            }

            expression = $"global::Cohesive.Model.Expr.{operation}({left}, {right})";
            failure = string.Empty;
            return true;
        }

        bool TryEmitObject(IObjectCreationOperation creation, out string expression, out string failure)
        {
            if (creation.Constructor is null)
            {
                return Failure("object creation requires a named constructor", out expression, out failure);
            }

            List<(string Name, IOperation Value)> members = [];
            foreach (var argument in creation.Arguments)
            {
                if (argument.Parameter is null)
                {
                    return Failure("object constructor arguments must map to named parameters", out expression, out failure);
                }

                var property = creation.Type?.GetMembers().OfType<IPropertySymbol>()
                    .SingleOrDefault(candidate =>
                        !candidate.IsStatic
                        && candidate.GetMethod is not null
                        && SymbolEqualityComparer.Default.Equals(candidate.Type, argument.Parameter.Type)
                        && string.Equals(candidate.Name, argument.Parameter.Name, StringComparison.OrdinalIgnoreCase));
                members.Add((SerializedName(property) ?? argument.Parameter.Name, argument.Value));
            }
            if (creation.Initializer is not null)
            {
                foreach (var initializer in creation.Initializer.Initializers)
                {
                    if (initializer is not ISimpleAssignmentOperation assignment
                        || Strip(assignment.Target) is not IPropertyReferenceOperation property)
                    {
                        return Failure(
                            "object initializers support only named property assignments",
                            out expression,
                            out failure);
                    }
                    members.Add((SerializedName(property.Property) ?? property.Property.Name, assignment.Value));
                }
            }
            members.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));
            for (var index = 1; index < members.Count; index++)
            {
                if (string.Equals(members[index - 1].Name, members[index].Name, StringComparison.Ordinal))
                {
                    return Failure(
                        $"object creation assigns semantic field '{members[index].Name}' more than once",
                        out expression,
                        out failure);
                }
            }
            List<string> arguments = [];
            foreach (var member in members)
            {
                if (!TryEmit(member.Value, out var value, out failure))
                {
                    expression = string.Empty;
                    return false;
                }
                arguments.Add($"global::Cohesive.Model.Expr.Const({Literal(member.Name)})");
                arguments.Add(value);
            }
            expression =
                $"new global::Cohesive.Model.CallExpr(function: global::Cohesive.Model.ExprFunctionNames.Object, arguments: [{string.Join(", ", arguments)}], returnType: {ReturnType(creation.Type)})";
            failure = string.Empty;
            return true;
        }

        bool TryEmitInterpolation(
            IInterpolatedStringOperation interpolation,
            out string expression,
            out string failure)
        {
            List<string> arguments = [];
            foreach (var part in interpolation.Parts)
            {
                switch (part)
                {
                    case IInterpolatedStringTextOperation text:
                        if (!TryEmitConstant(text.Text.ConstantValue, text.Text.Type, out var literal, out failure))
                        {
                            expression = string.Empty;
                            return false;
                        }
                        arguments.Add(literal);
                        break;
                    case IInterpolationOperation item:
                        if (item.FormatString is not null || item.Alignment is not null)
                        {
                            return Failure("formatted interpolation is not in the portable text subset", out expression, out failure);
                        }

                        if (item.Expression.Type?.SpecialType != SpecialType.System_String)
                        {
                            return Failure("interpolation currently requires string operands", out expression, out failure);
                        }

                        if (!TryEmit(item.Expression, out var value, out failure))
                        {
                            expression = string.Empty;
                            return false;
                        }
                        arguments.Add(value);
                        break;
                }
            }
            expression =
                $"new global::Cohesive.Model.CallExpr(function: global::Cohesive.Model.ExprFunctionNames.Concat, arguments: [{string.Join(", ", arguments)}], returnType: {ReturnType(interpolation.Type)})";
            failure = string.Empty;
            return true;
        }

        bool TryEmitInvocation(IInvocationOperation invocation, out string expression, out string failure)
        {
            string? function = null;
            var arguments = new List<IOperation>();
            if (invocation.TargetMethod.ContainingType.SpecialType == SpecialType.System_String
                && invocation.Instance is not null
                && invocation.Arguments.Length == 1)
            {
                function = invocation.TargetMethod.Name switch
                {
                    "StartsWith" => "StartsWith",
                    "EndsWith" => "EndsWith",
                    "Contains" => "TextContains",
                    _ => null
                };
                if (function is not null)
                {
                    arguments.Add(invocation.Instance);
                    arguments.Add(invocation.Arguments[0].Value);
                }
            }
            else if (invocation.TargetMethod.Name == "Contains"
                     && invocation.TargetMethod.ContainingNamespace.ToDisplayString() == "System.Linq"
                     && invocation.Arguments.Length == 2)
            {
                function = "Contains";
                arguments.Add(invocation.Arguments[0].Value);
                arguments.Add(invocation.Arguments[1].Value);
            }
            else if (invocation.TargetMethod.Name == "Contains"
                     && invocation.Instance is not null
                     && invocation.Arguments.Length == 1
                     && IsCollection(invocation.Instance.Type))
            {
                function = "Contains";
                arguments.Add(invocation.Instance);
                arguments.Add(invocation.Arguments[0].Value);
            }
            else if (invocation.TargetMethod.IsStatic
                     && invocation.TargetMethod.ContainingType.SpecialType == SpecialType.System_String
                     && invocation.TargetMethod.Name == "Concat"
                     && invocation.Arguments.Length != 0)
            {
                function = "Concat";
                arguments.AddRange(invocation.Arguments.Select(static argument => argument.Value));
            }
            if (function is null)
            {
                return Failure($"method '{invocation.TargetMethod.ToDisplayString()}' is not portable", out expression, out failure);
            }

            List<string> emitted = [];
            foreach (var argument in arguments)
            {
                if (!TryEmit(argument, out var value, out failure))
                {
                    expression = string.Empty;
                    return false;
                }
                emitted.Add(value);
            }
            expression =
                $"new global::Cohesive.Model.CallExpr(function: global::Cohesive.Model.ExprFunctionNames.{function}, arguments: [{string.Join(", ", emitted)}], returnType: {ReturnType(invocation.Type)})";
            failure = string.Empty;
            return true;
        }

        bool TryEmitCount(IOperation operation, out string expression, out string failure)
        {
            IOperation? source = operation switch
            {
                IPropertyReferenceOperation property
                    when property.Property.Name is "Count" or "Length"
                         && property.Instance is not null
                         && IsCollection(property.Instance.Type) => property.Instance,
                IInvocationOperation invocation
                    when invocation.TargetMethod.Name == "Count"
                         && invocation.TargetMethod.ContainingNamespace.ToDisplayString() == "System.Linq"
                         && invocation.Arguments.Length == 1 => invocation.Arguments[0].Value,
                _ => null
            };
            if (source is null)
            {
                expression = string.Empty;
                failure = string.Empty;
                return false;
            }
            if (!TryEmit(source, out var collection, out failure))
            {
                expression = string.Empty;
                return false;
            }
            expression =
                $"new global::Cohesive.Model.CallExpr(function: global::Cohesive.Model.ExprFunctionNames.Count, arguments: [{collection}], returnType: __typeMapper.Map(clrType: typeof(long), nullability: null))";
            return true;
        }

        bool TryResolvePureMember(IPropertyReferenceOperation property, out IOperation value)
        {
            value = null!;
            if (property.Instance is null)
            {
                return false;
            }

            var instance = Strip(property.Instance);
            if (TryResolvePureAlias(instance, out var alias))
            {
                instance = alias;
            }
            else if (instance is IPropertyReferenceOperation parent
                     && TryResolvePureMember(parent, out var parentValue))
            {
                instance = Strip(parentValue);
            }

            if (instance is not IObjectCreationOperation creation)
            {
                return false;
            }

            foreach (var argument in creation.Arguments)
            {
                if (argument.Parameter is null)
                {
                    continue;
                }

                var candidate = creation.Type?.GetMembers().OfType<IPropertySymbol>()
                    .SingleOrDefault(member =>
                        !member.IsStatic
                        && member.GetMethod is not null
                        && SymbolEqualityComparer.Default.Equals(member.Type, argument.Parameter.Type)
                        && string.Equals(member.Name, argument.Parameter.Name, StringComparison.OrdinalIgnoreCase));
                if (!SymbolEqualityComparer.Default.Equals(candidate, property.Property))
                {
                    continue;
                }

                value = argument.Value;
                return true;
            }
            if (creation.Initializer is null)
            {
                return false;
            }

            foreach (var memberInitializer in creation.Initializer.Initializers)
            {
                if (memberInitializer is not ISimpleAssignmentOperation assignment
                    || Strip(assignment.Target) is not IPropertyReferenceOperation target
                    || !SymbolEqualityComparer.Default.Equals(target.Property, property.Property))
                {
                    continue;
                }

                value = assignment.Value;
                return true;
            }
            return false;
        }

        bool TryResolvePureAlias(IOperation operation, out IOperation value)
        {
            value = Strip(operation);
            var changed = false;
            HashSet<ILocalSymbol> observed = new(SymbolEqualityComparer.Default);
            while (true)
            {
                if (TryResolveForkTupleElement(value, out var tupleElement))
                {
                    value = Strip(tupleElement);
                    changed = true;
                    continue;
                }
                if (value is ILocalReferenceOperation local
                    && pureLocals.TryGetValue(local.Local, out var initializer)
                    && observed.Add(local.Local))
                {
                    value = Strip(initializer);
                    changed = true;
                    continue;
                }
                if (value is IParameterReferenceOperation parameter
                    && projectedParameters?.TryGetValue(parameter.Parameter, out var projected) == true)
                {
                    value = Strip(projected);
                    changed = true;
                    continue;
                }

                return changed;
            }
        }

        static void CollectConcat(IOperation operation, List<IOperation> parts)
        {
            operation = Strip(operation);
            if (operation is IBinaryOperation binary
                && binary.OperatorKind == BinaryOperatorKind.Add
                && binary.Type?.SpecialType == SpecialType.System_String)
            {
                CollectConcat(binary.LeftOperand, parts);
                CollectConcat(binary.RightOperand, parts);
                return;
            }
            if (operation is IInvocationOperation invocation
                && invocation.TargetMethod.IsStatic
                && invocation.TargetMethod.ContainingType.SpecialType == SpecialType.System_String
                && invocation.TargetMethod.Name == "Concat")
            {
                foreach (var argument in invocation.Arguments)
                {
                    CollectConcat(argument.Value, parts);
                }

                return;
            }
            parts.Add(operation);
        }

        static bool IsCollection(ITypeSymbol? type) =>
            type is not null
            && type.SpecialType != SpecialType.System_String
            && (type.AllInterfaces.Any(static candidate =>
                    candidate.SpecialType == SpecialType.System_Collections_IEnumerable
                    || candidate.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T)
                || type.SpecialType == SpecialType.System_Collections_IEnumerable);

        bool TryEmitBindingPath(IOperation operation, out string expression)
        {
            List<string> segments = [];
            var current = operation;
            while (true)
            {
                current = Strip(current);
                if (TryResolveForkTupleElement(current, out var tupleElement))
                {
                    current = tupleElement;
                    continue;
                }
                switch (current)
                {
                    case IPropertyReferenceOperation property when property.Instance is not null:
                        segments.Add(SerializedName(property.Property) ?? property.Property.Name);
                        current = property.Instance;
                        continue;
                    case ILocalReferenceOperation local when pureLocals.TryGetValue(local.Local, out var initializer):
                        current = initializer;
                        continue;
                    case IParameterReferenceOperation parameter
                        when projectedParameters?.TryGetValue(parameter.Parameter, out var projected) == true:
                        current = projected;
                        continue;
                    case IParameterReferenceOperation parameter
                        when SymbolEqualityComparer.Default.Equals(parameter.Parameter, inputParameter):
                        segments.Reverse();
                        expression = BindingField("__builder.Input.Binding", segments);
                        return segments.Count != 0;
                    case IParameterReferenceOperation parameter when outputs.TryGetValue(parameter.Parameter, out var parameterOutput):
                        segments.Reverse();
                        expression = BindingField(parameterOutput + ".Binding", segments);
                        return segments.Count != 0;
                    case ILocalReferenceOperation local when outputs.TryGetValue(local.Local, out var output):
                        segments.Reverse();
                        expression = BindingField(output + ".Binding", segments);
                        return segments.Count != 0;
                    case ILocalReferenceOperation local
                        when patternOutputs.TryGetValue(local.Local, out var patternOutput):
                        segments.Reverse();
                        var path = patternOutput.Path.AddRange(segments);
                        expression = BindingField(patternOutput.Output.Variable + ".Binding", path);
                        return path.Length != 0;
                    default:
                        expression = string.Empty;
                        return false;
                }
            }
        }

        bool TryResolveForkTupleElement(IOperation operation, out IOperation value)
        {
            value = null!;
            operation = Strip(operation);
            if (operation is not IFieldReferenceOperation { Instance: { } instance } field
                || Strip(instance) is not ILocalReferenceOperation local
                || !forkResultTuples.TryGetValue(local.Local, out var values)
                || local.Local.Type is not INamedTypeSymbol { IsTupleType: true } tuple)
            {
                return false;
            }

            var elements = tuple.TupleElements;
            for (var index = 0; index < elements.Length && index < values.Length; index++)
            {
                if (!SymbolEqualityComparer.Default.Equals(elements[index], field.Field)
                    && !SymbolEqualityComparer.Default.Equals(
                        elements[index].CorrespondingTupleField,
                        field.Field.CorrespondingTupleField ?? field.Field))
                {
                    continue;
                }

                value = values[index];
                return true;
            }

            return false;
        }

        static string BindingField(string binding, IReadOnlyList<string> segments) =>
            $"global::Cohesive.Model.Expr.Field(binding: {binding}, path: new global::Cohesive.Model.FieldPath([{string.Join(", ", segments.Select(segment => $"global::Cohesive.Model.FieldPathSegment.ForField({Literal(segment)})"))}]))";

        static bool TryEmitConstant(
            Optional<object?> constant,
            ITypeSymbol? type,
            out string expression,
            out string failure)
        {
            if (!constant.HasValue)
            {
                return Failure("value is not a compile-time portable constant", out expression, out failure);
            }

            var value = constant.Value;
            if (value is null)
            {
                expression = "global::Cohesive.Model.Expr.Null()";
                failure = string.Empty;
                return true;
            }
            if (type?.TypeKind == TypeKind.Enum)
            {
                var enumMember = type.GetMembers().OfType<IFieldSymbol>()
                    .FirstOrDefault(field => field.HasConstantValue && Equals(field.ConstantValue, value));
                if (enumMember is null)
                {
                    return Failure("enum constant has no stable declared name", out expression, out failure);
                }

                expression = $"global::Cohesive.Model.Expr.Const({Literal(enumMember.Name)})";
                failure = string.Empty;
                return true;
            }
            expression = value switch
            {
                string text => $"global::Cohesive.Model.Expr.Const({Literal(text)})",
                char character => $"global::Cohesive.Model.Expr.Const({Literal(character.ToString())})",
                bool boolean => $"global::Cohesive.Model.Expr.Const({(boolean ? "true" : "false")})",
                sbyte or byte or short or ushort or int =>
                    $"global::Cohesive.Model.Expr.Const({Convert.ToInt32(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)})",
                uint or long =>
                    $"global::Cohesive.Model.Expr.Const({Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)}L)",
                float single =>
                    $"global::Cohesive.Model.Expr.Const({single.ToString("R", CultureInfo.InvariantCulture)}F)",
                double @double =>
                    $"global::Cohesive.Model.Expr.Const({@double.ToString("R", CultureInfo.InvariantCulture)})",
                decimal @decimal =>
                    $"global::Cohesive.Model.Expr.Const({@decimal.ToString(CultureInfo.InvariantCulture)}M)",
                _ => string.Empty
            };
            if (expression.Length == 0)
            {
                return Failure($"constant type '{value.GetType().Name}' is not portable", out expression, out failure);
            }

            failure = string.Empty;
            return true;
        }

        static string ReturnType(ITypeSymbol? type) =>
            $"__typeMapper.Map(clrType: typeof({FormatType(type ?? throw new InvalidOperationException("A portable expression requires a CLR result type."))}), nullability: null)";

        static bool Failure(string reason, out string expression, out string failure)
        {
            expression = string.Empty;
            failure = reason;
            return false;
        }
    }

    static IOperation Strip(IOperation operation)
    {
        while (true)
        {
            switch (operation)
            {
                case IConversionOperation conversion:
                    operation = conversion.Operand;
                    continue;
                case IParenthesizedOperation parenthesized:
                    operation = parenthesized.Operand;
                    continue;
                default:
                    return operation;
            }
        }
    }

    static IEnumerable<IOperation> SelfAndDescendants(IOperation operation)
    {
        yield return operation;
        foreach (var child in operation.ChildOperations)
        {
            foreach (var descendant in SelfAndDescendants(child))
            {
                yield return descendant;
            }
        }
    }

    static IArgumentOperation? Argument(IInvocationOperation invocation, string name) =>
        invocation.Arguments.FirstOrDefault(argument =>
            string.Equals(argument.Parameter?.Name, name, StringComparison.Ordinal));

    static string SourceArguments(SourceReference source, string member) =>
        $"sourceFile: {Literal(source.File)}, sourceLine: {source.Line.ToString(CultureInfo.InvariantCulture)}, sourceMember: {Literal(member)}";

    static string Path(ImmutableArray<string> segments) =>
        $"new global::Cohesive.Execution.ExecutionSemanticPath([{string.Join(", ", segments.Select(Literal))}])";

    readonly record struct FlowIdentity(
        string Variable,
        ImmutableArray<string> Path,
        string PathSegment);

    abstract record FlowStatement(
        FlowIdentity Identity,
        SourceReference Source,
        SyntaxNode Syntax);

    sealed record AwaitFlow(
        FlowIdentity Identity,
        ILocalSymbol Local,
        AwaitKind Kind,
        IInvocationOperation Invocation,
        SourceReference Source,
        SyntaxNode Syntax,
        string OutputVariable)
        : FlowStatement(Identity, Source, Syntax);

    sealed record ReturnFlow(
        FlowIdentity Identity,
        IOperation Result,
        TerminalAuthoringKind Kind,
        IInvocationOperation? Invocation,
        SourceReference Source,
        SyntaxNode Syntax)
        : FlowStatement(Identity, Source, Syntax);

    sealed record IfFlow(
        FlowIdentity Identity,
        IOperation Condition,
        FlowBlock WhenTrue,
        FlowBlock? WhenFalse,
        SourceReference Source,
        SyntaxNode Syntax)
        : FlowStatement(Identity, Source, Syntax);

    sealed record FlowBlock(ImmutableArray<FlowStatement> Statements)
    {
        public IEnumerable<FlowStatement> Descendants()
        {
            foreach (var statement in Statements)
            {
                yield return statement;
                if (statement is ForkJoinFlow forkJoin)
                {
                    foreach (var branch in forkJoin.Branches)
                    {
                        foreach (var nested in branch.Body.Descendants())
                        {
                            yield return nested;
                        }
                    }
                }
                else if (statement is RequestFlow request)
                {
                    foreach (var outcome in request.Outcomes)
                    {
                        foreach (var nested in outcome.Body.Descendants())
                        {
                            yield return nested;
                        }
                    }
                }
                else if (statement is PartitionFlow partition)
                {
                    foreach (var nested in partition.Failed.Descendants())
                    {
                        yield return nested;
                    }
                }
                else if (statement is RecurrenceFlow recurrence)
                {
                    foreach (var nested in recurrence.Occurrence.Descendants())
                    {
                        yield return nested;
                    }
                    foreach (var nested in recurrence.Exhausted.Descendants())
                    {
                        yield return nested;
                    }
                    foreach (var nested in recurrence.Stalled.Descendants())
                    {
                        yield return nested;
                    }
                }
                else if (statement is AwaitMatchFlow awaitMatch)
                {
                    foreach (var clause in awaitMatch.Clauses)
                    {
                        foreach (var nested in clause.Body.Descendants())
                        {
                            yield return nested;
                        }
                    }
                }
                else if (statement is ExplicitDecisionFlow decision)
                {
                    foreach (var arm in decision.Arms)
                    {
                        foreach (var nested in arm.Body.Descendants())
                        {
                            yield return nested;
                        }
                    }
                    if (decision.Fallback is not null)
                    {
                        foreach (var nested in decision.Fallback.Body.Descendants())
                        {
                            yield return nested;
                        }
                    }
                }
                else if (statement is not IfFlow conditional)
                {
                    if (statement is not MatchFlow match)
                    {
                        continue;
                    }

                    foreach (var arm in match.Arms)
                    {
                        foreach (var nested in arm.Body.Descendants())
                        {
                            yield return nested;
                        }
                    }

                    if (match.Fallback is null)
                    {
                        continue;
                    }

                    foreach (var nested in match.Fallback.Descendants())
                    {
                        yield return nested;
                    }
                }
                else
                {
                    foreach (var nested in conditional.WhenTrue.Descendants())
                    {
                        yield return nested;
                    }

                    if (conditional.WhenFalse is null)
                    {
                        continue;
                    }

                    foreach (var nested in conditional.WhenFalse.Descendants())
                    {
                        yield return nested;
                    }
                }
            }
        }
    }

    sealed record MatchArm(string Pattern, FlowBlock Body, SourceReference Source);

    sealed record MatchFlow(
        FlowIdentity Identity,
        IOperation Value,
        ImmutableArray<MatchArm> Arms,
        FlowBlock? Fallback,
        SourceReference Source,
        SyntaxNode Syntax)
        : FlowStatement(Identity, Source, Syntax);

    sealed record DecisionArmFlow(
        FlowIdentity Identity,
        string Name,
        IArgumentOperation Selector,
        FlowBlock Body,
        IInvocationOperation Declaration,
        SourceReference Source,
        SyntaxNode Syntax);

    sealed record DecisionFallbackFlow(
        FlowIdentity Identity,
        string Name,
        FlowBlock Body,
        SourceReference Source,
        SyntaxNode Syntax);

    sealed record ExplicitDecisionFlow(
        FlowIdentity Identity,
        DecisionAuthoringKind Kind,
        IOperation? Value,
        ImmutableArray<DecisionArmFlow> Arms,
        DecisionFallbackFlow? Fallback,
        IInvocationOperation Invocation,
        SourceReference Source,
        SyntaxNode Syntax)
        : FlowStatement(Identity, Source, Syntax);

    sealed record ForkBranchFlow(
        FlowIdentity Identity,
        string Name,
        FlowBlock Body,
        IOperation? Result,
        IArgumentOperation? ExplicitId,
        IArgumentOperation? CapacityDomain,
        IArgumentOperation? Role,
        IArgumentOperation? EdgeOwner,
        SourceReference Source,
        SyntaxNode Syntax);

    sealed record ForkJoinFlow(
        FlowIdentity Identity,
        FlowIdentity JoinIdentity,
        ImmutableArray<ForkBranchFlow> Branches,
        ForkAuthoringMode Mode,
        AuthoredOutput? Result,
        IInvocationOperation Invocation,
        SourceReference Source,
        SyntaxNode Syntax)
        : FlowStatement(Identity, Source, Syntax);

    sealed record AuthoredOutput(
        ISymbol? Symbol,
        ITypeSymbol Type,
        FlowIdentity Owner,
        string Role,
        string Variable,
        IInvocationOperation? Declaration,
        SourceReference Source);

    sealed record PatternOutput(
        ILocalSymbol Symbol,
        AuthoredOutput Output,
        ImmutableArray<string> Path);

    sealed record BranchObligation(
        IParameterSymbol Parameter,
        FlowIdentity Owner,
        string Variable,
        SourceReference Source);

    sealed record ActionFlow(
        FlowIdentity Identity,
        ActionKind Kind,
        IInvocationOperation Invocation,
        SourceReference Source,
        SyntaxNode Syntax)
        : FlowStatement(Identity, Source, Syntax);

    sealed record RequestOutcomeFlow(
        FlowIdentity Identity,
        string Name,
        FlowBlock Body,
        IParameterSymbol? Input,
        IInvocationOperation? Declaration,
        string? ChildTerminalMember,
        SourceReference Source,
        SyntaxNode Syntax);

    sealed record RequestFlow(
        FlowIdentity Identity,
        RequestAuthoringKind Kind,
        ImmutableArray<RequestOutcomeFlow> Outcomes,
        IInvocationOperation Invocation,
        SourceReference Source,
        SyntaxNode Syntax)
        : FlowStatement(Identity, Source, Syntax);

    sealed record ProjectionFlow(
        IParameterSymbol Parameter,
        IOperation Expression,
        SourceReference Source,
        SyntaxNode Syntax);

    sealed record PartitionFlow(
        FlowIdentity Identity,
        AuthoredOutput Partition,
        ProjectionFlow ProgressIdentity,
        ProjectionFlow ChildInput,
        ProjectionFlow? CapacityIdentity,
        string FailedName,
        FlowBlock Failed,
        IInvocationOperation Invocation,
        SourceReference Source,
        SyntaxNode Syntax)
        : FlowStatement(Identity, Source, Syntax);

    sealed record RecurrenceFlow(
        FlowIdentity Identity,
        string OccurrenceName,
        FlowBlock Occurrence,
        IOperation Result,
        ProjectionFlow ContinueWhen,
        ProjectionFlow Progress,
        string ExhaustedName,
        FlowBlock Exhausted,
        string StalledName,
        FlowBlock Stalled,
        IInvocationOperation Invocation,
        SourceReference Source,
        SyntaxNode Syntax)
        : FlowStatement(Identity, Source, Syntax);

    sealed record AwaitClauseFlow(
        FlowIdentity Identity,
        string Name,
        AwaitClauseKind Kind,
        FlowBlock Body,
        AuthoredOutput? Input,
        IParameterSymbol? RequestObligation,
        IInvocationOperation Declaration,
        SourceReference Source,
        SyntaxNode Syntax);

    sealed record TypedAwaitClauseDeclaration(
        FlowIdentity Identity,
        AwaitClauseKind Kind,
        ITypeSymbol CaseType,
        IInvocationOperation Declaration,
        SourceReference Source,
        SyntaxNode Syntax);

    sealed record TypedAwaitSelection(
        SwitchSectionSyntax Section,
        ILocalSymbol? PatternLocal,
        ImmutableArray<TypedAwaitPropertyBinding> PropertyBindings,
        SourceReference Source,
        SyntaxNode Syntax);

    sealed record TypedAwaitPropertyBinding(
        ILocalSymbol Symbol,
        ImmutableArray<string> Path);

    sealed record AwaitMatchFlow(
        FlowIdentity Identity,
        ImmutableArray<AwaitClauseFlow> Clauses,
        IInvocationOperation Invocation,
        SourceReference Source,
        SyntaxNode Syntax)
        : FlowStatement(Identity, Source, Syntax);

    sealed record GeneratedDefinition(
        ImmutableArray<string> IdentityDeclarations,
        ImmutableArray<string> OutputDeclarations,
        ImmutableArray<string> BuilderStatements,
        string EntryIdentity);

    enum AwaitKind
    {
        Unsupported = 0,
        Query = 1,
        Read = 2,
        Transition = 3,
        Effect = 4
    }

    enum RequestAuthoringKind
    {
        Request = 1,
        ChildProcess = 2
    }

    enum DecisionAuthoringKind
    {
        Choice = 1,
        Match = 2
    }

    enum TerminalAuthoringKind
    {
        Return = 1,
        Fail = 2
    }

    enum ForkAuthoringMode
    {
        Unsupported = 0,
        All = 1,
        Any = 2,
        RequiredCount = 3
    }

    enum ActionKind
    {
        Unsupported = 0,
        Timer = 1,
        Reply = 2,
        Transition = 3,
        ContinueAt = 4,
        Succeed = 5,
        Terminate = 6
    }

    enum AwaitClauseKind
    {
        Unsupported = 0,
        Event = 1,
        Signal = 2,
        Request = 3,
        Timer = 4
    }
}
