using Cohesive.Execution;
using Cohesive.Storage;
using Cohesive.Storage.Processes;
using Cohesive.Model;
using Cohesive.Model.Authoring;
using Cohesive.Simulation;
using SimulationSource = Cohesive.Simulation.Simulation;
using Cohesive.Simulation.Artifacts;
using Cohesive.Simulation.Scenarios;
using Cohesive.Simulation.Transitions;
using static Cohesive.Simulation.Transitions.MachineReactionScenarioInterpreter;

namespace Cohesive.Examples.TenderAcceptance;

// Source for a portable scenario document. The host fixture supplies real committed tender receipts
// and native in-memory target transitions. The operator world is not a replica of the load repository.
internal static class TenderEpisodeScenarios
{
    internal const string AcceptanceHost = "acceptance";
    internal const string ConfirmationHost = "confirmation";
    internal static readonly ReactionScenarioInput Acceptance1 = new(AcceptanceHost, "acceptance-1");
    internal static readonly ReactionScenarioInput Confirmation1 = new(ConfirmationHost, "confirmation-1");
    internal static readonly ReactionScenarioInput Acceptance2 = new(AcceptanceHost, "acceptance-2");
    internal static readonly ReactionScenarioInput Confirmation2 = new(ConfirmationHost, "confirmation-2");
    internal static readonly DateTimeOffset StartsAtUtc = new(2030, 1, 2, 0, 0, 0, TimeSpan.Zero);

    internal const string ReplayProfile = "tender/two-cycles/accepted-first-then-revise/v1";
    internal sealed record ReplaySeed(int InitialLoadEpisode);

    // This versioned profile creates the tender in Offered/episode 1, generates both source cycles,
    // completes the first load acceptance, then revises the load to episode 2 before delivery starts.
    internal static ReactionScenarioEnvironment Environment() => new(ReplayProfile,
        PortableValue.Concrete(new(new DefaultClrTypeRefMapper().Map(typeof(ReplaySeed), nullability: null)),
            ObservationValue.FromObject(new ReplaySeed(1))));

    // Start with load episode 2 ready and episode 1 acceptance already acknowledged.
    // A late confirmation from episode 1 must fail its target guard, never confirm episode 2.
    internal static ScenarioDefinitionDocument ConfirmationFirst() => Build([
        (Deliver, Confirmation2), (Recover, Confirmation2),
        (Deliver, Acceptance1), (Recover, Acceptance1), (Recover, Confirmation2),
        (Deliver, Acceptance2), (Recover, Acceptance2),
        (Deliver, Confirmation1), (Recover, Confirmation1), (Recover, Confirmation1),
        (Deliver, Confirmation2),
        (Recover, Confirmation2 with { Fault = ReactionScenarioFault.BeforeExecution }),
        (Recover, Confirmation2 with { Fault = ReactionScenarioFault.LoseAcknowledgement }),
        (Recover, Confirmation2),
        (Inspect, Confirmation2)
    ]);

    internal static ScenarioDefinitionDocument Build(IEnumerable<(string Operation, ReactionScenarioInput Input)> steps)
    {
        var shapes = new ClrShapeGraphBuilder().AddShape<Operator>(ShapeRoles.Entity)
            .BuildResult(new("tender-scenario/v1"));
        var generation = SimulationSource.Define<Operator>(shapes, actor => actor.Member(x => x.Worker, Gen.Constant(1)));
        var world = SimulationSource.DefineWorld("tender/operators", "1", world => world
            .Population("operators", count: 1, generation).Exemplar("worker", "operators", sequenceIndex: 0));
        var artifact = WorldArtifactManifest.FromWorld(world.Compile(), rootSeed: 42);
        return ScenarioDefinitionDocument.FromDefinition(SimulationSource.DefineScenario("tender/two-cycles", "1", artifact, StartsAtUtc,
            scenario =>
            {
                DeclareOperations(scenario).Actor("worker", "worker");
                var index = 0;
                foreach (var (operation, input) in steps)
                    scenario.Action($"step-{index:D3}", TimeSpan.FromMinutes(++index), "worker", operation, input);
            }));
    }

    internal static async ValueTask<MachineReactionScenarioInterpreter> Reconstruct(ReactionScenarioEnvironment environment, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (environment.Profile != ReplayProfile || environment.InitialState.Contract != Environment().InitialState.Contract)
            throw new InvalidOperationException("Unsupported tender replay recipe.");
        var episode = environment.InitialState.Value!.Value.Fields![nameof(ReplaySeed.InitialLoadEpisode)].GetInt32();
        if (episode != 1) throw new InvalidOperationException("This tender profile starts with load episode 1.");
        var fixture = await TenderEpisodeRuntime.Create(loadEpisode: episode, timeProvider: new FixedClock());
        var receipts = await fixture.TwoCycles();
        var first = (await fixture.Acceptance().AdmitAsync(fixture.Context, receipts.Acceptance1)).Single();
        await fixture.Acceptance().RecoverAsync(fixture.Context, first);
        await fixture.Execute(fixture.Loads, fixture.Model.Load, "load-1", fixture.Model.ReviseLoad, new EpisodeCommand(2));
        var faults = new ReactionScenarioFaults();
        var target = faults.Bind(ConfirmationHost, fixture.ConfirmTarget);
        return new MachineReactionScenarioInterpreter(environment.Identity, fixture.Store,
            new Dictionary<string, MachineReactionHost>
            {
                [AcceptanceHost] = fixture.Acceptance(), [ConfirmationHost] = fixture.Confirmation(target)
            }, new Dictionary<string, EntityTransitionOperationReceipt>
            {
                [Acceptance1.Receipt] = receipts.Acceptance1, [Confirmation1.Receipt] = receipts.Confirmation1,
                [Acceptance2.Receipt] = receipts.Acceptance2, [Confirmation2.Receipt] = receipts.Confirmation2
            }, faults: faults);
    }

    sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => StartsAtUtc;
    }

    sealed record Operator(int Worker);
}
