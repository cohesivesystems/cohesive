namespace Cohesive.Examples.TenderAcceptance;

internal enum PersistencePath { Legacy, EventSourced }
internal enum AcceptanceEffect
{
    CreateLoad, PersistLink, SendAcceptance, PersistPendingTender, PersistConfirmedTender,
    MarkLoadPending, MarkLoadConfirmed, PublishTripUpdated, PersistAcknowledgementLog
}

// An executable extraction of bounded source ordering, not a new framework IR or live host.
// E01/E02/E03; only the newly created, ordinary tender branch without visibility publication.
// D01 rejects the legacy original-trip workaround. This observed trace (including PublishTripUpdated)
// is retained for characterization, not as a requirement on the proposed host or order data model.
internal static class AcceptanceCoordination
{
    internal static AcceptanceEffect[] ObserveAcceptance(PersistencePath path, bool autoAcknowledged)
    {
        var tender = autoAcknowledged ? AcceptanceEffect.PersistConfirmedTender : AcceptanceEffect.PersistPendingTender;
        var load = autoAcknowledged ? AcceptanceEffect.MarkLoadConfirmed : AcceptanceEffect.MarkLoadPending;
        return path == PersistencePath.EventSourced
            ? [AcceptanceEffect.CreateLoad, AcceptanceEffect.PersistLink, AcceptanceEffect.SendAcceptance, tender, load]
            : [AcceptanceEffect.CreateLoad, AcceptanceEffect.PersistLink, AcceptanceEffect.SendAcceptance, load, tender];
    }

    // E04: source receipt result is logged, but does not select the handler path.
    // Scope: SendAckFor=Accept; no pending changes or cancellations. Correlation has already resolved a tender.
    internal static AcceptanceEffect[] ObserveReceipt(bool loadAlreadyAcknowledged) => loadAlreadyAcknowledged
        ? [AcceptanceEffect.PersistAcknowledgementLog]
        : [AcceptanceEffect.MarkLoadConfirmed, AcceptanceEffect.PublishTripUpdated, AcceptanceEffect.PersistConfirmedTender];
}

internal sealed record AcceptanceSnapshot(bool LoadExists = false, bool Linked = false,
    bool TenderAccepted = false, bool PendingReceipt = false, bool LoadAcknowledged = false);
internal sealed record AcceptanceRun(AcceptanceSnapshot Snapshot, IReadOnlyList<AcceptanceEffect> Completed,
    AcceptanceEffect? FailedBefore);

// Failure-injection interpretation of the extracted sequence. Each completed persistence effect commits
// independently. This deliberately cannot claim to undo an external send or deduplicate a retry.
internal static class AcceptanceTraceHost
{
    internal static AcceptanceRun Run(IEnumerable<AcceptanceEffect> plan, AcceptanceSnapshot? initial = null,
        AcceptanceEffect? failBefore = null)
    {
        var state = initial ?? new();
        var completed = new List<AcceptanceEffect>();
        foreach (var effect in plan)
        {
            if (effect == failBefore) return new(state, completed.ToArray(), effect);
            state = effect switch
            {
                AcceptanceEffect.CreateLoad => state with { LoadExists = true },
                AcceptanceEffect.PersistLink => state with { Linked = true },
                AcceptanceEffect.PersistPendingTender => state with { TenderAccepted = true, PendingReceipt = true },
                AcceptanceEffect.PersistConfirmedTender => state with { TenderAccepted = true, PendingReceipt = false },
                AcceptanceEffect.MarkLoadPending => state with { LoadAcknowledged = false },
                AcceptanceEffect.MarkLoadConfirmed => state with { LoadAcknowledged = true },
                _ => state
            };
            completed.Add(effect);
        }
        return new(state, completed.ToArray(), null);
    }
}
