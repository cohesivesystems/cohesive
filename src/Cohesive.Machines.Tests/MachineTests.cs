using Cohesive.Machines;

namespace Cohesive.Machines.Tests;

public class MachineTests
{
    static MachineBuilder Example() => new MachineBuilder("order", "1")
        .State("draft").State("done").Initial("draft").Final("done")
        .Edge("complete", "draft", "done");

    [Fact]
    public void SelectionIsPureAndDistinguishesIllegalFromUnknown()
    {
        var machine = Example().Build();
        Assert.Equal("done", machine.Decide("draft", "complete")!.To);
        Assert.Equal("draft", machine.Initial);
        Assert.Null(machine.Decide("done", "complete"));
        Assert.Throws<ArgumentException>(() => machine.Decide("draft", "missing"));
        Assert.Throws<ArgumentException>(() => machine.Enabled("missing"));
    }

    [Fact]
    public void CanonicalizationIsIndependentOfDeclarationOrderAndDetachedFromSource()
    {
        var builder = Example();
        var first = builder.Build();
        var second = new MachineBuilder("order", "1").State("done").State("draft")
            .Final("done").Initial("draft").Edge("complete", "draft", "done").Build();
        Assert.Equal(first.Document.Definition.GetRawText(), second.Document.Definition.GetRawText());
        Assert.Equal(first.Fingerprint, second.Fingerprint);
        builder.State("unreachable");
        Assert.Equal(2, first.States.Length);
        Assert.Contains(builder.Validate(), d => d.Code == "machines.state.unreachable");
    }

    [Fact]
    public void MalformedSourceRemainsInspectableButCannotBuild()
    {
        var builder = Example().Edge("again", "draft", "done", action: "complete")
            .Edge("exit", "done", "unknown");
        var diagnostics = builder.Validate();
        Assert.Contains(diagnostics, d => d.Code == "machines.selection.ambiguous");
        Assert.Contains(diagnostics, d => d.Code == "machines.terminal.outgoing");
        Assert.Contains(diagnostics, d => d.Code == "machines.state.unknown");
        Assert.Throws<InvalidOperationException>(() => builder.Build());
    }

    [Fact]
    public void CyclesAreLegalWithoutATerminationClaim()
    {
        var machine = new MachineBuilder("loop", "1").State("ready").Initial("ready")
            .Edge("tick", "ready", "ready").Build();
        Assert.Equal("ready", machine.Decide("ready", "tick")!.To);
    }

    [Fact]
    public void ProjectionRequiresExplicitComposablePaths()
    {
        var source = Example().Build();
        var target = Example().Build();
        var mapping = new MachineProjectionBuilder(source, target)
            .State("draft", "draft").State("done", "done");
        Assert.Contains(mapping.Validate(), d => d.Code == "machines.projection.edge.missing");
        mapping.Edge("complete");
        Assert.Contains(mapping.Validate(), d => d.Code == "machines.projection.path.target");
        Assert.Empty(new MachineProjectionBuilder(source, target)
            .State("draft", "draft").State("done", "done").Edge("complete", "complete").Validate());
    }
}
