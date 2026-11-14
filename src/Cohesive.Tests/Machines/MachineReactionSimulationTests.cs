using Cohesive.Execution;
using Cohesive.Examples.TenderAcceptance;
using Cohesive.Processes.Execution;
using Cohesive.Simulation.Scenarios;
using Cohesive.Simulation.Transitions;
using Cohesive.Storage;
using Cohesive.Storage.Processes;
using static Cohesive.Examples.TenderAcceptance.TenderEpisodeScenarios;
using static Cohesive.Simulation.Transitions.MachineReactionScenarioInterpreter;
using Fixture = Cohesive.Tests.Machines.MachineReactionEpisodeTests.Fixture;

namespace Cohesive.Tests.Machines;

public sealed class MachineReactionSimulationTests
{
    [Fact]
    public async Task Confirmation_first_duplicate_stale_episode_and_lost_ack_produce_a_reproducible_portable_trace()
    {
        var first = await RunExample();
        var second = await RunExample();
        Assert.Equal(ScenarioExecutionTraceJsonSerializer.Serialize(first), ScenarioExecutionTraceJsonSerializer.Serialize(second));
        Assert.Equal(first.Fingerprint, ScenarioExecutionTraceJsonSerializer.Deserialize(ScenarioExecutionTraceJsonSerializer.Serialize(first)).Fingerprint);
        Assert.Equal(nameof(MachineReactionStatus.Pending), Field(first, 1, nameof(ReactionScenarioObservation.Status)));
        Assert.Equal(nameof(MachineReactionStatus.Pending), Field(first, 4, nameof(ReactionScenarioObservation.Status)));
        Assert.Equal(Field(first, 1, nameof(ReactionScenarioObservation.Instance)), Field(first, 10, nameof(ReactionScenarioObservation.Instance)));
        Assert.Equal(Field(first, 1, nameof(ReactionScenarioObservation.Prerequisite)), Field(first, 6, nameof(ReactionScenarioObservation.Instance)));
        Assert.NotEqual(Field(first, 3, nameof(ReactionScenarioObservation.Instance)), Field(first, 1, nameof(ReactionScenarioObservation.Prerequisite)));
        Assert.Equal(nameof(MachineReactionStatus.Rejected), Field(first, 9, nameof(ReactionScenarioObservation.Status)));
        Assert.Equal("AdmissionRejected", Field(first, 9, nameof(ReactionScenarioObservation.Decision)));
        Assert.Equal(nameof(MachineReactionStatus.Completed), Field(first, 14, nameof(ReactionScenarioObservation.Status)));
        var uncertain = Enumerable.Range(11, 3).Single(i => Field(first, i, nameof(ReactionScenarioObservation.Status)) == nameof(MachineReactionStatus.Indeterminate));
        Assert.Equal(Field(first, uncertain, nameof(ReactionScenarioObservation.Operation)), Field(first, 14, nameof(ReactionScenarioObservation.Operation)));
        Assert.Equal(2, first.Outcomes[14].Output.Value!.Value.Fields![nameof(ReactionScenarioObservation.Attempts)].GetInt32());
        Assert.All(first.Outcomes, outcome => Assert.Empty(outcome.StateChanges));
    }

    [Fact]
    public async Task All_twenty_four_delivery_orders_preserve_episode_isolation_and_converge()
    {
        var count = 0;
        foreach (var order in Permutations([Acceptance1, Confirmation1, Acceptance2, Confirmation2]))
        {
            var fixture = await Fixture.Create(loadEpisode: 2, timeProvider: new FixedClock());
            var interpreter = await Interpreter(fixture);
            var steps = order.SelectMany(input => new[] { (Deliver, input), (Recover, input) }).ToList();
            steps.Add((Recover, Confirmation2));
            steps.Add((Recover, Confirmation1));
            var trace = await ScenarioRunner.ExecuteAsync(ScenarioWorldSnapshot.FromCoreWorld(Build(steps)), interpreter);
            Assert.Equal(nameof(MachineReactionStatus.Completed), Field(trace, 8, nameof(ReactionScenarioObservation.Status)));
            var acceptanceIndex = Array.IndexOf(order, Acceptance2) * 2 + 1;
            Assert.Equal(Field(trace, acceptanceIndex, nameof(ReactionScenarioObservation.Instance)),
                Field(trace, 8, nameof(ReactionScenarioObservation.Prerequisite)));
            var confirmationIndex = Array.IndexOf(order, Confirmation2) * 2 + 1;
            if (confirmationIndex < acceptanceIndex)
            {
                Assert.Equal(nameof(MachineReactionStatus.Pending), Field(trace, confirmationIndex, nameof(ReactionScenarioObservation.Status)));
                Assert.Equal(0, trace.Outcomes[confirmationIndex].Output.Value!.Value.Fields![nameof(ReactionScenarioObservation.Attempts)].GetInt32());
            }
            Assert.Equal(nameof(MachineReactionStatus.Failed), Field(trace, 9, nameof(ReactionScenarioObservation.Status)));
            Assert.Equal(0, trace.Outcomes[9].Output.Value!.Value.Fields![nameof(ReactionScenarioObservation.Attempts)].GetInt32());
            Assert.Contains("prerequisite", Field(trace, 9, nameof(ReactionScenarioObservation.Diagnostic)));
            var load = (await fixture.Loads.TryGet(fixture.Context, "load-1", EntityReadOptions.Full.WithPartitionKey("test")))!;
            Assert.Equal(nameof(EpisodeLoadStatus.Confirmed), load.Entity.Observation.GetField(nameof(EpisodeLoad.Status)).GetString());
            Assert.Equal(2, load.Entity.Observation.GetField(nameof(EpisodeLoad.AcceptanceEpisode)).GetInt32());
            count++;
        }
        Assert.Equal(24, count);
    }

