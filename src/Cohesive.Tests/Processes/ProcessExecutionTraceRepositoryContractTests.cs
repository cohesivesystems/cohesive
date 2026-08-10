using Cohesive.Execution;
using Cohesive.Processes.Execution;
using Cohesive.Processes.Runtime;
using Cohesive.Tests.ExecutionKernel;

namespace Cohesive.Tests.Processes;

public sealed class ProcessExecutionTraceRepositoryContractTests
{
    [Fact]
    public void Record_MakesCoverageExplicitAndRequiresExactTraceAffinity()
    {
        var fixture = ProcessDurabilityTestFixture.Create();
        var projection = ProcessExecutionTraceProjector.Project(fixture.Decision);
        var trace = Assert.IsType<NormalizedExecutionTrace>(projection.Trace);

        var partial = new ProcessExecutionTraceRecord(
            "physical/process/1",
            trace.Definition,
            trace.Continuation!.ProcessInstanceId,
            missingTracePrefixCount: 2,
            [trace]);
        var complete = new ProcessExecutionTraceRecord(
            "physical/process/1",
            trace.Definition,
            trace.Continuation.ProcessInstanceId,
            missingTracePrefixCount: 0,
            [trace]);

        Assert.False(partial.IsComplete);
        Assert.Equal(3, partial.ActivationEvidenceCount);
        Assert.True(complete.IsComplete);
        Assert.Equal(1, complete.ActivationEvidenceCount);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProcessExecutionTraceRecord(
            "physical/process/1",
            trace.Definition,
            trace.Continuation.ProcessInstanceId,
            missingTracePrefixCount: -1,
            [trace]));
        Assert.Throws<ArgumentException>(() => new ProcessExecutionTraceRecord(
            "physical/process/1",
            trace.Definition,
            trace.Continuation.ProcessInstanceId,
            missingTracePrefixCount: 0,
            [trace, trace]));
        Assert.Throws<ArgumentException>(() => new ProcessExecutionTraceRecord(
            "physical/process/1",
            trace.Definition,
            new("process-instance/other"),
            missingTracePrefixCount: 0,
            [trace]));
    }

    [Fact]
    public void ReadResult_DistinguishesEveryAvailabilityStateAndRequiresARecordOnlyWhenAvailable()
    {
        var fixture = ProcessDurabilityTestFixture.Create();
        var trace = Assert.IsType<NormalizedExecutionTrace>(
            ProcessExecutionTraceProjector.Project(fixture.Decision).Trace);
        var record = new ProcessExecutionTraceRecord(
            "physical/process/1",
            trace.Definition,
            trace.Continuation!.ProcessInstanceId,
            missingTracePrefixCount: 0,
            [trace]);

        Assert.Equal(ProcessExecutionTraceReadState.NotFound, ProcessExecutionTraceReadResult.NotFound().State);
        Assert.Equal(ProcessExecutionTraceReadState.InProgress, ProcessExecutionTraceReadResult.InProgress().State);
        Assert.Equal(
            ProcessExecutionTraceReadState.TerminalArtifactUnavailable,
            ProcessExecutionTraceReadResult.TerminalArtifactUnavailable().State);
        Assert.Same(record, ProcessExecutionTraceReadResult.Available(record).Record);
        Assert.Throws<ArgumentException>(() => new ProcessExecutionTraceReadResult(
            ProcessExecutionTraceReadState.Available));
        Assert.Throws<ArgumentException>(() => new ProcessExecutionTraceReadResult(
            ProcessExecutionTraceReadState.NotFound,
            record));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProcessExecutionTraceReadResult(
            ProcessExecutionTraceReadState.Unspecified));
    }
}
