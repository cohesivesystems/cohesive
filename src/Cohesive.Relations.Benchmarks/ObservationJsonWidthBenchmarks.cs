using System.Buffers;
using System.Diagnostics;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Relations.Benchmarks;

/// <summary>Object width costs after training the shared parser on narrow or dictionary-backed objects.</summary>
[Config(typeof(RelationBenchmarkConfig)), MemoryDiagnoser]
public class ObservationJsonWidthBenchmarks
{
    byte[] input = null!;

    /// <summary>The number of distinct numeric fields in the measured object.</summary>
    [Params(1, 4, 8, 9, 16, 32)]
    public int Width { get; set; }

    /// <summary>The training width; four stays inline and thirty-two uses dictionary storage.</summary>
    [Params(4, 32)]
    public int WarmupWidth { get; set; }

    /// <summary>Checks equivalent owned results, then trains shared parser methods before measured warmup.</summary>
    /// <exception cref="InvalidOperationException">The token and DOM parser interpretations differ.</exception>
    [GlobalSetup]
    public void Setup()
    {
        input = CreateInput(Width);
        using var document = JsonDocument.Parse(input);
        if (!Parse().Equals(ObservationValue.FromJsonElement(document.RootElement)))
            throw new InvalidOperationException("Width fixture parser interpretations differ.");
        var training = CreateInput(WarmupWidth);
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(1)) _ = Read(training);
    }

    /// <summary>Parses one object into fresh immutable storage.</summary>
    /// <returns>The independently owned object.</returns>
    [Benchmark]
    public ObservationValue Parse() => Read(input);

    static ObservationValue Read(byte[] bytes)
    {
        var reader = new Utf8JsonReader(bytes);
        reader.Read();
        return ObservationJsonReader.ReadValue(ref reader);
    }

    static byte[] CreateInput(int width)
    {
        var output = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            for (var index = 0; index < width; index++) writer.WriteNumber("field" + index, index);
            writer.WriteEndObject();
        }
        return output.WrittenSpan.ToArray();
    }
}
