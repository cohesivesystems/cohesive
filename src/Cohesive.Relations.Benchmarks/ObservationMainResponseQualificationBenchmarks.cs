using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using BenchmarkDotNet.Attributes;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Relations.Benchmarks;

/// <summary>Current-main-compatible harness for comparing serializer and newly prepared response ingestion profiles.</summary>
[Config(typeof(RelationBenchmarkConfig)), MemoryDiagnoser]
public class ObservationMainResponseQualificationBenchmarks
{
    ObservationValue value;
    Func<ObservationValue, ObservationBoundaryConversionBenchmarks.Response?> read = null!;

    /// <summary>Retained item count; each invocation returns independently owned arrays.</summary>
    [Params(1, 4096)]
    public int Count { get; set; }

    /// <summary>Compatibility runs on both libraries; prepared runs only where its API exists.</summary>
    [Params("compatibility", "prepared")]
    public string Route { get; set; } = "compatibility";

    /// <summary>Prepares equivalent profiles and verifies complete serialized result equality outside measurement.</summary>
    /// <exception cref="InvalidOperationException">A requested prepared API is unavailable or results differ.</exception>
    [GlobalSetup]
    public void Setup()
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        options.Converters.Add(new ObservationValueJsonConverter(ObservationBytesJsonEncoding.Base64String));
        options.Converters.Add(new JsonStringEnumConverter());
        options.MakeReadOnly(populateMissingResolver: true);
        value = ObservationValue.FromObject(new ObservationBoundaryConversionBenchmarks.Response(7, new string('x', 65536),
            [.. Enumerable.Range(0, Count).Select(index => new ObservationBoundaryConversionBenchmarks.Child(index))]));
        Func<ObservationValue, ObservationBoundaryConversionBenchmarks.Response?> compatibility = observed =>
            observed.Deserialize<ObservationBoundaryConversionBenchmarks.Response>(options, ObservationBytesJsonEncoding.Base64String);
        read = compatibility;
        if (Route == "prepared")
        {
            // Resolve only during setup, so this exact benchmark binary also loads against main,
            // where the newly introduced API is absent. Timing uses the ordinary typed delegate.
            var factory = typeof(ObservationMaterializer).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .SingleOrDefault(method => method.Name == "PrepareJsonValue" && method.IsGenericMethodDefinition && method.GetParameters().Length == 2)
                ?? throw new InvalidOperationException("The prepared response API is unavailable.");
            read = (Func<ObservationValue, ObservationBoundaryConversionBenchmarks.Response?>)factory
                .MakeGenericMethod(typeof(ObservationBoundaryConversionBenchmarks.Response))
                .Invoke(null, [options, ObservationBytesJsonEncoding.Base64String])!;
        }
        if (JsonSerializer.Serialize(read(value)) != JsonSerializer.Serialize(compatibility(value)))
            throw new InvalidOperationException("Qualified response routes differ.");
    }

    /// <summary>Projects the retained observation with the setup-selected reusable interpretation.</summary>
    /// <returns>An independent response graph consuming the complete fixture.</returns>
    [Benchmark]
    public ObservationBoundaryConversionBenchmarks.Response? Read() => read(value);
}
