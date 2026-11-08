using System.Collections.Frozen;
using Cohesive.Machines;

namespace Cohesive.Examples.TenderAcceptance;

// Proposed source, compiled by the characterization suite. Not a replacement for the application's model.
// E02/E03: both acceptance outcomes persist Accepted; acknowledgement is a separate dimension.
internal sealed record AcceptanceFacts(bool LinkedLoadExists, bool Cancelled, bool AlreadyAcceptedLink);
internal sealed record SendResult(bool AutoAcknowledged);
internal sealed record Receipt(bool Correlated, bool Positive);

// Each declaration owns its source and freezes it before exposing handles. Wire identities are declared
// once here; consumers resolve handles against that same definition rather than maintaining a second catalog.
internal static class TenderAcceptanceMachines
{
    internal static TenderDeclaration Tender() => new();
    internal static LoadAcknowledgementView LoadAcknowledgement() => new();
    internal static CoordinationDeclaration Coordination() => new();

    // One tender lifecycle for this bounded episode: linkage and acceptance share an owner.
    // AttachLoad observes a persisted relationship; the host must establish load existence.
    internal sealed class TenderDeclaration
    {
        internal MachineDefinition Definition { get; }
        internal MachineState Unlinked { get; }
        internal MachineState Offered { get; }
        internal MachineState Pending { get; }
        internal MachineState Confirmed { get; }
        internal MachineMovement AttachLoad { get; }
        internal MachineMovement SendPending { get; }
        internal MachineMovement SendConfirmed { get; }
        internal MachineMovement ReceiptConfirmed { get; }

        internal TenderDeclaration()
        {
            var source = Machine.Define("tender.acceptance", "proposal-2");
            Unlinked = source.State("unlinked").Initial();
            Offered = source.State("unaccepted");
            Pending = source.State("accepted-awaiting-ack");
            Confirmed = source.State("accepted-confirmed").Final();
            AttachLoad = source.Edge("attach-load").From(Unlinked).To(Offered);
            const string sendResult = "send-result";
            SendPending = source.Edge("send-pending").On(sendResult).From(Offered).To(Pending)
                .When((AcceptanceFacts facts, SendResult result) => facts.LinkedLoadExists && !facts.Cancelled
                    && !facts.AlreadyAcceptedLink && !result.AutoAcknowledged);
            SendConfirmed = source.Edge("send-confirmed").On(sendResult).From(Offered).To(Confirmed)
                .When((AcceptanceFacts facts, SendResult result) => facts.LinkedLoadExists && !facts.Cancelled
                    && !facts.AlreadyAcceptedLink && result.AutoAcknowledged);
            // P01: positive-result validation is proposed. The observed handler does not perform it.
            ReceiptConfirmed = source.Edge("receipt-confirmed").From(Pending).To(Confirmed)
                .When((AcceptanceFacts facts, Receipt receipt) => facts.LinkedLoadExists && receipt.Correlated && receipt.Positive);
            Definition = source.Build();
        }
    }

    // Read-only lifecycle slice for reasoning about a related load. This is not a second
    // authoritative binding on Load, nor a complete load lifecycle. D02 requires its eventual
    // projection from the full load machine once dispatch/cancellation behavior is extracted.
    internal sealed class LoadAcknowledgementView
    {
        internal MachineDefinition Definition { get; }
        internal MachineState Pending { get; }
        internal MachineState Confirmed { get; }
        internal MachineMovement AcceptPending { get; }
        internal MachineMovement Confirm { get; }

        internal LoadAcknowledgementView()
        {
            var source = Machine.Define("load.tender-acknowledgement", "proposal-1");
            Pending = source.State("not-acknowledged").Initial();
            Confirmed = source.State("acknowledged").Final();
            AcceptPending = source.Edge("accept-pending").From(Pending).To(Pending);
            Confirm = source.Edge("confirm").From(Pending).To(Confirmed);
            Definition = source.Build();
        }
    }

    // Proposed coordination progress; effect completion, not dispatch, advances the machine.
    // Reconciliation deliberately has no automatic retry edge until idempotency is established.
    internal sealed class CoordinationDeclaration
    {
        internal MachineDefinition Definition { get; }
        internal MachineState Preparing { get; }
        internal MachineState Linked { get; }
        internal MachineState AwaitingReceipt { get; }
        internal MachineState Confirming { get; }
        internal MachineState Complete { get; }
        internal MachineState ReconciliationRequired { get; }
        internal MachineMovement LinkPersisted { get; }
        internal MachineMovement SendPending { get; }
        internal MachineMovement SendConfirmed { get; }
        internal MachineMovement ReceiptConfirmed { get; }
        internal MachineMovement ParticipantsConfirmed { get; }
        internal IReadOnlyDictionary<MachineState, MachineMovement> Uncertain { get; }

        internal CoordinationDeclaration()
        {
            var source = Machine.Define("tender.acceptance-coordination", "proposal-1");
            Preparing = source.State("preparing").Initial();
            Linked = source.State("linked");
            AwaitingReceipt = source.State("awaiting-receipt");
            Confirming = source.State("confirming");
            Complete = source.State("complete").Final();
            ReconciliationRequired = source.State("reconciliation-required").Final();
            LinkPersisted = source.Edge("link-persisted").From(Preparing).To(Linked);
            SendPending = source.Edge("send-pending").From(Linked).To(AwaitingReceipt);
            SendConfirmed = source.Edge("send-confirmed").From(Linked).To(Confirming);
            ReceiptConfirmed = source.Edge("receipt-confirmed").From(AwaitingReceipt).To(Confirming);
            ParticipantsConfirmed = source.Edge("participants-confirmed").From(Confirming).To(Complete);
            Uncertain = new[] { Preparing, Linked, AwaitingReceipt, Confirming }.ToFrozenDictionary(
                state => state,
                state => source.Edge("uncertain-" + state.Id).On("uncertain").From(state).To(ReconciliationRequired));
            Definition = source.Build();
        }
    }
}
