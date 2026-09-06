using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Cohesive.Model;
using Cohesive.Model.Authoring;
using Cohesive.Simulation.Artifacts;
using Cohesive.Simulation.Generation;
using Cohesive.Simulation.Scenarios;

namespace Cohesive.Simulation.Tests;

public sealed class ScenarioTests
{
    static readonly DateTimeOffset StartsAtUtc = new(2030, 1, 2, 3, 4, 5, TimeSpan.Zero);
    static readonly DefaultClrTypeRefMapper TypeMapper = new();

    [Fact]
    public void TypedAuthoring_LowersToTheSameCanonicalScenarioAsDirectIr()
    {
        var initialWorld = InitialWorld();
        var authored = Simulation.DefineScenario(
            "scenario/freight-dispatch",
            "r1",
            initialWorld,
            StartsAtUtc,
            scenario => scenario
                .Operation<AssignLoad, AssignmentReceipt>("freight.assign-load")
                .Actor("dispatcher", "dispatcher-for-scenario")
                .Actor("carrier", "carrier-for-scenario")
                .Action(
                    id: "assign-load",
                    afterStart: TimeSpan.FromMinutes(5),
                    actorId: "dispatcher",
                    operationId: "freight.assign-load",
                    input: new AssignLoad("load-42"),
                    targetActorId: "carrier"));
        var direct = new ScenarioDefinition(
            "scenario/freight-dispatch",
            "r1",
            initialWorld,
            StartsAtUtc,
            [
                new(
                    "freight.assign-load",
                    new(TypeMapper.Map(typeof(AssignLoad), nullability: null)),
                    new(TypeMapper.Map(typeof(AssignmentReceipt), nullability: null)))
            ],
            [
                new("dispatcher", "dispatcher-for-scenario"),
                new("carrier", "carrier-for-scenario")
            ],
            [
                new(
                    "assign-load",
                    StartsAtUtc.AddMinutes(5),
                    "dispatcher",
                    "freight.assign-load",
                    ObservationValue.FromObject(new AssignLoad("load-42")),
                    "carrier")
            ]);

        Assert.Equal(
            ScenarioDefinitionJsonSerializer.Serialize(direct),
            ScenarioDefinitionJsonSerializer.Serialize(authored));
    }

    [Fact]
    public void Compilation_NormalizesIdentitySetsAndVirtualTimeSchedule()
    {
        var scenario = Scenario(
            operationOrder: ["freight.release-load", "freight.assign-load"],
            actorOrder: ["carrier", "dispatcher"],
            actionOrder: ["release-load", "assign-second", "assign-first"]);

        var plan = scenario.Compile();

        Assert.Equal(
            ["freight.assign-load", "freight.release-load"],
            plan.Definition.Operations.Select(static operation => operation.Id));
        Assert.Equal(
            ["carrier", "dispatcher"],
            plan.Definition.Actors.Select(static actor => actor.Id));
        Assert.Equal(
            ["assign-first", "assign-second", "release-load"],
            plan.Definition.Actions.Select(static action => action.Id));
        Assert.Equal(64, plan.Fingerprint.Length);
        Assert.Equal("carrier-for-scenario", plan.GetActor("carrier").ExemplarId);
        Assert.Equal("freight.assign-load", plan.GetOperation("freight.assign-load").Id);
    }

    [Fact]
    public void EquivalentDeclarationOrders_ProduceOneCanonicalDocument()
    {
        var first = Scenario(
            operationOrder: ["freight.assign-load", "freight.release-load"],
            actorOrder: ["dispatcher", "carrier"],
            actionOrder: ["assign-first", "assign-second", "release-load"]);
        var reordered = Scenario(
            operationOrder: ["freight.release-load", "freight.assign-load"],
            actorOrder: ["carrier", "dispatcher"],
            actionOrder: ["release-load", "assign-second", "assign-first"]);

        Assert.Equal(
            ScenarioDefinitionJsonSerializer.Serialize(first),
            ScenarioDefinitionJsonSerializer.Serialize(reordered));
        Assert.Equal(first.Compile().Fingerprint, reordered.Compile().Fingerprint);
    }

