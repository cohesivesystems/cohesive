using System.Collections.Immutable;
using Xunit.Abstractions;

namespace Cohesive.Tests.Model;

public sealed class AnnotationMapMergeTests(ITestOutputHelper output)
{
    [Fact]
    public void LaterValuesWinWithoutChangingInputsOrKeyComparison()
    {
        var first = ImmutableDictionary<AnnotationKey, AnnotationValue>.Empty
            .Add(new("name"), AnnotationValue.FromString("first"))
            .Add(new("Name"), AnnotationValue.FromString("upper"));
        var second = ImmutableDictionary<AnnotationKey, AnnotationValue>.Empty
            .Add(new("name"), AnnotationValue.FromString("last"));
        var merged = AnnotationMap.Merge(first, second);
        Assert.Equal(2, merged.Count);
        Assert.Same(second[new("name")], merged[new("name")]);
        Assert.Same(first[new("Name")], merged[new("Name")]);
        Assert.Equal("first", first[new("name")].Value.GetString());
        Assert.Empty(AnnotationMap.Merge());
    }

    [Fact]
    public void WarmMergeBuildsOnlyOneDictionary()
    {
        var value = AnnotationValue.FromString("value");
        var inputs = Enumerable.Range(0, 2).Select(part => Enumerable.Range(part * 128, 128)
            .ToImmutableDictionary(i => new AnnotationKey("field/" + i), _ => value)).ToArray();
        for (var warm = 0; warm < 4; warm++) AnnotationMap.Merge(inputs);
        var samples = new long[7];
        for (var run = 0; run < samples.Length; run++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var merged = AnnotationMap.Merge(inputs);
            samples[run] = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(256, merged.Count);
        }
        Array.Sort(samples);
        output.WriteLine($"Median merge allocation: {samples[3]} B / 256 keys");
        Assert.True(samples[3] <= 96 * 256, $"Median merge allocated {samples[3]} bytes.");
    }
}
