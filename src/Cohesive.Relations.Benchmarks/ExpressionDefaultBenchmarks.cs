using BenchmarkDotNet.Attributes;
using Cohesive.Model;
using Cohesive.Relations.Execution;

namespace Cohesive.Relations.Benchmarks;

/// <summary>Warm explicit-default evaluation; immutable inputs and expressions are prepared outside measurement.</summary>
[Config(typeof(RelationBenchmarkConfig))]
[MemoryDiagnoser]
public class ExpressionDefaultBenchmarks
{
    readonly RelationQueryExpressionEvaluator evaluator = new();
    readonly RelationQueryExpressionContext context = new();
    Expr expression = null!;

    /// <summary>Scalar, nested object, and bounded collection payloads retained by the expression.</summary>
    [Params("scalar", "nested", "collection", "large-collection")]
    public string Workload { get; set; } = "scalar";

    /// <summary>Whether evaluation selects the fallback rather than the present first value.</summary>
    [Params(false, true)]
    public bool UseFallback { get; set; }

    /// <summary>Constructs immutable payloads; evaluation returns existing storage without copying it.</summary>
    [GlobalSetup]
    public void Setup()
    {
        var value = ObservationValue.FromInt64(42);
        var depth = Workload == "nested" ? 8 : Workload == "scalar" ? 0 : 2;
        for (var i = 0; i < depth; i++)
            value = ObservationValue.FromObject(new Dictionary<string, ObservationValue> { ["value"] = value });
        if (Workload is "collection" or "large-collection")
            value = ObservationValue.FromArray([.. Enumerable.Repeat(value, Workload == "collection" ? 64 : 4096)]);
        expression = Expr.Coalesce(UseFallback ? Expr.Null() : Expr.Const(value), Expr.Const(value));
    }

    /// <summary>Evaluates one explicit default, including normal function capability dispatch.</summary>
    /// <returns>The retained source or fallback value.</returns>
    [Benchmark]
    public ObservationValue Evaluate() => evaluator.Evaluate(expression, context);
}
