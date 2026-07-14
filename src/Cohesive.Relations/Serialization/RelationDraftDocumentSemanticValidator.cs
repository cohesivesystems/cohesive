using System.Text.Json;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Drafts;

namespace Cohesive.Relations.Serialization;

/// <summary>
/// Validates portable relation draft document semantics and content integrity.
/// </summary>
public static class RelationDraftDocumentSemanticValidator
{
    /// <summary>
    /// Validates schema version, draft-local invariants, and semantic fingerprint integrity without
    /// resolving shape graphs or relationship catalogs.
    /// </summary>
    /// <param name="document">Portable relation draft document to validate.</param>
    /// <returns>Structured document and draft-local validation diagnostics.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// The document draft contains a value that has no canonical relation draft JSON encoding.
    /// </exception>
    /// <exception cref="JsonException">
    /// The document draft contains a value that cannot be written using the strict canonical wire contract.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// The document draft contains a runtime type that the canonical JSON serializer does not support.
    /// </exception>
    public static DocumentValidationResult Validate(RelationDraftDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        List<DocumentValidationDiagnostic> diagnostics = [];
        if (document.Draft is null)
        {
            diagnostics.Add(new(
                Code: "relationDraft.draft.missing",
                Severity: DiagnosticSeverity.Error,
                Message: "A relation draft document must contain a draft.",
                Location: "/draft"));
            return DocumentValidationResult.FromDiagnostics(diagnostics);
        }

        var draftValidation = RelationDraftValidator.Validate(document.Draft);

        if (!string.Equals(
                document.SchemaVersion,
                RelationDraftDocument.CurrentSchemaVersion,
                StringComparison.Ordinal))
        {
            diagnostics.Add(new(
                Code: "relationDraft.schemaVersion.unsupported",
                Severity: DiagnosticSeverity.Error,
                Message: $"Unsupported relation draft document schema version '{document.SchemaVersion}'.",
                Location: "/schemaVersion"));
        }

        if (document.DraftFingerprint is null)
        {
            diagnostics.Add(new(
                Code: "relationDraft.fingerprint.missing",
                Severity: DiagnosticSeverity.Error,
                Message: "A relation draft document must contain a draft fingerprint.",
                Location: "/draftFingerprint"));
        }
        else if (!string.Equals(
                     document.DraftFingerprint.Algorithm,
                     RelationDraftFingerprinter.Algorithm,
                     StringComparison.Ordinal)
                 || !string.Equals(
                     document.DraftFingerprint.Canonicalization,
                     RelationDraftFingerprinter.Canonicalization,
                     StringComparison.Ordinal))
        {
            diagnostics.Add(new(
                Code: "relationDraft.fingerprint.profileUnsupported",
                Severity: DiagnosticSeverity.Error,
                Message: $"Unsupported relation draft fingerprint profile '{document.DraftFingerprint.Algorithm}/{document.DraftFingerprint.Canonicalization}'.",
                Location: "/draftFingerprint"));
        }
        else if (!IsLowercaseSha256(document.DraftFingerprint.Value))
        {
            diagnostics.Add(new(
                Code: "relationDraft.fingerprint.valueInvalid",
                Severity: DiagnosticSeverity.Error,
                Message: "The draft fingerprint value must be a 64-character lowercase hexadecimal SHA-256 digest.",
                Location: "/draftFingerprint/value"));
        }
        else if (draftValidation.IsValid)
        {
            var expected = RelationDraftFingerprinter.Compute(document.Draft);
            if (!string.Equals(
                    expected.Value,
                    document.DraftFingerprint.Value,
                    StringComparison.Ordinal))
            {
                diagnostics.Add(new(
                    Code: "relationDraft.fingerprint.mismatch",
                    Severity: DiagnosticSeverity.Error,
                    Message: "Relation draft fingerprint does not match semantic draft content.",
                    Location: "/draftFingerprint/value"));
            }
        }

        ValidateMetadata(document, diagnostics);

        return DocumentValidationResult.Combine(
            DocumentValidationResult.FromDiagnostics(diagnostics),
            draftValidation);
    }

