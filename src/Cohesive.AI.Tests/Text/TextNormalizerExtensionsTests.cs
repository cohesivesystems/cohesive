using System.Buffers;
using Cohesive.AI.Text;

namespace Cohesive.AI.Tests.Text;

public sealed class TextNormalizerExtensionsTests
{
    [Fact]
    public void NormalizeToString_WritesNormalizedTextToReturnedString()
    {
        ITextNormalizer normalizer = new UpperInvariantNormalizer();

        var normalized = normalizer.NormalizeToString("order_id".AsSpan());

        Assert.Equal("ORDER_ID", normalized);
    }
    

    sealed class UpperInvariantNormalizer : ITextNormalizer
    {
        public void Normalize(ReadOnlySpan<char> input, IBufferWriter<char> output)
        {
            var destination = output.GetSpan(input.Length);
            for (var i = 0; i < input.Length; i++)
                destination[i] = char.ToUpperInvariant(input[i]);
            output.Advance(input.Length);
        }
    }
}
