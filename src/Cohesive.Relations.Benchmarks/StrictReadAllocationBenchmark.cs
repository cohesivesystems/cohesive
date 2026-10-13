using System.Text.Json;
using System.Text.Json.Nodes;
using Cohesive.Model.Serialization;

namespace Cohesive.Relations.Benchmarks;

internal static class StrictReadAllocationBenchmark
{
    internal static int Run(string[] args)
    {
        var count = AllocationMeasurement.Iterations(args);
        AllocationMeasurement.Header();
        var options = StrictDocumentJson.CreateOptions();
        options.MakeReadOnly(true);
        var wire = System.Text.Encoding.UTF8.GetString(StrictDocumentJson.GetCanonicalBytes(new Wire(Enumerable.Range(0, 128).ToDictionary(i => $"field{i:D3}", i => i)), options));
        bool FormerStrictSuccessPath()
        {
            using var document = JsonDocument.Parse(wire);
            if (StrictDocumentJson.TryFindDuplicateProperty(document.RootElement, string.Empty, out _)) return false;
            var node = JsonNode.Parse(document.RootElement.GetRawText())!;
            var before = CanonicalJsonWriter.GetCanonicalSequenceBytes(node, options, CanonicalJsonNumberSemantics.ExactDecimalRational);
            var decoded = JsonSerializer.Deserialize<Wire>(wire, options)!;
            return before.AsSpan().SequenceEqual(StrictDocumentJson.GetCanonicalBytes(decoded, options));
        }
        if (!FormerStrictSuccessPath() || !StrictDocumentJson.TryReadCanonicalObject<Wire>(wire, options, "wire", out _, out _))
            throw new InvalidOperationException("Strict wire differs.");
        AllocationMeasurement.Measure("strict-document-128", "former-success-path", () => FormerStrictSuccessPath(), count);
        AllocationMeasurement.Measure("strict-document-128", "current", () => StrictDocumentJson.TryReadCanonicalObject<Wire>(wire, options, "wire", out _, out _), count);
        return 0;
    }
    public sealed record Wire(Dictionary<string, int> Payload);
}
