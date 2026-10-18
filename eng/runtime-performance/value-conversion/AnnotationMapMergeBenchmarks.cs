using System.Collections.Immutable;
using BenchmarkDotNet.Attributes;
using Cohesive.Model;

[MemoryDiagnoser]
public class AnnotationMapMergeBenchmarks
{
    ImmutableDictionary<AnnotationKey, AnnotationValue>[] maps = null!;

    [Params(8, 128, 1024)] public int Entries { get; set; }
    [Params(1, 8)] public int OverlayEntries { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        maps = [Enumerable.Range(0, Entries).ToImmutableDictionary(i => new AnnotationKey($"field/{i}"),
                i => AnnotationValue.FromString($"value/{i}")),
            Enumerable.Range(Entries, OverlayEntries).ToImmutableDictionary(i => new AnnotationKey($"field/{i}"),
                i => AnnotationValue.FromString($"value/{i}"))];
        var old = Rebuild();
        var current = Reuse();
        if (old.Count != current.Count || old.Any(pair => !current.TryGetValue(pair.Key, out var value)
                || !ReferenceEquals(pair.Value, value)))
            throw new InvalidOperationException("Annotation merge output differs.");
    }

    [Benchmark(Baseline = true)]
    public ImmutableDictionary<AnnotationKey, AnnotationValue> Rebuild()
    {
        var builder = ImmutableDictionary.CreateBuilder<AnnotationKey, AnnotationValue>();
        foreach (var map in maps)
            foreach (var (key, value) in map) builder[key] = value;
        return builder.ToImmutable();
    }

    [Benchmark]
    public ImmutableDictionary<AnnotationKey, AnnotationValue> Reuse() => AnnotationMap.Merge(maps);
}
