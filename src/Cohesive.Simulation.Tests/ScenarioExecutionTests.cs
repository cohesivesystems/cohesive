using System.Text.Json.Nodes;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Simulation.Generation;
using Cohesive.Simulation.Scenarios;

namespace Cohesive.Simulation.Tests;

public sealed partial class ScenarioTests
{
    [Fact]
    public void CoreWorldSnapshot_MaterializesEveryExactActorDeterministically()
    {
        var document = ScenarioDefinitionDocument.FromDefinition(Scenario(
            operationOrder: ["freight.assign-load", "freight.release-load"],
            actorOrder: ["dispatcher", "carrier"],
            actionOrder: ["assign-first", "assign-second", "release-load"]));

        var first = ScenarioWorldSnapshot.FromCoreWorld(document);
        var second = ScenarioWorldSnapshot.FromCoreWorld(document);
        var reordered = ScenarioWorldSnapshot.Create(document, [.. first.Actors.Reverse()]);

        Assert.Equal(["carrier", "dispatcher"], first.Actors.Select(static actor => actor.Actor.Id));
        Assert.Equal(
            first.Actors.Select(static actor => actor.EntityId),
            second.Actors.Select(static actor => actor.EntityId));
        Assert.Equal(
            first.Actors.Select(static actor => actor.Observation.ToCanonicalJson()),
            second.Actors.Select(static actor => actor.Observation.ToCanonicalJson()));
        Assert.Equal(
            first.Actors.Select(static actor => actor.OriginReplayToken),
            second.Actors.Select(static actor => actor.OriginReplayToken));
        Assert.Equal(
            first.Actors.Select(static actor => actor.Actor.Id),
            reordered.Actors.Select(static actor => actor.Actor.Id));
        Assert.Equal(1, first.GetActor("carrier").Exemplar.SequenceIndex);
        Assert.False(first.TryGetActor("absent", out var absent));
        Assert.Null(absent);
    }

    [Fact]
    public void SnapshotCreation_FailsClosedForIncompleteOrInconsistentActorMaterializations()
    {
        var document = ScenarioDefinitionDocument.FromDefinition(Scenario(
            operationOrder: ["freight.assign-load", "freight.release-load"],
            actorOrder: ["dispatcher", "carrier"],
            actionOrder: ["assign-first", "assign-second", "release-load"]));
        var world = ScenarioWorldSnapshot.FromCoreWorld(document);
        var carrier = world.GetActor("carrier");
        var alternateDefinition = new ScenarioActorDefinition("carrier", "dispatcher-for-scenario");
        var alternateExemplar = document.Definition.InitialWorld.GetExemplar("dispatcher-for-scenario");
        var inconsistent = new ScenarioActorSnapshot(
            alternateDefinition,
            alternateExemplar,
            carrier.EntityId,
            carrier.Observation,
            carrier.OriginReplayToken);

        Assert.Throws<ArgumentException>(() => ScenarioWorldSnapshot.Create(document, [carrier]));
        Assert.Throws<ArgumentException>(() => ScenarioWorldSnapshot.Create(
            document,
            [inconsistent, world.GetActor("dispatcher")]));
    }

