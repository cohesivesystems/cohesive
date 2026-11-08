using Cohesive.Execution;
using Cohesive.Transitions.Authoring;
using Cohesive.Transitions.IR;
using Cohesive.Transitions.Machines;
using Cohesive.Transitions.Model;

namespace Cohesive.Examples.TenderAcceptance;

internal sealed record LinkLoad(string LoadId);
internal sealed record UpdateNote(string Note);

// One entity authority for the bounded acceptance episode. Host-resolved existence and duplicate
// evidence are command facts; classification depends only on the entity's own fields.
internal sealed class TenderEntityModel
{
    internal TenderAcceptanceMachines.TenderDeclaration Machine { get; } = TenderAcceptanceMachines.Tender();
    internal EntityDefinition Entity { get; }
    internal Transition<TenderState, LinkLoad, string> Link { get; }
    internal Transition<TenderState, SendResult, string> AcceptPending { get; }
    internal Transition<TenderState, SendResult, string> AcceptConfirmed { get; }
    internal Transition<TenderState, Receipt, string> ConfirmReceipt { get; }
    internal Transition<TenderState, UpdateNote, string> EditNote { get; }

    internal TenderEntityModel()
    {
        Entity = new MachinePredicateBinding<TenderState, SendResult>(Machine.Definition)
            .State(Machine.Unlinked, tender => tender.LoadId == "" && !tender.Accepted && !tender.PendingReceipt)
            .State(Machine.Offered, tender => tender.LoadId != "" && !tender.Accepted && !tender.PendingReceipt)
            .State(Machine.Pending, tender => tender.LoadId != "" && tender.Accepted && tender.PendingReceipt)
            .State(Machine.Confirmed, tender => tender.LoadId != "" && tender.Accepted && !tender.PendingReceipt)
            .Bind(ObjectEntityDefinition.For<TenderState>(new("tender.acceptance-episode")));

        Link = TransitionAuthoring.Create<TenderState, LinkLoad, string>(Entity.Shape,
            new("tender.attach-load"), new("1"), t => t
                .Move(Machine.AttachLoad, (tender, input) => "Cannot link")
                .Set(new("load"), tender => tender.LoadId, (tender, input) => input.LoadId)
                .Return(new("result"), TransitionOutcomeDisposition.Applied, "Linked"));
        AcceptPending = TransitionAuthoring.Create<TenderState, SendResult, string>(Entity.Shape,
            new("tender.accept-pending"), new("1"), t => t
                .Move(Machine.SendPending, (tender, input) => "Cannot accept")
                .Set(tender => tender.Accepted, true)
                .Set(tender => tender.PendingReceipt, true)
                .Return(new("result"), TransitionOutcomeDisposition.Applied, "Pending"));
        AcceptConfirmed = TransitionAuthoring.Create<TenderState, SendResult, string>(Entity.Shape,
            new("tender.accept-confirmed"), new("1"), t => t
                .Move(Machine.SendConfirmed, (tender, input) => "Cannot accept")
                .Set(tender => tender.Accepted, true)
                .Return(new("result"), TransitionOutcomeDisposition.Applied, "Confirmed"));
        ConfirmReceipt = TransitionAuthoring.Create<TenderState, Receipt, string>(Entity.Shape,
            new("tender.confirm-receipt"), new("1"), t => t
                .Move(Machine.ReceiptConfirmed, (tender, input) => "Cannot confirm")
                .Set(tender => tender.PendingReceipt, false)
                .Return(new("result"), TransitionOutcomeDisposition.Applied, "Confirmed"));
        EditNote = TransitionAuthoring.Create<TenderState, UpdateNote, string>(Entity.Shape,
            new("tender.edit-note"), new("1"), t => t
                .PreserveMachineState()
                .Set(new("note"), tender => tender.Note, (tender, input) => input.Note)
                .Return(new("result"), TransitionOutcomeDisposition.Applied, "Updated"));
    }
}
