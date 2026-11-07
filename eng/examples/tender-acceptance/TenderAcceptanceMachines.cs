using Cohesive.Machines;

namespace Cohesive.Examples.TenderAcceptance;

// Proposed source, compiled by the characterization suite. Not a replacement for the application's model.
// E02/E03: both acceptance outcomes persist Accepted; acknowledgement is a separate dimension.
internal sealed record AcceptanceFacts(bool LinkedLoadExists, bool Cancelled, bool AlreadyAcceptedLink);
internal sealed record SendResult(bool AutoAcknowledged);
internal sealed record Receipt(bool Correlated, bool Positive);

internal static class TenderAcceptanceMachines
{
    internal static MachineDefinition Tender()
    {
        var source = Machine.Define("tender.acceptance", "proposal-1");
        var offered = source.State("unaccepted").Initial();
        var pending = source.State("accepted-awaiting-ack");
        var confirmed = source.State("accepted-confirmed").Final();
        source.Edge("send-pending").On("send-result").From(offered).To(pending)
            .When((AcceptanceFacts facts, SendResult result) => facts.LinkedLoadExists && !facts.Cancelled
                && !facts.AlreadyAcceptedLink && !result.AutoAcknowledged);
        source.Edge("send-confirmed").On("send-result").From(offered).To(confirmed)
            .When((AcceptanceFacts facts, SendResult result) => facts.LinkedLoadExists && !facts.Cancelled
                && !facts.AlreadyAcceptedLink && result.AutoAcknowledged);
        // P01: positive-result validation is proposed. The observed handler does not perform it.
        source.Edge("receipt-confirmed").From(pending).To(confirmed)
            .When((AcceptanceFacts facts, Receipt receipt) => facts.LinkedLoadExists && receipt.Correlated && receipt.Positive);
        return source.Build();
    }

    internal static MachineDefinition Linkage()
    {
        var source = Machine.Define("tender.load-linkage", "proposal-1");
        var unlinked = source.State("unlinked").Initial();
        var linked = source.State("linked").Final();
        source.Edge("attach-load").From(unlinked).To(linked);
        return source.Build();
    }

    internal static MachineDefinition LoadAcknowledgement()
    {
        var source = Machine.Define("load.tender-acknowledgement", "proposal-1");
        var pending = source.State("not-acknowledged").Initial();
        var confirmed = source.State("acknowledged").Final();
        source.Edge("accept-pending").From(pending).To(pending);
        source.Edge("confirm").From(pending).To(confirmed);
        return source.Build();
    }

    // Proposed coordination progress; effect completion, not dispatch, advances the machine.
    // Reconciliation deliberately has no automatic retry edge until idempotency is established.
    internal static MachineDefinition Coordination()
    {
        var source = Machine.Define("tender.acceptance-coordination", "proposal-1");
        var preparing = source.State("preparing").Initial();
        var linked = source.State("linked");
        var awaiting = source.State("awaiting-receipt");
        var confirming = source.State("confirming");
        var complete = source.State("complete").Final();
        var reconcile = source.State("reconciliation-required").Final();
        source.Edge("link-persisted").From(preparing).To(linked);
        source.Edge("send-pending").From(linked).To(awaiting);
        source.Edge("send-confirmed").From(linked).To(confirming);
        source.Edge("receipt-confirmed").From(awaiting).To(confirming);
        source.Edge("participants-confirmed").From(confirming).To(complete);
        foreach (var state in new[] { preparing, linked, awaiting, confirming })
            source.Edge("uncertain-" + state.Id).On("uncertain").From(state).To(reconcile);
        return source.Build();
    }
}
