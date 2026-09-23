using BenchmarkDotNet.Attributes;
using Cohesive.Model;
using Cohesive.Relations.Execution;

namespace Cohesive.Relations.Benchmarks;

/// <summary>Warm generic mapping operations with immutable inputs prepared outside the measurement.</summary>
[Config(typeof(RelationBenchmarkConfig))]
[MemoryDiagnoser]
public class ExpressionValueContractBenchmarks
{
    readonly RelationQueryExpressionEvaluator evaluator = new();
    readonly RelationQueryExpressionContext context = new();
    Expr expression = null!;

    /// <summary>Short/maximum-precision text and flat/nested/collection-heavy single-item values.</summary>
    [Params("decimal", "decimal-max", "single-flat", "single-nested", "single-collection")]
    public string Workload { get; set; } = "decimal";

    /// <summary>Prepares immutable expressions and retained input values once.</summary>
    [GlobalSetup]
    public void Setup()
    {
        var payload = ObservationValue.FromArray([.. Enumerable.Repeat(ObservationValue.FromString("payload"), 4096)]);
        var value = Workload switch {
            "single-nested" => ObservationValue.FromObject(new Dictionary<string, ObservationValue> { ["nested"] =
                ObservationValue.FromObject(new Dictionary<string, ObservationValue> { ["id"] = ObservationValue.FromString("a") }) }),
            "single-collection" => payload,
            _ => ObservationValue.FromString("a")
        };
        expression = Workload.StartsWith("decimal", StringComparison.Ordinal)
            ? Expr.Call(ExprFunctionNames.ParseDecimal, Expr.Const(Workload == "decimal" ? "0012.50" : "79228162514264337593543950335"))
            : Expr.Call(ExprFunctionNames.Single, Expr.Const(ObservationValue.FromArray([value])));
    }

    /// <summary>Includes normal dispatch; single retains its input payload and decimal returns a scalar value.</summary>
    [Benchmark]
    public ObservationValue Evaluate() => evaluator.Evaluate(expression, context);
}
