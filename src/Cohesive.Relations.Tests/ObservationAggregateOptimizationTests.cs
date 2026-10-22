namespace Cohesive.Relations.Tests;

public sealed class ObservationAggregateOptimizationTests
{
    readonly RelationQueryExpressionEvaluator evaluator = new();
    readonly RelationQueryExpressionContext context = new();

    [Theory]
    [InlineData(ExprFunctionNames.Sum)]
    [InlineData(ExprFunctionNames.Avg)]
    [InlineData(ExprFunctionNames.Min)]
    [InlineData(ExprFunctionNames.Max)]
    public void SelectorsFinishBeforeReduction_AndScratchIsSafeAfterFailure(string function)
    {
        var selector = Expr.Field("item.child.value");
        var invalidNumber = Object(ObservationValue.FromString("not numeric"));
        // Reading the second item fails during selector evaluation, before the first selected
        // string could fail numeric reduction. Fusion must not change this diagnostic ordering.
        var invalidTraversal = ObservationValue.FromObject(new Dictionary<string, ObservationValue> { ["child"] = ObservationValue.FromBool(true) });
        var source = Expr.Const(ObservationValue.FromArray([invalidNumber, invalidTraversal]));
        var error = Assert.Throws<RelationQueryExpressionEvaluationException>(() =>
            evaluator.Evaluate(Expr.Call(function, source, selector), context));
        Assert.Equal(RelationQueryExpressionEvaluationError.InvalidOperand, error.Error);
        Assert.Contains("cannot navigate", error.Message);
        var valid = Expr.Const(ObservationValue.FromArray([Object(ObservationValue.FromInt64(2)), Object(ObservationValue.FromInt64(4))]));
        var result = evaluator.Evaluate(Expr.Call(function, valid, selector), context);
        Assert.Equal(function switch { ExprFunctionNames.Sum => 6m, ExprFunctionNames.Avg => 3m, ExprFunctionNames.Min => 2m, _ => 4m }, result.GetDecimal());
    }

    [Theory]
    [InlineData(ExprFunctionNames.Sum)]
    [InlineData(ExprFunctionNames.Avg)]
    public void NumericOverflow_RemainsTheDeclaredAggregateFailure(string function)
    {
        var source = Expr.Const(ObservationValue.FromArray([ObservationValue.FromDecimal(decimal.MaxValue), ObservationValue.FromInt64(1)]));
        var error = Assert.Throws<RelationQueryExpressionEvaluationException>(() =>
            evaluator.Evaluate(Expr.Call(function, source), context));
        Assert.Equal(RelationQueryExpressionEvaluationError.NumericFailure, error.Error);
        Assert.IsType<OverflowException>(error.InnerException);
    }

    [Fact]
    public void FieldLookupUsesCanonicalOrdinalNamesEvenForCallerCaseInsensitiveInput()
    {
        var source = ObservationValue.FromObject(new Dictionary<string, ObservationValue>(StringComparer.OrdinalIgnoreCase)
            { ["Amount"] = ObservationValue.FromInt64(7) });
        var itemContext = context.WithCurrentItem(source);
        Assert.Equal(7, evaluator.Evaluate(Expr.Field("item.Amount"), itemContext).Int64);
        Assert.Equal(ObservationValueKind.Undefined, evaluator.Evaluate(Expr.Field("item.amount"), itemContext).Kind);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WarmAggregates_DoNotAllocateInProportionToSourceCardinality(bool selector)
    {
        var values = new ObservationValue[4096];
        for (var index = 0; index < values.Length; index++)
            values[index] = selector ? Object(ObservationValue.FromInt64(1)) : ObservationValue.FromInt64(1);
        var source = Expr.Const(ObservationValue.FromArray(values));
        var expression = selector ? Expr.Call(ExprFunctionNames.Sum, source, Expr.Field("item.child.value")) : Expr.Call(ExprFunctionNames.Sum, source);
        for (var warm = 0; warm < 32; warm++) _ = evaluator.Evaluate(expression, context);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var result = evaluator.Evaluate(expression, context);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(values.Length, result.Int64);
        Assert.InRange(allocated, 0, 256);
    }

    static ObservationValue Object(ObservationValue value) => ObservationValue.FromObject(new Dictionary<string, ObservationValue>
        { ["child"] = ObservationValue.FromObject(new Dictionary<string, ObservationValue> { ["value"] = value }) });
}