    [Fact]
    public void Fingerprint_CoversInitialWorldTimeContractsActorsScheduleAndInputs()
    {
        var baseline = Scenario(
            operationOrder: ["freight.assign-load", "freight.release-load"],
            actorOrder: ["dispatcher", "carrier"],
            actionOrder: ["assign-first", "assign-second", "release-load"]);
        var changedWorld = Copy(baseline, initialWorld: InitialWorld(rootSeed: 43));
        var changedStart = Copy(
            baseline,
            startsAtUtc: StartsAtUtc.AddDays(1),
            actions:
            [
                .. baseline.Actions.Select(action => Copy(
                    action,
                    scheduledAtUtc: action.ScheduledAtUtc.AddDays(1)))
            ]);
        var changedContract = Copy(
            baseline,
            operations:
            [
                new(
                    baseline.Operations[0].Id,
                    baseline.Operations[0].Input,
                    new(new ScalarTypeRef(ScalarTypeKind.String))),
                .. baseline.Operations[1..]
            ]);
        var changedActor = Copy(
            baseline,
            actors:
            [
                new(baseline.Actors[0].Id, "carrier-for-scenario"),
                baseline.Actors[1]
            ]);
        var changedSchedule = Copy(
            baseline,
            actions:
            [
                Copy(baseline.Actions[0], scheduledAtUtc: baseline.Actions[0].ScheduledAtUtc.AddTicks(1)),
                .. baseline.Actions[1..]
            ]);
        var changedInput = Copy(
            baseline,
            actions:
            [
                Copy(
                    baseline.Actions[0],
                    input: ObservationValue.FromObject(new AssignLoad("another-load"))),
                .. baseline.Actions[1..]
            ]);
        var fingerprint = baseline.Compile().Fingerprint;

        Assert.NotEqual(fingerprint, changedWorld.Compile().Fingerprint);
        Assert.NotEqual(fingerprint, changedStart.Compile().Fingerprint);
        Assert.NotEqual(fingerprint, changedContract.Compile().Fingerprint);
        Assert.NotEqual(fingerprint, changedActor.Compile().Fingerprint);
        Assert.NotEqual(fingerprint, changedSchedule.Compile().Fingerprint);
        Assert.NotEqual(fingerprint, changedInput.Compile().Fingerprint);
    }

    [Theory]
    [InlineData("unknown-exemplar", "simulation.scenario.actorExemplarUnknown")]
    [InlineData("unknown-operation", "simulation.scenario.actionOperationUnknown")]
    [InlineData("unknown-actor", "simulation.scenario.actionActorUnknown")]
    [InlineData("unknown-target", "simulation.scenario.actionTargetUnknown")]
    [InlineData("before-start", "simulation.scenario.actionBeforeStart")]
    [InlineData("non-utc", "simulation.scenario.actionTimeNotUtc")]
    [InlineData("input-type", "simulation.scenario.actionInputInvalid")]
    public void Compilation_FailsClosedForInvalidScenarioSemantics(string mutation, string expectedCode)
    {
        var baseline = Scenario(
            operationOrder: ["freight.assign-load", "freight.release-load"],
            actorOrder: ["dispatcher", "carrier"],
            actionOrder: ["assign-first", "assign-second", "release-load"]);
        var actor = baseline.Actors[0];
        var action = baseline.Actions[0];
        var invalid = mutation switch
        {
            "unknown-exemplar" => Copy(
                baseline,
                actors: [new(actor.Id, "missing-exemplar"), baseline.Actors[1]]),
            "unknown-operation" => Copy(
                baseline,
                actions: [Copy(action, operationId: "missing-operation"), .. baseline.Actions[1..]]),
            "unknown-actor" => Copy(
                baseline,
                actions: [Copy(action, actorId: "missing-actor"), .. baseline.Actions[1..]]),
            "unknown-target" => Copy(
                baseline,
                actions: [Copy(action, targetActorId: "missing-target"), .. baseline.Actions[1..]]),
            "before-start" => Copy(
                baseline,
                actions: [Copy(action, scheduledAtUtc: StartsAtUtc.AddTicks(-1)), .. baseline.Actions[1..]]),
            "non-utc" => Copy(
                baseline,
                actions:
                [
                    Copy(action, scheduledAtUtc: action.ScheduledAtUtc.ToOffset(TimeSpan.FromHours(1))),
                    .. baseline.Actions[1..]
                ]),
            "input-type" => Copy(
                baseline,
                actions:
                [
                    Copy(action, input: ObservationValue.FromString("not-an-assignment")),
                    .. baseline.Actions[1..]
                ]),
            _ => throw new InvalidOperationException($"Unknown scenario mutation '{mutation}'.")
        };

        var compilation = invalid.CompileResult();

        Assert.Null(compilation.Plan);
        Assert.Contains(compilation.Validation.Diagnostics, diagnostic => diagnostic.Code == expectedCode);
    }

