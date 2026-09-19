using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Integrations;

/// <summary>Canonical acquisition and preparation evidence using the existing execution document authority.</summary>
/// <remarks>Validation does not perform I/O or establish retention, completeness, commit, or safe external retry.
/// Resolve and verify every referenced payload before use; pin the referenced definitions in the retained closure.</remarks>
public static class IngestionWorkDocuments
{
    static readonly ExecutionDefinitionDocumentProjection<IngestionWorkItem> Projection = new(
        new("integration.ingestion.work.v1"), "integrations.work.kind", "integrations.work.projection",
        "integrations.work.wire", "Ingestion work must use its canonical versioned wire representation.");

    /// <summary>Creates one immutable boundary document, with an identity derived from its original request.</summary>
    /// <param name="item">Complete frozen boundary, not a mutable progress snapshot.</param>
    /// <param name="provenance">Producer and source attribution.</param>
    /// <returns>Validated canonical document; retain it and its content before dispatching dependent work.</returns>
    /// <exception cref="ArgumentException">The boundary or provenance is invalid.</exception>
    /// <exception cref="ArgumentNullException">Item or provenance is null.</exception>
    public static ExecutionDefinitionDocument Create(IngestionWorkItem item, ExecutionProvenance provenance)
    {
        ArgumentNullException.ThrowIfNull(item);
        RequireValid(Validate(item));
        var (id, revision) = Identity(item);
        return ExecutionDefinitionDocument.Create(Projection.Kind, id, revision, item, provenance);
    }

    /// <summary>Reopens the strict envelope and validates identity, metadata, and typed evidence.</summary>
    /// <param name="json">Retained document JSON.</param>
    /// <param name="document">Parsed shared document when available.</param>
    /// <param name="item">Validated evidence only on success.</param>
    /// <returns>Structured envelope, wire, identity, and invariant diagnostics.</returns>
    public static DocumentValidationResult TryDeserialize(string json, out ExecutionDefinitionDocument? document, out IngestionWorkItem? item)
    {
        var result = ExecutionDefinitionJsonSerializer.TryDeserialize(json, out document);
        item = null;
        return result.IsValid ? TryRead(document!, out item) : result;
    }

    /// <summary>Validates a materialized boundary independently of any storage implementation.</summary>
    /// <param name="document">Exact boundary document.</param>
    /// <param name="item">Validated evidence only on success.</param>
    /// <returns>Structured diagnostics, including unsupported semantic extensions.</returns>
    /// <exception cref="ArgumentNullException">Document is null.</exception>
    public static DocumentValidationResult TryRead(ExecutionDefinitionDocument document, out IngestionWorkItem? item)
    {
        ArgumentNullException.ThrowIfNull(document);
        item = null;
        if (!document.Extensions.IsDefaultOrEmpty) return Error("extension", "Work extensions require an explicit interpreter.");
        var validation = Projection.ValidateAndProject(ExecutionDefinitionDocumentValidator.Validate(document), document, Validate, out item);
        if (!validation.IsValid) return validation;
        var (id, revision) = Identity(item!);
        if (document.Metadata.DefinitionId == id && document.Metadata.RevisionId == revision) return validation;
        item = null;
        return Error("identity", "Document identity must derive from the frozen operation and attempt.");
    }

