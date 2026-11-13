using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Contracts;

/// <summary>Canonical authoring and structural inspection of System Model contract properties.
/// Domain predicate interpretation and evidence qualification remain explicit interpreter obligations.</summary>
public static class ContractPropertyDocuments
{
    static readonly ExecutionDefinitionKind ContextKind = new("contract.property-context.v1");
    static readonly ExecutionDefinitionKind PredicateKind = new("contract.predicate.v1");
    static readonly ExecutionDefinitionDocumentProjection<ContractPropertyDefinition> Projection = new(
        new("contract.property.v1"), "contract.property.kind", "contract.property.projection",
        "contract.property.wire", "Use the canonical property representation.");

    /// <summary>Materializes a portable domain context for parameters, assumptions, intervals or bound annotations.</summary>
    /// <typeparam name="T">Application-owned structural source type.</typeparam>
    /// <param name="id">Stable context identity.</param>
    /// <param name="revision">Exact source revision.</param>
    /// <param name="source">Complete portable context declaration.</param>
    /// <param name="origin">Source attribution.</param>
    /// <returns>A fingerprinted context document.</returns>
    /// <exception cref="ArgumentNullException">Source or origin is null.</exception>
    /// <exception cref="ArgumentException">An identity is uninitialized.</exception>
    public static ExecutionDefinitionDocument DeclareContext<T>(ExecutionDefinitionId id, ExecutionRevisionId revision,
        T source, ExecutionProvenance origin) where T : class =>
        ExecutionDefinitionDocument.Create(ContextKind, id, revision, source, origin);

    /// <summary>Materializes a domain predicate whose interpretation is owned by the domain language.</summary>
    /// <typeparam name="T">Portable domain predicate source type.</typeparam>
    /// <param name="id">Stable predicate identity.</param>
    /// <param name="revision">Exact predicate revision.</param>
    /// <param name="source">Structural predicate declaration; executable delegates are unsupported.</param>
    /// <param name="origin">Source attribution.</param>
    /// <returns>A fingerprinted predicate document.</returns>
    /// <exception cref="ArgumentNullException">Source or origin is null.</exception>
    /// <exception cref="ArgumentException">An identity is uninitialized.</exception>
    public static ExecutionDefinitionDocument DeclarePredicate<T>(ExecutionDefinitionId id, ExecutionRevisionId revision,
        T source, ExecutionProvenance origin) where T : class =>
        ExecutionDefinitionDocument.Create(PredicateKind, id, revision, source, origin);

