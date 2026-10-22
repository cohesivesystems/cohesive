using System.Text.Json;
using BenchmarkDotNet.Attributes;
using Cohesive.Execution;
using Cohesive.Model;

namespace Cohesive.Relations.Benchmarks;

/// <summary>Measures fixed tagged metadata independently of retained payload text.</summary>
[Config(typeof(RelationBenchmarkConfig)), MemoryDiagnoser]
public class ObservationTaggedMetadataBenchmarks
{
    readonly JsonSerializerOptions options = CreateOptions();
    byte[] json = null!;

    /// <summary>The metadata spelling under measurement.</summary>
    [Params("null", "undefined", "escaped-null")]
    public string Workload { get; set; } = "null";

    /// <summary>Prepares the wire input outside measurement.</summary>
    [GlobalSetup]
    public void Setup() => json = System.Text.Encoding.UTF8.GetBytes(Workload switch
    {
        "escaped-null" => "{\"$kind\":\"n\\u0075ll\"}",
        _ => "{\"$kind\":\"" + Workload + "\"}"
    });

    /// <summary>Decodes a tagged singleton observation.</summary>
    /// <returns>The decoded immutable observation.</returns>
    [Benchmark]
    public ObservationValue Decode() => JsonSerializer.Deserialize<ObservationValue>(json, options);

    static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(PortableValueJsonConverter.TaggedObservationValues);
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