    [Fact]
    public async Task Abandoned_first_episode_never_releases_a_dependent_or_blocks_the_second()
    {
        var fixture = await Fixture.Create(loadEpisode: 2, timeProvider: new FixedClock());
        var interpreter = await Interpreter(fixture);
        var trace = await ScenarioRunner.ExecuteAsync(ScenarioWorldSnapshot.FromCoreWorld(Build([
            (Deliver, Acceptance1), (Abandon, Acceptance1),
            (Deliver, Confirmation1), (Recover, Confirmation1),
            (Deliver, Confirmation2), (Recover, Confirmation2),
            (Deliver, Acceptance2), (Recover, Acceptance2), (Recover, Confirmation2)
        ])), interpreter);
        Assert.Equal(nameof(MachineReactionStatus.Abandoned), Field(trace, 1, nameof(ReactionScenarioObservation.Status)));
        Assert.Equal(nameof(ProcessDurableRuntimeDisposition.Applied), Field(trace, 1, nameof(ReactionScenarioObservation.ControlDisposition)));
        Assert.Equal("machine.reaction.prerequisite.abandoned", Field(trace, 3, nameof(ReactionScenarioObservation.Diagnostic)));
        Assert.Equal(nameof(MachineReactionStatus.Completed), Field(trace, 8, nameof(ReactionScenarioObservation.Status)));
    }

