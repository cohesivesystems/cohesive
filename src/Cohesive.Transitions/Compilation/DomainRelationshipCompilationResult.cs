using System.Collections.Immutable;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Model;

namespace Cohesive.Transitions.Compilation;

/// <summary>
/// Result of compiling entity reference fields into a canonical relationship catalog.
/// </summary>
public sealed class DomainRelationshipCompilationResult
{
    internal DomainRelationshipCompilationResult(
        RelationshipCatalog? catalog,
        DocumentValidationResult validation)
    {
        Catalog = catalog;
        Validation = validation;
    }

    /// <summary>
    /// Canonical catalog when compilation completed without error diagnostics; otherwise <see langword="null"/>.
    /// </summary>
    public RelationshipCatalog? Catalog { get; }

    /// <summary>
    /// Structured diagnostics emitted while resolving entity references and validating the resulting catalog.
    /// </summary>
    public DocumentValidationResult Validation { get; }

    /// <summary>
    /// Structured diagnostics emitted while resolving entity references and validating the resulting catalog.
    /// </summary>
    public ImmutableArray<DocumentValidationDiagnostic> Diagnostics => Validation.Diagnostics;

    /// <summary>
    /// Whether compilation produced a complete canonical catalog.
    /// </summary>
    public bool IsValid => Catalog is not null && Validation.IsValid;
}
