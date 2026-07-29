using System.Text.Json;
using System.Text.Json.Nodes;
using Cohesive.Api;
using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Tests.ExecutionKernel;

namespace Cohesive.Tests.Api;

public sealed class ExecutionControlResultTests
{
    [Fact]
    public void Constructor_RequiresDispositionSpecificReceiptAndDiagnosticShapes()
    {
        var status = ExecutionStatusProjector.Project(ProcessControlTestFixture.Create().State());
        var receipt = new ExecutionControlReceiptSummary(
            new("command/result-shape"),
            ProcessControlReceiptDisposition.AlreadySatisfied,
            ProcessControlRevision.Initial,
            status.ControlRevision,
            status.UpdatedAtUtc);

        Assert.Throws<ArgumentException>(() => new ExecutionControlResult(
            ProcessControlDecisionDisposition.Applied,
            status));
        Assert.Throws<ArgumentException>(() => new ExecutionControlResult(
            ProcessControlDecisionDisposition.Inspected,
            status,
            receipt));
        Assert.Throws<ArgumentException>(() => new ExecutionControlResult(
            ProcessControlDecisionDisposition.StaleRevision,
            status));
        Assert.Throws<ArgumentException>(() => new ExecutionControlResult(
            ProcessControlDecisionDisposition.Inspected,
            status,
            diagnosticCodes: [ProcessControlDiagnosticCodes.InvalidCommand]));

        var observationReplay = new ExecutionControlResult(
            ProcessControlDecisionDisposition.Replayed,
            status);
        Assert.Null(observationReplay.Receipt);

        Assert.Throws<ArgumentException>(() => new ExecutionControlReceiptSummary(
            new("command/invalid-fence"),
            ProcessControlReceiptDisposition.Applied,
            ProcessControlRevision.Initial,
            ProcessControlRevision.Initial,
            status.UpdatedAtUtc));
    }

    [Fact]
    public void Json_RejectsFirstTimeCommandResultWithMissingReceipt()
    {
        var fixture = ProcessControlTestFixture.Create();
        var initial = fixture.State();
        var decision = fixture.Executor.Apply(
            initial,
            fixture.Pause(initial),
            initial.UpdatedAtUtc.AddMinutes(1));
        var result = ExecutionControlResult.FromDecision(decision);
        var options = InteractionEnvelopeJsonSerializer.CreateOptions();
        var json = JsonNode.Parse(JsonSerializer.Serialize(result, options))?.AsObject()
            ?? throw new InvalidOperationException("Execution-control result JSON must be an object.");
        json["receipt"] = null;

        Assert.Throws<ArgumentException>(() =>
            JsonSerializer.Deserialize<ExecutionControlResult>(json.ToJsonString(), options));
    }
}
