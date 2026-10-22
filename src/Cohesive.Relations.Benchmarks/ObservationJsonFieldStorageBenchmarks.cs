using System.Buffers;
using System.Diagnostics;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Relations.Benchmarks;

/// <summary>Construction and subsequent property lookup across the bounded vector and dictionary boundary.</summary>
[Config(typeof(RelationBenchmarkConfig)), MemoryDiagnoser]
public class ObservationJsonFieldStorageBenchmarks
{
    const int LookupCount = 16;
    byte[] input = null!;
    ObservationValue retained;
    string[] queries = null!;
    long expected;

    /// <summary>Object widths below, within and beyond the experimental contiguous storage range.</summary>
    [Params(4, 8, 9, 16, 32, 33, 64)]
    public int Width { get; set; } = 16;

    /// <summary>Early parser training shape, either inline or dictionary-backed in both implementations.</summary>
    [Params(4, 64)]
    public int WarmupWidth { get; set; } = 4;

    /// <summary>Checks parser equivalence and lookup results, then trains shared methods on the selected width.</summary>
    /// <exception cref="InvalidOperationException">Parser interpretations or lookup results differ.</exception>
    [GlobalSetup]
    public void Setup()
    {
        input = CreateInput(Width);
        retained = Parse();
        using var document = JsonDocument.Parse(input);
        if (!retained.Equals(ObservationValue.FromJsonElement(document.RootElement)))
            throw new InvalidOperationException("Field storage fixture parser interpretations differ.");
        // Independent strings prevent reference-identity hits against decoded property names.
        queries = [.. Enumerable.Range(0, LookupCount).Select(index => new string(((index % 4) switch
        {
            0 => "field0", 1 => "field" + Width / 2, 2 => "field" + (Width - 1), _ => "missing"
        }).ToCharArray()))];
        expected = 4L * (Width / 2 + Width - 1);
        if (Lookup() != expected || ParseAndLookup() != expected)
            throw new InvalidOperationException("Unexpected field lookup result.");
        var training = CreateInput(WarmupWidth);
        var trainingValue = Read(training);
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(1))
        {
            _ = Read(training);
            _ = Lookup(trainingValue);
        }
    }

    /// <summary>Parses into fresh independently owned object storage.</summary>
    /// <returns>The new immutable object.</returns>
    [Benchmark]
    public ObservationValue Parse() => Read(input);

    /// <summary>Reads sixteen properties from an already retained object: first, middle, last and missing, repeated four times.</summary>
    /// <returns>The sum of numeric hits, consuming each lookup result.</returns>
    [Benchmark(OperationsPerInvoke = LookupCount)]
    public long Lookup() => Lookup(retained);

    /// <summary>Parses one object and performs the same sixteen subsequent reads.</summary>
    /// <returns>The sum of numeric hits from the newly parsed object.</returns>
    [Benchmark]
    public long ParseAndLookup() => Lookup(Parse());

    /// <summary>Measures surviving managed bytes separately from total allocation for a batch of owned objects.</summary>
    /// <param name="args">Object width from 1 through 4096 and retained object count from 1000 through 100000.</param>
    /// <returns>Zero after printing heap and allocation deltas; two for invalid arguments.</returns>
    /// <exception cref="InvalidOperationException">Fixture parser or lookup checks fail.</exception>
    public static int RunRetained(string[] args)
    {
        if (args.Length != 2 || !int.TryParse(args[0], out var width) || width is < 1 or > 4096
            || !int.TryParse(args[1], out var count) || count is < 1000 or > 100000)
        {
            Console.Error.WriteLine("--field-storage-retained <width:1..4096> <count:1000..100000>");
            return 2;
        }
        var benchmark = new ObservationJsonFieldStorageBenchmarks { Width = width };
        benchmark.Setup();
        var values = new ObservationValue[count];
        var beforeHeap = ObservationBenchmarkMemory.CollectLiveBytes();
        var beforeAllocation = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < count; index++) values[index] = benchmark.Parse();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - beforeAllocation;
        var retainedBytes = ObservationBenchmarkMemory.CollectLiveBytes() - beforeHeap;
        GC.KeepAlive(values);
        GC.KeepAlive(benchmark);
        Console.WriteLine(FormattableString.Invariant($"width={width},count={count},allocated_bytes={allocated},retained_bytes={retainedBytes}"));
        return 0;
    }

    long Lookup(ObservationValue value)
    {
        long sum = 0;
        foreach (var query in queries)
            if (value.TryGetProperty(query, out var item)) sum += item.Int64;
        return sum;
    }

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