    [Fact]
    public void Compilation_RejectsOpaqueOperationTypes()
    {
        var baseline = Scenario(
            operationOrder: ["freight.assign-load", "freight.release-load"],
            actorOrder: ["dispatcher", "carrier"],
            actionOrder: ["assign-first", "assign-second", "release-load"]);
        var operation = baseline.Operations[0];
        var invalid = Copy(
            baseline,
            operations:
            [
                new(
                    operation.Id,
                    new(new OpaqueRuntimeTypeRef("runtime-only")),
                    operation.Output),
                .. baseline.Operations[1..]
            ]);

        var compilation = invalid.CompileResult();

        Assert.Null(compilation.Plan);
        Assert.Contains(
            compilation.Validation.Diagnostics,
            diagnostic => diagnostic.Location?.StartsWith(
                "/operations/0/input",
                StringComparison.Ordinal) == true);
    }

    [Fact]
    public void ExplicitOperationContracts_PreserveRootNullability()
    {
        var initialWorld = InitialWorld();
        var scenario = new ScenarioDefinition(
            "scenario/nullable-input",
            "r1",
            initialWorld,
            StartsAtUtc,
            [
                new(
                    "freight.optional-note",
                    new(
                        new ScalarTypeRef(ScalarTypeKind.String),
                        nullability: FieldNullability.Nullable),
                    new(new ScalarTypeRef(ScalarTypeKind.Bool)))
            ],
            [new("dispatcher", "dispatcher-for-scenario")],
            [
                new(
                    "omit-note",
                    StartsAtUtc,
                    "dispatcher",
                    "freight.optional-note",
                    ObservationValue.Null)
            ]);

        var plan = scenario.Compile();

        Assert.Equal(
            FieldNullability.Nullable,
            plan.GetOperation("freight.optional-note").Input.Nullability);
    }

    [Fact]
    public void PortableScenario_RoundTripsAndPinsExactInitialWorldAndFingerprint()
    {
        var scenario = Scenario(
            operationOrder: ["freight.assign-load", "freight.release-load"],
            actorOrder: ["dispatcher", "carrier"],
            actionOrder: ["assign-first", "assign-second", "release-load"]);
        var document = ScenarioDefinitionDocument.FromDefinition(scenario);

        var json = ScenarioDefinitionJsonSerializer.Serialize(document);
        var restored = ScenarioDefinitionJsonSerializer.Deserialize(json);

        Assert.Equal(ScenarioDefinitionDocument.CurrentSchemaVersion, restored.SchemaVersion);
        Assert.Equal(document.Fingerprint, restored.Fingerprint);
        Assert.Equal(scenario.InitialWorld.ArtifactId, restored.Definition.InitialWorld.ArtifactId);
        Assert.Equal(
            "eac80d581a81dab1cd1a622065eb822fde1de5133d310024b58dd3b8853eccdd",
            restored.Fingerprint.Value);
        Assert.Equal(json, ScenarioDefinitionJsonSerializer.Serialize(restored));
    }

    [Theory]
    [InlineData("fingerprint", "simulation.scenario.document.contentInvalid")]
    [InlineData("schema", "simulation.scenario.document.contentInvalid")]
    [InlineData("unknown", "simulation.scenario.document.contentInvalid")]
    [InlineData("operation-order", "simulation.scenario.document.wireNonCanonical")]
    [InlineData("actor-order", "simulation.scenario.document.wireNonCanonical")]
    [InlineData("action-order", "simulation.scenario.document.wireNonCanonical")]
    public void InvalidPortableScenarios_ProduceStructuredDiagnostics(string mutation, string expectedCode)
    {
        var json = ScenarioDefinitionJsonSerializer.Serialize(Scenario(
            operationOrder: ["freight.assign-load", "freight.release-load"],
            actorOrder: ["dispatcher", "carrier"],
            actionOrder: ["assign-first", "assign-second", "release-load"]));
        var invalid = mutation switch
        {
            "fingerprint" => Mutate(json, root => root["fingerprint"]!["value"] = new string('0', 64)),
            "schema" => Mutate(json, root => root["schemaVersion"] = "cohesive-simulation-scenario/v999"),
            "unknown" => Mutate(json, root => root["unexpected"] = true),
            "operation-order" => Mutate(json, root => Reverse(root, "operations")),
            "actor-order" => Mutate(json, root => Reverse(root, "actors")),
            "action-order" => Mutate(json, root => Reverse(root, "actions")),
            _ => throw new InvalidOperationException($"Unknown document mutation '{mutation}'.")
        };

        var validation = ScenarioDefinitionJsonSerializer.TryDeserialize(invalid, out var restored);

        Assert.Null(restored);
        Assert.False(validation.IsValid);
        Assert.Contains(validation.Diagnostics, diagnostic => diagnostic.Code == expectedCode);
    }

