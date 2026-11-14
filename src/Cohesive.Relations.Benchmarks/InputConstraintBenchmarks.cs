using System.ComponentModel.DataAnnotations;
using BenchmarkDotNet.Attributes;
using Cohesive.Model;

namespace Cohesive.Relations.Benchmarks;

/// <summary>Separates authoring/projection cost from repeated constraint admission.</summary>
[MemoryDiagnoser]
public class InputConstraintBenchmarks
{
    [Params("flat", "nested", "collection", "large")]
    public string Workload { get; set; } = "flat";
    [Params(false, true)]
    public bool Constrained { get; set; }
    GraphShapeId shape;
    ObservationValue value;
    Type root = null!;

    public sealed record Request([property: Required, MaxLength(64)] string RequestId);
    public sealed record Nested(Request Child);
    public sealed record Collection([property: MaxLength(1024)] Request[] Items);

    [GlobalSetup]
    public void Setup()
    {
        var request = new Request("request-123");
        object input = Workload switch
        {
            "nested" => new Nested(request),
            "collection" => new Collection(Enumerable.Repeat(request, 16).ToArray()),
            "large" => new Collection(Enumerable.Repeat(request, 1024).ToArray()),
            _ => request
        };
        root = input.GetType();
        shape = Build(); value = ObservationValue.FromObject(input);
        if (!WarmAdmission()) throw new InvalidOperationException("Invalid benchmark fixture.");
    }

    GraphShapeId Build()
    {
        var builder = new ClrShapeGraphBuilder();
        if (Constrained) builder.UseDataAnnotations();
        var graph = builder.AddShape(root).Build();
        return new(graph, graph.Shapes[0].Id);
    }

    [Benchmark]
    public bool WarmAdmission() => ObservationValidator.ValidateInput(value, shape).IsValid;

    [Benchmark]
    public bool FreshGraphAdmission() => ObservationValidator.ValidateInput(value, Build()).IsValid;
}