    [Fact]
    public async Task Execution_UsesCanonicalVirtualScheduleAndRetainsExactTrace()
    {
        var document = ScenarioDefinitionDocument.FromDefinition(Scenario(
            operationOrder: ["freight.release-load", "freight.assign-load"],
            actorOrder: ["dispatcher", "carrier"],
            actionOrder: ["release-load", "assign-second", "assign-first"]));
        RecordingScenarioInterpreter interpreter = new(Complete);

        var world = ScenarioWorldSnapshot.FromCoreWorld(document);
        var trace = await ScenarioRunner.ExecuteAsync(world, interpreter);

        Assert.Same(document, trace.Scenario);
        Assert.Same(world, interpreter.Contexts[0].World);
        Assert.Equal(RecordingScenarioInterpreter.InterpreterIdentity, trace.Interpreter);
        Assert.Equal(
            ["assign-first", "assign-second", "release-load"],
            interpreter.Contexts.Select(static context => context.Action.Id));
        Assert.Equal([0, 1, 2], interpreter.Contexts.Select(static context => context.SequenceIndex));
        Assert.Equal(
            [StartsAtUtc.AddMinutes(1), StartsAtUtc.AddMinutes(1), StartsAtUtc.AddMinutes(2)],
            interpreter.Contexts.Select(static context => context.Action.ScheduledAtUtc));
        Assert.Equal("dispatcher-for-scenario", interpreter.Contexts[0].Actor.ExemplarId);
        Assert.Equal("carrier-for-scenario", interpreter.Contexts[0].TargetActor!.ExemplarId);
        Assert.Equal("dispatcher", interpreter.Contexts[0].ActorSnapshot.Actor.Id);
        Assert.Equal("carrier", interpreter.Contexts[0].TargetActorSnapshot!.Actor.Id);
        Assert.Equal(
            world.GetActor("dispatcher").EntityId,
            interpreter.Contexts[0].ActorSnapshot.EntityId);
        Assert.Equal(
            ReferenceGenerationInterpreter.Identity,
            GenerationReplayEvidence.ParseToken(interpreter.Contexts[0].ActorSnapshot.OriginReplayToken).Interpreter);
        Assert.Equal("load-1", interpreter.Contexts[0].Input.Value!.Value.GetProperty("LoadId").String);
        Assert.Equal(
            ["assign-first", "assign-second", "release-load"],
            trace.Outcomes.Select(static outcome => outcome.ActionId));
        Assert.All(trace.Outcomes, static outcome => Assert.Equal(PortableValueState.Concrete, outcome.Output.State));
        Assert.Equal(
            "d17aab8b48f4445a58365d7a4b76712ac02fa809633c1bf9549a3c2653f03c25",
            trace.Fingerprint.Value);

        var json = ScenarioExecutionTraceJsonSerializer.Serialize(trace);
        var restored = ScenarioExecutionTraceJsonSerializer.Deserialize(json);

        Assert.Equal(ScenarioExecutionTraceDocument.CurrentSchemaVersion, restored.SchemaVersion);
        Assert.Equal(trace.Fingerprint, restored.Fingerprint);
        Assert.Equal(document.Fingerprint, restored.Scenario.Fingerprint);
        Assert.Equal(json, ScenarioExecutionTraceJsonSerializer.Serialize(restored));
    }

    [Fact]
    public async Task EquivalentExecutions_ProduceOneCanonicalTraceIdentity()
    {
        var document = ScenarioDefinitionDocument.FromDefinition(Scenario(
            operationOrder: ["freight.assign-load", "freight.release-load"],
            actorOrder: ["dispatcher", "carrier"],
            actionOrder: ["assign-first", "assign-second", "release-load"]));

        var first = await ScenarioRunner.ExecuteAsync(
            ScenarioWorldSnapshot.FromCoreWorld(document),
            new RecordingScenarioInterpreter(Complete));
        var second = await ScenarioRunner.ExecuteAsync(
            ScenarioWorldSnapshot.FromCoreWorld(document),
            new RecordingScenarioInterpreter(Complete));

        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.Equal(
            ScenarioExecutionTraceJsonSerializer.Serialize(first),
            ScenarioExecutionTraceJsonSerializer.Serialize(second));
    }

