using System.Buffers;
using System.Security.Cryptography;
using Cohesive.Model.Serialization;

namespace Cohesive.Tests.Model;

public sealed class LengthPrefixedFingerprintEncodingTests
{
    [Fact]
    public void TokensKeepExistingBigEndianLengthAndScalarFormat()
    {
        var output = new ArrayBufferWriter<byte>();
        LengthPrefixedFingerprintEncoding.Append(output, "é");
        LengthPrefixedFingerprintEncoding.Append(output, "");
        LengthPrefixedFingerprintEncoding.Append(output, new byte[] { 255 }.AsSpan());
        LengthPrefixedFingerprintEncoding.Append(output, -2);
        LengthPrefixedFingerprintEncoding.Append(output, -3L);
        LengthPrefixedFingerprintEncoding.Append(output, true);
        LengthPrefixedFingerprintEncoding.Append(output, false);
        Assert.Equal("00000002C3A90000000000000001FFFFFFFFFEFFFFFFFFFFFFFFFD0100", Convert.ToHexString(output.WrittenSpan));
    }

    [Fact]
    public void StreamingTokensKeepTheDigestWithoutGrowingOutputAllocation()
    {
        var token = new string('é', 8192);
        var expected = new ArrayBufferWriter<byte>();
        for (var index = 0; index < 32; index++) LengthPrefixedFingerprintEncoding.Append(expected, token);
        var digest = SHA256.HashData(expected.WrittenSpan);
        byte[] Hash()
        {
            using Sha256BufferWriter writer = new();
            for (var index = 0; index < 32; index++) LengthPrefixedFingerprintEncoding.Append(writer, token);
            var result = new byte[32];
            writer.Complete(result);
            return result;
        }
        for (var index = 0; index < 20; index++) Assert.Equal(digest, Hash());
        var samples = new long[7];
        for (var index = 0; index < samples.Length; index++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var actual = Hash();
            samples[index] = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(digest, actual);
        }
        Array.Sort(samples);
        Assert.InRange(samples[3], 0, 512);
    }
}
