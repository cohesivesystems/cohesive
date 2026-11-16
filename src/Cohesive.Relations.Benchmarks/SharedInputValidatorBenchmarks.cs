using BenchmarkDotNet.Attributes;
using Cohesive.Model;

namespace Cohesive.Relations.Benchmarks;

/// <summary>Measures cache population separately from per-request typed projection and warm dispatch.</summary>
[MemoryDiagnoser]
public class SharedInputValidatorBenchmarks
{
    [Params("flat", "nested", "collection", "large")]
    public string Workload { get; set; } = "flat";
    InputValidator shared = null!;
    InputConstraintBenchmarks.Request request = null!;
    InputConstraintBenchmarks.Nested nested = null!;
    InputConstraintBenchmarks.Collection collection = null!;
    ClrShapeGraphBuildResult shapes = null!;

    [GlobalSetup]
    public void Setup()
    {
        shared = InputValidator.Create(o => o.UseDataAnnotations());
        request = new("request-123"); nested = new(request);
        collection = new(Enumerable.Repeat(request, Workload == "large" ? 1024 : 16).ToArray());
        var builder = new ClrShapeGraphBuilder().UseDataAnnotations();
        shapes = Workload switch
        {
            "nested" => builder.AddShape<InputConstraintBenchmarks.Nested>().BuildResult(),
            "collection" or "large" => builder.AddShape<InputConstraintBenchmarks.Collection>().BuildResult(),
            _ => builder.AddShape<InputConstraintBenchmarks.Request>().BuildResult()
        };
        if (!SharedTyped() || !DirectTyped()) throw new InvalidOperationException("Invalid fixture");
    }

    [Benchmark(Baseline = true)]
    public bool DirectTyped() => Workload switch
    {
        "nested" => ObservationValidator.ValidateInput(nested, shapes).IsValid,
        "collection" or "large" => ObservationValidator.ValidateInput(collection, shapes).IsValid,
        _ => ObservationValidator.ValidateInput(request, shapes).IsValid
    };

    bool Validate(InputValidator inputs) => Workload switch
    {
        "nested" => inputs.Validate(nested).IsValid,
        "collection" or "large" => inputs.Validate(collection).IsValid,
        _ => inputs.Validate(request).IsValid
    };

    [Benchmark] public bool SharedTyped() => Validate(shared);
    [Benchmark] public bool ColdCache() => Validate(InputValidator.Create(o => o.UseDataAnnotations()));
}
