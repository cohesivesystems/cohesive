using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cohesive.Tests.Model;

public sealed class PreparedJsonValueTests
{
    public sealed record Child(int Value);
    public sealed record Row(int Number, string? Name, Child[]? Items);
    public sealed class MutableRow { public int Number { get; set; } = 42; public string? Name { get; set; } = "initial"; }
    public sealed record Customized([property: JsonPropertyName("n")] int Number);
    public sealed class RequiredRow { public required string Name { get; init; } }

    [Theory]
    [InlineData("{\"Number\":7,\"Name\":\"row\",\"Items\":[{\"Value\":3}]}")]
    [InlineData("{\"number\":7,\"name\":\"ignored\",\"items\":[{\"value\":3}]}")]
    [InlineData("{}")]
    [InlineData("{\"Number\":1.0,\"Items\":[null]}")]
    [InlineData("{\"Number\":\"7\"}")]
    [InlineData("{\"Number\":2147483648}")]
    [InlineData("{\"Items\":\"bad\"}")]
    [InlineData("\"bad\"")]
    [InlineData("null")]
    public void PreparedConventionalProjectionPreservesPlainJsonDefaults(string json) => Compare<Row>(json);

    [Fact]
    public void MutableCustomizedAndRequiredContractsRetainDefaultBehavior()
    {
        Compare<MutableRow>("{\"Number\":7,\"name\":\"ignored\"}");
        Compare<MutableRow>("{}");
        Compare<Customized>("{\"n\":7}");
        Compare<RequiredRow>("{}");
        Compare<byte[]>("\"AQID\"");
        Compare<int[]>("\"bad\"");
    }

    static void Compare<T>(string json)
    {
        using var document = JsonDocument.Parse(json);
        var value = ObservationValue.FromJsonElement(document.RootElement);
        T? expected = default, actual = default;
        var before = Record.Exception(() => expected = value.Deserialize<T>());
        var convert = ObservationMaterializer.PrepareJsonValue<T>();
        var after = Record.Exception(() => actual = convert(value));
        Assert.Equal(before?.GetType(), after?.GetType());
        if (before is null) Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(actual));
    }

    [Fact]
    public void WarmConversionAllocatesOnlyIndependentResultObjects()
    {
        var value = ObservationValue.FromObject(new Row(7, "row", [new(3)]));
        var convert = ObservationMaterializer.PrepareJsonValue<Row>();
        for (var i = 0; i < 20; i++) convert(value);
        var samples = new long[7];
        Row? previous = null;
        for (var i = 0; i < samples.Length; i++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var result = convert(value)!;
            samples[i] = GC.GetAllocatedBytesForCurrentThread() - before;
            if (previous is not null) { Assert.NotSame(previous, result); Assert.NotSame(previous.Items, result.Items); }
            previous = result;
        }
        Array.Sort(samples);
        Assert.InRange(samples[3], 0, 128);
    }
}
