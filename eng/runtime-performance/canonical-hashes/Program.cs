using System.Security.Cryptography;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Cohesive.Model.Serialization;

BenchmarkSwitcher.FromAssembly(typeof(CanonicalHashBenchmarks).Assembly).Run(args);

[MemoryDiagnoser]
[ShortRunJob]
public class CanonicalHashBenchmarks
{
    JsonElement value;
    JsonSerializerOptions options = null!;

    [Params("Flat", "Nested", "Rows", "LargeToken")]
    public string Shape { get; set; } = "Flat";

    [GlobalSetup]
    public void Setup()
    {
        options = StrictDocumentJson.CreateOptions();
        object payload = Shape switch
        {
            "Flat" => Enumerable.Range(0, 128).Reverse().ToDictionary(i => $"field{i:D3}", i => i),
            "Nested" => Nested(32),
            "Rows" => Enumerable.Range(0, 4096).Select(i => new { z = i, a = "<>&🙂", nested = new { enabled = true, values = new[] { 3, 1, 2 } } }).ToArray(),
            "LargeToken" => new { text = new string('é', 262_144) },
            _ => throw new InvalidOperationException(Shape)
        };
        value = JsonSerializer.SerializeToElement(payload);
    }

    static object Nested(int depth) => depth == 0 ? new { number = 1.2300m }
        : new Dictionary<string, object> { ["child"] = Nested(depth - 1), ["enabled"] = true };

    [Benchmark(Baseline = true)]
    public byte[] Contiguous() => SHA256.HashData(StrictDocumentJson.GetCanonicalBytes(new Document(value), options));

    [Benchmark]
    public byte[] Streaming() => StrictDocumentJson.ComputeCanonicalSha256(new Document(value), options);

    sealed record Document(JsonElement Value);
}