    static void ValidateMetadata(
        RelationDraftDocument document,
        ICollection<DocumentValidationDiagnostic> diagnostics)
    {
        if (document.Metadata is null)
        {
            diagnostics.Add(new(
                Code: "relationDraft.metadata.missing",
                Severity: DiagnosticSeverity.Error,
                Message: "A relation draft document must contain metadata.",
                Location: "/metadata"));
            return;
        }

        for (var index = 0; index < document.Metadata.ProducerArtifacts.Length; index++)
        {
            var reference = document.Metadata.ProducerArtifacts[index];
            if (reference is null
                || string.IsNullOrWhiteSpace(reference.Kind)
                || string.IsNullOrWhiteSpace(reference.Value))
            {
                diagnostics.Add(new(
                    Code: "relationDraft.metadata.producerArtifactInvalid",
                    Severity: DiagnosticSeverity.Error,
                    Message: "A producer artifact reference must contain a non-empty kind and value.",
                    Location: $"/metadata/producerArtifacts/{index}"));
            }
        }

        if (document.Draft.Projection is null)
            return;

        var assignments = document.Draft.Projection.Assignments.IsDefault
            ? []
            : document.Draft.Projection.Assignments;
        var slots = assignments
            .Where(static slot => slot is not null)
            .GroupBy(static slot => slot.Id)
            .ToDictionary(static group => group.Key, static group => group.First());
        for (var index = 0; index < document.Metadata.ConventionDecisions.Length; index++)
        {
            var decision = document.Metadata.ConventionDecisions[index];
            var location = $"/metadata/conventionDecisions/{index}";
            if (decision is null)
            {
                diagnostics.Add(new(
                    Code: "relationDraft.metadata.conventionDecisionMissing",
                    Severity: DiagnosticSeverity.Error,
                    Message: "A convention decision cannot be null.",
                    Location: location));
                continue;
            }
            if (string.IsNullOrWhiteSpace(decision.RuleId))
            {
                diagnostics.Add(new(
                    Code: "relationDraft.metadata.conventionDecisionRuleIdMissing",
                    Severity: DiagnosticSeverity.Error,
                    Message: "A convention decision must identify the rule that produced it.",
                    Location: $"{location}/ruleId"));
            }
            if (string.IsNullOrWhiteSpace(decision.SourceBinding.Value))
            {
                diagnostics.Add(new(
                    Code: "relationDraft.metadata.conventionDecisionSourceBindingMissing",
                    Severity: DiagnosticSeverity.Error,
                    Message: "A convention decision must identify the source binding it inspected.",
                    Location: $"{location}/sourceBinding"));
            }
            if (decision.Source is { } source && source.Segments.IsDefaultOrEmpty)
            {
                diagnostics.Add(new(
                    Code: "relationDraft.metadata.conventionDecisionSourceMissing",
                    Severity: DiagnosticSeverity.Error,
                    Message: "A convention decision source path cannot be empty when supplied.",
                    Location: $"{location}/source"));
            }
            if (!slots.TryGetValue(decision.SlotId, out var slot))
            {
                diagnostics.Add(new(
                    Code: "relationDraft.metadata.conventionDecisionSlotUnknown",
                    Severity: DiagnosticSeverity.Error,
                    Message: $"Convention decision references unknown slot '{decision.SlotId.Value}'.",
                    Location: $"{location}/slotId"));
                continue;
            }
            if (decision.Target != slot.Target)
            {
                diagnostics.Add(new(
                    Code: "relationDraft.metadata.conventionDecisionTargetMismatch",
                    Severity: DiagnosticSeverity.Error,
                    Message: $"Convention decision target '{decision.Target}' does not match slot target '{slot.Target}'.",
                    Location: $"{location}/target"));
            }
            if (decision.CandidateId is not { } candidateId)
                continue;

            var candidate = slot.Candidates.FirstOrDefault(candidate =>
                candidate is not null && candidate.Id == candidateId);
            if (candidate is null)
            {
                diagnostics.Add(new(
                    Code: "relationDraft.metadata.conventionDecisionCandidateUnknown",
                    Severity: DiagnosticSeverity.Error,
                    Message: $"Convention decision references candidate '{candidateId.Value}' outside slot '{slot.Id.Value}'.",
                    Location: $"{location}/candidateId"));
                continue;
            }

            if (candidate.Value is FieldExpr field
                && (field.Binding != decision.SourceBinding || field.Path != decision.Source))
            {
                diagnostics.Add(new(
                    Code: "relationDraft.metadata.conventionDecisionSourceMismatch",
                    Severity: DiagnosticSeverity.Error,
                    Message: "Convention decision source binding/path does not match its field-expression candidate.",
                    Location: $"{location}/source"));
            }
        }
    }

    static bool IsLowercaseSha256(string? value) =>
        !string.IsNullOrEmpty(value)
        && value.Length == 64
        && value.All(static character =>
            character is (>= '0' and <= '9') or (>= 'a' and <= 'f'));
}