    /// <summary>Validates the exact linked retained prefix before transformation, publication, or reconciliation.</summary>
    /// <param name="request">Original frozen request.</param>
    /// <param name="acquisition">Original acquisition evidence, or null before it is retained.</param>
    /// <param name="prepared">Original prepared publication, or null before it is retained.</param>
    /// <returns>Integrity, lineage, chronology, and ledger-profile diagnostics. Missing receipt never proves safe reacquisition.</returns>
    /// <exception cref="ArgumentNullException">Request is null.</exception>
    public static DocumentValidationResult ValidateChain(ExecutionDefinitionDocument request,
        ExecutionDefinitionDocument? acquisition = null, ExecutionDefinitionDocument? prepared = null)
    {
        var valid = TryRead(request, out var first);
        if (!valid.IsValid) return valid;
        if (first is not IngestionAcquisitionRequest selected) return Error("request", "The chain must start with an acquisition request.");
        if (acquisition is null)
            return prepared is null ? DocumentValidationResult.Valid : Error("lineage", "Prepared work requires its original acquisition evidence.");
        valid = TryRead(acquisition, out var second);
        if (!valid.IsValid) return valid;
        if (second is not IngestionAcquisitionReceipt acquired || acquired.Request != Reference(request))
            return Error("lineage", "Acquisition must bind the exact original request, including its fingerprint.");
        if (acquired.ObservedAtUtc < selected.SelectedAtUtc) return Error("chronology", "Acquisition cannot precede selection.");
        if (prepared is null) return DocumentValidationResult.Valid;
        valid = TryRead(prepared, out var third);
        if (!valid.IsValid) return valid;
        if (third is not IngestionPreparedPublication publication || publication.Acquisition != Reference(acquisition))
            return Error("lineage", "Preparation must bind the exact original acquisition, including its fingerprint.");
        if (publication.Transformation != selected.Transformation)
            return Error("transformation", "Preparation must use the transformation pinned before acquisition.");
        if (publication.PreparedAtUtc < acquired.ObservedAtUtc) return Error("chronology", "Preparation cannot precede acquisition.");
        if ((selected.ExpectedLedgerRevision is null) != (publication.NextPosition is null))
            return Error("ledger-profile", "Separate-ledger work requires both a frozen expectation and a proposed position; atomic-profile work has neither.");
        return DocumentValidationResult.Valid;
    }

    /// <summary>Binds confirmed sink evidence to retained work without reconstructing an expectation from current progress.</summary>
    /// <param name="request">Original request with its separate-ledger expectation.</param>
    /// <param name="acquisition">Original acquired evidence.</param>
    /// <param name="prepared">Authoritative prepared input.</param>
    /// <param name="receipt">Confirmed original sink receipt, independently verified by the publication adapter.</param>
    /// <param name="provenance">Attribution for the ledger advancement.</param>
    /// <returns>Canonical ledger advancement to retain before dispatch; never a new or rebased publication.</returns>
    /// <exception cref="ArgumentException">The chain, profile, or receipt binding differs.</exception>
    /// <exception cref="ArgumentNullException">A required document, receipt, or provenance is null.</exception>
    /// <remarks>The receipt's content fingerprint is the SHA-256 of the exact prepared bytes. This method
    /// validates attribution, not whether the sink committed. Unknown outcomes must not call this method.</remarks>
    public static ExecutionDefinitionDocument CreateLedgerAdvance(ExecutionDefinitionDocument request,
        ExecutionDefinitionDocument acquisition, ExecutionDefinitionDocument prepared,
        IngestionPublicationReceipt receipt, ExecutionProvenance provenance)
    {
        ArgumentNullException.ThrowIfNull(acquisition);
        ArgumentNullException.ThrowIfNull(prepared);
        ArgumentNullException.ThrowIfNull(receipt);
        RequireValid(ValidateChain(request, acquisition, prepared));
        TryRead(request, out var first);
        TryRead(prepared, out var last);
        var selected = (IngestionAcquisitionRequest)first!;
        var publication = (IngestionPreparedPublication)last!;
        if (selected.ExpectedLedgerRevision is not { } expected || publication.NextPosition is null)
            throw new ArgumentException("Atomic-profile work has no separate ledger advancement.", nameof(request));
        if (receipt.PublicationId != selected.OperationId || receipt.ContentFingerprint != publication.Content.Sha256)
            throw new ArgumentException("Receipt must bind the frozen publication identity and exact prepared bytes.", nameof(receipt));
        return IngestionLedgerDocuments.Create(new(selected.Address, selected.Definition, expected, publication.NextPosition, receipt), provenance);
    }

    static ExecutionDefinitionReference Reference(ExecutionDefinitionDocument document) =>
        new(document.Metadata.DefinitionId, document.Metadata.RevisionId, document.Metadata.Fingerprint);