    [Fact]
    public async Task Execution_AppliesExplicitStateChangesBeforeTheNextActionAndRetainsTheChain()
    {
        var document = ScenarioDefinitionDocument.FromDefinition(Scenario(
            operationOrder: ["freight.assign-load", "freight.release-load"],
            actorOrder: ["dispatcher", "carrier"],
            actionOrder: ["assign-first", "assign-second", "release-load"]));
        var initialWorld = ScenarioWorldSnapshot.FromCoreWorld(document);
        var generation = document.Definition.InitialWorld.GetCoreWorld().Definition.Populations.Single().Generation;
        var outputShape = new GraphShapeId(generation.ShapeGraph, generation.Root.ShapeId);
        var changedCarrier = Observation.Create(
            outputShape,
            ObservationValue.FromObject(new FreightActor("Assigned carrier")));
        var finalCarrier = Observation.Create(
            outputShape,
            ObservationValue.FromObject(new FreightActor("Released carrier")));
        RecordingScenarioInterpreter interpreter = new(context =>
        {
            if (context.SequenceIndex == 0)
            {
                return new(
                    Complete(context),
                    [ScenarioActorStateChange.Replace(context.TargetActorSnapshot!, changedCarrier)]);
            }

            if (context.SequenceIndex == 1)
            {
                Assert.Equal(changedCarrier, context.TargetActorSnapshot!.Observation);
                return new(
                    Complete(context),
                    [ScenarioActorStateChange.Replace(context.TargetActorSnapshot, finalCarrier)]);
            }

            Assert.Equal(finalCarrier, context.ActorSnapshot.Observation);

            return ScenarioActionResult.Unchanged(Complete(context));
        });

        var trace = await ScenarioRunner.ExecuteAsync(initialWorld, interpreter);

        var change = Assert.Single(trace.Outcomes[0].StateChanges);
        Assert.Equal("carrier", change.ActorId);
        Assert.Equal(initialWorld.GetActor("carrier").Observation, change.Before);
        Assert.Equal(changedCarrier, change.After);
        Assert.Equal(
            initialWorld.GetActor("carrier").Observation,
            trace.InitialActors.Single(static actor => actor.ActorId == "carrier").Observation);
        Assert.Equal(finalCarrier, trace.ToFinalWorldSnapshot().GetActor("carrier").Observation);
        Assert.Equal(
            initialWorld.GetActor("carrier").OriginReplayToken,
            trace.ToFinalWorldSnapshot().GetActor("carrier").OriginReplayToken);

        var json = ScenarioExecutionTraceJsonSerializer.Serialize(trace);
        var restored = ScenarioExecutionTraceJsonSerializer.Deserialize(json);
        Assert.Equal(finalCarrier, restored.ToFinalWorldSnapshot().GetActor("carrier").Observation);
        Assert.Equal(json, ScenarioExecutionTraceJsonSerializer.Serialize(restored));

        var staleSecond = new ScenarioActionOutcome(
            trace.Outcomes[1].ActionId,
            trace.Outcomes[1].Output,
            [new("carrier", initialWorld.GetActor("carrier").Observation, finalCarrier)]);
        var exception = Assert.Throws<ArgumentException>(() => new ScenarioExecutionTraceDocument(
            trace.SchemaVersion,
            trace.Scenario,
            trace.Interpreter,
            trace.InitialActors,
            [trace.Outcomes[0], staleSecond, trace.Outcomes[2]],
            trace.Fingerprint));
        Assert.Contains("stale before-state evidence", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Execution_FailsClosedWhenStateChangeBeforeEvidenceIsStale()
    {
        var document = ScenarioDefinitionDocument.FromDefinition(Scenario(
            operationOrder: ["freight.assign-load", "freight.release-load"],
            actorOrder: ["dispatcher", "carrier"],
            actionOrder: ["assign-first", "assign-second", "release-load"]));
        var initialWorld = ScenarioWorldSnapshot.FromCoreWorld(document);
        var initialCarrier = initialWorld.GetActor("carrier");
        var generation = document.Definition.InitialWorld.GetCoreWorld().Definition.Populations.Single().Generation;
        var outputShape = new GraphShapeId(generation.ShapeGraph, generation.Root.ShapeId);
        var first = Observation.Create(
            outputShape,
            ObservationValue.FromObject(new FreightActor("First state")));
        var second = Observation.Create(
            outputShape,
            ObservationValue.FromObject(new FreightActor("Second state")));
        RecordingScenarioInterpreter interpreter = new(context => new(
            Complete(context),
            [new("carrier", initialCarrier.Observation, context.SequenceIndex == 0 ? first : second)]));

        var exception = await Assert.ThrowsAsync<ScenarioExecutionException>(
            () => ScenarioRunner.ExecuteAsync(initialWorld, interpreter));

        var diagnostic = Assert.Single(exception.Validation.Diagnostics);
        Assert.Equal(ScenarioExecutionDiagnosticCodes.StateChangesInvalid, diagnostic.Code);
        Assert.Equal("/outcomes/1/stateChanges", diagnostic.Location);
        Assert.Equal("assign-second", diagnostic.Evidence!.Subject);
        Assert.Equal(2, interpreter.Contexts.Count);
    }

    [Fact]
    public void ActionResult_NormalizesIndependentActorChangesBeforeWorldApplication()
    {
        var document = ScenarioDefinitionDocument.FromDefinition(Scenario(
            operationOrder: ["freight.assign-load", "freight.release-load"],
            actorOrder: ["dispatcher", "carrier"],
            actionOrder: ["assign-first", "assign-second", "release-load"]));
        var world = ScenarioWorldSnapshot.FromCoreWorld(document);
        var generation = document.Definition.InitialWorld.GetCoreWorld().Definition.Populations.Single().Generation;
        var outputShape = new GraphShapeId(generation.ShapeGraph, generation.Root.ShapeId);
        var carrierChange = ScenarioActorStateChange.Replace(
            world.GetActor("carrier"),
            Observation.Create(outputShape, ObservationValue.FromObject(new FreightActor("Changed carrier"))));
        var dispatcherChange = ScenarioActorStateChange.Replace(
            world.GetActor("dispatcher"),
            Observation.Create(outputShape, ObservationValue.FromObject(new FreightActor("Changed dispatcher"))));
        var output = PortableValue.Unknown(new(new ScalarTypeRef(ScalarTypeKind.Bool)));

        var result = new ScenarioActionResult(output, [dispatcherChange, carrierChange]);
        var changedWorld = world.Apply(result.StateChanges);

        Assert.Equal(["carrier", "dispatcher"], result.StateChanges.Select(static change => change.ActorId));
        Assert.Equal("Changed carrier", changedWorld.GetActor("carrier").Observation.Materialize<FreightActor>().Name);
        Assert.Equal(
            "Changed dispatcher",
            changedWorld.GetActor("dispatcher").Observation.Materialize<FreightActor>().Name);
        Assert.Throws<ArgumentException>(() => new ScenarioActionResult(output, [carrierChange, carrierChange]));
    }

    [Fact]
    public async Task FailedPortableOutcome_IsRetainedWithoutImplicitlyStoppingTheSchedule()
    {
        var document = ScenarioDefinitionDocument.FromDefinition(Scenario(
            operationOrder: ["freight.assign-load", "freight.release-load"],
            actorOrder: ["dispatcher", "carrier"],
            actionOrder: ["assign-first", "assign-second", "release-load"]));
        RecordingScenarioInterpreter interpreter = new(context =>
            context.SequenceIndex == 0
                ? PortableValue.Failed(
                    context.Operation.Output,
                    new(
                        Code: "freight.assignment.rejected",
                        Severity: DiagnosticSeverity.Error,
                        Message: "The carrier rejected the load."))
                : Complete(context));

        var trace = await ScenarioRunner.ExecuteAsync(ScenarioWorldSnapshot.FromCoreWorld(document), interpreter);

        Assert.Equal(3, interpreter.Contexts.Count);
        Assert.Equal(PortableValueState.Failed, trace.Outcomes[0].Output.State);
        Assert.Equal("freight.assignment.rejected", trace.Outcomes[0].Output.Failure!.Code);
        Assert.Equal(PortableValueState.Concrete, trace.Outcomes[1].Output.State);
    }

    [Fact]
    public async Task Execution_PreservesMissingAndNullInputsAsDistinctPortableStates()
    {
        var inputContract = new ValueContract(
            new ScalarTypeRef(ScalarTypeKind.String),
            presence: FieldPresence.Optional,
            nullability: FieldNullability.Nullable);
        var outputContract = new ValueContract(new ScalarTypeRef(ScalarTypeKind.Bool));
        var scenario = new ScenarioDefinition(
            "scenario/input-states",
            "r1",
            InitialWorld(),
            StartsAtUtc,
            [new("inspect", inputContract, outputContract)],
            [new("dispatcher", "dispatcher-for-scenario")],
            [
                new("null", StartsAtUtc, "dispatcher", "inspect", ObservationValue.Null),
                new("missing", StartsAtUtc, "dispatcher", "inspect", ObservationValue.Undefined)
            ]);
        RecordingScenarioInterpreter interpreter = new(context => PortableValue.Concrete(
            context.Operation.Output,
            ObservationValue.FromBool(true)));

        var document = ScenarioDefinitionDocument.FromDefinition(scenario);
        await ScenarioRunner.ExecuteAsync(ScenarioWorldSnapshot.FromCoreWorld(document), interpreter);

        Assert.Equal(
            [PortableValueState.Missing, PortableValueState.Null],
            interpreter.Contexts.Select(static context => context.Input.State));
    }

    [Fact]
    public async Task Execution_FailsClosedWhenInterpreterReturnsAnotherContract()
    {
        var document = ScenarioDefinitionDocument.FromDefinition(Scenario(
            operationOrder: ["freight.assign-load", "freight.release-load"],
            actorOrder: ["dispatcher", "carrier"],
            actionOrder: ["assign-first", "assign-second", "release-load"]));
        RecordingScenarioInterpreter interpreter = new(_ =>
            PortableValue.Concrete(
                new(new ScalarTypeRef(ScalarTypeKind.String)),
                ObservationValue.FromString("wrong contract")));

        var exception = await Assert.ThrowsAsync<ScenarioExecutionException>(
            () => ScenarioRunner.ExecuteAsync(ScenarioWorldSnapshot.FromCoreWorld(document), interpreter));

        var diagnostic = Assert.Single(exception.Validation.Diagnostics);
        Assert.Equal(ScenarioExecutionDiagnosticCodes.OutputContractMismatch, diagnostic.Code);
        Assert.Equal("/outcomes/0/output/contract", diagnostic.Location);
        Assert.Equal("assign-first", diagnostic.Evidence!.Subject);
        Assert.Single(interpreter.Contexts);
    }

    [Fact]
    public async Task Execution_FailsClosedWhenOutputViolatesItsDeclaredContract()
    {
        var document = ScenarioDefinitionDocument.FromDefinition(Scenario(
            operationOrder: ["freight.assign-load", "freight.release-load"],
            actorOrder: ["dispatcher", "carrier"],
            actionOrder: ["assign-first", "assign-second", "release-load"]));
        RecordingScenarioInterpreter interpreter = new(context => PortableValue.Null(context.Operation.Output));

        var exception = await Assert.ThrowsAsync<ScenarioExecutionException>(
            () => ScenarioRunner.ExecuteAsync(ScenarioWorldSnapshot.FromCoreWorld(document), interpreter));

        var diagnostic = Assert.Single(exception.Validation.Diagnostics);
        Assert.Equal(PortableExecutionDiagnosticCodes.NullabilityMismatch, diagnostic.Code);
        Assert.Equal("/outcomes/0/output/state", diagnostic.Location);
        Assert.Equal("assign-first", diagnostic.Evidence!.Subject);
    }

    [Fact]
    public async Task Cancellation_StopsBeforeInterpretingTheNextAction()
    {
        var document = ScenarioDefinitionDocument.FromDefinition(Scenario(
            operationOrder: ["freight.assign-load", "freight.release-load"],
            actorOrder: ["dispatcher", "carrier"],
            actionOrder: ["assign-first", "assign-second", "release-load"]));
        using CancellationTokenSource cancellation = new();
        RecordingScenarioInterpreter interpreter = new(context =>
        {
            cancellation.Cancel();
            return Complete(context);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ScenarioRunner.ExecuteAsync(
                ScenarioWorldSnapshot.FromCoreWorld(document),
                interpreter,
                cancellation.Token));

        Assert.Single(interpreter.Contexts);
    }

    [Theory]
    [InlineData("fingerprint")]
    [InlineData("schema")]
    [InlineData("unknown")]
    [InlineData("outcome")]
    [InlineData("outcome-order")]
    [InlineData("outcome-missing")]
    [InlineData("scenario")]
    public async Task InvalidPortableTraces_ProduceStructuredDiagnostics(string mutation)
    {
        var document = ScenarioDefinitionDocument.FromDefinition(Scenario(
            operationOrder: ["freight.assign-load", "freight.release-load"],
            actorOrder: ["dispatcher", "carrier"],
            actionOrder: ["assign-first", "assign-second", "release-load"]));
        var trace = await ScenarioRunner.ExecuteAsync(
            ScenarioWorldSnapshot.FromCoreWorld(document),
            new RecordingScenarioInterpreter(Complete));
        var root = JsonNode.Parse(ScenarioExecutionTraceJsonSerializer.Serialize(trace))!.AsObject();
        switch (mutation)
        {
            case "fingerprint":
                root["fingerprint"]!["value"] = new string('0', 64);
                break;
            case "schema":
                root["schemaVersion"] = "cohesive-simulation-scenario-trace/v999";
                break;
            case "unknown":
                root["unexpected"] = true;
                break;
            case "outcome":
                root["outcomes"]![0]!["output"]!["value"]!["$value"]!["Accepted"]!["$value"] = false;
                break;
            case "outcome-order":
                var outcomes = root["outcomes"]!.AsArray();
                var first = outcomes[0]!.DeepClone();
                outcomes[0] = outcomes[1]!.DeepClone();
                outcomes[1] = first;
                break;
            case "outcome-missing":
                root["outcomes"]!.AsArray().RemoveAt(0);
                break;
            case "scenario":
                root["scenario"]!["definition"]!["id"] = "scenario/another";
                break;
            default:
                throw new InvalidOperationException($"Unknown trace mutation '{mutation}'.");
        }

        var validation = ScenarioExecutionTraceJsonSerializer.TryDeserialize(root.ToJsonString(), out var restored);

        Assert.Null(restored);
        Assert.False(validation.IsValid);
        Assert.Contains(
            validation.Diagnostics,
            diagnostic => diagnostic.Code == "simulation.scenario.trace.contentInvalid");
    }

    static PortableValue Complete(ScenarioActionContext context) =>
        context.Operation.Id switch
        {
            "freight.assign-load" => PortableValue.Concrete(
                context.Operation.Output,
                ObservationValue.FromObject(new AssignmentReceipt(Accepted: true))),
            "freight.release-load" => PortableValue.Concrete(
                context.Operation.Output,
                ObservationValue.FromObject(new ReleaseReceipt(Released: true))),
            _ => throw new InvalidOperationException($"Unknown operation '{context.Operation.Id}'.")
        };

    sealed class RecordingScenarioInterpreter : IScenarioActionInterpreter
    {
        readonly Func<ScenarioActionContext, ScenarioActionResult> execute;

        public RecordingScenarioInterpreter(Func<ScenarioActionContext, PortableValue> execute)
            : this(context => ScenarioActionResult.Unchanged(execute(context)))
        {
        }

        public RecordingScenarioInterpreter(Func<ScenarioActionContext, ScenarioActionResult> execute) =>
            this.execute = execute;

        public const string InterpreterIdentity = "tests/freight-scenario-interpreter/v1";

        public List<ScenarioActionContext> Contexts { get; } = [];

        public string Identity => InterpreterIdentity;

        public ValueTask<ScenarioActionResult> ExecuteAsync(
            ScenarioActionContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Contexts.Add(context);
            return ValueTask.FromResult(execute(context));
        }
    }
}
