using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Authoring;
using Cohesive.Model.Serialization;
using Cohesive.Simulation.Artifacts;
using Cohesive.Simulation.Scenarios;
using Cohesive.Transitions.Authoring;
using Cohesive.Transitions.Compilation;
using Cohesive.Transitions.IR;

namespace Cohesive.Simulation.Transitions.Tests;

public sealed class TransitionScenarioActionInterpreterTests
{
    static readonly DateTimeOffset StartsAtUtc = new(2030, 1, 2, 3, 4, 5, TimeSpan.Zero);

    [Fact]
    public async Task CanonicalTransition_EvolvesTargetActorAcrossTheScenarioAndPortableTrace()
    {
        var fixture = Fixture();
        var interpreter = new TransitionScenarioActionInterpreter(
        [
            new(
                operationId: "counter.increment",
                transition: fixture.Transition,
                subject: ScenarioTransitionSubject.TargetActor)
        ]);

        var initialWorld = ScenarioWorldSnapshot.FromCoreWorld(fixture.Scenario);
        var trace = await ScenarioRunner.ExecuteAsync(initialWorld, interpreter);

        Assert.StartsWith(
            $"{TransitionScenarioActionInterpreter.ProfileIdentity}/sha256/",
            interpreter.Identity,
            StringComparison.Ordinal);
        Assert.Equal(["increment-first", "increment-second"], trace.Outcomes.Select(static outcome => outcome.ActionId));
        Assert.All(trace.Outcomes, static outcome => Assert.True(outcome.Output.Value?.GetBoolean()));
        Assert.All(trace.Outcomes, static outcome => Assert.Single(outcome.StateChanges));
        Assert.Equal(0, initialWorld.GetActor("counter").Observation.Materialize<Counter>().Count);
        Assert.Equal(2, trace.Outcomes[0].StateChanges[0].After.Materialize<Counter>().Count);
        Assert.Equal(2, trace.Outcomes[1].StateChanges[0].Before.Materialize<Counter>().Count);

        var finalWorld = trace.ToFinalWorldSnapshot();
        Assert.Equal(5, finalWorld.GetActor("counter").Observation.Materialize<Counter>().Count);
        Assert.Equal(0, finalWorld.GetActor("operator").Observation.Materialize<Counter>().Count);

        var json = ScenarioExecutionTraceJsonSerializer.Serialize(trace);
        var restored = ScenarioExecutionTraceJsonSerializer.Deserialize(json);
        Assert.Equal(5, restored.ToFinalWorldSnapshot().GetActor("counter").Observation.Materialize<Counter>().Count);
        Assert.Equal(json, ScenarioExecutionTraceJsonSerializer.Serialize(restored));
    }

    [Fact]
    public void InterpreterIdentity_PinsSubjectPolicyAndExactTransitionReference()
    {
        var fixture = Fixture();
        var actor = new TransitionScenarioActionInterpreter(
            [new("counter.increment", fixture.Transition, ScenarioTransitionSubject.Actor)]);
        var target = new TransitionScenarioActionInterpreter(
            [new("counter.increment", fixture.Transition, ScenarioTransitionSubject.TargetActor)]);
        var repeated = new TransitionScenarioActionInterpreter(
            [new("counter.increment", fixture.Transition, ScenarioTransitionSubject.TargetActor)]);

        Assert.NotEqual(actor.Identity, target.Identity);
        Assert.Equal(target.Identity, repeated.Identity);
        Assert.Throws<ArgumentException>(() => new TransitionScenarioActionInterpreter(
        [
            new("counter.increment", fixture.Transition),
            new("counter.increment", fixture.Transition)
        ]));
    }

    [Fact]
    public async Task ContractMismatch_FailsBeforeTheTransitionIsInterpreted()
    {
        var fixture = Fixture();
        var scenario = new ScenarioDefinition(
            "scenario/counter-mismatch",
            "r1",
            fixture.Scenario.Definition.InitialWorld,
            StartsAtUtc,
            [
                new(
                    "counter.increment",
                    fixture.Transition.Definition.Input,
                    new(new ScalarTypeRef(ScalarTypeKind.String)))
            ],
            [new("counter", "counter-for-scenario")],
            [
                new(
                    "increment",
                    StartsAtUtc,
                    "counter",
                    "counter.increment",
                    ObservationValue.FromObject(new IncrementCounter(1)))
            ]);
        var document = ScenarioDefinitionDocument.FromDefinition(scenario);
        var interpreter = new TransitionScenarioActionInterpreter(
            [new("counter.increment", fixture.Transition)]);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ScenarioRunner.ExecuteAsync(ScenarioWorldSnapshot.FromCoreWorld(document), interpreter));

