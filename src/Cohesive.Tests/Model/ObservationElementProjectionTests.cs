using System.Text.Json;
using Cohesive.Model;
using Xunit.Abstractions;

namespace Cohesive.Tests.Model;

public sealed class ObservationElementProjectionTests(ITestOutputHelper output)
{
    [Fact]
    public void ProjectionOwnsNestedValuesAndPreservesOrdinalKeysAndLastDuplicate()
    {
        ObservationValue value;
        using (var source = JsonDocument.Parse("""{"z":{"é":"text","A":1,"a":2},"a":[{"last":1,"last":3}],"empty":{}}"""))
            value = ObservationValue.FromJsonElement(source.RootElement);
        Assert.Equal(1, value.Fields!["z"].Fields!["A"].GetInt64());
        Assert.Equal(2, value.Fields["z"].Fields!["a"].GetInt64());
        Assert.Equal(3, value.Fields["a"].Array[0].Fields!["last"].GetInt64());
        using var canonical = JsonDocument.Parse(value.GetRawText());
        Assert.Equal("""{"a":[{"last":3}],"empty":{},"z":{"A":1,"a":2,"é":"text"}}""",
            JsonSerializer.Serialize(canonical.RootElement,
                new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        ObservationValue wide;
        var wideJson = JsonSerializer.Serialize(Enumerable.Range(0, 16).ToDictionary(i => $"field{i}", i => i));
        using (var wideSource = JsonDocument.Parse(wideJson[..^1] + ",\"field\\u0031\\u0035\":99}"))
            wide = ObservationValue.FromJsonElement(wideSource.RootElement);
        Assert.Equal(16, wide.Fields!.Count);
        Assert.Equal(99, wide.Fields["field15"].GetInt64());
        var fields = Assert.IsAssignableFrom<IDictionary<string, ObservationValue>>(wide.Fields);
        Assert.True(fields.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => fields["new"] = ObservationValue.Null);
        Assert.Throws<NotSupportedException>(() => fields.Clear());
    }

    [Theory]
    [InlineData(1, 128, 13_000)]
    [InlineData(16, 32, 60_000)]
    public void ProjectionHasFixedWarmAllocationCeiling(int objects, int fields, long ceiling)
    {
        object payload = objects == 1
            ? Enumerable.Range(0, fields).ToDictionary(i => $"field{i:D4}", i => i)
            : Enumerable.Range(0, objects).Select(i => Enumerable.Range(0, fields)
                .ToDictionary(j => $"field{j:D4}", j => i + j)).ToArray();
        using var document = JsonSerializer.SerializeToDocument(payload);
        for (var i = 0; i < 20; i++) ObservationValue.FromJsonElement(document.RootElement);
        var samples = new long[7];
        for (var i = 0; i < samples.Length; i++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var value = ObservationValue.FromJsonElement(document.RootElement);
            samples[i] = GC.GetAllocatedBytesForCurrentThread() - before;
            GC.KeepAlive(value);
        }
        Array.Sort(samples);
        output.WriteLine($"Objects={objects}, fields={fields}: {samples[3]} B");
        Assert.InRange(samples[3], 0, ceiling);
    }
}
