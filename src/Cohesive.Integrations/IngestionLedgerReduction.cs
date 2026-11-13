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

    /// <summary>Checks proposed new source progress against an observed ledger entry before external dispatch.</summary>
    /// <param name="current">Definitively observed current entry, or null for an absent entry. Unknown reads must not call this method.</param>
    /// <param name="address">Exact consumer whose progress was observed.</param>
    /// <param name="definition">Pinned ingestion definition.</param>
    /// <param name="expectedRevision">Frozen revision selected for this new work; zero means absent.</param>
    /// <param name="position">Proposed forward progress, distinct from any acquisition lookback interval.</param>
    /// <returns>Structured diagnostics, or a valid observation. A valid result is not a reservation or dispatch capability.</returns>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">Address or revision is invalid.</exception>
    /// <remarks>Uses the same progress rules as commit evaluation. Call before new acquisition/publication to reject
    /// known stale, rewinding or skipped windows. Concurrent progress can change after this check; commit CAS and
    /// target-specific fencing remain necessary. Retained-result reconciliation must use original receipt semantics,
    /// not this new-work check. This method performs no I/O and creates no publication receipt.</remarks>
    public static DocumentValidationResult ValidateSelection(IngestionLedgerEntry? current, IngestionLedgerAddress address,
        ExecutionDefinitionReference definition, long expectedRevision, IngestionPosition position)
    {
        ArgumentNullException.ThrowIfNull(address); ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(position);
        address.Validate();
        if (expectedRevision < 0 || expectedRevision == long.MaxValue)
            throw new ArgumentException("Revision must permit another advancement.", nameof(expectedRevision));
        var validation = IngestionLedgerDocuments.ValidatePosition(position);
        if (!validation.IsValid) return validation;
        var conflict = CheckProgress(current, address, definition, expectedRevision, position);
        return conflict is null ? DocumentValidationResult.Valid : new([new("integrations.ledger." + conflict,
            DiagnosticSeverity.Error, "New work conflicts with observed consumer progress: " + conflict + ". Inspect progress or select an independent backfill flow.")]);
    }

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
