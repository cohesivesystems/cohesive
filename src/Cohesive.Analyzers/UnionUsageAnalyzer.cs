using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Cohesive.Analyzers;

/// <summary>
/// Enforces robust union usage patterns in user code.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class UnionUsageAnalyzer : DiagnosticAnalyzer
{
    /// <summary>
    /// Reported when switch-based union handling includes a catch-all arm.
    /// </summary>
    public static readonly DiagnosticDescriptor SwitchOnUnionUsesCatchAll = new(
        id: "COHDU100",
        title: "Switch over union uses catch-all arm",
        messageFormat: "Switch over union type '{0}' uses a catch-all arm. Prefer explicit case coverage via Match or explicit case patterns.",
        category: "Cohesive.Unions",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Catch-all switch arms can hide newly added union cases. Prefer explicit case handling for union evolvability.",
        helpLinkUri: "https://github.com/eulerfx/Cohesive/blob/main/Cohesive.Analyzers/docs/COHDU100.md");

    /// <summary>
    /// Reported when Match receives a null callback.
    /// </summary>
    public static readonly DiagnosticDescriptor MatchCallbackIsNull = new(
        id: "COHDU101",
        title: "Match callback cannot be null",
        messageFormat: "Match callback '{0}' is null. Provide a non-null callback for every union case.",
        category: "Cohesive.Unions",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Match requires one callback per case. Null callbacks undermine exhaustive handling guarantees.",
        helpLinkUri: "https://github.com/eulerfx/Cohesive/blob/main/Cohesive.Analyzers/docs/COHDU101.md");

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
    [
        SwitchOnUnionUsesCatchAll,
        MatchCallbackIsNull,
    ];

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeSwitchExpression, SyntaxKind.SwitchExpression);
        context.RegisterSyntaxNodeAction(AnalyzeSwitchStatement, SyntaxKind.SwitchStatement);
        context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
    }

    static void AnalyzeSwitchExpression(SyntaxNodeAnalysisContext context)
    {
        var node = (SwitchExpressionSyntax)context.Node;
        if (!IsUnionLike(typeSymbol: context.SemanticModel.GetTypeInfo(expression: node.GoverningExpression, cancellationToken: context.CancellationToken).Type))
        {
            return;
        }

        var hasDiscardArm = node.Arms.Any(static arm => arm.Pattern.IsKind(kind: SyntaxKind.DiscardPattern));
        if (!hasDiscardArm)
        {
            return;
        }

        var expectedCaseCount = GetExpectedCaseCount(typeSymbol: context.SemanticModel.GetTypeInfo(expression: node.GoverningExpression, cancellationToken: context.CancellationToken).Type);
        var explicitArmCount = node.Arms.Count(static arm => !arm.Pattern.IsKind(kind: SyntaxKind.DiscardPattern));
        if (expectedCaseCount is not null && explicitArmCount >= expectedCaseCount.Value)
        {
            return;
        }

        var unionTypeName = context.SemanticModel.GetTypeInfo(expression: node.GoverningExpression, cancellationToken: context.CancellationToken).Type?.ToDisplayString() ?? "unknown";
        context.ReportDiagnostic(
            diagnostic: Diagnostic.Create(
                descriptor: SwitchOnUnionUsesCatchAll,
                location: node.SwitchKeyword.GetLocation(),
                messageArgs: [unionTypeName]));
    }

    static void AnalyzeSwitchStatement(SyntaxNodeAnalysisContext context)
    {
        var node = (SwitchStatementSyntax)context.Node;
        if (!IsUnionLike(typeSymbol: context.SemanticModel.GetTypeInfo(expression: node.Expression, cancellationToken: context.CancellationToken).Type))
        {
            return;
        }

        var hasDefaultLabel = node.Sections
            .SelectMany(selector: static section => section.Labels)
            .Any(static label => label.IsKind(kind: SyntaxKind.DefaultSwitchLabel));
        if (!hasDefaultLabel)
        {
            return;
        }

        var expectedCaseCount = GetExpectedCaseCount(typeSymbol: context.SemanticModel.GetTypeInfo(expression: node.Expression, cancellationToken: context.CancellationToken).Type);
        var explicitSectionCount = node.Sections.Count(section =>
            section.Labels.All(label => !label.IsKind(kind: SyntaxKind.DefaultSwitchLabel)));
        if (expectedCaseCount is not null && explicitSectionCount >= expectedCaseCount.Value)
        {
            return;
        }

        var unionTypeName = context.SemanticModel.GetTypeInfo(expression: node.Expression, cancellationToken: context.CancellationToken).Type?.ToDisplayString() ?? "unknown";
        context.ReportDiagnostic(
            diagnostic: Diagnostic.Create(
                descriptor: SwitchOnUnionUsesCatchAll,
                location: node.SwitchKeyword.GetLocation(),
                messageArgs: [unionTypeName]));
    }

    static void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
    {
        var node = (InvocationExpressionSyntax)context.Node;
        var symbolInfo = context.SemanticModel.GetSymbolInfo(expression: node, cancellationToken: context.CancellationToken);
        if (symbolInfo.Symbol is not IMethodSymbol methodSymbol)
        {
            return;
        }

        if (!string.Equals(a: methodSymbol.Name, b: "Match", comparisonType: StringComparison.Ordinal))
        {
            return;
        }

        if (!IsUnionLike(typeSymbol: methodSymbol.ContainingType))
        {
            return;
        }

        for (var index = 0; index < node.ArgumentList.Arguments.Count; index++)
        {
            var argument = node.ArgumentList.Arguments[index];
            if (!IsNullLike(argumentExpression: argument.Expression))
            {
                continue;
            }

            var parameterName = argument.NameColon?.Name.Identifier.ValueText
                                ?? methodSymbol.Parameters.ElementAtOrDefault(index)?.Name
                                ?? $"arg{index}";
            context.ReportDiagnostic(
                diagnostic: Diagnostic.Create(
                    descriptor: MatchCallbackIsNull,
                    location: argument.GetLocation(),
                    messageArgs: [parameterName]));
        }
    }

    static bool IsNullLike(ExpressionSyntax argumentExpression) =>
        argumentExpression.IsKind(kind: SyntaxKind.NullLiteralExpression)
        || argumentExpression.IsKind(kind: SyntaxKind.DefaultLiteralExpression)
        || argumentExpression is DefaultExpressionSyntax;

    static bool IsUnionLike(ITypeSymbol? typeSymbol)
    {
        if (typeSymbol is null)
        {
            return false;
        }

        if (typeSymbol.ToDisplayString(format: SymbolDisplayFormat.FullyQualifiedFormat) == "global::Cohesive.Prelude.IDiscriminatedUnion")
        {
            return true;
        }

        return typeSymbol.AllInterfaces.Any(interfaceSymbol =>
            interfaceSymbol.ToDisplayString(format: SymbolDisplayFormat.FullyQualifiedFormat)
            == "global::Cohesive.Prelude.IDiscriminatedUnion");
    }

    static int? GetExpectedCaseCount(ITypeSymbol? typeSymbol)
    {
        if (typeSymbol is not INamedTypeSymbol namedTypeSymbol)
        {
            return null;
        }

        if (string.Equals(a: namedTypeSymbol.Name, b: "Either", comparisonType: StringComparison.Ordinal)
            && namedTypeSymbol.Arity is >= 2 and <= 8)
        {
            return namedTypeSymbol.Arity;
        }

        var caseNumbers = namedTypeSymbol
            .GetMembers()
            .OfType<IMethodSymbol>()
            .Select(selector: static methodSymbol => methodSymbol.Name)
            .Where(predicate: static methodName => methodName.StartsWith(value: "TryGetCase", comparisonType: StringComparison.Ordinal))
            .Select(static methodName =>
            {
                var suffix = methodName.Substring(startIndex: "TryGetCase".Length);
                return int.TryParse(s: suffix, result: out var number) ? number : 0;
            })
            .Where(predicate: static number => number > 0)
            .DefaultIfEmpty()
            .Max();

        return caseNumbers > 0 ? caseNumbers : null;
    }
}
