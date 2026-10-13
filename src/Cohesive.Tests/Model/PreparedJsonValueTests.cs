using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit.Abstractions;

namespace Cohesive.Tests.Model;

public sealed class PreparedJsonValueTests(ITestOutputHelper output)
{
    public sealed record Child(int Value);
    public sealed record OptionalCount(int Count = 7);
    public sealed record OptionalValues(bool Enabled = true, long Count = 42, decimal Fee = 1.5m);
    public sealed record OptionalNullable(int? Count = 7);
    public enum Status { None, Ready }
    public sealed record MixedRow(string Text, Status Status, IReadOnlyList<Child>? Items, byte[]? Bytes, ObservationValue Content);
    public sealed class InitRow { public int Number { get; init; } = 42; public IReadOnlyList<Child>? Items { get; init; } }


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

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"Count\":9}")]
    [InlineData("{\"Count\":null}")]
    public void ValueTypeConstructorDefaultsPrepareAndMatchJson(string json)
    {
        Compare<OptionalCount>(json);
        Compare<OptionalValues>(json);
        Compare<OptionalNullable>(json);
    }

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

    [Theory]
    [InlineData("{\"Text\":\"row\",\"Status\":1,\"Items\":[{\"Value\":3}],\"Bytes\":\"AQID\",\"Content\":{\"n\":42}}")]
    [InlineData("{\"Status\":\"Ready\"}")]
    [InlineData("{\"Items\":null,\"Bytes\":null,\"Content\":null}")]
    [InlineData("{}")]
    public void MixedMembersAndInitPropertiesRetainPlainJsonSemantics(string json)
    {
        Compare<MixedRow>(json);
        Compare<InitRow>(json);
    }

    [Fact]
    public void MixedProjectionDoesNotCopyTheWholeRowsLargeString()
    {
        var text = new string('x', 64 * 1024);
        var value = ObservationValue.FromObject(new Dictionary<string, ObservationValue>
        {
            [nameof(MixedRow.Text)] = ObservationValue.FromString(text),
            [nameof(MixedRow.Status)] = ObservationValue.FromInt64(1),
            [nameof(MixedRow.Items)] = ObservationValue.FromObject(new[] { new Child(3) }),
            [nameof(MixedRow.Bytes)] = ObservationValue.FromString("AQID"),
            [nameof(MixedRow.Content)] = ObservationValue.FromObject(new Child(42))
        });
        var convert = ObservationMaterializer.PrepareJsonValue<MixedRow>();
        for (var i = 0; i < 8; i++) convert(value);
        var samples = new long[7];
        MixedRow? previous = null;
        for (var i = 0; i < samples.Length; i++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var result = convert(value)!;
            samples[i] = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(Status.Ready, result.Status);
            Assert.Equal(text, result.Text);
            Assert.Equal(3, Assert.Single(result.Items!).Value);
            Assert.Equal(new byte[] { 1, 2, 3 }, result.Bytes);
            if (previous is not null)
            {
                Assert.NotSame(previous.Items, result.Items);
                Assert.NotSame(previous.Bytes, result.Bytes);
            }
            previous = result;
        }
        Array.Sort(samples);
        output.WriteLine($"Mixed row allocation: {samples[3]} bytes (median of seven).");
        Assert.InRange(samples[3], 0, 4096);
        Assert.Same(text, previous!.Text);
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
        Assert.InRange(samples[3], 0, 192);
    }
}
