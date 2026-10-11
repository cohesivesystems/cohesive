using System.Buffers;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using Cohesive.Execution;
using Cohesive.Model.Serialization;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class ExecutionDefinitionNormalizationTests
{
    static readonly Func<JsonElement, JsonElement> Normalize = typeof(ExecutionDefinitionFingerprinter)
        .GetMethod("NormalizeDefinition", BindingFlags.Static | BindingFlags.NonPublic)!
        .CreateDelegate<Func<JsonElement, JsonElement>>();
    static readonly Action<JsonElement> Validate = typeof(ExecutionDefinitionFingerprinter)
        .GetMethod("ValidateDefinitionProperties", BindingFlags.Static | BindingFlags.NonPublic)!
        .CreateDelegate<Action<JsonElement>>();
    static readonly Action<Utf8JsonWriter, JsonElement> Write = typeof(CanonicalJsonWriter)
        .GetMethod("WriteCanonicalSequence", BindingFlags.Static | BindingFlags.NonPublic)!
        .CreateDelegate<Action<Utf8JsonWriter, JsonElement>>();

    [Theory]
    [InlineData(128)]
    [InlineData(4096)]
    public void NormalizationPreservesBytesAndAvoidsTemporaryOwnedBuffer(int rows)
    {
        using var source = JsonDocument.Parse("{\"rows\":[" + string.Join(",", Enumerable.Repeat(
            "{\"z\":\"λ/\\\"<>&\",\"values\":[1.00,-0.0,1e21,null],\"a\":true}", rows)) + "]}");
        var input = source.RootElement;
        for (var i = 0; i < 4; i++) _ = Normalize(input);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var expected = Previous(input);
        var previousBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        before = GC.GetAllocatedBytesForCurrentThread();
        var actual = Normalize(input);
        var revisedBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(expected.GetRawText(), actual.GetRawText());
        Assert.True(revisedBytes < previousBytes * 0.95,
            $"Previous normalization allocated {previousBytes} B; revised allocated {revisedBytes} B.");
    }

    [Fact]
    public void NormalizedElementOwnsBytesAfterSourceDisposalAndPoolReuse()
    {
        JsonElement normalized;
        using (var source = JsonDocument.Parse("{\"z\":1.2300,\"a\":\"λ\"}"))
            normalized = Normalize(source.RootElement);
        Parallel.For(0, 64, i =>
        {
            using var source = JsonDocument.Parse("{\"value\":\"" + new string('x', 8192) + "\"}");
            _ = Normalize(source.RootElement);
        });
        Assert.Equal("{\"a\":\"λ\",\"z\":1.23}", normalized.GetRawText());
    }

    [Theory]
    [InlineData("{\"z\":1.000e0,\"a\":[true,false,null,{},[],\"λ/\\\\<>&\",-0.0,1e999]}")]
    [InlineData("{\"β\":{\"z\":1.2300,\"a\":12345678901234567890.123456789},\"a\":1e-7}")]
    public void CanonicalKeyMatchesOwnedCanonicalText(string json)
    {
        var key = Key();
        using var source = JsonDocument.Parse(json);
        Assert.Equal(Normalize(source.RootElement).GetRawText(), key(source.RootElement));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"a\":1,\"a\":2}")]
    [InlineData("{\"nested\":{\"a\":1,\"a\":2}}")]
    public void CanonicalKeyPreservesNormalizationFailures(string json)
    {
        var key = Key();
        using var source = JsonDocument.Parse(json);
        var expected = Assert.Throws<ArgumentException>(() => Normalize(source.RootElement));
        var actual = Assert.Throws<ArgumentException>(() => key(source.RootElement));
        Assert.Equal(expected.Message, actual.Message);
    }

    static Func<JsonElement, string> Key() => typeof(ExecutionDefinitionFingerprinter)
        .GetMethod("GetCanonicalDefinitionKey", BindingFlags.Static | BindingFlags.NonPublic)!
        .CreateDelegate<Func<JsonElement, string>>();

    static JsonElement Previous(JsonElement input)
    {
        Validate(input);
        ArrayBufferWriter<byte> buffer = new();
        using (var writer = new Utf8JsonWriter(buffer, new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            Write(writer, input);
        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }
}
