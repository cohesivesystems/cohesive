using BenchmarkDotNet.Attributes;
using Cohesive.Model;
using Cohesive.Relations.Execution;

namespace Cohesive.Relations.Benchmarks;

/// <summary>Warm key-filter evaluation, including normal expression dispatch; preparation is excluded.</summary>
[Config(typeof(RelationBenchmarkConfig))]
[MemoryDiagnoser]
public class ExpressionJoinBenchmarks
{
    readonly RelationQueryExpressionEvaluator evaluator = new();
    readonly RelationQueryExpressionContext context = new();
    Expr expression = null!;

    /// <summary>Scalar, nested, collection-heavy and bounded-large input shapes.</summary>
    [Params("scalar", "nested", "collection", "large-collection")]
    public string Workload { get; set; } = "scalar";

    /// <summary>Whether half the source items match; false measures no-match traversal.</summary>
    [Params(false, true)]
    public bool Matches { get; set; }

    /// <summary>Builds immutable inputs outside the measured operation.</summary>
    [GlobalSetup]
    public void Setup()
    {
        var count = Workload == "large-collection" ? 4096 : 64;
        var payload = ObservationValue.FromArray([.. Enumerable.Repeat(ObservationValue.FromString("payload"), 64)]);
        var values = new ObservationValue[count];
        for (var index = 0; index < count; index++)
        {
            var key = ObservationValue.FromInt64(Matches && index % 2 == 0 ? 1 : 2);
            values[index] = Workload switch
            {
                "scalar" => key,
                "nested" => ObservationValue.FromObject(new Dictionary<string, ObservationValue> {
                    ["nested"] = ObservationValue.FromObject(new Dictionary<string, ObservationValue> { ["key"] = key }) }),
                _ => ObservationValue.FromObject(new Dictionary<string, ObservationValue> { ["key"] = key, ["payload"] = payload })
            };
        }
        var selector = Workload == "scalar" ? Expr.CurrentItem()
            : Expr.Field(Workload == "nested" ? "item.nested.key" : "item.key");
        expression = Expr.Join(Expr.Const(1), selector, Expr.Const(ObservationValue.FromArray(values)));
    }

    /// <summary>Traverses each source item once and retains matching values without copying their payloads.</summary>
    /// <returns>The ordered matching values.</returns>
    [Benchmark]
    public ObservationValue Evaluate() => evaluator.Evaluate(expression, context);
}
