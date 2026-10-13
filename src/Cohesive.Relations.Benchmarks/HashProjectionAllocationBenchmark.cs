using System.Buffers;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Relations.Benchmarks;

internal static class HashProjectionAllocationBenchmark
{
    internal static int Run(string[] args)
    {
        var count = AllocationMeasurement.Iterations(args);
        AllocationMeasurement.Header();
        var options = StrictDocumentJson.CreateOptions();
        options.MakeReadOnly(true);
        var payload = new Dictionary<string, object> { ["id"] = "omit", ["items"] = Enumerable.Range(0, 128).Reverse()
            .Select(i => new { id = $"field{i:D3}", text = "é<>&", tags = new[] { "z", "a" } }).ToArray() };
        var exclusions = new CanonicalJsonPropertyExclusions("/id");
        var writerOptions = new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        byte[] prefix = "version\0"u8.ToArray();
        static CanonicalJsonArrayOrdering Ordering(CanonicalJsonArrayPath path) => path.Value switch
        {
            "/items" => CanonicalJsonArrayOrdering.ObjectSet("id"),
            "/items/*/tags" => CanonicalJsonArrayOrdering.StringSet,
            _ => CanonicalJsonArrayOrdering.Sequence
        };
        byte[] Tree()
        {
            var node = JsonSerializer.SerializeToNode(payload, options)!.AsObject();
            node.Remove("id");
            var bytes = CanonicalJsonWriter.GetCanonicalBytes(node, options, Ordering);
            var output = new byte[prefix.Length + bytes.Length];
            prefix.CopyTo(output, 0);
            bytes.CopyTo(output, prefix.Length);
            return SHA256.HashData(output);
        }
        byte[] Stream()
        {
            using var document = JsonSerializer.SerializeToDocument(payload, options);
            return CanonicalJsonWriter.ComputeCanonicalSequenceSha256(document.RootElement, Ordering,
                writerOptions, exclusions: exclusions, prefix: prefix);
        }
        if (!Tree().AsSpan().SequenceEqual(Stream())) throw new InvalidOperationException("Hash differs.");
        AllocationMeasurement.Measure("ordered-projected-hash-128", "node-output", () => Tree(), count);
        AllocationMeasurement.Measure("ordered-projected-hash-128", "document-stream", () => Stream(), count);
        var token = new string('é', 8192);
        byte[] ContiguousTokens()
        {
            var output = new ArrayBufferWriter<byte>();
            for (var i = 0; i < 32; i++) LengthPrefixedFingerprintEncoding.Append(output, token);
            return SHA256.HashData(output.WrittenSpan);
        }
        byte[] StreamingTokens()
        {
            using Sha256BufferWriter output = new();
            for (var i = 0; i < 32; i++) LengthPrefixedFingerprintEncoding.Append(output, token);
            byte[] digest = new byte[32];
            output.Complete(digest);
            return digest;
        }
        if (!ContiguousTokens().AsSpan().SequenceEqual(StreamingTokens())) throw new InvalidOperationException("Tokens differ.");
        AllocationMeasurement.Measure("binary-tokens-512KiB", "contiguous", () => ContiguousTokens(), count);
        AllocationMeasurement.Measure("binary-tokens-512KiB", "stream", () => StreamingTokens(), count);
        return 0;
    }
}
