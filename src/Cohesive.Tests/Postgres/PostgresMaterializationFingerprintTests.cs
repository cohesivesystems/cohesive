using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Cohesive.Adapters.Postgres;
using Xunit.Abstractions;

namespace Cohesive.Tests.Postgres;

public sealed class PostgresMaterializationFingerprintTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(1024)]
    [InlineData(65536)]
    [InlineData(262144)]
    public void Warm_ledger_serialization_and_hashing_have_bounded_allocations(int payloadBytes)
    {
        var node = new JsonObject { ["nested"] = new JsonObject { ["payload"] = new string('x', payloadBytes), ["a"] = 1 } };
        string Legacy() => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(node.ToJsonString())));
        string Canonical() => PostgresMaterializationDocumentAuthority.Fingerprint(node.ToJsonString());
        var legacy = Measure(Legacy);
        var canonical = Measure(Canonical);
        output.WriteLine($"payload={payloadBytes}; warm serialize+hash, 32 iterations: legacy {legacy.Bytes} B/op, {legacy.Microseconds:F1} us/op; canonical {canonical.Bytes} B/op, {canonical.Microseconds:F1} us/op");
        // Guard against accidental retained copies or superlinear payload amplification, not a speed claim.
        Assert.True(canonical.Bytes < payloadBytes * 16L + 32768, $"Unexpected allocation: {canonical.Bytes}");
        Assert.Equal(PostgresMaterializationDocumentAuthority.Fingerprint("{\"b\":1,\"a\":[2,3]}"),
            PostgresMaterializationDocumentAuthority.Fingerprint("{\"a\":[2,3],\"b\":1.0}"));
    }

    static (long Bytes, double Microseconds) Measure(Func<string> operation)
    {
        for (var i = 0; i < 8; i++) GC.KeepAlive(operation());
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var started = Stopwatch.GetTimestamp();
        for (var i = 0; i < 32; i++) GC.KeepAlive(operation());
        return ((GC.GetAllocatedBytesForCurrentThread() - allocated) / 32,
            Stopwatch.GetElapsedTime(started).TotalMicroseconds / 32);
    }
}