        Assert.Contains("contracts do not exactly match", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmissionEffects_FailClosedBeforeScenarioStateCanAdvance()
    {
        var fixture = Fixture();
        var interaction = new ExecutionDefinitionReference(
            new("interaction/counter/incremented"),
            new("r1"),
            new("sha256", "tests/v1", new string('a', 64)));
        var authored = TransitionAuthoring.Create<Counter, IncrementCounter, bool>(
            fixture.Shapes.Graph.GetShape(fixture.Shapes.GetShape<Counter>().ShapeId),
            Metadata("transition/counter/increment-and-emit"),
            transition => transition
                .Increment(new("increment"), state => state.Count, (state, input) => input.Amount)
                .Emit(new("emit"), interaction, (state, input) => input)
                .Return(new("accepted"), TransitionOutcomeDisposition.Applied, true));
        var compilation = authored.Compile(fixture.Shapes.Graph);
        Assert.True(compilation.Validation.IsValid, Format(compilation.Validation));
        var interpreter = new TransitionScenarioActionInterpreter(
        [
            new(
                "counter.increment",
                Assert.IsType<CompiledTransitionPlan>(compilation.Plan),
                ScenarioTransitionSubject.TargetActor)
        ]);

        var exception = await Assert.ThrowsAsync<NotSupportedException>(() => ScenarioRunner.ExecuteAsync(
            ScenarioWorldSnapshot.FromCoreWorld(fixture.Scenario),
            interpreter));

        Assert.Contains("emission intents", exception.Message, StringComparison.Ordinal);
    }

    static FixtureState Fixture()
    {
        var shapes = new ClrShapeGraphBuilder()
            .AddShape<Counter>(ShapeRoles.Entity)
            .BuildResult(new("simulation-transition-tests/v1"));
        var generation = Simulation.Define<Counter>(shapes, counter => counter
            .Member(value => value.Count, Gen.Constant(0)));
        var world = Simulation.DefineWorld(
            "world/counter-scenario",
            "r1",
            world => world
                .Population("counters", count: 2, generation)
                .Exemplar("operator-for-scenario", "counters", sequenceIndex: 0)
                .Exemplar("counter-for-scenario", "counters", sequenceIndex: 1));
        var artifact = WorldArtifactManifest.FromWorld(world.Compile(), rootSeed: 42);
        var scenario = Simulation.DefineScenario(
            "scenario/counter",
            "r1",
            artifact,
            StartsAtUtc,
            scenario => scenario
                .Operation<IncrementCounter, bool>("counter.increment")
                .Actor("operator", "operator-for-scenario")
                .Actor("counter", "counter-for-scenario")
                .Action(
                    id: "increment-first",
                    afterStart: TimeSpan.FromMinutes(1),
                    actorId: "operator",
                    operationId: "counter.increment",
                    input: new IncrementCounter(2),
                    targetActorId: "counter")
                .Action(
                    id: "increment-second",
                    afterStart: TimeSpan.FromMinutes(2),
                    actorId: "operator",
                    operationId: "counter.increment",
                    input: new IncrementCounter(3),
                    targetActorId: "counter"));
        var authored = TransitionAuthoring.Create<Counter, IncrementCounter, bool>(
            shapes.Graph.GetShape(shapes.GetShape<Counter>().ShapeId),
            Metadata("transition/counter/increment"),
            transition => transition
                .Increment(new("increment"), state => state.Count, (state, input) => input.Amount)
                .Return(new("accepted"), TransitionOutcomeDisposition.Applied, true));
        var compilation = authored.Compile(shapes.Graph);
        Assert.True(compilation.Validation.IsValid, Format(compilation.Validation));

        return new(
            ScenarioDefinitionDocument.FromDefinition(scenario),
            Assert.IsType<CompiledTransitionPlan>(compilation.Plan),
            shapes);
    }

    static TransitionAuthoringMetadata Metadata(string definitionId) => new(
        new(definitionId),
        new("r1"),
        new("body"),
        new(
            new(TransitionAuthoring.Producer),
            new("simulation-transition-tests"),
            DocumentOrigin.Generated));

    static string Format(DocumentValidationResult validation) => string.Join(
        Environment.NewLine,
        validation.Diagnostics.Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}"));

    sealed record FixtureState(
        ScenarioDefinitionDocument Scenario,
        CompiledTransitionPlan Transition,
        ClrShapeGraphBuildResult Shapes);

    sealed record Counter(int Count);

    sealed record IncrementCounter(int Amount);
}
