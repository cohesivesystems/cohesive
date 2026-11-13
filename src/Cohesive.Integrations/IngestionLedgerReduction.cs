using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Integrations;

/// <summary>Pure receipt-before-CAS semantics shared by reference and future physical ledger interpreters.</summary>
public static class IngestionLedgerReduction
{
    /// <summary>Decides one advancement from an atomic, definitive view of the current entry and exact-operation receipt slot.</summary>
    /// <param name="current">Current entry under the requested ledger address, or definitively absent.</param>
    /// <param name="priorReceipt">Receipt under (address, publication ID), or definitively absent.</param>
    /// <param name="document">Validated immutable advancement.</param>
    /// <returns>Proposed committed evidence, exact replay, or conflict. New evidence must be committed atomically before a caller reports Advanced.</returns>
    /// <exception cref="ArgumentException">The advancement document is invalid.</exception>
    /// <remarks>Adapters with inconclusive receipt visibility must return Unknown without calling this reducer.
    /// The caller must serialize the read/evaluate/write protocol against competing advances, retain the original receipt,
    /// and never apply a new entry on Replay or Conflict. This function does no I/O or external-effect execution.</remarks>
    public static IngestionLedgerResult Evaluate(IngestionLedgerEntry? current, IngestionLedgerReceipt? priorReceipt,
        ExecutionDefinitionDocument document)
    {
        var advance = IngestionLedgerDocuments.Read(document);
        var reference = document.Reference;
        if (priorReceipt is not null)
        {
            if (priorReceipt.Entry is null || priorReceipt.Advance is null) return IngestionLedgerResult.Conflict("receipt-integrity");
            if (priorReceipt.Advance != reference || priorReceipt.Entry.Address != advance.Address)
                return IngestionLedgerResult.Conflict("identity-conflict");
            if (priorReceipt.Entry.Definition != advance.Definition || priorReceipt.Entry.Position != advance.Position
                || priorReceipt.Entry.Revision != advance.ExpectedRevision + 1)
                return IngestionLedgerResult.Conflict("receipt-integrity");
            return IngestionLedgerResult.Success(priorReceipt, replayed: true);
        }
        var conflict = CheckProgress(current, advance.Address, advance.Definition, advance.ExpectedRevision, advance.Position);
        if (conflict is not null) return IngestionLedgerResult.Conflict(conflict);
        var entry = new IngestionLedgerEntry(advance.Address, advance.Definition, advance.ExpectedRevision + 1, advance.Position);
        return IngestionLedgerResult.Success(new(reference, entry), replayed: false);
    }

    /// <summary>Checks frozen acquisition work before a new source effect.</summary>
    /// <param name="current">Definitively observed entry, or null for absent progress. Unknown reads must fail closed.</param>
    /// <param name="request">Retained canonical acquisition request owning consumer, definition and expected revision.</param>
    /// <param name="position">Proposed progress selected before acquisition; reuse it when preparing publication.</param>
    /// <returns>Diagnostics for invalid work or conflicting progress; validity is an observation, not a reservation.</returns>
    /// <remarks>Performs no I/O. Hosts invoke this at new dispatch boundaries; it does not automatically intercept
    /// arbitrary provider clients. Retained-result reconciliation uses receipt-first commit semantics instead.</remarks>
    public static DocumentValidationResult ValidateSelection(IngestionLedgerEntry? current,
        ExecutionDefinitionDocument? request, IngestionPosition? position)
    {
        if (request is null) return Invalid("request", "A retained acquisition request is required.");
        var validation = IngestionWorkDocuments.TryRead(request, out var item);
        if (!validation.IsValid) return validation;
        if (item is not IngestionAcquisitionRequest selected) return Invalid("request", "Expected acquisition work.");
        return ValidateSelectedRequest(current, selected, position);
    }

    /// <summary>Checks a linked retained publication against current progress without rebuilding its position.</summary>
    /// <param name="current">Definitively observed entry, or null for absent progress. Unknown reads must fail closed.</param>
    /// <param name="request">Original canonical acquisition request.</param>
    /// <param name="acquisition">Retained acquisition receipt linked to that exact request.</param>
    /// <param name="prepared">Retained preparation owning the proposed next position.</param>
    /// <returns>Lineage, work-validation or progress diagnostics; no receipt or reservation is created.</returns>
    /// <remarks>Reads NextPosition from preparation and revision/scope/definition from its linked request.
    /// Concurrent advancement after validation still requires commit CAS and adapter-specific effect fencing.</remarks>
    public static DocumentValidationResult ValidatePublication(IngestionLedgerEntry? current,
        ExecutionDefinitionDocument? request, ExecutionDefinitionDocument? acquisition, ExecutionDefinitionDocument? prepared)
    {
        if (request is null || acquisition is null || prepared is null)
            return Invalid("lineage", "Publication requires the complete retained work chain.");
        var validation = IngestionWorkDocuments.ValidateChain(request, acquisition, prepared);
        if (!validation.IsValid) return validation;
        var publication = (IngestionPreparedPublication)prepared.GetDefinition<IngestionWorkItem>();
        return ValidateSelectedRequest(current, (IngestionAcquisitionRequest)request.GetDefinition<IngestionWorkItem>(), publication.NextPosition);
    }