    /// <summary>Derives the stable request lookup identity without reading mutable progress or manufacturing a draft request.</summary>
    /// <param name="address">Independent flow/source/destination/partition scope.</param>
    /// <param name="operationId">Original logical publication identity.</param>
    /// <returns>The identity used by Create for every attempt of this operation; lookup also requires its original attempt revision.</returns>
    /// <exception cref="ArgumentNullException">Address is null.</exception>
    /// <exception cref="ArgumentException">Scope or operation identity is invalid.</exception>
    public static ExecutionDefinitionId RequestId(IngestionLedgerAddress address, string operationId)
    {
        ArgumentNullException.ThrowIfNull(address);
        address.Validate();
        IngestionLedgerAddress.Require(operationId);
        return new("ingestion-work/" + Uri.EscapeDataString(address.Flow) + "/"
            + Uri.EscapeDataString(address.Source) + "/" + Uri.EscapeDataString(address.Destination) + "/"
            + Uri.EscapeDataString(address.Partition) + "/" + Uri.EscapeDataString(operationId));
    }

    static (ExecutionDefinitionId, ExecutionRevisionId) Identity(IngestionWorkItem item) => item switch
    {
        IngestionAcquisitionRequest request => (RequestId(request.Address, request.OperationId), new(request.AttemptId)),
        IngestionAcquisitionReceipt acquired => (new(acquired.Request.DefinitionId.Value + "/acquired"), acquired.Request.RevisionId),
        IngestionPreparedPublication prepared => (new(prepared.Acquisition.DefinitionId.Value + "/prepared"), prepared.Acquisition.RevisionId),
        _ => throw new ArgumentException("Unsupported ingestion boundary.", nameof(item))
    };

    static DocumentValidationResult Validate(IngestionWorkItem item)
    {
        try
        {
            switch (item)
            {
                case IngestionAcquisitionRequest request:
                    ArgumentNullException.ThrowIfNull(request.Address);
                    ArgumentNullException.ThrowIfNull(request.Definition);
                    ArgumentNullException.ThrowIfNull(request.Selection);
                    ArgumentNullException.ThrowIfNull(request.Transformation);
                    request.Address.Validate();
                    IngestionLedgerAddress.Require(request.OperationId);
                    IngestionLedgerAddress.Require(request.AttemptId);
                    request.Selection.Validate();
                    if (request.SelectedAtUtc.Offset != TimeSpan.Zero) return Error("time", "Selection observation must be UTC.");
                    if (request.ExpectedLedgerRevision is < 0 or long.MaxValue) return Error("revision", "Ledger expectation must leave room to advance.");
                    break;
                case IngestionAcquisitionReceipt acquired:
                    ArgumentNullException.ThrowIfNull(acquired.Request);
                    ArgumentNullException.ThrowIfNull(acquired.Content);
                    acquired.Content.Validate();
                    if (acquired.ObservedAtUtc.Offset != TimeSpan.Zero) return Error("time", "Acquisition observation must be UTC.");
                    break;
                case IngestionPreparedPublication prepared:
                    ArgumentNullException.ThrowIfNull(prepared.Acquisition);
                    ArgumentNullException.ThrowIfNull(prepared.Transformation);
                    ArgumentNullException.ThrowIfNull(prepared.Content);
                    prepared.Content.Validate();
                    if (prepared.PreparedAtUtc.Offset != TimeSpan.Zero) return Error("time", "Preparation observation must be UTC.");
                    if (prepared.NextPosition is not null) return IngestionLedgerDocuments.ValidatePosition(prepared.NextPosition);
                    break;
                default: return Error("boundary", "Unsupported retained boundary.");
            }
            return DocumentValidationResult.Valid;
        }
        catch (ArgumentException) { return Error("metadata", "Work requires valid identities, exact content metadata, and definition references."); }
    }

    static void RequireValid(DocumentValidationResult validation)
    {
        if (!validation.IsValid) throw new ArgumentException("Invalid ingestion work: " + validation.Diagnostics[0].Code);
    }

    static DocumentValidationResult Error(string code, string message) =>
        new([new("integrations.work." + code, DiagnosticSeverity.Error, message, "/definition")]);
}
