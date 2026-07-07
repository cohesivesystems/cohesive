using System.Text.Json;
using Cohesive.Processes.Runtime;

namespace Cohesive.Tests.Processes;

/// <summary>
/// Tests for retained process execution record helpers.
/// </summary>
public sealed class ProcessExecutionRecordExtensionsTests
{
    [Fact]
    public void TryGetParameter_DeserializesJsonElementParameter()
    {
        var parameter = JsonSerializer.SerializeToElement(new TestParameter(Id: "edi-204"));
        var record = new ProcessExecutionRecord(
            ProcessId: "process--scope--001",
            ProcessName: "CompileShapeGraph",
            Status: ProcessExecutionStatus.Completed,
            StartedAtUtc: null,
            UpdatedAtUtc: null,
            CompletedAtUtc: null,
            Parameters: new Dictionary<string, object?> { ["trigger"] = parameter }
            );

        var result = record.TryGetParameter<TestParameter>();

        Assert.NotNull(result);
        Assert.Equal(expected: "edi-204", actual: result.Id);
    }

    [Fact]
    public void TryGetOutput_DeserializesJsonElementOutput()
    {
        var output = JsonSerializer.SerializeToElement(new TestOutput(ShapeGraphId: "graph-204"));
        var record = new ProcessExecutionRecord(
            ProcessId: "process--scope--001",
            ProcessName: "CompileShapeGraph",
            Status: ProcessExecutionStatus.Completed,
            StartedAtUtc: null,
            UpdatedAtUtc: null,
            CompletedAtUtc: null,
            Output: output
            );

        var result = record.TryGetOutput<TestOutput>();

        Assert.NotNull(result);
        Assert.Equal(expected: "graph-204", actual: result.ShapeGraphId);
    }

    [Fact]
    public void ResolveFailureMessage_PrefersTopLevelFailureMessage()
    {
        var record = new ProcessExecutionRecord(
            ProcessId: "process--scope--001",
            ProcessName: "CompileShapeGraph",
            Status: ProcessExecutionStatus.Failed,
            StartedAtUtc: null,
            UpdatedAtUtc: null,
            CompletedAtUtc: null,
            FailureMessage: " top level ",
            Error: new ProcessExecutionError(
                ErrorType: null,
                ErrorMessage: "error message",
                StackTrace: null,
                InnerError: new ProcessExecutionError(
                    ErrorType: null,
                    ErrorMessage: "inner message",
                    StackTrace: null
                    )
                )
            );

        Assert.Equal(expected: "top level", actual: record.ResolveFailureMessage());
    }

    sealed record TestParameter(string Id);

    sealed record TestOutput(string ShapeGraphId);
}
