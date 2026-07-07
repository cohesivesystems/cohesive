using System.Collections.Immutable;
using System.Composition;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Cohesive.Analyzers;

/// <summary>
/// Applies targeted code fixes for union diagnostics.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(UnionPartialCodeFixProvider)), Shared]
public sealed class UnionPartialCodeFixProvider : CodeFixProvider
{
    /// <inheritdoc />
    public override ImmutableArray<string> FixableDiagnosticIds => ["COHDU001"];

    /// <inheritdoc />
    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    /// <inheritdoc />
    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var cancellationToken = context.CancellationToken;
        var root = await context.Document.GetSyntaxRootAsync(cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        if (root is null)
        {
            return;
        }

        var diagnostic = context.Diagnostics.FirstOrDefault();
        if (diagnostic is null)
        {
            return;
        }

        var node = root.FindNode(span: diagnostic.Location.SourceSpan);
        var typeDeclaration = node.FirstAncestorOrSelf<TypeDeclarationSyntax>();
        if (typeDeclaration is null)
        {
            return;
        }

        context.RegisterCodeFix(
            action: CodeAction.Create(
                title: "Add partial modifier",
                createChangedDocument: token => AddPartialModifierAsync(document: context.Document, root: root, declaration: typeDeclaration, cancellationToken: token),
                equivalenceKey: "Cohesive.AddPartialModifier"),
            diagnostic: diagnostic);
    }

    static Task<Document> AddPartialModifierAsync(
        Document document,
        SyntaxNode root,
        TypeDeclarationSyntax declaration,
        CancellationToken cancellationToken)
    {
        if (declaration.Modifiers.Any(predicate: static modifier => modifier.IsKind(kind: SyntaxKind.PartialKeyword)))
        {
            return Task.FromResult(result: document);
        }

        var updatedDeclaration = declaration.WithModifiers(
            modifiers: declaration.Modifiers.Add(SyntaxFactory.Token(kind: SyntaxKind.PartialKeyword)));
        var updatedRoot = root.ReplaceNode(oldNode: declaration, newNode: updatedDeclaration);
        return Task.FromResult(result: document.WithSyntaxRoot(root: updatedRoot));
    }
}
