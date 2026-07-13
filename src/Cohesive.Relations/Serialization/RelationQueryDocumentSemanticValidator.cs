using Cohesive.Model.Serialization;
using Cohesive.Relations.IR;

namespace Cohesive.Relations.Serialization;

/// <summary>
/// Validates portable relation/query document semantics and content integrity.
/// </summary>
public static class RelationQueryDocumentSemanticValidator
{
    /// <summary>Validates schema version, definition structure, and fingerprint integrity.</summary>
    /// <param name="document">Portable relation/query document to validate.</param>
    /// <returns>Structured validation diagnostics.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// The document definition contains a value that has no canonical relation/query JSON encoding.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// The document definition contains a runtime type that the canonical JSON serializer does not support.
    /// </exception>
    public static DocumentValidationResult Validate(RelationQueryDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        List<DocumentValidationDiagnostic> diagnostics = [];
        if (document.Definition is null)
        {
            diagnostics.Add(new(
                Code: "relationQuery.definition.missing",
                Severity: DiagnosticSeverity.Error,
                Message: "A relation/query document must contain a definition.",
                Location: "/definition"));
            return DocumentValidationResult.FromDiagnostics(diagnostics);
        }

        var definitionValidation = RelationQueryDefinitionValidator.Validate(document.Definition);

        if (!string.Equals(document.SchemaVersion, RelationQueryDocument.CurrentSchemaVersion, StringComparison.Ordinal))
        {
            diagnostics.Add(new(
                Code: "relationQuery.schemaVersion.unsupported",
                Severity: DiagnosticSeverity.Error,
                Message: $"Unsupported relation/query document schema version '{document.SchemaVersion}'.",
                Location: "/schemaVersion"));
        }

        if (document.DefinitionFingerprint is null)
        {
            diagnostics.Add(new(
                Code: "relationQuery.fingerprint.missing",
                Severity: DiagnosticSeverity.Error,
                Message: "A relation/query document must contain a definition fingerprint.",
                Location: "/definitionFingerprint"));
        }
        else if (!string.Equals(document.DefinitionFingerprint.Algorithm, RelationQueryDefinitionFingerprinter.Algorithm, StringComparison.Ordinal)
            || !string.Equals(document.DefinitionFingerprint.Canonicalization, RelationQueryDefinitionFingerprinter.Canonicalization, StringComparison.Ordinal))
        {
            diagnostics.Add(new(
                Code: "relationQuery.fingerprint.profileUnsupported",
                Severity: DiagnosticSeverity.Error,
                Message: $"Unsupported fingerprint profile '{document.DefinitionFingerprint.Algorithm}/{document.DefinitionFingerprint.Canonicalization}'.",
                Location: "/definitionFingerprint"));
        }
        else if (string.IsNullOrEmpty(document.DefinitionFingerprint.Value)
                 || document.DefinitionFingerprint.Value.Length != 64
                 || document.DefinitionFingerprint.Value.Any(static character =>
                     character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
        {
            diagnostics.Add(new(
                Code: "relationQuery.fingerprint.valueInvalid",
                Severity: DiagnosticSeverity.Error,
                Message: "The definition fingerprint value must be a 64-character lowercase hexadecimal SHA-256 digest.",
                Location: "/definitionFingerprint/value"));
        }
        else if (definitionValidation.IsValid)
        {
            var expected = RelationQueryDefinitionFingerprinter.Compute(document.Definition);
            if (!string.Equals(expected.Value, document.DefinitionFingerprint.Value, StringComparison.Ordinal))
            {
                diagnostics.Add(new(
                    Code: "relationQuery.fingerprint.mismatch",
                    Severity: DiagnosticSeverity.Error,
                    Message: "Relation/query definition fingerprint does not match semantic definition content.",
                    Location: "/definitionFingerprint/value"));
            }
        }

        return DocumentValidationResult.Combine(
            DocumentValidationResult.FromDiagnostics(diagnostics),
            definitionValidation);
    }
}
