using Cohesive.Execution;
using Cohesive.Model.Serialization;
using Cohesive.Transitions.Authoring;
using Cohesive.Transitions.Compilation;
using Cohesive.Transitions.IR;

namespace AspireFirst.Orders;

/// <summary>POCO-authored order lifecycle, compiled once for reuse by the HTTP binding.</summary>
public static class OrderTransitions
{
    /// <summary>Only draft orders may be submitted; a rejected decision produces no commit.</summary>
    public static CompiledTransitionPlan Submit { get; } = CompileSubmit();

    static CompiledTransitionPlan CompileSubmit()
    {
        var compilation = TransitionAuthoring.Create<Order, bool, string>(OrderStorage.Entity.Shape,
            new(definitionId: new("example/order/submit"), revisionId: new("1"), bodyId: new("submit-body"),
                provenance: new(new(TransitionAuthoring.Producer), new("example/order/submit"), DocumentOrigin.Generated)),
            transition => transition
                .Requires(new("draft-only"), (order, _) => order.Status == "Draft", (_, _) => "Already submitted")
                .Set(new("submit"), order => order.Status, "Submitted")
                .Return(new("result"), TransitionOutcomeDisposition.Applied, "Submitted"))
            .Compile();
        return compilation.Plan ?? throw new InvalidOperationException(string.Join("; ", compilation.Validation.Diagnostics));
    }
}
