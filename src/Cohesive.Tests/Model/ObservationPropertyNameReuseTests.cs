using System.Text.Json;
using System.Runtime.CompilerServices;

namespace Cohesive.Tests.Model;

public sealed class ObservationPropertyNameReuseTests
{
    [Fact]
    public void RepeatedNamesShareStringsWithinOneTraversalAndOwnThemAfterDisposal()
    {
        var name = new string('n', 512);
        ObservationValue value;
        using (var document = JsonDocument.Parse(JsonSerializer.Serialize(Enumerable.Range(1, 3)
            .Select(i => new Dictionary<string, int> { [name] = i }))))
            value = ObservationValue.FromJsonElement(document.RootElement);
        var names = value.Array.Select(item => item.Fields!.Keys.Single()).ToArray();
        Assert.Same(names[0], names[1]);
        Assert.Same(names[1], names[2]);
        Assert.Equal(3, value.Array[2].GetProperty(name).Int64);
    }

    [Theory]
    [InlineData("[{\"na\\u006de\":1},{\"name\":2},{\"na\\u006de\":3}]", "name")]
    [InlineData("[{\"\\u00e9\":1},{\"é\":2},{\"\\u00e9\":3}]", "é")]
    [InlineData("[{\"😀\":1},{\"😀\":2},{\"😀\":3}]", "😀")]
    public void EscapedAndUnicodeNamesKeepTheirDecodedIdentity(string json, string name)
    {
        using var document = JsonDocument.Parse(json);
        var value = ObservationValue.FromJsonElement(document.RootElement);
        for (var i = 0; i < 3; i++) Assert.Equal(i + 1, value.Array[i].GetProperty(name).Int64);
    }

    [Fact]
    public void EscapedDuplicateNamesKeepLastPropertyWins()
    {
        using var document = JsonDocument.Parse("{\"na\\u006de\":1,\"name\":2}");
        var value = ObservationValue.FromJsonElement(document.RootElement);
        Assert.Single(value.Fields!);
        Assert.Equal(2, value.GetProperty("name").Int64);
    }

    [Fact]
    public void InvalidNamesStillFailAndDoNotPoisonLaterTraversals()
    {
        using var invalid = JsonDocument.Parse("{\"\\uD800\":1}");
        Assert.Throws<InvalidOperationException>(() => ObservationValue.FromJsonElement(invalid.RootElement));
        using var valid = JsonDocument.Parse("{\"name\":42}");
        Assert.Equal(42, ObservationValue.FromJsonElement(valid.RootElement).GetProperty("name").Int64);
    }

    [Fact]
    public void ParallelTraversalsDoNotShareParserOrTableOwnership()
    {
        Parallel.For(0, 128, i =>
        {
            using var document = JsonDocument.Parse($"[{{\"name\":{i}}},{{\"name\":{i + 1}}}]");
            var value = ObservationValue.FromJsonElement(document.RootElement);
            Assert.Equal(i, value.Array[0].GetProperty("name").Int64);
            Assert.Equal(i + 1, value.Array[1].GetProperty("name").Int64);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TemporaryTableDoesNotRetainParserAfterSuccessOrFailure(bool fail)
    {
        var parser = ProjectAndReleaseParser(fail);
        for (var i = 0; i < 3 && parser.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        Assert.False(parser.IsAlive);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static WeakReference ProjectAndReleaseParser(bool fail)
    {
        var prefix = "{\"padding\":\"" + new string('x', 1024) + "\",\"name\":1";
        using var document = JsonDocument.Parse(prefix + (fail ? ",\"\\uD800\":2}" : "}"));
        if (fail) Assert.Throws<InvalidOperationException>(() => ObservationValue.FromJsonElement(document.RootElement));
        else ObservationValue.FromJsonElement(document.RootElement);
        return new WeakReference(document);
    }

    [Fact]
    public void RepeatedLongFieldNamesHaveABoundedAllocationPerRow()
    {
        var name = new string('n', 256);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(Enumerable.Range(0, 1024)
            .Select(i => new Dictionary<string, int> { [name] = i })));
        for (var i = 0; i < 20; i++) ObservationValue.FromJsonElement(document.RootElement);
        var samples = new long[7];
        ObservationValue result = default;
        for (var run = 0; run < samples.Length; run++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            result = ObservationValue.FromJsonElement(document.RootElement);
            samples[run] = GC.GetAllocatedBytesForCurrentThread() - before;
        }
        Array.Sort(samples);
        Assert.Equal(1024, result.Array.Length);
        Assert.InRange(samples[3], 0, 1024 * 200L);
    }
}
