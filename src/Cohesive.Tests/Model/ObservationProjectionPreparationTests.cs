using System.Text.Json.Serialization;
using Cohesive.Model;

namespace Cohesive.Tests.Model;

public sealed class ObservationProjectionPreparationTests
{
    public sealed class Row
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "first";
        public string Label { get; set; } = "label";
        public string? Optional { get; set; }
        [JsonIgnore] public string Hidden => throw new InvalidOperationException("Ignored getter ran.");
    }

    [Fact]
    public void PreparedMetadataDoesNotRetainValues()
    {
        var row = new Row();
        var first = ObservationValue.FromObject(row);
        row.Id = "second"; row.Optional = "present";
        var second = ObservationValue.FromObject(row);
        Assert.Equal("first", first.GetProperty("id").GetString());
        Assert.Equal("second", second.GetProperty("id").GetString());
        Assert.Equal(ObservationValueKind.Null, first.GetProperty("Optional").Kind);
        Assert.Equal("present", second.GetProperty("Optional").GetString());
        Assert.False(second.TryGetProperty("Hidden", out _));
        Parallel.For(0, 64, i => Assert.Equal(i.ToString(),
            ObservationValue.FromObject(new Row { Id = i.ToString() }).GetProperty("id").GetString()));
    }

    [Theory]
    [InlineData(128)]
    [InlineData(1024)]
    public void WarmProjectionHasBoundedAllocationPerRow(int count)
    {
        var rows = Enumerable.Range(0, count).Select(i => new Row { Id = i.ToString() }).ToArray();
        for (var i = 0; i < 8; i++) _ = ObservationValue.FromObject(rows);
        long[] samples = new long[7];
        for (var i = 0; i < samples.Length; i++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var result = ObservationValue.FromObject(rows);
            samples[i] = GC.GetAllocatedBytesForCurrentThread() - before;
            GC.KeepAlive(result);
        }
        Array.Sort(samples);
        Assert.True(samples[3] <= count * 1024L + 4096,
            $"Projected {count} rows allocated {samples[3]} bytes.");
    }
}
