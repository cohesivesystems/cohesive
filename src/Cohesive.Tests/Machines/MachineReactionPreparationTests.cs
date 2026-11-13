using Cohesive.Execution;
using Cohesive.Examples.TenderAcceptance;
using Cohesive.Model;
using Cohesive.Processes.Machines;
using Cohesive.Storage.Processes;

namespace Cohesive.Tests.Machines;

public sealed class MachineReactionPreparationTests
{
    [Fact]
    public async Task Episode_selection_has_bounded_cold_cost_and_allocation_free_reuse()
    {
        var f = await MachineReactionEpisodeTests.Fixture.Create();
        var receipts = await f.TwoCycles();
        var id = Assert.Single(await f.Acceptance().AdmitAsync(f.Context, receipts.Acceptance1));
        var start = (await f.Store.LoadAsync(f.Context, id))!.Checkpoint.Start.Request;
        var preparation = MachineReactionInputs.For(f.Model.Acceptance);
        const int iterations = 1024;
        var inputs = new PortableValue[iterations];
        for (var index = 0; index < inputs.Length; index++)
            inputs[index] = PortableValue.Concrete(start.Input!.Contract, start.Input.Value!.Value);
        for (var warmup = 0; warmup < 100; warmup++) preparation.SelectEpisode(start.Input!);
        var before = GC.GetAllocatedBytesForCurrentThread();
        foreach (var input in inputs) preparation.SelectEpisode(input);
        var cold = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.InRange(cold, 0, 512L * iterations);
        before = GC.GetAllocatedBytesForCurrentThread();
        foreach (var input in inputs) preparation.SelectEpisode(input);
        var warm = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.InRange(warm, 0, 16L * iterations);
        var invocation = MachineReactionInvocation.Restore(f.Model.Acceptance, start);
        Assert.Same(invocation.Episode, (await f.Acceptance().InspectLifecycleAsync(f.Context, id))!.Episode);
        // Restoring retains full policy/origin/integrity checks. Its existing fingerprint cost is
        // separate from selecting a scalar, but must not regress past the measured old ~6.9 KB/call.
        for (var index = 0; index < 100; index++) MachineReactionInvocation.Restore(f.Model.Acceptance, start);
        before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < iterations; index++) MachineReactionInvocation.Restore(f.Model.Acceptance, start);
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 6656L * iterations);
    }

    [Fact]
    public void Identity_wire_projection_preserves_the_v1_shape_and_fingerprints()
    {
        var entity = new TenderReactionModel();
        var episodes = new TenderEpisodeModel();
        var occurrence = MachineReaction.On<EpisodeTender>(episodes.Tender, episodes.Accepted, "occurrence-compatibility", "1")
            .Request(episodes.MarkPending, source => source.LoadId, source => new EpisodeCommand(source.AcceptanceEpisode));
        foreach (var policy in new[] { occurrence, entity.PendingAcceptance, entity.ReceiptConfirmation, episodes.Acceptance, episodes.Confirmation })
        {
            var legacy = MachineReactionTests.Persist(policy, policy.Payload);
            Assert.Equal(ExecutionDefinitionJsonSerializer.Serialize(legacy), ExecutionDefinitionJsonSerializer.Serialize(policy.Document));
            Assert.Equal(policy.Document.Reference, MachineReaction.Deserialize(ExecutionDefinitionJsonSerializer.Serialize(legacy)).Document.Reference);
        }
        Assert.Null(typeof(MachineReaction).GetMethod("SelectEpisode"));
        Assert.Null(typeof(MachineReaction).GetProperty("EpisodeContract"));
    }

    [Fact]
    public void Failed_selection_is_not_cached_and_scalar_rules_are_shared_with_validation()
    {
        var policy = new TenderEpisodeModel().Acceptance;
        var preparation = MachineReactionInputs.For(policy);
        var invalid = PortableValue.Concrete(preparation.SourceContract, ObservationValue.EmptyObject);
        Assert.Throws<InvalidOperationException>(() => preparation.SelectEpisode(invalid));
        Assert.Throws<InvalidOperationException>(() => preparation.SelectEpisode(invalid));
    }
}
