using System.Text.Json;
using System.Text.Json.Serialization;
using BenchmarkDotNet.Attributes;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Relations.Benchmarks;

/// <summary>Fresh checkpoint payload decoding and response materialization; preparation is excluded.</summary>
[Config(typeof(RelationBenchmarkConfig)), MemoryDiagnoser]
public class ObservationBoundaryConversionBenchmarks
{
    byte[] portableJson = null!;
    ObservationValue response;
    readonly JsonSerializerOptions options = CreateOptions();

    /// <summary>Flat, nested, collection-heavy and bounded-large portable payloads.</summary>
    [Params("flat", "nested", "collection", "large")]
    public string Workload { get; set; } = "flat";

    /// <summary>Builds input outside measurement and verifies lossless wire round trips.</summary>
    [GlobalSetup]
    public void Setup()
    {
        var count = Workload == "large" ? 4096 : Workload == "collection" ? 128 : 1;
        var value = ObservationValue.FromObject(new Dictionary<string, ObservationValue>
        {
            ["name"] = ObservationValue.FromString("load <&> café 🚚"),
            ["amount"] = new(ObservationValueKind.Decimal, dec: 12.50m),
            ["bytes"] = ObservationValue.FromBytes(new byte[] { 1, 2, 3 }),
            ["null"] = ObservationValue.Null
        });
        if (Workload == "nested")
            for (var level = 0; level < 16; level++)
                value = ObservationValue.FromObject(new Dictionary<string, ObservationValue> { ["child"] = value });
        if (Workload is "collection" or "large")
            value = ObservationValue.FromImmutableArray([.. Enumerable.Repeat(value, count)]);
        var portable = PortableValue.Concrete(new ValueContract(), value);
        portableJson = JsonSerializer.SerializeToUtf8Bytes(portable, options);
        var decoded = DecodePortable();
        if (!portableJson.AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(decoded, options)))
            throw new InvalidOperationException("Lossless payload bytes differ.");
        response = ObservationValue.FromObject(new Response(7, new string('x', 65536),
            [.. Enumerable.Range(0, count).Select(i => new Child(i))]));
        if (MaterializeResponse()?.Items.Length != count) throw new InvalidOperationException("Unexpected response.");
    }

    /// <summary>Decodes fresh checkpoint payload storage, including contract and state.</summary>
    /// <returns>The fresh portable payload; it does not convey contextual admission evidence.</returns>
    [Benchmark]
    public PortableValue DecodePortable() => JsonSerializer.Deserialize<PortableValue>(portableJson, options)!;

    /// <summary>Interprets an existing value through the HTTP response's case-insensitive JSON profile.</summary>
    /// <returns>An independent response graph.</returns>
    [Benchmark]
    public Response? MaterializeResponse() => response.Deserialize<Response>(options, ObservationBytesJsonEncoding.Base64String);

    /// <summary>A conventional mutable-array response contract.</summary>
    /// <param name="Number">Example scalar.</param>
    /// <param name="Text">Large retained text.</param>
    /// <param name="Items">Independent result items.</param>
    public sealed record Response(int Number, string Text, Child[] Items);

    /// <summary>A nested result item.</summary>
    /// <param name="Value">The scalar item value.</param>
    public sealed record Child(int Value);

    static JsonSerializerOptions CreateOptions()
    {
        var result = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, MaxDepth = 256 };
        result.Converters.Add(new ObservationValueJsonConverter(ObservationBytesJsonEncoding.Base64String));
        result.Converters.Add(new JsonStringEnumConverter());
        result.MakeReadOnly(populateMissingResolver: true);
        return result;
    }
}
