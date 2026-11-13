using Cohesive.Machines;
using Cohesive.Processes.Machines;
using Cohesive.Transitions.Authoring;
using Cohesive.Transitions.IR;
using Cohesive.Transitions.Machines;
using Cohesive.Transitions.Model;

namespace Cohesive.Examples.TenderAcceptance;

internal enum LoadAcknowledgementStatus { Ready, Pending, Confirmed }
internal sealed record LoadAcknowledgementState(LoadAcknowledgementStatus Status, bool CanAcknowledge);
internal sealed record AcknowledgeLoad(bool Confirmed);

// A bounded load acknowledgement episode, not a claim to model the complete load lifecycle.
// Ordered customer stops belong to the load; trips and original-trip proxies are not involved.
internal sealed class TenderReactionModel
{
    internal TenderEntityModel Tender { get; } = new();
    internal EntityDefinition Load { get; }
    internal Transition<LoadAcknowledgementState, AcknowledgeLoad, string> MarkPending { get; }
    internal Transition<LoadAcknowledgementState, AcknowledgeLoad, string> Confirm { get; }
    internal MachineReaction PendingAcceptance { get; }
    internal MachineReaction ReceiptConfirmation { get; }

    internal TenderReactionModel()
    {
        var machine = Machine.Define("load.acknowledgement-episode", "1");
        var ready = machine.State("ready").Initial();
        var pending = machine.State("pending");
        var confirmed = machine.State("confirmed").Final();
        var markPending = machine.Edge("mark-pending").From(ready).To(pending)
            .When((LoadAcknowledgementState load, AcknowledgeLoad command) => load.CanAcknowledge && !command.Confirmed);
        var confirm = machine.Edge("confirm").From(pending).To(confirmed)
            .When((LoadAcknowledgementState load, AcknowledgeLoad command) => load.CanAcknowledge && command.Confirmed);
        Load = new MachineDiscriminatorBinding<LoadAcknowledgementState, LoadAcknowledgementStatus>(machine.Build(), load => load.Status)
            .Map(ready, LoadAcknowledgementStatus.Ready).Map(pending, LoadAcknowledgementStatus.Pending)
            .Map(confirmed, LoadAcknowledgementStatus.Confirmed)
            .Bind(ObjectEntityDefinition.For<LoadAcknowledgementState>(new("load.acknowledgement-episode")));
        MarkPending = TransitionAuthoring.Create<LoadAcknowledgementState, AcknowledgeLoad, string>(Load.Shape,
            new("load.mark-pending"), new("1"), t => t.Move(markPending, (load, command) => "Cannot acknowledge")
                .Return(new("result"), TransitionOutcomeDisposition.Applied, "Pending"));
        Confirm = TransitionAuthoring.Create<LoadAcknowledgementState, AcknowledgeLoad, string>(Load.Shape,
            new("load.confirm"), new("1"), t => t.Move(confirm, (load, command) => "Cannot acknowledge")
                .Return(new("result"), TransitionOutcomeDisposition.Applied, "Confirmed"));

        PendingAcceptance = MachineReaction.On<TenderState>(Tender.Entity, Tender.Machine.SendPending,
                id: "tender-to-load.pending", revision: "2")
            .OncePerSourceEntity()
            .Request(MarkPending, subject: tender => tender.LoadId, input: tender => new AcknowledgeLoad(false));
        ReceiptConfirmation = MachineReaction.On<TenderState>(Tender.Entity, Tender.Machine.ReceiptConfirmed,
                id: "tender-to-load.confirmed", revision: "2")
            .After(PendingAcceptance)
            .Request(Confirm, subject: tender => tender.LoadId, input: tender => new AcknowledgeLoad(true));
    }
}
