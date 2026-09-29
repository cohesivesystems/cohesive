using System.Text.Json;
using System.Text.Json.Serialization;
using Cohesive.Model.Serialization;

namespace Cohesive.Tests.CodeGen;

public sealed class DeclaredJsonValueConverterTests
{
    [Fact]
    public void PortableObservationAndForeignEnvelope_RetainTheOwningValueProfile()
    {
        var value = new Envelope("outer", new("inner", Mode.Accepted));
        var observed = ObservationValue.FromObject(value);
        using var json = JsonDocument.Parse(observed.GetRawText());
        Assert.Equal("outer", json.RootElement.GetProperty("Name").GetString());
        var document = json.RootElement.GetProperty("Document");
        Assert.Equal("inner", document.GetProperty("name").GetString());
        Assert.Equal("Accepted", document.GetProperty("mode").GetString());
        var options = new JsonSerializerOptions();
        options.Converters.Add(new DeclaredJsonValueConverterFactory());
        var restored = JsonSerializer.Deserialize<Envelope>(observed.GetRawText(), options);
        Assert.Equal(value, restored);
        Assert.True(JsonElement.DeepEquals(json.RootElement, JsonSerializer.SerializeToElement(value, options)));
    }

    [Fact]
    public void RepeatedConcurrentProjection_ResolvesTheValueProfileOnce()
    {
        Parallel.For(0, 32, i => ObservationValue.FromObject(new CachedDocument(i)));
        Assert.Equal(1, Profiles.CachedCalls);
    }

    [Fact]
    public void RecursiveValueProfile_FailsBeforeSerializerRecursion()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new DeclaredJsonValueConverterFactory());
        Assert.Throws<InvalidOperationException>(() => JsonSerializer.Serialize(new Recursive("x"), options));
    }

    [PortableJsonValue(JsonTypeKind.Object)]
    public sealed record Envelope(string Name, Document Document);
    [PortableJsonValue(JsonTypeKind.Object)]
    [JsonContractOptions(typeof(Profiles), nameof(Profiles.Document))]
    public sealed record Document(string Name, Mode Mode);
    [JsonContractOptions(typeof(Profiles), nameof(Profiles.Recursive))]
    public sealed record Recursive(string Name);
    [PortableJsonValue(JsonTypeKind.Object)]
    [JsonContractOptions(typeof(Profiles), nameof(Profiles.Cached))]
    public sealed record CachedDocument(int Value);
    public enum Mode { Accepted }
    public static class Profiles
    {
        public static int CachedCalls;
        public static JsonSerializerOptions Cached()
        {
            Interlocked.Increment(ref CachedCalls);
            return new JsonSerializerOptions();
        }
        public static JsonSerializerOptions Document()
        {
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            options.Converters.Add(new JsonStringEnumConverter());
            return options;
        }
        public static JsonSerializerOptions Recursive()
        {
            var options = new JsonSerializerOptions();
            options.Converters.Add(new DeclaredJsonValueConverterFactory());
            return options;
        }
    }
}
