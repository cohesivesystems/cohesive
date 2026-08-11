using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cohesive.Execution;
using Cohesive.Processes.Execution;
using Cohesive.Processes.Runtime;
using Cohesive.Tests.ExecutionKernel;

namespace Cohesive.Tests.Processes;

public sealed class ProcessExecutionTraceRepositoryContractTests
{
    [Fact]
    public void Artifact_IsVersionedMakesCoverageExplicitAndRequiresExactTraceAffinity()
    {
        var fixture = ProcessDurabilityTestFixture.Create();
        var projection = ProcessExecutionTraceProjector.Project(fixture.Decision);
        var trace = Assert.IsType<NormalizedExecutionTrace>(projection.Trace);

        var partial = new ProcessExecutionTraceArtifact(
            ProcessExecutionTraceArtifact.CurrentSchemaVersion,
            trace.Definition,
            trace.Continuation!.ProcessInstanceId,
            missingTracePrefixCount: 2,
            [trace]);
        var complete = new ProcessExecutionTraceArtifact(
            ProcessExecutionTraceArtifact.CurrentSchemaVersion,
            trace.Definition,
            trace.Continuation.ProcessInstanceId,
            missingTracePrefixCount: 0,
            [trace]);

        Assert.False(partial.IsComplete);
        Assert.Equal(ProcessExecutionTraceArtifact.CurrentSchemaVersion, partial.SchemaVersion);
        Assert.Equal("cohesive.processes.execution-traces", ProcessExecutionTraceWireNames.SemanticAuthority);
        Assert.Equal(new ExecutionSemanticPath(["queries", "traces"]), ProcessExecutionTraceWireNames.QueryPath);
        Assert.Equal(3, partial.ActivationEvidenceCount);
        Assert.True(complete.IsComplete);
        Assert.Equal(1, complete.ActivationEvidenceCount);
        Assert.Throws<ArgumentException>(() => new ProcessExecutionTraceArtifact(
            new("cohesive-process-execution-traces/unsupported"),
            trace.Definition,
            trace.Continuation.ProcessInstanceId,
            missingTracePrefixCount: 0,
            [trace]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProcessExecutionTraceArtifact(
            ProcessExecutionTraceArtifact.CurrentSchemaVersion,
            trace.Definition,
            trace.Continuation.ProcessInstanceId,
            missingTracePrefixCount: -1,
            [trace]));
        Assert.Throws<ArgumentException>(() => new ProcessExecutionTraceArtifact(
            ProcessExecutionTraceArtifact.CurrentSchemaVersion,
            trace.Definition,
            trace.Continuation.ProcessInstanceId,
            missingTracePrefixCount: 0,
            [trace, trace]));
        Assert.Throws<ArgumentException>(() => new ProcessExecutionTraceArtifact(
            ProcessExecutionTraceArtifact.CurrentSchemaVersion,
            trace.Definition,
            new("process-instance/other"),
            missingTracePrefixCount: 0,
            [trace]));
    }

    [Fact]
    public void ReadResult_DistinguishesEveryAvailabilityStateAndRequiresAnArtifactOnlyWhenAvailable()
    {
        var fixture = ProcessDurabilityTestFixture.Create();
        var trace = Assert.IsType<NormalizedExecutionTrace>(
            ProcessExecutionTraceProjector.Project(fixture.Decision).Trace);
        var artifact = new ProcessExecutionTraceArtifact(
            ProcessExecutionTraceArtifact.CurrentSchemaVersion,
            trace.Definition,
            trace.Continuation!.ProcessInstanceId,
            missingTracePrefixCount: 0,
            [trace]);

        Assert.Equal(ProcessExecutionTraceReadState.NotFound, ProcessExecutionTraceReadResult.NotFound().State);
        Assert.Equal(ProcessExecutionTraceReadState.InProgress, ProcessExecutionTraceReadResult.InProgress().State);
        Assert.Equal(
            ProcessExecutionTraceReadState.TerminalArtifactUnavailable,
            ProcessExecutionTraceReadResult.TerminalArtifactUnavailable().State);
        Assert.Same(artifact, ProcessExecutionTraceReadResult.Available(artifact).Artifact);
        Assert.Throws<ArgumentException>(() => new ProcessExecutionTraceReadResult(
            ProcessExecutionTraceReadState.Available));
        Assert.Throws<ArgumentException>(() => new ProcessExecutionTraceReadResult(
            ProcessExecutionTraceReadState.NotFound,
            artifact));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProcessExecutionTraceReadResult(
            ProcessExecutionTraceReadState.Unspecified));
    }

    [Fact]
    public void JsonSerializer_EmitsCanonicalPortableArtifactAndRejectsOpenOrInvalidWires()
    {
        var fixture = ProcessDurabilityTestFixture.Create();
        var trace = Assert.IsType<NormalizedExecutionTrace>(
            ProcessExecutionTraceProjector.Project(fixture.Decision).Trace);
        var artifact = new ProcessExecutionTraceArtifact(
            ProcessExecutionTraceArtifact.CurrentSchemaVersion,
            trace.Definition,
            trace.Continuation!.ProcessInstanceId,
            missingTracePrefixCount: 0,
            [trace]);

        var json = ProcessExecutionTraceJsonSerializer.Serialize(artifact);
        var canonical = ProcessExecutionTraceJsonSerializer.GetCanonicalBytes(artifact);
        var roundTrip = ProcessExecutionTraceJsonSerializer.Deserialize(json);

        Assert.Equal(canonical, Encoding.UTF8.GetBytes(json));
        Assert.Equal(json, ProcessExecutionTraceJsonSerializer.Serialize(roundTrip));
        Assert.DoesNotContain("\"processId\"", json, StringComparison.Ordinal);

        var unknown = JsonNode.Parse(json)!.AsObject();
        unknown["unexpected"] = true;
        Assert.Throws<JsonException>(() =>
            ProcessExecutionTraceJsonSerializer.Deserialize(unknown.ToJsonString()));

        var unsupported = JsonNode.Parse(json)!.AsObject();
        unsupported["schemaVersion"] = "cohesive-process-execution-traces/unsupported";
        Assert.Throws<JsonException>(() =>
            ProcessExecutionTraceJsonSerializer.Deserialize(unsupported.ToJsonString()));

        var duplicate = json[..^1]
            + $",\"schemaVersion\":\"{ProcessExecutionTraceArtifact.CurrentSchemaVersion.Value}\"}}";
        Assert.Throws<JsonException>(() =>
            ProcessExecutionTraceJsonSerializer.Deserialize(duplicate));
    }
}
