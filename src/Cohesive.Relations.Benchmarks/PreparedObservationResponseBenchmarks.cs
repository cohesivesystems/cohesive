using System.Text.Json;
using System.Text.Json.Serialization;
using BenchmarkDotNet.Attributes;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Relations.Benchmarks;

/// <summary>Response interpretation and registration compilation measured independently.</summary>
[Config(typeof(RelationBenchmarkConfig)), MemoryDiagnoser]
public class PreparedObservationResponseBenchmarks
{
    readonly JsonSerializerOptions options = CreateOptions();
    ObservationValue value;
    Func<ObservationValue, ObservationBoundaryConversionBenchmarks.Response?> prepared = null!;

    /// <summary>The number of independently materialized response items.</summary>
    [Params(1, 128, 4096)]
    public int Count { get; set; }

    /// <summary>Prepares one conventional response with 64 KiB text and checks serializer equivalence.</summary>
    [GlobalSetup]
    public void Setup()
    {
        value = ObservationValue.FromObject(new ObservationBoundaryConversionBenchmarks.Response(7, new string('x', 65536),
            [.. Enumerable.Range(0, Count).Select(i => new ObservationBoundaryConversionBenchmarks.Child(i))]));
        prepared = PrepareReader();
        if (JsonSerializer.Serialize(Compatibility()) != JsonSerializer.Serialize(Prepared()))
            throw new InvalidOperationException("Prepared response differs from serializer behavior.");
    }

    /// <summary>Materializes through the existing JSON compatibility path.</summary>
    /// <returns>An independent CLR response graph.</returns>
    [Benchmark(Baseline = true)]
    public ObservationBoundaryConversionBenchmarks.Response? Compatibility() =>
        value.Deserialize<ObservationBoundaryConversionBenchmarks.Response>(options, ObservationBytesJsonEncoding.Base64String);

    /// <summary>Materializes through a registration-owned prepared interpretation.</summary>
    /// <returns>An independent response graph sharing immutable text.</returns>
    [Benchmark]
    public ObservationBoundaryConversionBenchmarks.Response? Prepared() => prepared(value);

    /// <summary>Compiles a new registration reader with already-warm serializer metadata.</summary>
    /// <returns>A new reader; this method intentionally includes expression compilation.</returns>
    [Benchmark]
    public Func<ObservationValue, ObservationBoundaryConversionBenchmarks.Response?> PrepareReader() =>
        ObservationMaterializer.PrepareJsonValue<ObservationBoundaryConversionBenchmarks.Response>(options, ObservationBytesJsonEncoding.Base64String);

    static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        options.Converters.Add(new ObservationValueJsonConverter(ObservationBytesJsonEncoding.Base64String));
        options.Converters.Add(new JsonStringEnumConverter());
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
