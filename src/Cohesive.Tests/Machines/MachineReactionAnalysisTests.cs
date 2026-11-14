using Cohesive.Examples.TenderAcceptance;
using Cohesive.Processes.Machines;
using Cohesive.Storage.Processes;

namespace Cohesive.Tests.Machines;

public sealed class MachineReactionAnalysisTests
{
    [Fact]
    public void Registration_is_closed_exact_and_order_independent()
    {
        var model = new TenderEpisodeModel();
        Assert.True(MachineReactionAnalysis.Analyze([model.Acceptance, model.Confirmation]).IsValid);
        var missing = MachineReactionAnalysis.Analyze([model.Confirmation]);
        Assert.Equal(MachineReactionAnalysis.MissingPrerequisite, Assert.Single(missing.Diagnostics).Code);
        var duplicate = MachineReactionAnalysis.Analyze([model.Acceptance, model.Acceptance, model.Confirmation]);
        Assert.Equal(MachineReactionAnalysis.DuplicateRegistration, Assert.Single(duplicate.Diagnostics).Code);
        Assert.Equal(duplicate, MachineReactionAnalysis.Analyze([model.Confirmation, model.Acceptance, model.Acceptance]));
    }

    [Fact]
    public void Distinct_policies_cannot_share_a_recovery_process_route()
    {
        var model = new TenderEpisodeModel();
        var collision = MachineReaction.On<EpisodeTender>(model.Tender, model.Confirmed,
                model.Acceptance.Document.Metadata.DefinitionId.Value, model.Acceptance.Document.Metadata.RevisionId.Value)
            .OncePerEpisode(s => s.AcceptanceEpisode)
            .Request(model.MarkPending, s => s.LoadId, s => new EpisodeCommand(s.AcceptanceEpisode));
        Assert.Equal(model.Acceptance.Payload.Process.Reference, collision.Payload.Process.Reference);
        var result = MachineReactionAnalysis.Analyze([model.Acceptance, collision]);
        Assert.Equal(MachineReactionAnalysis.AmbiguousProcess, Assert.Single(result.Diagnostics).Code);
        Assert.Equal(result, MachineReactionAnalysis.Analyze([collision, model.Acceptance]));
    }

    [Fact]
    public void Another_revision_cannot_replace_the_pinned_prerequisite()
    {
        var model = new TenderEpisodeModel();
        var replacement = MachineReaction.On<EpisodeTender>(model.Tender, model.Accepted,
                model.Acceptance.Document.Metadata.DefinitionId.Value, "different-revision")
            .OncePerEpisode(s => s.AcceptanceEpisode)
            .Request(model.MarkPending, s => s.LoadId, s => new EpisodeCommand(s.AcceptanceEpisode));
        Assert.Equal(MachineReactionAnalysis.MissingPrerequisite,
            Assert.Single(MachineReactionAnalysis.Analyze([replacement, model.Confirmation]).Diagnostics).Code);
    }

    [Fact]
    public void Logical_recurrence_is_a_warning_not_an_impossible_exact_cycle()
    {
        var model = new TenderEpisodeModel();
        var next = MachineReaction.On<EpisodeTender>(model.Tender, model.Accepted,
                model.Acceptance.Document.Metadata.DefinitionId.Value, "next")
            .After(model.Confirmation)
            .Request(model.MarkPending, s => s.LoadId, s => new EpisodeCommand(s.AcceptanceEpisode));
        var result = MachineReactionAnalysis.Analyze([next, model.Confirmation, model.Acceptance]);
        Assert.True(result.IsValid);
        Assert.Equal(MachineReactionAnalysis.RecurringDefinition, Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public async Task Recovery_rejects_a_catalog_that_cannot_advance_its_prerequisite()
    {
        var fixture = await MachineReactionEpisodeTests.Fixture.Create();
        var exception = Assert.Throws<ArgumentException>(() => new MachineReactionRecovery(fixture.Store, [fixture.Confirmation()]));
        Assert.Contains(MachineReactionAnalysis.MissingPrerequisite, exception.Message);
        _ = new MachineReactionRecovery(fixture.Store, [fixture.Confirmation(), fixture.Acceptance()]);
    }
    [Fact]
    public async Task A_prerequisite_registered_for_another_authority_does_not_close_the_local_catalog()
    {
        var local = await MachineReactionEpisodeTests.Fixture.Create();
        var foreign = await MachineReactionEpisodeTests.Fixture.Create(authority: new("other", "test"));
        foreign.Store = local.Store;
        Assert.Throws<ArgumentException>(() => new MachineReactionRecovery(local.Store, [local.Confirmation(), foreign.Acceptance()]));
        _ = new MachineReactionRecovery(local.Store, [local.Confirmation(), foreign.Acceptance(), local.Acceptance()]);
    }

}
