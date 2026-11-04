using Cohesive.Execution;
using Cohesive.Machines;
using Cohesive.Transitions.Compilation;
using Cohesive.Transitions.Execution;
using Cohesive.Transitions.IR;
using Cohesive.Transitions.Machines;

namespace Cohesive.Tests.Machines;

public sealed class GuardedDiscriminatorTests
{
    public sealed record Order(string Status, int ItemCount, string SubmittedBy);
    public sealed record SubmitOrder(string Actor, bool AcceptTerms);

    [Theory]
    [InlineData("draft", 1, true, false, true)]
    [InlineData("draft", 0, true, false, false)]
    [InlineData("draft", 1, false, false, false)]
    [InlineData("submitted", 1, true, false, false)]
    [InlineData("draft", 1, true, true, true)]
    [InlineData("draft", 2, true, true, false)]
    public void GuardedMovementOwnsPreconditionsAndDiscriminatorAssignment(
        string status, int itemCount, bool acceptTerms, bool competingEdge, bool accepted)
    {
        var source = Machine.Define("order.lifecycle", revision: "1");
        var draft = source.State("draft").Initial();
        var submitted = source.State("submitted").Final();
        var submit = source.Edge("submit").From(draft).To(submitted)
            .When((Order order, SubmitOrder command) => order.ItemCount > 0 && command.AcceptTerms);
        if (competingEdge)
            source.Edge("alternate-submit").On("submit").From(draft).To(submitted)
                .When((Order order, SubmitOrder command) => order.ItemCount >= 2 && command.AcceptTerms);
        var lifecycle = source.Build();
        var links = new MachineDiscriminatorBinding<Order, string>(lifecycle, order => order.Status)
            .Map(draft, "draft").Map(submitted, "submitted").Build();
        var entity = ObjectEntityDefinition.For<Order>(new("order"));
        var transition = TransitionAuthoring.Create<Order, SubmitOrder, string>(
            entity.Shape, new("order.submit"), new("1"),
            t => t.Move(submit, rejection: (order, command) => "Cannot submit")
                .Set(new("record-submitter"), order => order.SubmittedBy, (order, command) => command.Actor)
                .Return(new("result"), TransitionOutcomeDisposition.Applied, "Submitted"));
        var compiled = TransitionStaticCompiler.Compile(transition.Document, machineLinks: links);
        Assert.True(compiled.IsSuccessful, string.Join(";", compiled.Validation.Diagnostics.Select(d => d.Message)));
        var order = new Order(status, itemCount, "");
        var command = new SubmitOrder("alice", acceptTerms);
        var facts = ObservationValue.FromObject(order);
        var input = ObservationValue.FromObject(command);
        var standalone = lifecycle.Evaluate(status, "submit", facts, input);
        Assert.Equal(accepted, standalone.Kind == MachineDecisionKind.Accepted);
        if (competingEdge && itemCount >= 2) Assert.Equal(MachineDecisionKind.Ambiguous, standalone.Kind);
        var decision = TransitionReferenceInterpreter.DecideFullState(compiled.Plan!, new("operation"),
            PortableValue.Concrete(transition.Definition.Input, input),
            PortableValue.Concrete(transition.Definition.Observation, facts));
        Assert.Equal(accepted ? TransitionDecisionKind.Applied : TransitionDecisionKind.AdmissionRejected, decision.Kind);
        if (accepted)
        {
            var original = entity.CreateState("order-1", order, version: 1);
            var candidate = TransitionStateProjector.ApplyToEntity(entity, "order-1", decision, original.Snapshot);
            Assert.Equal("submitted", candidate.Fields["Status"].GetString());
            Assert.Equal("alice", candidate.Fields["SubmittedBy"].GetString());
            Assert.Single(decision.MachineMovements);
            Assert.Equal("draft", original.Fields["Status"].GetString());
        }
        else
        {
            Assert.Empty(decision.Patch);
            Assert.Empty(decision.MachineMovements);
            Assert.Empty(decision.Emissions);
        }
    }
}
