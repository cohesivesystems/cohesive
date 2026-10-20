using System.Buffers;
using System.Collections.Immutable;
using Cohesive.Model.Serialization;

namespace Cohesive.Tests.Model;

public sealed class OrdinalFingerprintSetTests
{
    [Fact]
    public void WireBytesKeepOrdinalUtf16OrderingAndDeduplication()
    {
        ImmutableArray<string> values = ["😀", "\uE000", "é", "a", "😀", ""];
        var output = new ArrayBufferWriter<byte>();
        LengthPrefixedFingerprintEncoding.AppendOrdinalSet(output, values, static value => value);
        Assert.Equal("0000000500000000000000016100000002C3A900000004F09F988000000003EE8080",
            Convert.ToHexString(output.WrittenSpan));
        Assert.Equal(new[] { "😀", "\uE000", "é", "a", "😀", "" }, values);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(128)]
    [InlineData(1024)]
    public void CanonicalReverseAndDuplicateInputsKeepLegacyBytes(int count)
    {
        var sorted = Enumerable.Range(0, count).Select(i => $"id/{i:D4}/é🙂").ToImmutableArray();
        foreach (var values in new[] { sorted, sorted.Reverse().ToImmutableArray(), sorted.Concat(sorted).Reverse().ToImmutableArray() })
        {
            var expected = new ArrayBufferWriter<byte>();
            var legacy = values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            LengthPrefixedFingerprintEncoding.Append(expected, legacy.Length);
            foreach (var value in legacy) LengthPrefixedFingerprintEncoding.Append(expected, value);
            var actual = new ArrayBufferWriter<byte>();
            LengthPrefixedFingerprintEncoding.AppendOrdinalSet(actual, values, static value => value);
            Assert.Equal(expected.WrittenSpan.ToArray(), actual.WrittenSpan.ToArray());
        }
    }

    [Theory]
    [InlineData(128)]
    [InlineData(1024)]
    public void WarmCanonicalEncodingHasNoPerEntryAllocation(int count)
    {
        var values = Enumerable.Range(0, count).Select(i => $"id/{i:D4}").ToImmutableArray();
        var output = new ArrayBufferWriter<byte>(16384);
        for (var i = 0; i < 20; i++)
        {
            output.Clear();
            LengthPrefixedFingerprintEncoding.AppendOrdinalSet(output, values, static value => value);
        }
        var samples = new long[7];
        for (var i = 0; i < samples.Length; i++)
        {
            output.Clear();
            var before = GC.GetAllocatedBytesForCurrentThread();
            LengthPrefixedFingerprintEncoding.AppendOrdinalSet(output, values, static value => value);
            samples[i] = GC.GetAllocatedBytesForCurrentThread() - before;
        }
        Array.Sort(samples);
        Assert.Equal(0, samples[3]);
    }

}
