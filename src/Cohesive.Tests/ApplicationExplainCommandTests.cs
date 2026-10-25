using Cohesive.Host.Composition;
using System.Text.Json.Nodes;

namespace Cohesive.Tests;

public sealed class ApplicationExplainCommandTests
{
    [Fact]
    public void Normal_host_path_does_not_evaluate_report()
    {
        var command = new ApplicationExplainCommand(["--environment=local"]);
        Assert.Equal(["--environment=local"], command.Arguments);
        Assert.False(command.TryWrite(TextWriter.Null, () => throw new InvalidOperationException()));
    }

    [Fact]
    public void Explain_consumes_only_its_switch_and_writes_once()
    {
        var command = new ApplicationExplainCommand(["--explain", "--EntityState:Authority=Events"]);
        Assert.Equal(["--EntityState:Authority=Events"], command.Arguments);
        using var output = new StringWriter();
        var calls = 0;
        Assert.True(command.TryWrite(output, () => { calls++; return new JsonObject { ["scope"] = "offline" }; }));
        Assert.Equal(1, calls);
        Assert.Equal("offline", JsonNode.Parse(output.ToString())!["scope"]!.GetValue<string>());
    }

    [Fact]
    public void Failed_projection_writes_no_partial_report()
    {
        using var output = new StringWriter();
        var command = new ApplicationExplainCommand(["--explain"]);
        Assert.Throws<ArgumentException>(() => command.TryWrite(output, () => throw new ArgumentException("invalid plan")));
        Assert.Equal("", output.ToString());
    }
}