    /// <summary>Materializes one scoped assertion. Inspect the document to validate all linked declarations.</summary>
    /// <param name="id">Stable assertion identity.</param>
    /// <param name="revision">Exact assertion revision.</param>
    /// <param name="source">Assertion source; subject and classification sets are sorted during lowering.</param>
    /// <param name="origin">Source attribution.</param>
    /// <returns>A canonical assertion referenced by role-specific uses.</returns>
    /// <exception cref="ArgumentNullException">Source or origin is null.</exception>
    /// <exception cref="ArgumentException">An identity is uninitialized.</exception>
    public static ExecutionDefinitionDocument Declare(ExecutionDefinitionId id, ExecutionRevisionId revision,
        ContractPropertyDefinition source, ExecutionProvenance origin)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(origin);
        var normalized = source with
        {
            Classification = CanonicalDocumentCollections.SortIfNeeded(source.Classification.IsDefault ? [] : source.Classification,
                static (left, right) => left.CompareTo(right)),
            Scope = source.Scope is null ? null! : source.Scope with
            {
                Subjects = CanonicalDocumentCollections.SortIfNeeded(source.Scope.Subjects.IsDefault ? [] : source.Scope.Subjects,
                    static (left, right) => StringComparer.Ordinal.Compare(left, right))
            }
        };
        return ExecutionDefinitionDocument.Create(Projection.Kind, id, revision, normalized, origin,
            sourceMap: new([.. new[] { "kind", "classification", "predicate", "scope", "parameters", "assumptions", "quantitativeBound" }
                .Select(field => new ExecutionSourceProvenance(origin.Source.Reference + "#/" + field, new([field])))]));
    }

    /// <summary>Validates one property use and resolves every exact dependency. Success establishes
    /// structural validity only; no domain predicate is evaluated and no offer is proven sufficient.</summary>
    /// <param name="use">Role-specific assertion reference.</param>
    /// <param name="catalog">Exact declaration closure.</param>
    /// <param name="resolved">Receives the assertion only on complete structural success.</param>
    /// <returns>Structured declaration and dependency diagnostics.</returns>
    /// <exception cref="ArgumentNullException">Use or catalog is null.</exception>
    public static DocumentValidationResult Inspect(ContractProperty use, ExecutionDefinitionDocumentCatalog catalog,
        out ResolvedContractProperty? resolved)
    {
        ArgumentNullException.ThrowIfNull(use);
        ArgumentNullException.ThrowIfNull(catalog);
        resolved = null;
        if (!Enum.IsDefined(use.Role) || use.Property is null)
            return Error("use", "Supply a recognized contract role and exact property reference.", "/");
        var validation = catalog.ValidateReference(use.Property, "/property", out var document);
        if (!validation.IsValid) return validation;
        if (!document!.Extensions.IsDefaultOrEmpty)
            return Error("extension", "This interpreter cannot interpret property extensions.", "/extensions");
        validation = Projection.ValidateAndProject(ExecutionDefinitionDocumentValidator.Validate(document), document, Validate, out var definition);
        if (!validation.IsValid) return validation;
        (ExecutionDefinitionReference Reference, ExecutionDefinitionKind Kind, string Path)[] links = [
            (definition!.Predicate, PredicateKind, "/predicate"),
            (definition.Scope.Interval, ContextKind, "/scope/interval"),
            (definition.Parameters, ContextKind, "/parameters"),
            (definition.Assumptions, ContextKind, "/assumptions")];
        foreach (var link in links)
        {
            validation = Resolve(link.Reference, link.Kind, link.Path, catalog);
            if (!validation.IsValid) return validation;
        }
        if (definition.QuantitativeBound is { } bound)
        {
            validation = Resolve(bound, ContextKind, "/quantitativeBound", catalog);
            if (!validation.IsValid) return validation;
        }
        resolved = new(use, definition);
        return DocumentValidationResult.Valid;
    }

    static DocumentValidationResult Resolve(ExecutionDefinitionReference reference, ExecutionDefinitionKind kind,
        string path, ExecutionDefinitionDocumentCatalog catalog)
    {
        var result = catalog.ValidateReference(reference, path, out var document);
        if (!result.IsValid) return result;
        return document!.Kind != kind || !document.Extensions.IsDefaultOrEmpty
            ? Error("dependency", "Expected a supported predicate or context declaration at this boundary.", path)
            : DocumentValidationResult.Valid;
    }

    static DocumentValidationResult Validate(ContractPropertyDefinition value)
    {
        if (string.IsNullOrWhiteSpace(value.Kind) || value.Predicate is null || value.Parameters is null || value.Assumptions is null)
            return Error("assertion", "Declare the property kind, predicate, parameters and assumptions.", "/");
        if (value.Classification.IsDefaultOrEmpty)
            return Error("classification", "Declare at least one predicate classification.", "/classification");
        var previous = -1;
        foreach (var classification in value.Classification)
        {
            if (!Enum.IsDefined(classification) || (int)classification <= previous)
                return Error("classification", "Use distinct known classifications in canonical enum order.", "/classification");
            previous = (int)classification;
        }
        if (value.Classification.Contains(ContractPredicateClassification.Invariant)
            && !value.Classification.Contains(ContractPredicateClassification.Safety))
            return Error("classification", "An invariant must also declare Safety.", "/classification");
        var scope = value.Scope;
        if (scope is null || scope.Interval is null || string.IsNullOrWhiteSpace(scope.KeyDomain)
            || string.IsNullOrWhiteSpace(scope.Boundary) || string.IsNullOrWhiteSpace(scope.TimeBasis) || scope.Subjects.IsDefaultOrEmpty)
            return Error("scope", "Declare subjects, key domain, boundary, interval and time basis.", "/scope");
        string? prior = null;
        foreach (var subject in scope.Subjects)
        {
            if (string.IsNullOrWhiteSpace(subject) || prior is not null && StringComparer.Ordinal.Compare(prior, subject) >= 0)
                return Error("subjects", "Use distinct nonempty subject identities in ordinal order.", "/scope/subjects");
            prior = subject;
        }
        return DocumentValidationResult.Valid;
    }

    static DocumentValidationResult Error(string code, string message, string path) =>
        new([new("contract.property." + code, DiagnosticSeverity.Error, message, path)]);
}
