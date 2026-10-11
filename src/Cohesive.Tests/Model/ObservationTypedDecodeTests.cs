using System.Collections;
using System.Text.Json;

namespace Cohesive.Tests.Model;

public sealed class ObservationTypedDecodeTests
{
    [Theory]
    [InlineData("null")]
    [InlineData("[true,false,null,\"é🙂<>&\",1.2300,-0.0,1e-300]")]
    [InlineData("{\"empty\":{},\"rows\":[{\"n\":42,\"text\":\"owned\"}]}")]
    public void PooledDecodeMatchesPriorTextProjectionAndOwnsJsonStorage(string json)
    {
        using var source = JsonDocument.Parse(json);
        var value = ObservationValue.FromJsonElement(source.RootElement);
        var expected = JsonSerializer.Deserialize<JsonElement>(value.GetRawText());
        var actual = value.Deserialize<JsonElement>();
        for (var i = 0; i < 32; i++) ObservationValue.FromString("overwrite").Deserialize<string>();
        Assert.Equal(expected.GetRawText(), actual.GetRawText());
    }

    [Fact]
    public void DecodeRetainsOptionsBinaryPolicyAndFailureIsolation()
    {
        var value = ObservationValue.FromObject(new { NUMBER = "42" });
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true,
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString };
        Assert.Equal(42, value.Deserialize<Numeric>(options)!.Number);
        Assert.Throws<JsonException>(() => ObservationValue.FromObject(new { Number = "bad" }).Deserialize<Numeric>());
        Assert.Equal(42, ObservationValue.FromObject(new { Number = 42 }).Deserialize<Numeric>()!.Number);
        var bytes = ObservationValue.FromBytes(new byte[] { 0, 1, 255 });
        Assert.Throws<InvalidOperationException>(() => bytes.Deserialize<byte[]>());
        Assert.Equal(new byte[] { 0, 1, 255 }, bytes.Deserialize<byte[]>(bytesEncoding: ObservationBytesJsonEncoding.Base64String));
    }

    [Theory]
    [InlineData("\"converted/é🙂\"", true)]
    [InlineData("\" \"", false)]
    [InlineData("1", false)]
    [InlineData("null", false)]
    [InlineData("{}", false)]
    public void ConvertedDictionaryKeysRetainStringOnlyAdmission(string json, bool accepted)
    {
        var key = new DeclaredJsonObservationProjectionTests.RawValue(json);
        var input = new Hashtable { [key] = 42 };
        if (!accepted) Assert.Throws<InvalidOperationException>(() => ObservationValue.FromObject(input));
        else Assert.Equal(42, ObservationValue.FromObject(input).GetProperty("converted/é🙂").GetInt32());
    }

    [Fact]
    public void WarmPublicDecodeHasABoundedAllocationPerRow()
    {
        var value = ObservationValue.FromObject(new Payload(Enumerable.Range(0, 1024)
            .Select(i => new Row(i, "é<>&🙂", 1.2300m)).ToArray()));
        for (var i = 0; i < 4; i++) value.Deserialize<Payload>();
        var samples = new long[7];
        Payload? actual = null;
        for (var i = 0; i < samples.Length; i++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            actual = value.Deserialize<Payload>();
            samples[i] = GC.GetAllocatedBytesForCurrentThread() - before;
        }
        Array.Sort(samples);
        Assert.Equal(1024, actual!.Rows.Length);
        Assert.True(samples[3] <= 296L * 1024, $"Median public decode allocation {samples[3]} exceeds 296 bytes per row.");
    }

    public sealed record Numeric(long Number);
    public sealed record Payload(Row[] Rows);
    public sealed record Row(long Number, string Text, decimal Amount);
}
