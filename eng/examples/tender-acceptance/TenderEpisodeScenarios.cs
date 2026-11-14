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

    // Start with load episode 2 ready and episode 1 acceptance already acknowledged.
    // A late confirmation from episode 1 must fail its target guard, never confirm episode 2.
    internal static ScenarioDefinitionDocument ConfirmationFirst() => Build([
        (Deliver, Confirmation2), (Recover, Confirmation2),
        (Deliver, Acceptance1), (Recover, Acceptance1), (Recover, Confirmation2),
        (Deliver, Acceptance2), (Recover, Acceptance2),
        (Deliver, Confirmation1), (Recover, Confirmation1), (Recover, Confirmation1),
        (Deliver, Confirmation2), (Recover, Confirmation2), (Recover, Confirmation2), (Recover, Confirmation2),
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

    sealed record Operator(int Worker);
}
