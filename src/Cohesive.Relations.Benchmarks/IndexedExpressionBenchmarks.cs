using BenchmarkDotNet.Attributes;
using Cohesive.Model;
using Cohesive.Relations.Execution;

namespace Cohesive.Relations.Benchmarks;

/// <summary>Warm selected-element projection over immutable flat, nested and collection-heavy payloads.</summary>
[MemoryDiagnoser]
public class IndexedExpressionBenchmarks
{
    [Params(1, 32, 1000)] public int Rows { get; set; }
    [Params("flat", "nested", "collection")] public string Payload { get; set; } = "flat";
    readonly RelationQueryExpressionEvaluator evaluator = new();
    readonly RelationQueryExpressionContext context = new();
    Expr expression = null!;

    [GlobalSetup]
    public void Setup()
    {
        var value = Payload switch
        {
            "nested" => ObservationValue.FromObject(new Dictionary<string, ObservationValue> { ["value"] = ObservationValue.FromString("retained") }),
            "collection" => ObservationValue.FromArray([.. Enumerable.Repeat(ObservationValue.FromString("retained"), 32)]),
            _ => ObservationValue.FromString("retained")
        };
        var row = ObservationValue.FromObject(new Dictionary<string, ObservationValue> { ["value"] = value });
        expression = Expr.Call(ExprFunctionNames.ElementAt,
            Expr.Const(ObservationValue.FromArray([.. Enumerable.Repeat(row, Rows)])), Expr.Const(Rows - 1), Expr.Field("item.value"));
        if (evaluator.Evaluate(expression, context) != value) throw new InvalidOperationException("Projection differs.");
    }

    [Benchmark]
    public ObservationValue SelectOne() => evaluator.Evaluate(expression, context);
}
