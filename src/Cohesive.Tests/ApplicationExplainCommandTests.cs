using Cohesive.Host.Composition;

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
    public void Failed_projection_writes_no_partial_report()
    {
        using var output = new StringWriter();
        var command = new ApplicationExplainCommand(["--explain"]);
        Assert.Throws<ArgumentException>(() => command.TryWrite(output, () => throw new ArgumentException("invalid plan")));
        Assert.Equal("", output.ToString());
    }
}