    static DocumentValidationResult ValidateSelectedRequest(IngestionLedgerEntry? current,
        IngestionAcquisitionRequest selected, IngestionPosition? position) =>
        selected.ExpectedLedgerRevision is { } revision
            ? ValidateSelection(current, selected.Address, selected.Definition, revision, position)
            : Invalid("revision", "Separate-ledger progress requires a frozen revision.");

    internal static DocumentValidationResult ValidateSelection(IngestionLedgerEntry? current, IngestionLedgerAddress? address,
        ExecutionDefinitionReference? definition, long expectedRevision, IngestionPosition? position)
    {
        if (address is null || definition is null) return Invalid("identity", "Consumer and definition are required.");
        try { address.Validate(); }
        catch (ArgumentException) { return Invalid("identity", "Consumer identity is invalid."); }
        if (expectedRevision < 0 || expectedRevision == long.MaxValue)
            return Invalid("revision", "Revision must permit another advancement.");
        var validation = IngestionLedgerDocuments.ValidatePosition(position);
        if (!validation.IsValid) return validation;
        var conflict = CheckProgress(current, address, definition, expectedRevision, position!);
        return conflict is null ? DocumentValidationResult.Valid : Invalid(conflict,
            "New work conflicts with observed consumer progress: " + conflict + ". Inspect progress or select an independent backfill flow.");
    }

    static DocumentValidationResult Invalid(string code, string message) =>
        new([new("integrations.ledger." + code, DiagnosticSeverity.Error, message)]);

    static string? CheckProgress(IngestionLedgerEntry? current, IngestionLedgerAddress address,
        ExecutionDefinitionReference definition, long expectedRevision, IngestionPosition position)
    {
        if (current is not null && (current.Revision < 1 || current.Position is null || current.Definition is null))
            return "entry-integrity";
        if (current is not null && current.Address != address) return "scope-conflict";
        if ((current?.Revision ?? 0) != expectedRevision) return "revision-conflict";
        if (current is not null)
        {
            if (current.Definition != definition) return "definition-migration-required";
            switch (current.Position, position)
            {
                case (IngestionDateRangePosition previous, IngestionDateRangePosition next) when previous.EndExclusive == next.StartInclusive:
                    break;
                case (IngestionCursorPosition previous, IngestionCursorPosition next) when previous.Format == next.Format && previous.Token is not null:
                    // Equality/order/expiry of opaque token values is source-owned. The source operation owns cycle detection.
                    break;
                default: return "position-transition";
            }
        }
        return null;
    }

}

/// <summary>Thread-safe, process-local reference interpreter. It is not a durable ledger or a database crash proof.</summary>
/// <remarks>Retains every advancement receipt for its lifetime. Do not use it when recovery must survive process loss.</remarks>
public sealed class InMemoryIngestionLedger : IIngestionLedger
{
    readonly object gate = new();
    readonly Dictionary<IngestionLedgerAddress, IngestionLedgerEntry> entries = [];
    readonly Dictionary<(IngestionLedgerAddress, string), IngestionLedgerReceipt> receipts = [];

    /// <inheritdoc />
    public ValueTask<IngestionLedgerEntry?> ReadAsync(IngestionLedgerAddress address, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(address);
        address.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate) return ValueTask.FromResult(entries.GetValueOrDefault(address));
    }

    /// <inheritdoc />
    public ValueTask<IngestionLedgerResult> AdvanceAsync(ExecutionDefinitionDocument advance, CancellationToken cancellationToken)
    {
        var projected = IngestionLedgerDocuments.Read(advance);
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = (projected.Address, projected.Publication.PublicationId);
            var result = IngestionLedgerReduction.Evaluate(entries.GetValueOrDefault(projected.Address), receipts.GetValueOrDefault(key), advance);
            if (result.Disposition == IngestionLedgerDisposition.Advanced)
            {
                receipts.Add(key, result.Receipt!);
                entries[projected.Address] = result.Receipt!.Entry;
            }
            return ValueTask.FromResult(result);
        }
    }
}
