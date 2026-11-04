using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Transitions.Authoring;
using Cohesive.Transitions.IR;

namespace DevelopmentMcp.Example;

/// <summary>Small authoring fixture shared by the executable example and the MCP primer.</summary>
public static class OrderModel
{
    /// <summary>Owning source reference retained by lowering.</summary>
    public const string Source = "eng/examples/development-mcp/OrderModel.cs";

    /// <summary>Lowers an order cancellation declaration using ordinary typed source.</summary>
    /// <param name="revision">Exact authoring input revision; the exporter supplies its declared-input digest.</param>
    /// <returns>The derived canonical document with authoring provenance and source mappings.</returns>
    public static ExecutionDefinitionDocument Create(string revision = "example/revision-1")
    {
        var entity = ObjectEntityDefinition.For<Order>(new("orders"));
        var transition = TransitionAuthoring.Create<Order, CancelOrder, string>(
            entity.Shape,
            new TransitionAuthoringMetadata(
                new("orders/cancel"), new(revision), new("orders/cancel/body"),
                new(new(TransitionAuthoring.Producer), new(Source), DocumentOrigin.User),
                displayName: "Cancel order"),
            t => t
                .Requires(new("orders/cancel/eligible"), (order, input) => order.Status == "pending", (_, _) => "notPending")
                .Set(new("orders/cancel/status"), order => order.Status, "cancelled")
                .Return(new("orders/cancel/result"), TransitionOutcomeDisposition.Applied, "cancelled"));
        return transition.Document;
    }
}

/// <summary>Authoritative observation for the example.</summary>
/// <param name="Status">Current order lifecycle state.</param>
public sealed record Order(string Status);

/// <summary>Cancellation input.</summary>
/// <param name="Reason">Caller-provided reason; not yet persisted by this example.</param>
public sealed record CancelOrder(string Reason);
