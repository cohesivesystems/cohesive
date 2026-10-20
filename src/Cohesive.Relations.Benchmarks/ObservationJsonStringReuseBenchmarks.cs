using System.Buffers;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Relations.Benchmarks;

/// <summary>Adversarial and repeated-text parser costs, including segmented and oversized tokens.</summary>
[Config(typeof(RelationBenchmarkConfig)), MemoryDiagnoser]
public class ObservationJsonStringReuseBenchmarks
{
    byte[] input = null!;
    ReadOnlySequence<byte> sequence;

    /// <summary>The bounded text distribution and parser-buffer arrangement.</summary>
    [Params("unique", "escaped", "segmented", "large-text")]
    public string Workload { get; set; } = "unique";

    /// <summary>Builds bytes and segments outside timing and checks exact parser equivalence.</summary>
    [GlobalSetup]
    public void Setup()
    {
        var output = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartArray();
            var count = Workload == "large-text" ? 64 : 512;
            for (var index = 0; index < count; index++)
            {
                writer.WriteStartObject();
                writer.WriteString(Workload == "unique" ? $"key{index:D5}" : "name <&> café",
                    Workload == "large-text" ? new string('x', 8192) : Workload == "unique" ? $"value{index:D5}" : "load <&> café 🚚");
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        input = output.WrittenSpan.ToArray();
        sequence = Workload == "segmented" ? CreateSegments(input) : new(input);
        if (!ParseDirect().Equals(ParseThroughDocument())) throw new InvalidOperationException("Parser values differ.");
    }

    /// <summary>Reads a fresh value directly from the supplied token sequence.</summary>
    /// <returns>Fresh owned value storage.</returns>
    [Benchmark]
    public ObservationValue ParseDirect()
    {
        var reader = new Utf8JsonReader(sequence);
        reader.Read();
        return ObservationJsonReader.ReadValue(ref reader);
    }

    /// <summary>Reads the same bytes through the existing element interpretation.</summary>
    /// <returns>Fresh value storage independent of the temporary document.</returns>
    [Benchmark]
    public ObservationValue ParseThroughDocument()
    {
        using var document = JsonDocument.Parse(sequence);
        return ObservationValue.FromJsonElement(document.RootElement);
    }

    static ReadOnlySequence<byte> CreateSegments(byte[] bytes)
    {
        const int SegmentLength = 11;
        Segment? first = null, last = null;
        for (var offset = 0; offset < bytes.Length; offset += SegmentLength)
        {
            var next = new Segment(bytes.AsMemory(offset, Math.Min(SegmentLength, bytes.Length - offset)));
            if (first is null) first = next;
            else last!.Append(next);
            last = next;
        }
        return new(first!, 0, last!, last!.Memory.Length);
    }

    sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        internal Segment(ReadOnlyMemory<byte> memory) { Memory = memory; }
        internal void Append(Segment next) { next.RunningIndex = RunningIndex + Memory.Length; Next = next; }
    }
}
