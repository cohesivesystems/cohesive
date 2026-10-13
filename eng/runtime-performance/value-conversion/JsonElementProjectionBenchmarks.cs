using System.Collections.Immutable;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using Cohesive.Model;

[MemoryDiagnoser]
[ShortRunJob]
public class JsonElementProjectionBenchmarks
{
    JsonDocument document = null!;

    [Params("Tiny", "Small", "Medium", "Flat", "Nested", "Rows", "Large")]
    public string Shape { get; set; } = "Flat";

    [GlobalSetup]
    public void Setup()
    {
        object payload = Shape switch
        {
            "Tiny" => Fields(4),
            "Small" => Fields(8),
            "Medium" => Fields(16),
            "Flat" => Fields(128),
            "Nested" => Enumerable.Range(0, 128).ToDictionary(i => $"item{i}", i => Fields(4)),
            "Rows" => Enumerable.Range(0, 4096).Select(i => new { z = i, a = "value", nested = Fields(4) }).ToArray(),
            "Large" => Fields(4096),
            _ => throw new InvalidOperationException(Shape)
        };
        document = JsonSerializer.SerializeToDocument(payload);
    }

    static Dictionary<string, int> Fields(int count) => Enumerable.Range(0, count).Reverse()
        .ToDictionary(i => $"field{i:D4}", i => i);

    [GlobalCleanup]
    public void Cleanup() => document.Dispose();

    [Benchmark(Baseline = true)]
    public ObservationValue SortedTree() => Previous(document.RootElement);

    [Benchmark]
    public ObservationValue OwnedFields() => ObservationValue.FromJsonElement(document.RootElement);

    // Former object storage; scalar and array semantics are identical in both paths.
    static ObservationValue Previous(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var fields = ImmutableSortedDictionary.CreateBuilder<string, ObservationValue>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject()) fields[property.Name] = Previous(property.Value);
            return ObservationValue.FromObject(fields.ToImmutable());
        }
        if (element.ValueKind == JsonValueKind.Array)
        {
            var values = ImmutableArray.CreateBuilder<ObservationValue>(element.GetArrayLength());
            foreach (var value in element.EnumerateArray()) values.Add(Previous(value));
            return ObservationValue.FromImmutableArray(values.MoveToImmutable());
        }
        return ObservationValue.FromJsonElement(element);
    }
}
