using AspireFirst.Orders;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Transitions.Execution;
using Cohesive.Transitions.IR;

namespace Cohesive.Adapters.Aspire.Tests;

public sealed class OrderTransitionTests
{
    [Theory]
    [InlineData("Draft", "order-1", TransitionDecisionKind.Applied, "Order submitted.")]
    [InlineData("Submitted", "order-1", TransitionDecisionKind.AdmissionRejected, "Order must be Draft to submit.")]
    [InlineData("Draft", "other-order", TransitionDecisionKind.AdmissionRejected, "Order identity does not match.")]
    public void Submission_requires_draft_and_preserves_input(string status, string target, TransitionDecisionKind expected, string reason)
    {
        var compilation = OrderTransitions.Submit.Compile();
        Assert.True(compilation.IsSuccessful, string.Join("; ", compilation.Validation.Diagnostics));
        var plan = compilation.Plan!;
        var order = new Order("order-1", FulfillmentDemo.LocalPartition, status);
        var decision = TransitionReferenceInterpreter.DecideFullState(plan, new("test/submit"),
            PortableValue.Concrete(plan.Definition.Input, ObservationValue.FromObject(new SubmitOrder(target))),
            PortableValue.Concrete(plan.Definition.Observation, ObservationValue.FromObject(order)));
        Assert.Equal(expected, decision.Kind);
        var outcome = decision.Outcome!.Value!.Value;
        Assert.Equal(expected == TransitionDecisionKind.Applied ? "Submitted" : status,
            outcome.GetProperty("Status").GetRequiredString());
        Assert.Equal(reason,
            outcome.GetProperty("Reason").GetRequiredString());
        Assert.Equal(status, order.Status);
        Assert.Equal(expected == TransitionDecisionKind.Applied, decision.GuaranteeDemands.CommitRequired);
    }
}
