using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cohesive.Model.Serialization;
using Cohesive.Execution;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class PreparedConverterProjectionTests
{
    [Theory]
    [InlineData("{\"z\":1.2300,\"text\":\"é<>&😀\"}")]
    [InlineData("[{\"nested\":[1,null,true]},2]")]
    [InlineData("\"text é<>&😀\"")]
    [InlineData("true")]
    [InlineData("null")]
    public void ConverterOutputIsPreparedExactlyOnceWithUnchangedValues(string json)
    {
        var token = new Token(json);
        var writes = TokenConverter.Writes;
        var first = ObservationValue.FromObject(token);
        var second = ObservationValue.FromObject(token);
        Assert.Equal(1, TokenConverter.Writes - writes);
        using var expected = JsonDocument.Parse(json);
        Assert.Equal(ObservationValue.FromJsonElement(expected.RootElement), first);
        Assert.Equal(first, second);
        if (first.Kind == ObservationValueKind.Object) Assert.Same(first.Fields, second.Fields);
        if (first.Kind == ObservationValueKind.Array)
            Assert.Same(System.Runtime.InteropServices.ImmutableCollectionsMarshal.AsArray(first.Array),
                System.Runtime.InteropServices.ImmutableCollectionsMarshal.AsArray(second.Array));
    }

    [Fact]
    public void IndependentEnvelopesReuseOnlyTheImmutableLeaf()
    {
        var token = new Token("{\"name\":\"same\"}");
        var first = ObservationValue.FromObject(new { Name = "first", Payload = token });
        var second = ObservationValue.FromObject(new { Name = "second", Payload = token });
        Assert.NotSame(first.Fields, second.Fields);
        Assert.Same(first.GetProperty("Payload").Fields, second.GetProperty("Payload").Fields);
        Assert.Equal("first", first.GetProperty("Name").String);
        Assert.Equal("second", second.GetProperty("Name").String);
        var changed = ObservationValue.FromObject(new Token("{\"name\":\"changed\"}"));
        Assert.NotEqual(first.GetProperty("Payload"), changed);
        var mutable = new MutableToken { Json = "{\"name\":\"before\"}" };
        var before = ObservationValue.FromObject(mutable);
        mutable.Json = "{\"name\":\"after\"}";
        Assert.NotEqual(before, ObservationValue.FromObject(mutable));
    }

    [Fact]
    public async Task ConcurrentFirstReadersShareOneSuccessfulProjection()
    {
        var token = new Token("{\"value\":42}");
        var writes = TokenConverter.Writes;
        var values = await Task.WhenAll(Enumerable.Range(0, 32)
            .Select(_ => Task.Run(() => ObservationValue.FromObject(token))));
        Assert.Equal(1, TokenConverter.Writes - writes);
        Assert.All(values, value => Assert.Same(values[0].Fields, value.Fields));
    }

    [Theory]
    [InlineData("{\"name\":1,\"name\":2}")]
    [InlineData("{\"name\":1,\"na\\u006de\":2}")]
    [InlineData("{")]
    public void InvalidConverterOutputIsRejectedAgainOnRetry(string json)
    {
        var token = new Token(json);
        var first = Record.Exception(() => ObservationValue.FromObject(token));
        Assert.IsAssignableFrom<JsonException>(first);
        var retry = Record.Exception(() => ObservationValue.FromObject(token));
        Assert.IsAssignableFrom<JsonException>(retry);
        Assert.NotSame(first, retry);
    }

    [Fact]
    public void WarmOpaqueProjectionStillChecksTheEnclosingDepth()
    {
        var token = new Token(new string('[', 63) + "0" + new string(']', 63));
        _ = ObservationValue.FromObject(token);
        Assert.Throws<JsonException>(() => ObservationValue.FromObject(new Outer(new Envelope(token))));
        var bounded = new Token(new string('[', 62) + "0" + new string(']', 62));
        _ = ObservationValue.FromObject(new Outer(new Envelope(bounded)));
    }

    [Fact]
    public void PreparedLeafDoesNotKeepItsOwnerAlive()
    {
        var weak = Temporary();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Assert.False(weak.IsAlive);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static WeakReference Temporary()
    {
        var token = new Token("{\"temporary\":true}");
        _ = ObservationValue.FromObject(token);
        return new(token);
    }

    [Fact]
    public void DecodedOpaqueValuesReuseStorageOnlyAfterEncodingVerification()
    {
        var source = ObservationValue.FromObject(new Token("{\"name\":\"same\",\"number\":1.2300}"));
        var decoded = source.Deserialize<Token>(ExecutionDefinitionJsonSerializer.GetClrContractReadOnlyOptions())!;
        Assert.Same(source.Fields, ObservationValue.FromObject(decoded).Fields);
        // A deterministic converter may normalize input. A successful read alone is not encoding evidence.
        _ = ObservationValue.FromObject(new Normalized("seed"));
        using var input = JsonDocument.Parse("{\"name\":\" padded \"}");
        var original = ObservationValue.FromJsonElement(input.RootElement);
        var normalized = original.Deserialize<Normalized>(ExecutionDefinitionJsonSerializer.GetClrContractReadOnlyOptions())!;
        var projected = ObservationValue.FromObject(normalized);
        Assert.NotSame(original.Fields, projected.Fields);
        Assert.Equal("padded", projected.GetProperty("name").String);
        Assert.Equal(" padded ", original.GetProperty("name").String);
    }

    [Fact]
    public void OpaqueReadAndReprojectionCycleDoesNotRetainStorage()
    {
        var weak = TemporaryRoundTrip();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Assert.All(weak, item => Assert.False(item.IsAlive));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static WeakReference[] TemporaryRoundTrip()
    {
        var source = ObservationValue.FromObject(new Token("{\"value\":42}"));
        var decoded = source.Deserialize<Token>(ExecutionDefinitionJsonSerializer.GetClrContractReadOnlyOptions())!;
        var projected = ObservationValue.FromObject(decoded);
        Assert.Same(source.Fields, projected.Fields);
        return [new(decoded), new(source.Fields!)];
    }

    [Fact]
    public void WarmLargeNestedPayloadHasFixedAllocationBudget()
    {
        var token = new Token("{\"items\":[" + string.Join(',', Enumerable.Repeat(
            "{\"value\":\"é<>&😀\",\"nested\":[1,2,3]}", 128)) + "]}");
        _ = ObservationValue.FromObject(token);
        var samples = new long[7];
        for (var sample = 0; sample < samples.Length; sample++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 128; i++) _ = ObservationValue.FromObject(token);
            samples[sample] = GC.GetAllocatedBytesForCurrentThread() - before;
        }
        Array.Sort(samples);
        Assert.InRange(samples[3], 0, 512);
    }

    [PortableJsonValue(JsonTypeKind.Any)]
    [JsonConverter(typeof(TokenConverter))]
    public sealed record Token(string Json) : IImmutableObservationProjection;

    [PortableJsonValue(JsonTypeKind.Object)]
    public sealed record Envelope(Token Payload) : IImmutableObservationProjection;

    [PortableJsonValue(JsonTypeKind.Object)]
    public sealed record Outer(Envelope Child) : IImmutableObservationProjection;

    public sealed class TokenConverter : JsonConverter<Token>
    {
        internal static int Writes;
        public override Token Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            return new(document.RootElement.GetRawText());
        }
        public override void Write(Utf8JsonWriter writer, Token value, JsonSerializerOptions options)
        {
            Interlocked.Increment(ref Writes);
            writer.WriteRawValue(value.Json, skipInputValidation: true);
        }
    }

    [PortableJsonValue(JsonTypeKind.Object)]
    [JsonConverter(typeof(NormalizedConverter))]
    public sealed record Normalized(string Name) : IImmutableObservationProjection;
    public sealed class NormalizedConverter : JsonConverter<Normalized>
    {
        public override Normalized Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            return new(document.RootElement.GetProperty("name").GetString()!.Trim());
        }
        public override void Write(Utf8JsonWriter writer, Normalized value, JsonSerializerOptions options)
        {
            writer.WriteStartObject(); writer.WriteString("name", value.Name); writer.WriteEndObject();
        }
    }

    [JsonConverter(typeof(MutableTokenConverter))]
    public sealed class MutableToken { public string Json { get; set; } = "null"; }
    public sealed class MutableTokenConverter : JsonConverter<MutableToken>
    {
        public override MutableToken Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
            throw new NotSupportedException();
        public override void Write(Utf8JsonWriter writer, MutableToken value, JsonSerializerOptions options) =>
            writer.WriteRawValue(value.Json, skipInputValidation: true);
    }
}
