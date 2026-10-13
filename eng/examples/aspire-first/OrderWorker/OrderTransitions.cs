using Cohesive.Transitions.Authoring;
using System.Text.Json.Serialization;

namespace AspireFirst.Orders;

/// <summary>Submit the subject selected by the invocation binding; contains no competing subject ID.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SubmitOrder>))]
public enum SubmitOrder
{
    /// <summary>Submit the selected order.</summary>
    [JsonStringEnumMemberName("Submit")] Submit
}

/// <summary>Closed submission outcomes, independent of transport messages and status codes.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SubmissionOutcome>))]
public enum SubmissionOutcome
{
    /// <summary>The order was submitted.</summary>
    [JsonStringEnumMemberName("Submitted")] Submitted,
    /// <summary>The current lifecycle does not permit submission.</summary>
    [JsonStringEnumMemberName("NotDraft")] NotDraft
}

/// <summary>Domain outcome of an order submission attempt.</summary>
/// <param name="Status">Order lifecycle state reported by the decision.</param>
/// <param name="Outcome">Stable closed outcome; presentation text belongs to the API projection.</param>
public sealed record SubmitOrderResult(OrderStatus Status, SubmissionOutcome Outcome);

/// <summary>POCO-authored order lifecycle; preparation belongs to application binding.</summary>
public static class OrderTransitions
{
    /// <summary>Only draft orders may be submitted; a rejected decision produces no commit.</summary>
    public static Transition<Order, SubmitOrder, SubmitOrderResult> Submit { get; } = TransitionAuthoring.Create<Order, SubmitOrder, SubmitOrderResult>(FulfillmentDomain.Orders.Definition.Shape,
            id: new("example/order/submit"), revision: new("4"),
            transition => transition
                .Requires((order, _) => order.Status == OrderStatus.Draft,
                    (order, _) => new SubmitOrderResult(order.Status, SubmissionOutcome.NotDraft))
                .Set(order => order.Status, OrderStatus.Submitted)
                .Return(new SubmitOrderResult(OrderStatus.Submitted, SubmissionOutcome.Submitted)));
}
