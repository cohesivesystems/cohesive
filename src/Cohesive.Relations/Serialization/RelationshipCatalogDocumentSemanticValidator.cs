using Cohesive.Model.Serialization;
using Cohesive.Relations.Model;

namespace Cohesive.Relations.Serialization;

/// <summary>
/// Validates standalone relationship catalog document semantics and content integrity.
/// </summary>
public static class RelationshipCatalogDocumentSemanticValidator
{
    /// <summary>
    /// Validates schema version, catalog-local invariants, and semantic fingerprint integrity
    /// without resolving any shape graph.
    /// </summary>
    /// <param name="document">Portable relationship catalog document to validate.</param>
    /// <returns>Structured document and catalog-local validation diagnostics.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// The document catalog contains a value that has no canonical relationship catalog JSON encoding.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// The document catalog contains a runtime type that the canonical JSON serializer does not support.
    /// </exception>
    public static DocumentValidationResult Validate(RelationshipCatalogDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        List<DocumentValidationDiagnostic> diagnostics = [];
        if (document.Catalog is null)
        {
            diagnostics.Add(new(
                Code: "relationshipCatalog.catalog.missing",
                Severity: DiagnosticSeverity.Error,
                Message: "A relationship catalog document must contain a catalog.",
                Location: "/catalog"));
            return DocumentValidationResult.FromDiagnostics(diagnostics);
        }

        var catalogValidation = RelationshipCatalogValidator.Validate(document.Catalog);

        if (!string.Equals(
                document.SchemaVersion,
                RelationshipCatalogDocument.CurrentSchemaVersion,
                StringComparison.Ordinal))
        {
            diagnostics.Add(new(
                Code: "relationshipCatalog.schemaVersion.unsupported",
                Severity: DiagnosticSeverity.Error,
                Message: $"Unsupported relationship catalog document schema version '{document.SchemaVersion}'.",
                Location: "/schemaVersion"));
        }

        if (document.CatalogFingerprint is null)
        {
            diagnostics.Add(new(
                Code: "relationshipCatalog.fingerprint.missing",
                Severity: DiagnosticSeverity.Error,
                Message: "A relationship catalog document must contain a catalog fingerprint.",
                Location: "/catalogFingerprint"));
        }
        else if (!string.Equals(
                     document.CatalogFingerprint.Algorithm,
                     RelationshipCatalogFingerprinter.Algorithm,
                     StringComparison.Ordinal)
                 || !string.Equals(
                     document.CatalogFingerprint.Canonicalization,
                     RelationshipCatalogFingerprinter.Canonicalization,
                     StringComparison.Ordinal))
        {
            diagnostics.Add(new(
                Code: "relationshipCatalog.fingerprint.profileUnsupported",
                Severity: DiagnosticSeverity.Error,
                Message: $"Unsupported relationship catalog fingerprint profile '{document.CatalogFingerprint.Algorithm}/{document.CatalogFingerprint.Canonicalization}'.",
                Location: "/catalogFingerprint"));
        }
        else if (!IsLowercaseSha256(document.CatalogFingerprint.Value))
        {
            diagnostics.Add(new(
                Code: "relationshipCatalog.fingerprint.valueInvalid",
                Severity: DiagnosticSeverity.Error,
                Message: "The catalog fingerprint value must be a 64-character lowercase hexadecimal SHA-256 digest.",
                Location: "/catalogFingerprint/value"));
        }
        else if (catalogValidation.IsValid)
        {
            var expected = RelationshipCatalogFingerprinter.Compute(document.Catalog);
            if (!string.Equals(
                    expected.Value,
                    document.CatalogFingerprint.Value,
                    StringComparison.Ordinal))
            {
                diagnostics.Add(new(
                    Code: "relationshipCatalog.fingerprint.mismatch",
                    Severity: DiagnosticSeverity.Error,
                    Message: "Relationship catalog fingerprint does not match semantic catalog content.",
                    Location: "/catalogFingerprint/value"));
            }
        }

        return DocumentValidationResult.Combine(
            DocumentValidationResult.FromDiagnostics(diagnostics),
            catalogValidation);
    }

    static bool IsLowercaseSha256(string? value) =>
        !string.IsNullOrEmpty(value)
        && value.Length == 64
        && value.All(static character =>
            character is (>= '0' and <= '9') or (>= 'a' and <= 'f'));
}
