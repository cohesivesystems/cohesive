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
    object input = null!;
    ClrShapeGraphBuildResult shapes = null!;

    public sealed record Request([property: Required, MaxLength(64)] string RequestId);
    public sealed record Nested(Request Child);
    public sealed record Collection([property: MaxLength(1024)] Request[] Items);

    [GlobalSetup]
    public void Setup()
    {
        var request = new Request("request-123");
        input = Workload switch
        {
            "nested" => new Nested(request),
            "collection" => new Collection(Enumerable.Repeat(request, 16).ToArray()),
            "large" => new Collection(Enumerable.Repeat(request, 1024).ToArray()),
            _ => request
        };
        root = input.GetType();
        shapes = Build();
        shape = new(shapes.Graph, shapes.Graph.Shapes[0].Id); value = ObservationValue.FromObject(input);
        if (!WarmAdmission()) throw new InvalidOperationException("Invalid benchmark fixture.");
    }

    ClrShapeGraphBuildResult Build()
    {
        var builder = new ClrShapeGraphBuilder();
        if (Constrained) builder.UseDataAnnotations();
        return builder.AddShape(root).BuildResult();
    }

    [Benchmark]
    public bool WarmAdmission() => ObservationValidator.ValidateInput(value, shape).IsValid;

    // Includes record-to-observation materialization on every call; no cached input projection.
    [Benchmark]
    public bool TypedAdmission() => input switch
    {
        Request request => ObservationValidator.ValidateInput(request, shapes).IsValid,
        Nested nested => ObservationValidator.ValidateInput(nested, shapes).IsValid,
        Collection collection => ObservationValidator.ValidateInput(collection, shapes).IsValid,
        _ => throw new InvalidOperationException()
    };

    [Benchmark]
    public bool FreshGraphAdmission()
    {
        var built = Build();
        return ObservationValidator.ValidateInput(value, new GraphShapeId(built.Graph, built.Graph.Shapes[0].Id)).IsValid;
    }
}
