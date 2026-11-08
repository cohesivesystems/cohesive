using Cohesive.Model;
using Cohesive.Relations.Execution;

namespace Cohesive.Relations.Tests;

public sealed class IndexedExpressionTests
{
    [Fact]
    public void Reference_projection_retains_selected_payload_and_does_not_allocate_a_result_array()
    {
        var evaluator = new RelationQueryExpressionEvaluator();
        var context = new RelationQueryExpressionContext();
        var payload = ObservationValue.FromArray([ObservationValue.FromString("retained")]);
        var row = ObservationValue.FromObject(new Dictionary<string, ObservationValue> { ["value"] = payload });
        var expression = Expr.Call(ExprFunctionNames.ElementAt,
            Expr.Const(ObservationValue.FromArray([.. Enumerable.Repeat(row, 1000)])), Expr.Const(999), Expr.Field("item.value"));
        for (var i = 0; i < 100; i++) Assert.Equal(payload, evaluator.Evaluate(expression, context));
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++) _ = evaluator.Evaluate(expression, context);
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - allocated, 0, 256_000);
        Assert.Throws<RelationQueryExpressionEvaluationException>(() => evaluator.Evaluate(Expr.Call(ExprFunctionNames.ElementAt,
            Expr.Const(ObservationValue.FromArray([])), Expr.Const(0), Expr.CurrentItem()), context));
    }

}
