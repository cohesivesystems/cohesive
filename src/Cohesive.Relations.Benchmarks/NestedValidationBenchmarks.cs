using System.Collections.Immutable;
using BenchmarkDotNet.Attributes;
using Cohesive.Model;

namespace Cohesive.Relations.Benchmarks;

/// <summary>Repeated instance checks; immutable types and instance values are constructed outside execution.</summary>
[MemoryDiagnoser]
public class NestedValidationBenchmarks
{
    TypeRef type = null!;
    ObservationValue value;

    [Params("flat", "nested", "collection", "large")]
    public string Shape { get; set; } = "flat";
    [Params("exact", "case", "unknown")]
    public string Input { get; set; } = "exact";

    [GlobalSetup]
    public void Setup()
    {
        var count = Shape == "large" ? 128 : 16;
        type = new ObjectTypeRef([..Enumerable.Range(0, count).Select(i =>
            new ObjectFieldTypeDef($"field{i}", new ScalarTypeRef(ScalarTypeKind.String)))]);
        var fields = ImmutableDictionary.CreateBuilder<string, ObservationValue>(StringComparer.Ordinal);
        for (var i = 0; i < count; i++) fields.Add(Input == "case" ? $"FIELD{i}" : $"field{i}", ObservationValue.FromString("value"));
        if (Input == "unknown") fields.Add("unknown", ObservationValue.FromString("value"));
        value = ObservationValue.FromObject(fields.ToImmutable());
        if (Shape == "nested")
            for (var i = 0; i < 8; i++)
            {
                type = new ObjectTypeRef([new("child", type)]);
                value = ObservationValue.FromObject(ImmutableDictionary<string, ObservationValue>.Empty.Add("child", value));
            }
        if (Shape == "collection")
        {
            type = new ArrayTypeRef(type);
            value = ObservationValue.FromArray([..Enumerable.Repeat(value, 32)]);
        }
        for (var i = 0; i < 64; i++) _ = Validate();
    }

    [Benchmark]
    public bool Validate() => ObservationValidator.TryValidateAgainstType(value, type, out _);
}