    static ScenarioDefinition Scenario(
        IReadOnlyList<string> operationOrder,
        IReadOnlyList<string> actorOrder,
        IReadOnlyList<string> actionOrder)
    {
        var initialWorld = InitialWorld();
        Dictionary<string, ScenarioOperationDefinition> operations = new(StringComparer.Ordinal)
        {
            ["freight.assign-load"] = new(
                "freight.assign-load",
                new(TypeMapper.Map(typeof(AssignLoad), nullability: null)),
                new(TypeMapper.Map(typeof(AssignmentReceipt), nullability: null))),
            ["freight.release-load"] = new(
                "freight.release-load",
                new(TypeMapper.Map(typeof(ReleaseLoad), nullability: null)),
                new(TypeMapper.Map(typeof(ReleaseReceipt), nullability: null)))
        };
        Dictionary<string, ScenarioActorDefinition> actors = new(StringComparer.Ordinal)
        {
            ["dispatcher"] = new("dispatcher", "dispatcher-for-scenario"),
            ["carrier"] = new("carrier", "carrier-for-scenario")
        };
        Dictionary<string, ScenarioActionDefinition> actions = new(StringComparer.Ordinal)
        {
            ["assign-first"] = new(
                "assign-first",
                StartsAtUtc.AddMinutes(1),
                "dispatcher",
                "freight.assign-load",
                ObservationValue.FromObject(new AssignLoad("load-1")),
                "carrier"),
            ["assign-second"] = new(
                "assign-second",
                StartsAtUtc.AddMinutes(1),
                "dispatcher",
                "freight.assign-load",
                ObservationValue.FromObject(new AssignLoad("load-2")),
                "carrier"),
            ["release-load"] = new(
                "release-load",
                StartsAtUtc.AddMinutes(2),
                "carrier",
                "freight.release-load",
                ObservationValue.FromObject(new ReleaseLoad("load-1")))
        };
        return new(
            "scenario/freight-dispatch",
            "r1",
            initialWorld,
            StartsAtUtc,
            [.. operationOrder.Select(id => operations[id])],
            [.. actorOrder.Select(id => actors[id])],
            [.. actionOrder.Select(id => actions[id])]);
    }

    static WorldArtifactManifest InitialWorld(long rootSeed = 42)
    {
        var actors = Simulation.Define<FreightActor>(actor => actor
            .Member(value => value.Name, Gen.Categorical(
                Gen.Weighted("Dispatcher", weight: 1d),
                Gen.Weighted("Carrier", weight: 1d))));
        var world = Simulation.DefineWorld(
            "world/freight-scenario",
            "r1",
            builder => builder
                .Population("actors", count: 2, actors)
                .Exemplar("dispatcher-for-scenario", "actors", sequenceIndex: 0)
                .Exemplar("carrier-for-scenario", "actors", sequenceIndex: 1));
        return WorldArtifactManifest.FromWorld(world.Compile(), rootSeed);
    }

    static ScenarioDefinition Copy(
        ScenarioDefinition source,
        WorldArtifactManifest? initialWorld = null,
        DateTimeOffset? startsAtUtc = null,
        ImmutableArray<ScenarioOperationDefinition> operations = default,
        ImmutableArray<ScenarioActorDefinition> actors = default,
        ImmutableArray<ScenarioActionDefinition> actions = default) =>
        new(
            source.Id,
            source.Revision,
            initialWorld ?? source.InitialWorld,
            startsAtUtc ?? source.StartsAtUtc,
            operations.IsDefault ? source.Operations : operations,
            actors.IsDefault ? source.Actors : actors,
            actions.IsDefault ? source.Actions : actions);

    static ScenarioActionDefinition Copy(
        ScenarioActionDefinition source,
        DateTimeOffset? scheduledAtUtc = null,
        string? actorId = null,
        string? operationId = null,
        ObservationValue? input = null,
        string? targetActorId = null) =>
        new(
            source.Id,
            scheduledAtUtc ?? source.ScheduledAtUtc,
            actorId ?? source.ActorId,
            operationId ?? source.OperationId,
            input ?? source.Input,
            targetActorId ?? source.TargetActorId);

    static string Mutate(string json, Action<JsonObject> mutate)
    {
        var root = JsonNode.Parse(json)?.AsObject()
                   ?? throw new InvalidOperationException("Scenario JSON did not contain an object.");
        mutate(root);
        return root.ToJsonString();
    }

    static void Reverse(JsonObject root, string property)
    {
        var values = root["definition"]![property]!.AsArray();
        var reversed = values.Select(static value => value!.DeepClone()).Reverse().ToArray();
        values.Clear();
        foreach (var value in reversed)
            values.Add(value);
    }

    sealed record FreightActor(string Name);

    sealed record AssignLoad(string LoadId);

    sealed record AssignmentReceipt(bool Accepted);

    sealed record ReleaseLoad(string LoadId);

    sealed record ReleaseReceipt(bool Released);
}
