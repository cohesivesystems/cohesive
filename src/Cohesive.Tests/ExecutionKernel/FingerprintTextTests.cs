using Cohesive.Execution;
using Cohesive.Model;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class FingerprintTextTests
{
    [Theory]
    [InlineData("sha256", "cohesive-execution-definition/v1-c14n/v1", "abc", "sha256:cohesive-execution-definition/v1-c14n/v1:abc")]
    [InlineData("a:b", "c%3Ad", "x:y%", "a%3Ab:c%253Ad:x%3Ay%25")]
    [InlineData("算法", "profile/α", "123", "算法:profile/α:123")]
    public void SharedEncodingPreservesOrdinaryTextAndSeparatesEscapedComponents(string algorithm, string profile, string digest, string expected)
    {
        var fingerprint = new ExecutionDefinitionFingerprint(algorithm, profile, digest);
        Assert.Equal(expected, fingerprint.ToCanonicalString());
        Assert.Equal(expected, FingerprintText.Format(algorithm, profile, digest));
    }

    [Fact]
    public void ComponentBoundariesAndLiteralEscapeSequencesCannotCollide()
    {
        Assert.NotEqual(FingerprintText.Format("a:b", "c", "d"), FingerprintText.Format("a", "b:c", "d"));
        Assert.NotEqual(FingerprintText.Format("a:b", "c", "d"), FingerprintText.Format("a%3Ab", "c", "d"));
    }

    [Fact]
    public void OrdinaryFormattingAllocatesOnlyTheResultLikePriorInterpolation()
    {
        var fingerprint = new ExecutionDefinitionFingerprint("sha256", "profile/v1", new string('a', 64));
        static string Prior(ExecutionDefinitionFingerprint f) => $"{f.Algorithm}:{f.Canonicalization}:{f.Value}";
        for (var i = 0; i < 100; i++) { _ = Prior(fingerprint); _ = fingerprint.ToCanonicalString(); }
        var start = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++) GC.KeepAlive(Prior(fingerprint));
        var previous = GC.GetAllocatedBytesForCurrentThread() - start;
        start = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++) GC.KeepAlive(fingerprint.ToCanonicalString());
        Assert.Equal(previous, GC.GetAllocatedBytesForCurrentThread() - start);
    }
}
