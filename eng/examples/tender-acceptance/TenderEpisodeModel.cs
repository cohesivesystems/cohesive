using Cohesive.Machines;
using Cohesive.Processes.Machines;
using Cohesive.Transitions.Authoring;
using Cohesive.Transitions.IR;
using Cohesive.Transitions.Machines;
using Cohesive.Transitions.Model;

namespace Cohesive.Examples.TenderAcceptance;

internal enum EpisodeTenderStatus { Offered, Pending, Confirmed }
internal enum EpisodeLoadStatus { Ready, Pending, Confirmed }
internal sealed record EpisodeTender(EpisodeTenderStatus Status, string LoadId, int AcceptanceEpisode);
internal sealed record EpisodeLoad(EpisodeLoadStatus Status, int AcceptanceEpisode);
internal sealed record EpisodeCommand(int Episode);

// Proposed repeated acceptance lifecycle. Source transitions authorize episode advancement;
// reaction correlation never grants permission to mutate a newer load episode.
internal sealed class TenderEpisodeModel
{
    internal EntityDefinition Tender { get; }
    internal EntityDefinition Load { get; }
    internal MachineMovement Accepted { get; }
    internal MachineMovement Confirmed { get; }
    internal Transition<EpisodeTender, EpisodeCommand, string> Accept { get; }
    internal Transition<EpisodeTender, EpisodeCommand, string> ConfirmReceipt { get; }
    internal Transition<EpisodeTender, EpisodeCommand, string> Resubmit { get; }
    internal Transition<EpisodeLoad, EpisodeCommand, string> MarkPending { get; }
    internal Transition<EpisodeLoad, EpisodeCommand, string> ConfirmLoad { get; }
    internal Transition<EpisodeLoad, EpisodeCommand, string> ReviseLoad { get; }
    internal MachineReaction Acceptance { get; }
    internal MachineReaction Confirmation { get; }

    internal TenderEpisodeModel()
    {
        var tender = Machine.Define("tender.repeated-acceptance", "1");
        var offered = tender.State("offered").Initial();
        var pending = tender.State("pending");
        var confirmed = tender.State("confirmed");
        Accepted = tender.Edge("accept").From(offered).To(pending)
            .When((EpisodeTender state, EpisodeCommand command) => state.AcceptanceEpisode == command.Episode);
        Confirmed = tender.Edge("confirm").From(pending).To(confirmed)
            .When((EpisodeTender state, EpisodeCommand command) => state.AcceptanceEpisode == command.Episode);
        var resubmit = tender.Edge("resubmit").From(confirmed).To(offered)
            .When((EpisodeTender state, EpisodeCommand command) => command.Episode > state.AcceptanceEpisode);
        Tender = new MachineDiscriminatorBinding<EpisodeTender, EpisodeTenderStatus>(tender.Build(), state => state.Status)
            .Map(offered, EpisodeTenderStatus.Offered).Map(pending, EpisodeTenderStatus.Pending).Map(confirmed, EpisodeTenderStatus.Confirmed)
            .Bind(ObjectEntityDefinition.For<EpisodeTender>(new("tender.repeated-acceptance")));
        Accept = TransitionAuthoring.Create<EpisodeTender, EpisodeCommand, string>(Tender.Shape,
            new("tender.episode.accept"), new("1"), t => t.Move(Accepted, (state, command) => "Acceptance episode mismatch")
                .Return(new("result"), TransitionOutcomeDisposition.Applied, "Accepted"));
        ConfirmReceipt = TransitionAuthoring.Create<EpisodeTender, EpisodeCommand, string>(Tender.Shape,
            new("tender.episode.confirm"), new("1"), t => t.Move(Confirmed, (state, command) => "Confirmation episode mismatch")
                .Return(new("result"), TransitionOutcomeDisposition.Applied, "Confirmed"));
        Resubmit = TransitionAuthoring.Create<EpisodeTender, EpisodeCommand, string>(Tender.Shape,
            new("tender.episode.resubmit"), new("1"), t => t.Move(resubmit, (state, command) => "A new acceptance episode is required")
                .Set(new("episode"), state => state.AcceptanceEpisode, (state, command) => command.Episode)
                .Return(new("result"), TransitionOutcomeDisposition.Applied, "Resubmitted"));

        var load = Machine.Define("load.repeated-acknowledgement", "1");
        var ready = load.State("ready").Initial();
        var loadPending = load.State("pending");
        var loadConfirmed = load.State("confirmed").Final();
        var markPending = load.Edge("mark-pending").From(ready).To(loadPending)
            .When((EpisodeLoad state, EpisodeCommand command) => state.AcceptanceEpisode == command.Episode);
        var confirm = load.Edge("confirm").From(loadPending).To(loadConfirmed)
            .When((EpisodeLoad state, EpisodeCommand command) => state.AcceptanceEpisode == command.Episode);
        var revise = load.Edge("revise").From(loadPending).To(ready)
            .When((EpisodeLoad state, EpisodeCommand command) => command.Episode > state.AcceptanceEpisode);
        Load = new MachineDiscriminatorBinding<EpisodeLoad, EpisodeLoadStatus>(load.Build(), state => state.Status)
            .Map(ready, EpisodeLoadStatus.Ready).Map(loadPending, EpisodeLoadStatus.Pending).Map(loadConfirmed, EpisodeLoadStatus.Confirmed)
            .Bind(ObjectEntityDefinition.For<EpisodeLoad>(new("load.repeated-acknowledgement")));
        MarkPending = TransitionAuthoring.Create<EpisodeLoad, EpisodeCommand, string>(Load.Shape,
            new("load.episode.pending"), new("1"), t => t.Move(markPending, (state, command) => "Acceptance episode mismatch")
                .Return(new("result"), TransitionOutcomeDisposition.Applied, "Pending"));
        ConfirmLoad = TransitionAuthoring.Create<EpisodeLoad, EpisodeCommand, string>(Load.Shape,
            new("load.episode.confirm"), new("1"), t => t.Move(confirm, (state, command) => "Stale confirmation episode")
                .Return(new("result"), TransitionOutcomeDisposition.Applied, "Confirmed"));
        ReviseLoad = TransitionAuthoring.Create<EpisodeLoad, EpisodeCommand, string>(Load.Shape,
            new("load.episode.revise"), new("1"), t => t.Move(revise, (state, command) => "A newer load episode is required")
                .Set(new("episode"), state => state.AcceptanceEpisode, (state, command) => command.Episode)
                .Return(new("result"), TransitionOutcomeDisposition.Applied, "Revised"));

        Acceptance = MachineReaction.On<EpisodeTender>(Tender, Accepted, id: "tender-to-load.episode.acceptance", revision: "1")
            .OncePerEpisode(state => state.AcceptanceEpisode)
            .Request(MarkPending, subject: state => state.LoadId, input: state => new EpisodeCommand(state.AcceptanceEpisode));
        Confirmation = MachineReaction.On<EpisodeTender>(Tender, Confirmed, id: "tender-to-load.episode.confirmation", revision: "1")
            .After(Acceptance)
            .Request(ConfirmLoad, subject: state => state.LoadId, input: state => new EpisodeCommand(state.AcceptanceEpisode));
    }
}
