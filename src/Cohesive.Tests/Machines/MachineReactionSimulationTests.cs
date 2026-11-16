using System.Text.Json;
using Cohesive.Model;
using Cohesive.Execution;
using Cohesive.Examples.TenderAcceptance;
using Cohesive.Processes.Execution;
using Cohesive.Simulation.Scenarios;
using Cohesive.Simulation.Transitions;
using Cohesive.Storage;
using Cohesive.Storage.Processes;
using static Cohesive.Examples.TenderAcceptance.TenderEpisodeScenarios;
using static Cohesive.Simulation.Transitions.MachineReactionScenarioInterpreter;
using Fixture = Cohesive.Examples.TenderAcceptance.TenderEpisodeRuntime;

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
        var uncertain = 12;
        Assert.Equal(nameof(MachineReactionStatus.Indeterminate), Field(first, uncertain, nameof(ReactionScenarioObservation.Status)));
        Assert.Equal(Field(first, uncertain, nameof(ReactionScenarioObservation.Operation)), Field(first, 14, nameof(ReactionScenarioObservation.Operation)));
        Assert.Equal(3, first.Outcomes[14].Output.Value!.Value.Fields![nameof(ReactionScenarioObservation.Attempts)].GetInt32());
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

    [Theory]
    [InlineData(Deliver)]
    [InlineData(Inspect)]
    [InlineData(Abandon)]
    public async Task Invalid_fault_placement_fails_before_any_admission(string operation)
    {
        var fixture = await Fixture.Create(timeProvider: new FixedClock());
        var faults = new ReactionScenarioFaults();
        var interpreter = Create(fixture, await fixture.TwoCycles(),
            faults.Bind(ConfirmationHost, fixture.ConfirmTarget), "invalid-placement", faults);
        var scenario = Build([(Deliver, Confirmation2),
            (operation, Confirmation2 with { Fault = ReactionScenarioFault.BeforeExecution })]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ScenarioRunner.ExecuteAsync(
            ScenarioWorldSnapshot.FromCoreWorld(scenario), interpreter));
        Assert.Empty(await ((IProcessInstanceSource)fixture.Store).ListInstancesAsync(fixture.Context, null, 32));
    }

    [Fact]
    public async Task Missing_fault_instrumentation_fails_before_any_admission()
    {
        var fixture = await Fixture.Create(timeProvider: new FixedClock());
        var interpreter = await Interpreter(fixture);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ScenarioRunner.ExecuteAsync(
            ScenarioWorldSnapshot.FromCoreWorld(ConfirmationFirst()), interpreter));
        Assert.Empty(await ((IProcessInstanceSource)fixture.Store).ListInstancesAsync(fixture.Context, null, 32));
    }

    [Fact]
    public async Task A_fault_on_a_waiting_reaction_is_reported_as_unexercised()
    {
        var fixture = await Fixture.Create(timeProvider: new FixedClock());
        var faults = new ReactionScenarioFaults();
        var interpreter = Create(fixture, await fixture.TwoCycles(),
            faults.Bind(ConfirmationHost, fixture.ConfirmTarget), "waiting", faults);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ScenarioRunner.ExecuteAsync(
            ScenarioWorldSnapshot.FromCoreWorld(Build([(Deliver, Confirmation2),
                (Recover, Confirmation2 with { Fault = ReactionScenarioFault.LoseAcknowledgement })])), interpreter));
        Assert.Contains("exactly one target", error.Message);
    }

    [Fact]
    public async Task Replay_rejects_changed_recipe_or_receipts_before_scheduled_execution()
    {
        var environment = Environment();
        var interpreter = await Reconstruct(environment, default);
        var trace = await ScenarioRunner.ExecuteAsync(ScenarioWorldSnapshot.FromCoreWorld(ConfirmationFirst()), interpreter);
        var artifact = ReactionScenarioReplay.Capture(environment, interpreter, trace);
        Assert.Throws<ArgumentException>(() => ReactionScenarioReplay.Capture(
            new ReactionScenarioEnvironment("other-profile", environment.InitialState), interpreter, trace));
        var fixture = await Fixture.Create(timeProvider: new FixedClock());
        fixture.OperationPrefix = "changed-source";
        var faults = new ReactionScenarioFaults();
        var changed = Create(fixture, await fixture.TwoCycles(), faults.Bind(ConfirmationHost, fixture.ConfirmTarget), environment.Identity, faults);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ReactionScenarioReplay.ReplayAsync(artifact,
            (_, _) => ValueTask.FromResult(changed)));
        Assert.Empty(await ((IProcessInstanceSource)fixture.Store).ListInstancesAsync(fixture.Context, null, 32));
        var factoryCalled = false;
        await Assert.ThrowsAsync<JsonException>(() => ReactionScenarioReplay.ReplayAsync(artifact.Replace(
            "cohesive-reaction-scenario-replay/v1", "cohesive-reaction-scenario-replay/v0"), (_, _) =>
            { factoryCalled = true; return ValueTask.FromResult(changed); }));
        Assert.False(factoryCalled);
    }

    [Fact]
    public async Task Before_execution_fault_has_no_effect_and_lost_ack_replay_does_not_mutate_twice()
    {
        var fixture = await Fixture.Create(timeProvider: new FixedClock());
        var receipts = await fixture.TwoCycles();
        var first = Assert.Single(await fixture.Acceptance().AdmitAsync(fixture.Context, receipts.Acceptance1));
        await fixture.Acceptance().RecoverAsync(fixture.Context, first);
        var faults = new ReactionScenarioFaults();
        var recording = new RecordingTarget(fixture.ConfirmTarget, fixture);
        var interpreter = Create(fixture, receipts, faults.Bind(ConfirmationHost, recording), "boundary-check", faults);
        var trace = await ScenarioRunner.ExecuteAsync(ScenarioWorldSnapshot.FromCoreWorld(Build([
            (Deliver, Confirmation1), (Recover, Confirmation1),
            (Recover, Confirmation1 with { Fault = ReactionScenarioFault.BeforeExecution }),
            (Recover, Confirmation1 with { Fault = ReactionScenarioFault.LoseAcknowledgement }),
            (Recover, Confirmation1), (Deliver, Confirmation1), (Recover, Confirmation1)
        ])), interpreter);
        Assert.Equal(2, recording.Versions.Count);
        Assert.Equal(recording.Versions[0], recording.Versions[1]);
        Assert.Equal(nameof(ReactionScenarioFault.BeforeExecution), Field(trace, 2, nameof(ReactionScenarioObservation.Fault)));
        Assert.Equal(nameof(MachineReactionStatus.Completed), Field(trace, 6, nameof(ReactionScenarioObservation.Status)));
        Assert.Equal(Field(trace, 2, nameof(ReactionScenarioObservation.Operation)), Field(trace, 4, nameof(ReactionScenarioObservation.Operation)));
    }

    [Fact]
    public async Task Replay_detects_a_factory_that_claims_the_recipe_but_reconstructs_different_state()
    {
        var environment = new ReactionScenarioEnvironment("tender/basic/v1", Environment().InitialState);
        var first = await Fixture.Create(loadEpisode: 1, timeProvider: new FixedClock());
        var original = Create(first, await first.TwoCycles(), first.ConfirmTarget, environment.Identity);
        var trace = await ScenarioRunner.ExecuteAsync(ScenarioWorldSnapshot.FromCoreWorld(Build([
            (Deliver, Acceptance1), (Recover, Acceptance1)])), original);
        var artifact = ReactionScenarioReplay.Capture(environment, original, trace);
        var different = await Fixture.Create(loadEpisode: 2, timeProvider: new FixedClock());
        var candidate = Create(different, await different.TwoCycles(), different.ConfirmTarget, environment.Identity);
        Assert.Equal(original.Identity, candidate.Identity); // A factory's initial-state assertion is not a sandbox.
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ReactionScenarioReplay.ReplayAsync(
            artifact, (_, _) => ValueTask.FromResult(candidate)));
        Assert.Contains("diverged", error.Message);
    }

    [Fact]
    public async Task Fault_sessions_are_single_owner_and_binding_changes_are_pinned()
    {
        var fixture = await Fixture.Create(timeProvider: new FixedClock());
        var receipts = await fixture.TwoCycles();
        var faults = new ReactionScenarioFaults();
        var target = faults.Bind(ConfirmationHost, fixture.ConfirmTarget);
        var instrumented = Create(fixture, receipts, target, "binding", faults);
        Assert.NotEqual(instrumented.Identity, Create(fixture, receipts, fixture.ConfirmTarget, "binding").Identity);
        Assert.Throws<InvalidOperationException>(() => Create(fixture, receipts, target, "binding", faults));
        Assert.Throws<InvalidOperationException>(() => faults.Bind("late", fixture.AcceptTarget));
        var mismatch = new ReactionScenarioFaults();
        mismatch.Bind(ConfirmationHost, fixture.AcceptTarget);
        Assert.Throws<ArgumentException>(() => Create(fixture, receipts, fixture.ConfirmTarget, "binding", mismatch));
    }

    [Fact]
    public async Task Invalid_or_cancelled_replay_does_not_reconstruct_an_environment()
    {
        var called = false;
        ValueTask<MachineReactionScenarioInterpreter> Factory(ReactionScenarioEnvironment _, CancellationToken __)
        { called = true; throw new InvalidOperationException(); }
        await Assert.ThrowsAsync<JsonException>(() => ReactionScenarioReplay.ReplayAsync("{}", Factory));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReactionScenarioReplay.ReplayAsync("{}", Factory, cancellation.Token));
        Assert.False(called);
    }

    [Fact]
    public async Task Real_target_failure_is_not_reported_as_an_injected_lost_acknowledgement()
    {
        var fixture = await Fixture.Create(timeProvider: new FixedClock());
        var receipts = await fixture.TwoCycles();
        var accepted = Assert.Single(await fixture.Acceptance().AdmitAsync(fixture.Context, receipts.Acceptance1));
        await fixture.Acceptance().RecoverAsync(fixture.Context, accepted);
        var faults = new ReactionScenarioFaults();
        var failing = new FailingTarget(fixture.ConfirmTarget);
        var wrapped = faults.Bind(ConfirmationHost, failing);
        Assert.Equal(FailingTarget.Failure, wrapped.DescribeFailure(new IOException()));
        var interpreter = Create(fixture, receipts, wrapped, "real-failure", faults);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ScenarioRunner.ExecuteAsync(
            ScenarioWorldSnapshot.FromCoreWorld(Build([(Deliver, Confirmation1), (Recover, Confirmation1),
                (Recover, Confirmation1 with { Fault = ReactionScenarioFault.LoseAcknowledgement })])), interpreter));
        Assert.Contains("exactly one target", error.Message);
    }

    sealed class FailingTarget(MachineReactionTarget inner) : MachineReactionTarget(inner.Transition, inner.Authority)
    {
        internal static readonly DurableOperationFailure Failure = new(DurableOperationFailurePhase.PreCall,
            DurableOperationEffectEvidence.NotExecuted, DurableOperationFailureDisposition.Terminal, "test.real-failure");
        public override ValueTask<ProcessOperationResult> ExecuteAsync(OperationContext context, ProcessTransitionInvocation invocation)
            => throw new IOException("Real target failure before any return");
        public override DurableOperationFailure DescribeFailure(Exception exception) => Failure;
        public override ValueTask<EntityTransitionOperationReceipt?> ResolveAsync(OperationContext context, EntityTransitionOperationReference reference)
            => inner.ResolveAsync(context, reference);
    }

    sealed class RecordingTarget(MachineReactionTarget inner, Fixture fixture) : MachineReactionTarget(inner.Transition, inner.Authority)
    {
        internal List<string> Versions { get; } = [];
        public override async ValueTask<ProcessOperationResult> ExecuteAsync(OperationContext context, ProcessTransitionInvocation invocation)
        {
            var result = await inner.ExecuteAsync(context, invocation);
            Versions.Add((await fixture.Loads.TryGet(context, "load-1", EntityReadOptions.Full.WithPartitionKey("test")))!.ConcurrencyToken.ToString()!);
            return result;
        }
        public override ValueTask<EntityTransitionOperationReceipt?> ResolveAsync(OperationContext context, EntityTransitionOperationReference reference)
            => inner.ResolveAsync(context, reference);
    }

    static async Task<ScenarioExecutionTraceDocument> RunExample()
    {
        var environment = Environment();
        var interpreter = await Reconstruct(environment, default);
        var scenario = ScenarioDefinitionJsonSerializer.Deserialize(ScenarioDefinitionJsonSerializer.Serialize(ConfirmationFirst()));
        var trace = await ScenarioRunner.ExecuteAsync(ScenarioWorldSnapshot.FromCoreWorld(scenario), interpreter);
        var artifact = ReactionScenarioReplay.Capture(environment, interpreter, trace);
        var replayed = await ReactionScenarioReplay.ReplayAsync(artifact, Reconstruct);
        Assert.Equal(ScenarioExecutionTraceJsonSerializer.Serialize(trace), ScenarioExecutionTraceJsonSerializer.Serialize(replayed));
        return trace;
    }

    static async Task<MachineReactionScenarioInterpreter> Interpreter(Fixture fixture) =>
        Create(fixture, await fixture.TwoCycles(), fixture.ConfirmTarget, "tender/load-ready-2/native/v1");

    static MachineReactionScenarioInterpreter Create(Fixture fixture,
        (EntityTransitionOperationReceipt Acceptance1, EntityTransitionOperationReceipt Confirmation1,
         EntityTransitionOperationReceipt Acceptance2, EntityTransitionOperationReceipt Confirmation2) receipts,
        MachineReactionTarget confirmationTarget, string environment, ReactionScenarioFaults? faults = null) => new(environment, fixture.Store,
        new Dictionary<string, MachineReactionHost>
        {
            [AcceptanceHost] = fixture.Acceptance(), [ConfirmationHost] = fixture.Confirmation(confirmationTarget)
        }, new Dictionary<string, EntityTransitionOperationReceipt>
        {
            [Acceptance1.Receipt] = receipts.Acceptance1, [Confirmation1.Receipt] = receipts.Confirmation1,
            [Acceptance2.Receipt] = receipts.Acceptance2, [Confirmation2.Receipt] = receipts.Confirmation2
        }, authorizeAbandon: (_, _, _) => ValueTask.FromResult(true), faults: faults);

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

}
