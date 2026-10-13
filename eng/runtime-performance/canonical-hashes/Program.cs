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

// The same borrowed document is warm; input serialization is outside the hash boundary.
[MemoryDiagnoser]
[ShortRunJob]
public class LexicalHashBenchmarks
{
    JsonDocument document = null!;

    [Params("Flat", "Nested", "Rows", "EscapedNames", "LargeToken")]
    public string Shape { get; set; } = "Flat";

    [GlobalSetup]
    public void Setup()
    {
        var payload = Shape switch
        {
            "Flat" => JsonSerializer.Serialize(Enumerable.Range(0, 128).Reverse().ToDictionary(i => $"field{i:D3}", i => i)),
            "Nested" => new string('[', 32) + "{\"z\":1.2300,\"a\":-0,\"a\":1e+03}" + new string(']', 32),
            "Rows" => "[" + string.Join(",", Enumerable.Repeat("{\"z\":1.2300,\"a\":\"<>&\\n🙂\",\"nested\":{\"enabled\":true,\"values\":[3,1,2]}}", 4096)) + "]",
            "EscapedNames" => "{" + string.Join(",", Enumerable.Range(0, 128).Reverse().Select(i => JsonSerializer.Serialize($"field{i:D3}/é/🙂/\n") + ":1.00")) + "}",
            "LargeToken" => JsonSerializer.Serialize(new { text = new string('é', 262_144) }),
            _ => throw new InvalidOperationException(Shape)
        };
        document = JsonDocument.Parse(payload);
    }

    [GlobalCleanup]
    public void Cleanup() => document.Dispose();

    [Benchmark(Baseline = true)]
    public byte[] Contiguous()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
            CanonicalJsonWriter.WriteCanonicalSequence(writer, document.RootElement, CanonicalJsonSequenceProfile.PreserveLexical);
        return SHA256.HashData(stream.GetBuffer().AsSpan(0, (int)stream.Length));
    }

    [Benchmark]
    public byte[] Pooled() => CanonicalJsonWriter.ComputeCanonicalSequenceSha256(
        document.RootElement, CanonicalJsonSequenceProfile.PreserveLexical);

    [Benchmark]
    public byte[] CryptoStream()
    {
        using var hash = SHA256.Create();
        using var stream = new System.Security.Cryptography.CryptoStream(Stream.Null, hash, CryptoStreamMode.Write);
        using (var writer = new Utf8JsonWriter(stream))
            CanonicalJsonWriter.WriteCanonicalSequence(writer, document.RootElement, CanonicalJsonSequenceProfile.PreserveLexical);
        stream.FlushFinalBlock();
        return hash.Hash!;
    }
}


[MemoryDiagnoser]
[ShortRunJob]
public class LexicalProjectionBenchmarks
{
    object payload = null!;
    JsonSerializerOptions options = null!;
    readonly CanonicalJsonPropertyExclusions exclusions = new("/canonicalHash", "/closure/inventoryHash", "/identity/contentHash");

    [Params(16, 128)]
    public int Fields { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        options = new(JsonSerializerDefaults.Web);
        payload = new
        {
            canonicalHash = "sha256:previous", closure = new { inventoryHash = "sha256:previous", count = Fields },
            identity = new { id = "synthetic-contract", contentHash = "sha256:previous" },
            entries = Enumerable.Range(0, Fields).Select(i => new { name = $"field{i:D3}", value = i + .125m, text = new string('a', 128) }).ToArray()
        };
    }

    [Benchmark(Baseline = true)]
    public byte[] NodeTree()
    {
        var node = JsonSerializer.SerializeToNode(payload, options)!.AsObject();
        node.Remove("canonicalHash");
        node["closure"]!.AsObject().Remove("inventoryHash");
        node["identity"]!.AsObject().Remove("contentHash");
        using var document = JsonSerializer.SerializeToDocument(node, options);
        return CanonicalJsonWriter.ComputeCanonicalSequenceSha256(document.RootElement, CanonicalJsonSequenceProfile.PreserveLexical);
    }

    [Benchmark]
    public byte[] DirectDocument()
    {
        using var document = JsonSerializer.SerializeToDocument(payload, options);
        return CanonicalJsonWriter.ComputeCanonicalSequenceSha256(document.RootElement,
            CanonicalJsonSequenceProfile.PreserveLexical, exclusions: exclusions);
    }
}
