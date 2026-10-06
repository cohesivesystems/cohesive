using AspireFirst.Orders;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Transitions.Execution;
using Cohesive.Transitions.IR;

namespace Cohesive.Adapters.Aspire.Tests;

public sealed class OrderTransitionTests
{
    [Theory]
    [InlineData("Draft", TransitionDecisionKind.Applied)]
    [InlineData("Submitted", TransitionDecisionKind.AdmissionRejected)]
    public void Submission_requires_draft_and_preserves_input(string status, TransitionDecisionKind expected)
    {
        var compilation = OrderTransitions.Submit.Compile();
        Assert.True(compilation.IsSuccessful);
        var plan = compilation.Plan!;
        var order = new Order("order-1", OrderStorage.LocalPartition, status);
        var decision = TransitionReferenceInterpreter.DecideFullState(plan, new("test/submit"),
            PortableValue.Concrete(plan.Definition.Input, ObservationValue.FromBool(true)),
            PortableValue.Concrete(plan.Definition.Observation, ObservationValue.FromObject(order)));
        Assert.Equal(expected, decision.Kind);
        Assert.Equal(status, order.Status);
        Assert.Equal(expected == TransitionDecisionKind.Applied, decision.GuaranteeDemands.CommitRequired);
    }
}
