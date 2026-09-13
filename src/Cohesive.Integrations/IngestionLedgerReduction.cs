using Cohesive.Execution;

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
        var reference = new ExecutionDefinitionReference(document.Metadata.DefinitionId, document.Metadata.RevisionId, document.Metadata.Fingerprint);
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
        if (current is not null && (current.Revision < 1 || current.Position is null || current.Definition is null))
            return IngestionLedgerResult.Conflict("entry-integrity");
        if (current is not null && current.Address != advance.Address) return IngestionLedgerResult.Conflict("scope-conflict");
        if ((current?.Revision ?? 0) != advance.ExpectedRevision) return IngestionLedgerResult.Conflict("revision-conflict");
        if (current is not null)
        {
            if (current.Definition != advance.Definition) return IngestionLedgerResult.Conflict("definition-migration-required");
            switch (current.Position, advance.Position)
            {
                case (IngestionDateRangePosition previous, IngestionDateRangePosition next) when previous.EndExclusive == next.StartInclusive:
                    break;
                case (IngestionCursorPosition previous, IngestionCursorPosition next) when previous.Format == next.Format && previous.Token is not null:
                    // Equality/order/expiry of opaque token values is source-owned. The source operation owns cycle detection.
                    break;
                default: return IngestionLedgerResult.Conflict("position-transition");
            }
        }
        var entry = new IngestionLedgerEntry(advance.Address, advance.Definition, advance.ExpectedRevision + 1, advance.Position);
        return IngestionLedgerResult.Success(new(reference, entry), replayed: false);
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
