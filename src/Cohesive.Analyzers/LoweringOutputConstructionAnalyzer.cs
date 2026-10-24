using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Cohesive.Analyzers;

/// <summary>
/// Reports direct construction of canonical IR types that should be produced by lowering source.
/// </summary>
/// <remarks>
/// A type is restricted when it, or one of its base types, carries <c>Cohesive.Prelude.LoweringOutputAttribute</c>.
/// Construction is allowed in the assembly that declares the marked type and in assemblies that declare
/// <c>Cohesive.Prelude.IrProducerAttribute</c>. Both object creation and <c>with</c> copies are reported, since a
/// copy with replaced members is also a hand-assembled definition.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class LoweringOutputConstructionAnalyzer : DiagnosticAnalyzer
{
    const string LoweringOutputAttributeName = "Cohesive.Prelude.LoweringOutputAttribute";
    const string IrProducerAttributeName = "Cohesive.Prelude.IrProducerAttribute";

    /// <summary>
    /// Reported when code outside a lowering pipeline, serializer, or test assembly constructs a lowering output.
    /// </summary>
    public static readonly DiagnosticDescriptor LoweringOutputConstructedDirectly = new(
        id: "COHIR001",
        title: "Canonical IR constructed outside lowering",
        messageFormat: "'{0}' is canonical IR produced by lowering source; build it through an authoring API or "
            + "a serializer instead of constructing it in this assembly",
        category: "Cohesive.IR",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Source is the authoring authority and canonical IR is derived from it. Definition roots and "
            + "graph nodes are constructed only by lowering pipelines, IR serialization and persistence, and "
            + "compiler tests. Emit source through an authoring API, or declare [assembly: IrProducer(reason)] "
            + "in an assembly that is one of those producers.",
        helpLinkUri: "https://github.com/cohesivesystems/cohesive/blob/main/src/Cohesive.Analyzers/docs/COHIR001.md");

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [LoweringOutputConstructedDirectly];

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(OnCompilationStart);
    }

    static void OnCompilationStart(CompilationStartAnalysisContext context)
    {
        var loweringOutput = context.Compilation.GetTypeByMetadataName(LoweringOutputAttributeName);
        if (loweringOutput is null)
            return;

        var irProducer = context.Compilation.GetTypeByMetadataName(IrProducerAttributeName);
        if (irProducer is not null && HasAttribute(context.Compilation.Assembly.GetAttributes(), irProducer))
            return;

        var assembly = context.Compilation.Assembly;

        // Marking is resolved once per type definition per compilation, so repeated constructions of the same type,
        // and shared base types, do not re-read attributes along the base chain.
        var markedTypes = new ConcurrentDictionary<INamedTypeSymbol, INamedTypeSymbol?>(SymbolEqualityComparer.Default);
        context.RegisterOperationAction(
            operationContext => Analyze(operationContext, ((IObjectCreationOperation)operationContext.Operation).Type),
            OperationKind.ObjectCreation);
        context.RegisterOperationAction(
            operationContext => Analyze(operationContext, ((IWithOperation)operationContext.Operation).Type),
            OperationKind.With);

        void Analyze(OperationAnalysisContext operationContext, ITypeSymbol? type)
        {
            var marked = type is INamedTypeSymbol named ? FindMarkedType(named, loweringOutput, markedTypes) : null;
            if (marked is null || SymbolEqualityComparer.Default.Equals(marked.ContainingAssembly, assembly))
                return;

            operationContext.ReportDiagnostic(Diagnostic.Create(
                LoweringOutputConstructedDirectly,
                operationContext.Operation.Syntax.GetLocation(),
                type!.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
        }
    }

    // Returns the nearest type in the base chain that carries the marker, so a node declared in the Relations
    // assembly is checked against Relations even when the constructed type derives from it elsewhere.
    static INamedTypeSymbol? FindMarkedType(
        INamedTypeSymbol type,
        INamedTypeSymbol loweringOutput,
        ConcurrentDictionary<INamedTypeSymbol, INamedTypeSymbol?> cache)
    {
        var definition = type.OriginalDefinition;
        if (cache.TryGetValue(definition, out var cached))
            return cached;

        var marked = HasAttribute(definition.GetAttributes(), loweringOutput)
            ? definition
            : definition.BaseType is { } baseType
                ? FindMarkedType(baseType, loweringOutput, cache)
                : null;
        cache.TryAdd(definition, marked);
        return marked;
    }

    static bool HasAttribute(ImmutableArray<AttributeData> attributes, INamedTypeSymbol attributeType)
    {
        foreach (var attribute in attributes)
        {
            if (SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, attributeType))
                return true;
        }

        return false;
    }
}
