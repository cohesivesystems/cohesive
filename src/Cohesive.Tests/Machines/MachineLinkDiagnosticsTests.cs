using System.Linq.Expressions;
using Cohesive.Machines;
using Cohesive.Transitions.Compilation;
using Cohesive.Transitions.IR;
using Cohesive.Transitions.Machines;

namespace Cohesive.Tests.Machines;

public sealed class MachineLinkDiagnosticsTests
{
    public sealed record Facts(string Status, int Choice);
    public sealed record Input(int Choice, bool Allowed);

    [Theory]
    [InlineData(0, false)] // Different equality constants, including reversed operands and conjunction.
    [InlineData(1, true)]  // Facts and input are different bindings.
    [InlineData(2, true)]  // A disjunction does not imply either equality.
    [InlineData(3, true)]  // Equal constants do not prove disjointness.
    public void OnlyNecessaryContradictoryConstraintsSuppressWarnings(int variant, bool warns)
    {
        Expression<Func<Facts, Input, bool>> other = variant switch
        {
            0 => (facts, input) => 2 == facts.Choice && input.Allowed,
            1 => (facts, input) => input.Choice == 2,
            2 => (facts, input) => facts.Choice == 2 || input.Allowed,
            _ => (facts, input) => facts.Choice == 1
        };
        var source = Machine.Define("diagnostics", "1");
        var ready = source.State("ready").Initial();
        source.Edge("first").On("choose").From(ready).To(ready)
            .When((Facts facts, Input input) => facts.Choice == 1);
        var second = source.Edge("second").On("choose").From(ready).To(ready).When(other);
        var unrelated = source.Edge("unrelated").From(ready).To(ready);
        var links = new MachineDiscriminatorBinding<Facts, string>(source.Build(), facts => facts.Status)
            .Map(ready, "ready").Build();
        Assert.Equal(warns, links.Diagnostics.Any());
        var entity = ObjectEntityDefinition.For<Facts>(new("facts"));
        foreach (var movement in new[] { second, unrelated })
        {
            var transition = TransitionAuthoring.Create<Facts, Input, string>(entity.Shape, new(movement.Id), new("1"),
                t => t.Move(movement, (facts, input) => "rejected")
                    .Return(new("result"), TransitionOutcomeDisposition.Applied, "ok"));
            var compiled = TransitionStaticCompiler.Compile(transition.Document, machineLinks: links);
            Assert.True(compiled.IsSuccessful);
            Assert.Equal(warns && movement == second,
                compiled.Validation.Diagnostics.Any(d => d.Code == "machines.linking.disjointnessUnknown"));
        }
    }

    [Fact]
    public void ConstantTrueSiblingGuardsFailAtLinkTime()
    {
        var source = Machine.Define("overlap", "1");
        var state = source.State("ready").Initial();
        source.Edge("first").On("choose").From(state).To(state).When((Facts f, Input i) => true);
        source.Edge("second").On("choose").From(state).To(state).When((Facts f, Input i) => true);
        Assert.Throws<ArgumentException>(() => new MachineDiscriminatorBinding<Facts, string>(source.Build(), f => f.Status)
            .Map(state, "ready").Build());
    }
}
