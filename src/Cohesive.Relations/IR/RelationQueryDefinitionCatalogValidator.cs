using System.Collections.Immutable;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Serialization;

namespace Cohesive.Relations.IR;

public static partial class RelationQueryDefinitionValidator
{
    /// <summary>
    /// Validates against a persisted catalog snapshot and retains that exact document and fingerprint
    /// for downstream compiler provenance.
    /// </summary>
    /// <param name="definition">Canonical relation or query definition to validate.</param>
    /// <param name="catalogDocument">Exact persisted relationship catalog snapshot.</param>
    /// <returns>A catalog-bound validation result retaining the consumed snapshot.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="definition"/> or <paramref name="catalogDocument"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The catalog contains a value with no canonical relationship catalog JSON encoding.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// The catalog contains a runtime type unsupported by canonical relationship catalog serialization.
    /// </exception>
    public static RelationQueryCatalogValidationResult ValidateWithCatalog(
        RelationQueryDefinition definition,
        RelationshipCatalogDocument catalogDocument)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(catalogDocument);

        var documentValidation = RelationshipCatalogDocumentSemanticValidator.Validate(catalogDocument);
        if (catalogDocument.Catalog is null)
        {
            return new(
                catalogDocument,
                DocumentValidationResult.Combine(documentValidation, Validate(definition)),
                bindingShapes: []);
        }

        var bindingFlow = RelationQueryBindingFlowAnalyzer.Analyze(definition, catalogDocument.Catalog);
        var expressionAnalysis = RelationQueryExpressionAnalyzer.AnalyzeWithBindingFlow(
            definition,
            bindingFlow,
            catalogDocument,
            DocumentValidationResult.Combine(
                documentValidation,
                DocumentValidationResult.FromDiagnostics(bindingFlow.CatalogDiagnostics)));
        return new(
            catalogDocument,
            expressionAnalysis.Validation,
            expressionAnalysis.BindingShapes);
    }
}

/// <summary>
/// Whether a visible binding is guaranteed to have a value for every row emitted by a logical node.
/// </summary>
public enum RelationQueryBindingAvailability
{
    /// <summary>Every emitted row contains the binding.</summary>
    AlwaysPresent = 0,

    /// <summary>An emitted row may preserve the binding in an absent state.</summary>
    MayBeAbsent = 1
}

/// <summary>
/// Shape resolved for one visible value binding at the output of a logical query node.
/// </summary>
/// <param name="Node">Logical node whose output binding environment was analyzed.</param>
/// <param name="Binding">Value binding visible at the node output.</param>
/// <param name="Shape">
/// Graph-qualified semantic shape, or <see langword="null"/> when the binding is visible but its
/// shape cannot be established statically.
/// </param>
/// <param name="Availability">Whether the binding may be absent on an emitted row.</param>
public readonly record struct RelationQueryBindingShape(
    QueryNodeId Node,
    ValueBindingId Binding,
    QualifiedShapeId? Shape,
    RelationQueryBindingAvailability Availability);

/// <summary>
/// Catalog-aware relation/query validation result retaining the exact catalog snapshot consumed.
/// </summary>
public sealed class RelationQueryCatalogValidationResult
{
    /// <summary>Creates a catalog-bound validation result.</summary>
    /// <param name="catalogDocument">Exact catalog document consumed by validation.</param>
    /// <param name="validation">Combined document, catalog, and relation/query diagnostics.</param>
    /// <param name="bindingShapes">Shape analysis for bindings visible at each logical node output.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="catalogDocument"/> or <paramref name="validation"/> is <see langword="null"/>.
    /// </exception>
    internal RelationQueryCatalogValidationResult(
        RelationshipCatalogDocument catalogDocument,
        DocumentValidationResult validation,
        ImmutableArray<RelationQueryBindingShape> bindingShapes = default)
    {
        CatalogDocument = Guard.RequireNotNull(catalogDocument);
        Validation = Guard.RequireNotNull(validation);
        BindingShapes = bindingShapes.IsDefault
            ? []
            :
            [
                .. bindingShapes
                    .OrderBy(static binding => binding.Node.Value, StringComparer.Ordinal)
                    .ThenBy(static binding => binding.Binding.Value, StringComparer.Ordinal)
            ];
    }

    /// <summary>Exact versioned catalog snapshot consumed by validation.</summary>
    public RelationshipCatalogDocument CatalogDocument { get; }

    /// <summary>Catalog content fingerprint consumed by validation, or <see langword="null"/> when absent.</summary>
    public RelationshipCatalogFingerprint? CatalogFingerprint => CatalogDocument.CatalogFingerprint;

    /// <summary>Combined document, catalog, and relation/query validation result.</summary>
    public DocumentValidationResult Validation { get; }

    /// <summary>
    /// Deterministically ordered shape analysis for bindings visible at each logical node output.
    /// </summary>
    public ImmutableArray<RelationQueryBindingShape> BindingShapes { get; }

    /// <summary>Structured validation diagnostics.</summary>
    public IReadOnlyList<DocumentValidationDiagnostic> Diagnostics => Validation.Diagnostics;

    /// <summary>Whether the catalog document and relation/query definition are valid together.</summary>
    public bool IsValid => Validation.IsValid;
}