    [Fact]
    public async Task Recover_before_delivery_is_rejected_without_target_execution()
    {
        var fixture = await Fixture.Create(timeProvider: new FixedClock());
        var interpreter = await Interpreter(fixture);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await ScenarioRunner.ExecuteAsync(ScenarioWorldSnapshot.FromCoreWorld(Build([(Recover, Acceptance1)])), interpreter));
        Assert.Empty(await ((IProcessInstanceSource)fixture.Store).ListInstancesAsync(fixture.Context, null, 32));
    }

    [Fact]
    public async Task Abandonment_is_denied_by_default_and_does_not_dispatch()
    {
        var fixture = await Fixture.Create(timeProvider: new FixedClock());
        var receipts = await fixture.TwoCycles();
        var host = fixture.Acceptance();
        var interpreter = new MachineReactionScenarioInterpreter("tender/default-denial/v1", fixture.Store,
            new Dictionary<string, MachineReactionHost> { [AcceptanceHost] = host },
            new Dictionary<string, EntityTransitionOperationReceipt> { [Acceptance1.Receipt] = receipts.Acceptance1 });
        await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
            await ScenarioRunner.ExecuteAsync(ScenarioWorldSnapshot.FromCoreWorld(Build([
                (Deliver, Acceptance1), (Abandon, Acceptance1)])), interpreter));
        var instance = Assert.Single(await ((IProcessInstanceSource)fixture.Store).ListInstancesAsync(fixture.Context, null, 32));
        var inspection = (await host.InspectLifecycleAsync(fixture.Context, instance))!;
        Assert.Equal(MachineReactionStatus.Pending, inspection.Progress.Status);
        Assert.Empty(inspection.Progress.Attempts);
    }

    [Fact]
    public async Task Interpreter_identity_pins_environment_and_is_independent_of_registration_order()
    {
        var fixture = await Fixture.Create(timeProvider: new FixedClock());
        var receipts = await fixture.TwoCycles();
        Dictionary<string, MachineReactionHost> hosts = new()
        { [AcceptanceHost] = fixture.Acceptance(), [ConfirmationHost] = fixture.Confirmation() };
        Dictionary<string, EntityTransitionOperationReceipt> sources = new()
        { [Acceptance1.Receipt] = receipts.Acceptance1, [Confirmation2.Receipt] = receipts.Confirmation2 };
        var first = new MachineReactionScenarioInterpreter("environment/1", fixture.Store, hosts, sources);
        var reordered = new MachineReactionScenarioInterpreter("environment/1", fixture.Store, hosts.Reverse().ToDictionary(), sources.Reverse().ToDictionary());
        Assert.Equal(first.Identity, reordered.Identity);
        Assert.NotEqual(first.Identity, new MachineReactionScenarioInterpreter("environment/2", fixture.Store, hosts, sources).Identity);
        sources[Acceptance1.Receipt] = receipts.Acceptance2;
        Assert.NotEqual(first.Identity, new MachineReactionScenarioInterpreter("environment/1", fixture.Store, hosts, sources).Identity);
    }

    static async Task<ScenarioExecutionTraceDocument> RunExample()
    {
        var fixture = await Fixture.Create(timeProvider: new FixedClock());
        // Existing completed episode is part of this exact environment's initial state.
        var receipts = await fixture.TwoCycles();
        var first = Assert.Single(await fixture.Acceptance().AdmitAsync(fixture.Context, receipts.Acceptance1));
        await fixture.Acceptance().RecoverAsync(fixture.Context, first);
        await fixture.Execute(fixture.Loads, fixture.Model.Load, "load-1", fixture.Model.ReviseLoad, new EpisodeCommand(2));
        var target = new LostAcknowledgement(fixture.ConfirmTarget);
        var interpreter = Create(fixture, receipts, target, "tender/load-ready-2/accepted-1/lost-first-confirmation-ack/v1");
        var trace = await ScenarioRunner.ExecuteAsync(ScenarioWorldSnapshot.FromCoreWorld(ConfirmationFirst()), interpreter);
        Assert.Equal(2, target.Calls);
        return trace;
    }

    static async Task<MachineReactionScenarioInterpreter> Interpreter(Fixture fixture) =>
        Create(fixture, await fixture.TwoCycles(), fixture.ConfirmTarget, "tender/load-ready-2/native/v1");

    static MachineReactionScenarioInterpreter Create(Fixture fixture,
        (EntityTransitionOperationReceipt Acceptance1, EntityTransitionOperationReceipt Confirmation1,
         EntityTransitionOperationReceipt Acceptance2, EntityTransitionOperationReceipt Confirmation2) receipts,
        MachineReactionTarget confirmationTarget, string environment) => new(environment, fixture.Store,
        new Dictionary<string, MachineReactionHost>
        {
            [AcceptanceHost] = fixture.Acceptance(), [ConfirmationHost] = fixture.Confirmation(confirmationTarget)
        }, new Dictionary<string, EntityTransitionOperationReceipt>
        {
            [Acceptance1.Receipt] = receipts.Acceptance1, [Confirmation1.Receipt] = receipts.Confirmation1,
            [Acceptance2.Receipt] = receipts.Acceptance2, [Confirmation2.Receipt] = receipts.Confirmation2
        }, authorizeAbandon: (_, _, _) => ValueTask.FromResult(true));

    static string? Field(ScenarioExecutionTraceDocument trace, int index, string field) =>
        trace.Outcomes[index].Output.Value!.Value.Fields![field].GetString();

    static IEnumerable<ReactionScenarioInput[]> Permutations(ReactionScenarioInput[] values)
    {
        if (values.Length == 0) { yield return []; yield break; }
        for (var i = 0; i < values.Length; i++)
            foreach (var rest in Permutations([.. values.Where((_, index) => index != i)]))
                yield return [values[i], .. rest];
    }

    sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => StartsAtUtc;
    }

    sealed class LostAcknowledgement(MachineReactionTarget inner) : MachineReactionTarget(inner.Transition, inner.Authority)
    {
        internal int Calls;
        public override async ValueTask<ProcessOperationResult> ExecuteAsync(OperationContext context, ProcessTransitionInvocation invocation)
        {
            var result = await inner.ExecuteAsync(context, invocation);
            if (invocation.Input.Value!.Value.Fields![nameof(EpisodeCommand.Episode)].GetInt32() == 2 && ++Calls == 1)
                throw new TimeoutException("Simulated lost acknowledgement after native commit");
            return result;
        }
        public override ValueTask<EntityTransitionOperationReceipt?> ResolveAsync(OperationContext context, EntityTransitionOperationReference reference)
            => inner.ResolveAsync(context, reference);
    }
}
