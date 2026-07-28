using System.Security.Cryptography;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Execution;

/// <summary>
/// Validates execution-definition document integrity and portable extension values independently of activation.
/// </summary>
/// <remarks>
/// Persisted metadata diagnostics are retained producer observations, not current admission evidence. This
/// validator reports only findings recomputed from the document and the supplied resolution context.
/// </remarks>
public static class ExecutionDefinitionDocumentValidator
{
    /// <summary>Recomputes extension portability and semantic fingerprint integrity.</summary>
    /// <param name="document">Portable execution-definition document to validate.</param>
    /// <param name="graph">
    /// Optional shared shape graph used to resolve named extension-payload types and graph-qualified shapes.
    /// </param>
    /// <returns>Deterministically ordered structured integrity and portability diagnostics.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">Semantic content has no canonical JSON encoding.</exception>
    /// <exception cref="System.Text.Json.JsonException">
    /// Semantic content cannot be encoded using the strict JSON contract.
    /// </exception>
    /// <exception cref="NotSupportedException">Semantic content contains an unsupported runtime type.</exception>
    public static DocumentValidationResult Validate(
        ExecutionDefinitionDocument document,
        ShapeGraph? graph = null)
    {
        ArgumentNullException.ThrowIfNull(document);

        List<DocumentValidationDiagnostic> diagnostics = [];
        ValidateExtensions(document, graph, diagnostics);
        ValidateFingerprint(document, diagnostics);
        diagnostics.Sort(DocumentValidationDiagnosticComparer.Ordinal);
        return DocumentValidationResult.FromDiagnostics(diagnostics);
    }

    static void ValidateExtensions(
        ExecutionDefinitionDocument document,
        ShapeGraph? graph,
        ICollection<DocumentValidationDiagnostic> diagnostics)
    {
        for (var index = 0; index < document.Extensions.Length; index++)
        {
            var prefix = $"/extensions/{index}/value";
            var value = document.Extensions[index].Value;
            var validation = PortableExecutionValidator.Validate(value, graph);
            foreach (var diagnostic in validation.Diagnostics)
            {
                diagnostics.Add(diagnostic with
                {
                    Location = PrefixLocation(prefix, diagnostic.Location)
                });
            }
        }
    }

    static void ValidateFingerprint(
        ExecutionDefinitionDocument document,
        ICollection<DocumentValidationDiagnostic> diagnostics)
    {
        var fingerprint = document.Metadata.Fingerprint;
        if (!string.Equals(
                fingerprint.Algorithm,
                ExecutionDefinitionFingerprinter.Algorithm,
                StringComparison.Ordinal)
            || !string.Equals(
                fingerprint.Canonicalization,
                ExecutionDefinitionFingerprinter.Canonicalization,
                StringComparison.Ordinal))
        {
            diagnostics.Add(Error(
                ExecutionDefinitionDiagnosticCodes.FingerprintProfileUnsupported,
                $"Unsupported execution-definition fingerprint profile '{fingerprint.Algorithm}/{fingerprint.Canonicalization}'.",
                "/metadata/fingerprint"));
            return;
        }

        if (!IsLowercaseSha256(fingerprint.Value))
        {
            diagnostics.Add(Error(
                ExecutionDefinitionDiagnosticCodes.FingerprintValueInvalid,
                "The execution-definition fingerprint must be a 64-character lowercase hexadecimal SHA-256 digest.",
                "/metadata/fingerprint/value"));
            return;
        }

        var expected = ExecutionDefinitionFingerprinter.Compute(document);
        if (!string.Equals(expected.Value, fingerprint.Value, StringComparison.Ordinal))
        {
            diagnostics.Add(Error(
                ExecutionDefinitionDiagnosticCodes.FingerprintMismatch,
                "The execution-definition fingerprint does not match normalized semantic content.",
                "/metadata/fingerprint/value"));
        }
    }

    static string PrefixLocation(string prefix, string? location)
    {
        if (string.IsNullOrEmpty(location) || location == "$")
            return prefix;
        if (location[0] == '/')
            return prefix + location;
        return $"{prefix}/{location}";
    }

    static bool IsLowercaseSha256(string? value) =>
        !string.IsNullOrEmpty(value)
        && value.Length == SHA256.HashSizeInBytes * 2
        && value.All(static character =>
            character is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    static DocumentValidationDiagnostic Error(string code, string message, string location) =>
        new(
            Code: code,
            Severity: DiagnosticSeverity.Error,
            Message: message,
            Location: location);
}
