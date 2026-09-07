using System.Text.Json.Nodes;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Simulation.Scenarios;

namespace Cohesive.Simulation.Tests;

public sealed partial class ScenarioTests
{
    [Fact]
    public async Task Execution_UsesCanonicalVirtualScheduleAndRetainsExactTrace()
    {
        var document = ScenarioDefinitionDocument.FromDefinition(Scenario(
            operationOrder: ["freight.release-load", "freight.assign-load"],
            actorOrder: ["dispatcher", "carrier"],
            actionOrder: ["release-load", "assign-second", "assign-first"]));
        RecordingScenarioInterpreter interpreter = new(Complete);

        var trace = await ScenarioRunner.ExecuteAsync(document, interpreter);

        Assert.Same(document, trace.Scenario);
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
        Assert.Equal("load-1", interpreter.Contexts[0].Input.Value!.Value.GetProperty("LoadId").String);
        Assert.Equal(
            ["assign-first", "assign-second", "release-load"],
            trace.Outcomes.Select(static outcome => outcome.ActionId));
        Assert.All(trace.Outcomes, static outcome => Assert.Equal(PortableValueState.Concrete, outcome.Output.State));
        Assert.Equal(
            "5498bc77f50e60ea7c195cbfca06c62cc8687fe1eb088dc30c381bb94eb461f7",
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

        var first = await ScenarioRunner.ExecuteAsync(document, new RecordingScenarioInterpreter(Complete));
        var second = await ScenarioRunner.ExecuteAsync(document, new RecordingScenarioInterpreter(Complete));

        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.Equal(
            ScenarioExecutionTraceJsonSerializer.Serialize(first),
            ScenarioExecutionTraceJsonSerializer.Serialize(second));
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

        var trace = await ScenarioRunner.ExecuteAsync(document, interpreter);

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

        await ScenarioRunner.ExecuteAsync(ScenarioDefinitionDocument.FromDefinition(scenario), interpreter);

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
            () => ScenarioRunner.ExecuteAsync(document, interpreter));

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
            () => ScenarioRunner.ExecuteAsync(document, interpreter));

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
            () => ScenarioRunner.ExecuteAsync(document, interpreter, cancellation.Token));

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
        var trace = await ScenarioRunner.ExecuteAsync(document, new RecordingScenarioInterpreter(Complete));
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

    sealed class RecordingScenarioInterpreter(Func<ScenarioActionContext, PortableValue> execute)
        : IScenarioActionInterpreter
    {
        public const string InterpreterIdentity = "tests/freight-scenario-interpreter/v1";

        public List<ScenarioActionContext> Contexts { get; } = [];

        public string Identity => InterpreterIdentity;

        public ValueTask<PortableValue> ExecuteAsync(
            ScenarioActionContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Contexts.Add(context);
            return ValueTask.FromResult(execute(context));
        }
    }
}
