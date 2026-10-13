using System.Collections.Immutable;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using Cohesive.Model;
using Cohesive.Model.Serialization;

[MemoryDiagnoser]
[ShortRunJob]
public class AnnotationMapBenchmarks
{
    byte[] json = null!;
    JsonSerializerOptions baseline = null!;
    JsonSerializerOptions shared = null!;
    ImmutableDictionary<AnnotationKey, AnnotationValue> map = null!;

    [Params(0, 1, 8, 32)]
    public int Entries { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        shared = StrictDocumentJson.CreateOptions();
        shared.Converters.Add(new AnnotationMapJsonConverter());
        baseline = new(shared);
        baseline.Converters.Remove(baseline.Converters.Single(c => c.CanConvert(typeof(ImmutableDictionary<AnnotationKey, AnnotationValue>))));
        json = JsonSerializer.SerializeToUtf8Bytes(Enumerable.Range(0, Entries)
            .ToDictionary(i => $"annotation{i}", i => new { identity = "shared-contract", fields = new[] { "a", "b", "c" }, ordinal = i }));
        map = PerValue();
        if (!PerValueWrite().AsSpan().SequenceEqual(SharedMapWrite())) throw new InvalidOperationException("Annotation output differs.");
    }

    [Benchmark(Baseline = true)]
    public ImmutableDictionary<AnnotationKey, AnnotationValue> PerValue() => JsonSerializer.Deserialize<ImmutableDictionary<AnnotationKey, AnnotationValue>>(json, baseline)!;

    [Benchmark]
    public ImmutableDictionary<AnnotationKey, AnnotationValue> SharedMap() => JsonSerializer.Deserialize<ImmutableDictionary<AnnotationKey, AnnotationValue>>(json, shared)!;
    [Benchmark]
    public byte[] PerValueWrite() => JsonSerializer.SerializeToUtf8Bytes(map, baseline);

    [Benchmark]
    public byte[] SharedMapWrite() => JsonSerializer.SerializeToUtf8Bytes(map, shared);
}

