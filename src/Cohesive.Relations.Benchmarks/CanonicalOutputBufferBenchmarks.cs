using System.Buffers;
using System.Text.Encodings.Web;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using Cohesive.Model.Serialization;

namespace Cohesive.Relations.Benchmarks;

/// <summary>Isolates temporary canonical output storage from the shared exact-number walk.</summary>
[MemoryDiagnoser]
[ShortRunJob]
public class CanonicalOutputBufferBenchmarks
{
    JsonDocument document = null!;
    [Params(1, 128, 4096)]
    public int Rows { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        document = JsonDocument.Parse(JsonSerializer.Serialize(Enumerable.Range(0, Rows)
            .Select(i => new { z = "<>&é😀", a = i, nested = new { name = "observation", enabled = true } })));
        if (!GrowingArray().AsSpan().SequenceEqual(PooledOutput())) throw new InvalidOperationException("Output differs.");
    }

    [GlobalCleanup]
    public void Cleanup() => document.Dispose();

    [Benchmark(Baseline = true)]
    public byte[] GrowingArray()
    {
        ArrayBufferWriter<byte> buffer = new();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            CanonicalJsonWriter.WriteCanonicalSequence(writer, document.RootElement, CanonicalJsonSequenceProfile.ExactDecimalRational);
        return buffer.WrittenSpan.ToArray();
    }

    [Benchmark]
    public byte[] PooledOutput() => CanonicalJsonWriter.GetCanonicalSequenceBytes(document.RootElement);
}

/// <summary>Measures lexical text output, including its required owned string.</summary>
[MemoryDiagnoser]
[ShortRunJob]
public class CanonicalTextBufferBenchmarks
{
    JsonDocument document = null!;
    [Params(1, 128, 4096)]
    public int Rows { get; set; }
    static readonly JsonWriterOptions Options = new() { Encoder = JavaScriptEncoder.Default };
    [GlobalSetup]
    public void Setup()
    {
        document = JsonDocument.Parse(JsonSerializer.Serialize(Enumerable.Range(0, Rows)
            .Select(i => new { z = "<>&é😀", a = i, nested = new { name = "observation", enabled = true } })));
        if (StreamText() != PooledText()) throw new InvalidOperationException("Text differs.");
    }
    [GlobalCleanup]
    public void Cleanup() => document.Dispose();
    [Benchmark(Baseline = true)]
    public string StreamText()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, Options))
            CanonicalJsonWriter.WriteCanonicalSequence(writer, document.RootElement, CanonicalJsonSequenceProfile.PreserveLexical);
        return System.Text.Encoding.UTF8.GetString(stream.GetBuffer().AsSpan(0, checked((int)stream.Length)));
    }
    [Benchmark]
    public string PooledText() => CanonicalJsonWriter.GetCanonicalSequenceText(document.RootElement,
        CanonicalJsonSequenceProfile.PreserveLexical, Options);
}
