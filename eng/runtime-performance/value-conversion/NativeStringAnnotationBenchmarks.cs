using System.Buffers;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using Cohesive.Model;

/// <summary>Public annotation authoring and writing without forcing a JSON compatibility view.</summary>
[MemoryDiagnoser]
public class NativeStringAnnotationBenchmarks
{
    readonly ArrayBufferWriter<byte> buffer = new(4096);
    Utf8JsonWriter writer = null!;
    string text = null!;

    [Params(32, 1024)]
    public int Length { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        text = new string('x', Length);
        writer = new Utf8JsonWriter(buffer);
    }

    [Benchmark]
    public int CreateAndWrite()
    {
        buffer.Clear();
        writer.Reset(buffer);
        JsonSerializer.Serialize(writer, AnnotationValue.FromString(text));
        writer.Flush();
        return buffer.WrittenCount;
    }

    [GlobalCleanup]
    public void Cleanup() => writer.Dispose();
}
