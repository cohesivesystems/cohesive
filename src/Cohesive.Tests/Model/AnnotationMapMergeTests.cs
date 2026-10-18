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
    public void SingleNonemptyDefaultMapKeepsItsPersistentRoot()
    {
        var map = ImmutableDictionary<AnnotationKey, AnnotationValue>.Empty
            .Add(new("name"), AnnotationValue.FromString("value"));
        Assert.Same(map, AnnotationMap.Merge(ImmutableDictionary<AnnotationKey, AnnotationValue>.Empty, map,
            ImmutableDictionary<AnnotationKey, AnnotationValue>.Empty));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void CustomInputComparersDoNotBecomeTheMergePolicy(bool customKeys, bool customValues)
    {
        var first = ImmutableDictionary.Create<AnnotationKey, AnnotationValue>(
                customKeys ? new IgnoreCaseKeys() : EqualityComparer<AnnotationKey>.Default,
                customValues ? new EqualValues() : EqualityComparer<AnnotationValue>.Default)
            .Add(new("Name"), AnnotationValue.FromString("first"));
        var replacement = AnnotationValue.FromString("last");
        var second = ImmutableDictionary<AnnotationKey, AnnotationValue>.Empty
            .Add(new("Name"), replacement)
            .Add(new("name"), AnnotationValue.FromString("lower"));
        var merged = AnnotationMap.Merge(first, second);
        Assert.Same(EqualityComparer<AnnotationKey>.Default, merged.KeyComparer);
        Assert.Same(EqualityComparer<AnnotationValue>.Default, merged.ValueComparer);
        Assert.Equal(2, merged.Count);
        Assert.Same(replacement, merged[new("Name")]);
        Assert.Equal("lower", merged[new("name")].Value.GetString());
        Assert.Equal("first", first[new("Name")].Value.GetString());
    }

    [Fact]
    public void EqualOverlaysRetainExistingValueAndIndependentMergesDoNotMutateTheRoot()
    {
        var original = AnnotationValue.FromString("same");
        var first = ImmutableDictionary<AnnotationKey, AnnotationValue>.Empty.Add(new("name"), original);
        var equal = first.SetItem(new("name"), AnnotationValue.FromString("same"));
        Assert.Same(first, AnnotationMap.Merge(first, equal));
        var overlays = Enumerable.Range(0, 16).Select(i => ImmutableDictionary<AnnotationKey, AnnotationValue>.Empty
            .Add(new("name"), AnnotationValue.FromString(i.ToString()))).ToArray();
        Parallel.For(0, overlays.Length, i =>
            Assert.Same(overlays[i][new("name")], AnnotationMap.Merge(first, overlays[i])[new("name")]));
        Assert.Same(original, first[new("name")]);
    }

    [Fact]
    public void SmallOverlayAllocatesForChangedPathsInsteadOfEveryRetainedEntry()
    {
        var value = AnnotationValue.FromString("value");
        var first = Enumerable.Range(0, 1024).ToImmutableDictionary(i => new AnnotationKey("field/" + i), _ => value);
        var overlay = ImmutableDictionary<AnnotationKey, AnnotationValue>.Empty.Add(new("extra"), value);
        ImmutableDictionary<AnnotationKey, AnnotationValue>[] inputs = [first, overlay];
        for (var warm = 0; warm < 8; warm++) AnnotationMap.Merge(inputs);
        var samples = new long[7];
        for (var run = 0; run < samples.Length; run++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var merged = AnnotationMap.Merge(inputs);
            samples[run] = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(1025, merged.Count);
            Assert.Same(value, merged[new("extra")]);
        }
        Array.Sort(samples);
        output.WriteLine($"Median 1024-key merge with one new key: {samples[3]} B");
        Assert.True(samples[3] <= 4096, $"Small overlay allocated {samples[3]} bytes.");
    }

    sealed class IgnoreCaseKeys : IEqualityComparer<AnnotationKey>
    {
        public bool Equals(AnnotationKey x, AnnotationKey y) => StringComparer.OrdinalIgnoreCase.Equals(x.Value, y.Value);
        public int GetHashCode(AnnotationKey value) => StringComparer.OrdinalIgnoreCase.GetHashCode(value.Value);
    }

    sealed class EqualValues : IEqualityComparer<AnnotationValue>
    {
        public bool Equals(AnnotationValue? x, AnnotationValue? y) => true;
        public int GetHashCode(AnnotationValue value) => 0;
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
