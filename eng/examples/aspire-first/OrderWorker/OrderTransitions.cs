using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Transitions.Authoring;
using Cohesive.Transitions.IR;

namespace AspireFirst.Orders;

/// <summary>Requests submission of a specific order.</summary>
/// <param name="OrderId">Must match the loaded order identity.</param>
public sealed record SubmitOrder(string OrderId);

/// <summary>Domain outcome of an order submission attempt.</summary>
/// <param name="Status">Order lifecycle state reported by the decision.</param>
/// <param name="Reason">Explanation of the submission result.</param>
public sealed record SubmitOrderResult(string Status, string Reason);

/// <summary>POCO-authored order lifecycle; preparation belongs to application binding.</summary>
public static class OrderTransitions
{
    /// <summary>Only draft orders may be submitted; a rejected decision produces no commit.</summary>
    public static Transition<Order, SubmitOrder, SubmitOrderResult> Submit { get; } = TransitionAuthoring.Create<Order, SubmitOrder, SubmitOrderResult>(OrderStorage.Entity.Shape,
            new(definitionId: new("example/order/submit"), revisionId: new("2"), bodyId: new("submit-body"),
                provenance: new(new(TransitionAuthoring.Producer), new("example/order/submit"), DocumentOrigin.Generated)),
            transition => transition
                .Requires(new("target-matches"), (order, input) => order.Id == input.OrderId,
                    (order, _) => new SubmitOrderResult(order.Status, "Order identity does not match."))
                .Requires(new("draft-only"), (order, _) => order.Status == "Draft", (order, _) => new SubmitOrderResult(order.Status, "Order must be Draft to submit."))
                .Set(new("submit"), order => order.Status, "Submitted")
                .Return(new("result"), TransitionOutcomeDisposition.Applied, new SubmitOrderResult("Submitted", "Order submitted.")));
}
