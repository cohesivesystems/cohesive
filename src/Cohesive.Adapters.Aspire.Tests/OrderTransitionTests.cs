using Cohesive.Host.Composition;
using AspireFirst.Orders;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Transitions.Execution;
using Cohesive.Transitions.IR;

namespace Cohesive.Adapters.Aspire.Tests;

public sealed class OrderTransitionTests
{
    static readonly AspireFirst.Orders.FulfillmentApplication Model = ModelActivator.Create<AspireFirst.Orders.FulfillmentApplication>();

    [Theory]
    [InlineData(OrderStatus.Draft, TransitionDecisionKind.Applied, SubmissionOutcome.Submitted)]
    [InlineData(OrderStatus.Submitted, TransitionDecisionKind.AdmissionRejected, SubmissionOutcome.NotDraft)]
    public void Submission_requires_draft_and_preserves_input(OrderStatus status, TransitionDecisionKind expected, SubmissionOutcome outcomeKind)
    {
        var compilation = Model.Domain.OrderTransitions.Submit.Compile();
        Assert.True(compilation.IsSuccessful, string.Join("; ", compilation.Validation.Diagnostics));
        var plan = compilation.Plan!;
        var order = new Order("order-1", FulfillmentStoragePolicy.LocalPartition, status);
        var decision = TransitionReferenceInterpreter.DecideFullState(plan, new("test/submit"),
            PortableValue.Concrete(plan.Definition.Input, ObservationValue.FromObject(Cohesive.Prelude.Unit.Value)),
            PortableValue.Concrete(plan.Definition.Observation, ObservationValue.FromObject(order)));
        Assert.Equal(expected, decision.Kind);
        var outcome = decision.Outcome!.Value!.Value;
        Assert.Equal(expected == TransitionDecisionKind.Applied ? "Submitted" : status.ToString(),
            outcome.GetProperty("Status").GetRequiredString());
        Assert.Equal(outcomeKind.ToString(),
            outcome.GetProperty("Outcome").GetRequiredString());
        Assert.Equal(status, order.Status);
        Assert.Equal(expected == TransitionDecisionKind.Applied, decision.GuaranteeDemands.CommitRequired);
    }
}
