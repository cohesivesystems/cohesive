using System.Collections.Immutable;
using BenchmarkDotNet.Attributes;
using Cohesive.Model;

namespace Cohesive.Relations.Benchmarks;

/// <summary>Graph-bound validation with reused named children; graph/value construction is excluded.</summary>
[MemoryDiagnoser]
public class NamedValidationBenchmarks
{
    TypeRef type = null!;
    ShapeGraph graph = null!;
    ObservationValue value;

    [Params("flat", "nested", "collection", "large")]
    public string Shape { get; set; } = "flat";

    [GlobalSetup]
    public void Setup()
    {
        var count = Shape == "large" ? 128 : 16;
        List<TypeDefinition> definitions = [new TypeDefinition.Enum(new("text"), PrimitiveType.String, [new("value", "value")])];
        definitions.Add(new TypeDefinition.Structural(new("root"), [..Enumerable.Range(0, count).Select(i =>
            new StructuralField(new($"field{i}"), new NamedTypeRef(new("text"))))]));
        type = new NamedTypeRef(new("root"));
        value = ObservationValue.FromObject(Enumerable.Range(0, count).ToImmutableDictionary(
            i => $"field{i}", _ => ObservationValue.FromString("value")));
        if (Shape == "nested")
            for (var i = 0; i < 8; i++)
            {
                var id = new TypeId($"parent{i}");
                definitions.Add(new TypeDefinition.Structural(id, [new(new("child"), type)]));
                type = new NamedTypeRef(id);
                value = ObservationValue.FromObject(ImmutableDictionary<string, ObservationValue>.Empty.Add("child", value));
            }
        if (Shape == "collection")
        {
            type = new ArrayTypeRef(type);
            value = ObservationValue.FromArray([..Enumerable.Repeat(value, 32)]);
        }
        graph = new(new("benchmark"), [], [..definitions]);
        for (var i = 0; i < 64; i++) _ = Validate();
    }

    [Benchmark]
    public bool Validate() => ObservationValidator.TryValidateAgainstType(value, type, out _, graph);
}
