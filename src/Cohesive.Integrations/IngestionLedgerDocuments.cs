using Cohesive.Model;
using Cohesive.Execution;
using Cohesive.Model.Serialization;

namespace Cohesive.Integrations;

/// <summary>Canonical durable advancement intents sharing the execution document wire, fingerprints and provenance.</summary>
public static class IngestionLedgerDocuments
{
    static readonly ExecutionDefinitionDocumentProjection<IngestionLedgerAdvance> Projection = new(
        new("integration.ingestion.ledger-advance.v1"), "integrations.ledger.kind", "integrations.ledger.projection",
        "integrations.ledger.wire", "Ledger advancement must use the canonical wire representation.");

    /// <summary>Creates a validated immutable advancement proposal for persistence before ledger dispatch.</summary>
    /// <param name="advance">Exact scope, revision, progress and confirmed sink evidence.</param>
    /// <param name="provenance">Producer attribution, retained in the shared envelope.</param>
    /// <returns>A canonical fingerprinted document; persistence is the caller's responsibility.</returns>
    /// <exception cref="ArgumentException">An advancement field or provenance is invalid.</exception>
    /// <exception cref="ArgumentNullException">Advancement or provenance is null.</exception>
    public static ExecutionDefinitionDocument Create(IngestionLedgerAdvance advance, ExecutionProvenance provenance)
    {
        ArgumentNullException.ThrowIfNull(advance);
        var validation = Validate(advance);
        if (!validation.IsValid) throw new ArgumentException("Invalid ledger advancement: " + validation.Diagnostics[0].Code, nameof(advance));
        return ExecutionDefinitionDocument.Create(Projection.Kind,
            new("ingestion-ledger/" + Uri.EscapeDataString(advance.Address.Flow) + "/"
                + Uri.EscapeDataString(advance.Address.Source) + "/" + Uri.EscapeDataString(advance.Address.Destination)
                + "/" + Uri.EscapeDataString(advance.Address.Partition)),
            new(advance.Publication.PublicationId), advance, provenance);
    }

    /// <summary>Reopens the strict shared document envelope and validates its ledger projection.</summary>
    /// <param name="json">Persisted advancement document JSON.</param>
    /// <param name="document">Receives the parsed shared document when available.</param>
    /// <param name="advance">Receives the validated advancement only on success.</param>
    /// <returns>Structured integrity, version, wire and invariant diagnostics.</returns>
    public static DocumentValidationResult TryDeserialize(string json, out ExecutionDefinitionDocument? document,
        out IngestionLedgerAdvance? advance)
    {
        var result = ExecutionDefinitionJsonSerializer.TryDeserialize(json, out document);
        if (!result.IsValid) { advance = null; return result; }
        return TryRead(document!, out advance);
    }

    /// <summary>Validates an immutable advancement without I/O.</summary>
    /// <param name="document">Exact advancement document.</param>
    /// <param name="advance">Receives a valid projection only on success.</param>
    /// <returns>Structured diagnostics, including unsupported extensions.</returns>
    /// <exception cref="ArgumentNullException">Document is null.</exception>
    public static DocumentValidationResult TryRead(ExecutionDefinitionDocument document, out IngestionLedgerAdvance? advance)
    {
        ArgumentNullException.ThrowIfNull(document);
        advance = null;
        if (!document.Extensions.IsDefaultOrEmpty) return Error("extension", "Ledger extensions require explicit interpretation.");
        return Projection.ValidateAndProject(ExecutionDefinitionDocumentValidator.Validate(document), document, Validate, out advance);
    }

    internal static IngestionLedgerAdvance Read(ExecutionDefinitionDocument document)
    {
        var result = TryRead(document, out var advance);
        if (!result.IsValid) throw new ArgumentException("Invalid ledger advancement: " + result.Diagnostics[0].Code, nameof(document));
        return advance!;
    }

    static DocumentValidationResult Validate(IngestionLedgerAdvance advance)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(advance.Address);
            ArgumentNullException.ThrowIfNull(advance.Definition);
            ArgumentNullException.ThrowIfNull(advance.Publication);
            advance.Address.Validate();
            IngestionLedgerAddress.Require(advance.Publication.PublicationId);
            IngestionLedgerAddress.Require(advance.Publication.ContentFingerprint);
            IngestionLedgerAddress.Require(advance.Publication.ReceiptReference);
            if (advance.ExpectedRevision is < 0 or long.MaxValue) return Error("revision", "Expected revision must be nonnegative with room to advance.");
            switch (advance.Position)
            {
                case IngestionDateRangePosition range when range.EndExclusive > range.StartInclusive:
                    break;
                case IngestionCursorPosition cursor:
                    IngestionLedgerAddress.Require(cursor.Format);
                    if (cursor.Token is { } token && (token.Length == 0 || IngestionLedgerAddress.StrictUtf8.GetByteCount(token) > 16384))
                        return Error("cursor", "Cursor must be nonempty and bounded to 16384 UTF-8 bytes, or null for source exhaustion.");
                    if (cursor.ValidUntilUtc is { } expires && expires.Offset != TimeSpan.Zero)
                        return Error("cursor", "Cursor expiry must be UTC.");
                    break;
                default: return Error("position", "Declare an opaque cursor or a nonempty half-open date range.");
            }
            return DocumentValidationResult.Valid;
        }
        catch (ArgumentException) { return Error("identity", "Ledger scope, definition and publication evidence must be complete valid identities."); }
    }

    static DocumentValidationResult Error(string code, string message) =>
        new([new("integrations.ledger." + code, DiagnosticSeverity.Error, message, "/definition")]);
}
