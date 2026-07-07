using Cohesive.Processes.Runtime;

namespace Cohesive.Tests.Processes;

/// <summary>
/// Tests for scoped process instance identifiers.
/// </summary>
public sealed class ScopedProcessInstanceIdTests
{
    [Fact]
    public void Create_NormalizesSegments()
    {
        var id = ScopedProcessInstanceId.Create(
            processType: "Compile Shape Graph",
            scopeId: "UI Test",
            suffix: "Run 001"
            );

        Assert.Equal(expected: "compile-shape-graph--ui-test--run-001", actual: id);
    }

    [Fact]
    public void TryParse_ReturnsStructuredSegments()
    {
        var parsed = ScopedProcessInstanceId.TryParse(
            "shape-graph-compilation--ui-test--019e810b55327531a1670608f08be989",
            out var id
            );

        Assert.True(parsed);
        Assert.NotNull(id);
        Assert.Equal(expected: "shape-graph-compilation", actual: id.ProcessType);
        Assert.Equal(expected: "ui-test", actual: id.ScopeId);
        Assert.Equal(expected: "019e810b55327531a1670608f08be989", actual: id.Suffix);
        Assert.True(id.Matches(processType: "Shape Graph Compilation", scopeId: "UI Test"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("shape-graph-compilation")]
    [InlineData("shape-graph-compilation--")]
    [InlineData("--ui-test--suffix")]
    public void TryParse_RejectsMissingSegments(string? value)
    {
        Assert.False(ScopedProcessInstanceId.TryParse(value, out var id));
        Assert.Null(id);
    }
}
