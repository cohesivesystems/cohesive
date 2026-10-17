using System.Text.Json;

namespace Cohesive.Tests.Model;

public sealed class ObservationJsonStringReuseTests
{
    [Fact]
    public void RepeatedStringsShareOnlyTraversalOwnedStrings()
    {
        var text = new string('x', 256) + "é😀";
        var json = JsonSerializer.Serialize(Enumerable.Repeat(text, 128));
        using var document = JsonDocument.Parse(json);
        var first = ObservationValue.FromJsonElement(document.RootElement).EnumerateArray();
        var second = ObservationValue.FromJsonElement(document.RootElement).EnumerateArray();
        foreach (var row in first) Assert.Same(first[0].String, row.String);
        Assert.Equal(text, first[0].String);
        Assert.NotSame(first[0].String, second[0].String);
        for (var i = 0; i < 4; i++) ObservationValue.FromJsonElement(document.RootElement);
        var samples = new long[7];
        for (var i = 0; i < samples.Length; i++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var actual = ObservationValue.FromJsonElement(document.RootElement);
            samples[i] = GC.GetAllocatedBytesForCurrentThread() - before;
            GC.KeepAlive(actual);
        }
        Array.Sort(samples);
        Assert.True(samples[3] <= 48 * 1024, $"Repeated-string projection allocated {samples[3]} bytes.");
    }

    [Fact]
    public void NameAndValueReusePreservesEscapesAndLastPropertyWins()
    {
        var rows = Enumerable.Repeat("{\"na\\u006de\":\"na\\u006de\",\"é\":\"😀\",\"\\u00e9\":\"last\"}", 128);
        using var document = JsonDocument.Parse("[" + string.Join(',', rows) + "]");
        var actual = ObservationValue.FromJsonElement(document.RootElement).EnumerateArray();
        Assert.All(actual.ToArray(), row =>
        {
            Assert.Equal("name", row.GetProperty("name").String);
            Assert.Equal("last", row.GetProperty("é").String);
            Assert.Equal(2, row.Fields!.Count);
        });
    }

    [Theory]
    [InlineData("{\"value\":\"\\uD800\"}")]
    [InlineData("{\"\\uD800\":\"value\"}")]
    public void StringReuseDoesNotHideMalformedUtf16(string row)
    {
        using var document = JsonDocument.Parse("[" + string.Join(',', Enumerable.Repeat(row, 128)) + "]");
        Assert.Throws<InvalidOperationException>(() => ObservationValue.FromJsonElement(document.RootElement));
    }
}
