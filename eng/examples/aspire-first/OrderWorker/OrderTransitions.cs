using Cohesive.Transitions.Authoring;

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
    public static Transition<Order, SubmitOrder, SubmitOrderResult> Submit { get; } = TransitionAuthoring.Create<Order, SubmitOrder, SubmitOrderResult>(FulfillmentDomain.Orders.Definition.Shape,
            id: new("example/order/submit"), revision: new("3"),
            transition => transition
                .Requires((order, input) => order.Id == input.OrderId,
                    (order, _) => new SubmitOrderResult(order.Status, "Order identity does not match."))
                .Requires((order, _) => order.Status == "Draft", (order, _) => new SubmitOrderResult(order.Status, "Order must be Draft to submit."))
                .Set(order => order.Status, "Submitted")
                .Return(new SubmitOrderResult("Submitted", "Order submitted.")));
}
