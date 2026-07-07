using System.Text;
using Cohesive.AI.Text;
using Microsoft.ML.Tokenizers;

namespace Cohesive.Adapters.MicrosoftML;

/// <summary>
/// Adapts a <see cref="Tokenizer"/> from <c>Microsoft.ML.Tokenizers</c> to <see cref="ITokenizer"/>.
/// </summary>
public sealed class MicrosoftMlTokenizer : ITokenizer
{
    readonly Tokenizer tokenizer;
    readonly int? maxTokenCount;

    /// <summary>
    /// Creates a tokenizer adapter.
    /// </summary>
    /// <param name="tokenizer">Underlying Microsoft.ML tokenizer instance.</param>
    /// <param name="maxTokenCount">Optional max output length. When set, ids are truncated.</param>
    public MicrosoftMlTokenizer(Tokenizer tokenizer, int? maxTokenCount = null)
    {
        ArgumentNullException.ThrowIfNull(tokenizer);
        
        if (maxTokenCount is <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxTokenCount), "Max token count must be positive when specified.");

        this.tokenizer = tokenizer;
        this.maxTokenCount = maxTokenCount;
    }

    /// <summary>
    /// Encodes UTF-8 text with the underlying Microsoft.ML tokenizer.
    /// </summary>
    /// <param name="utf8Input">UTF-8 encoded text.</param>
    /// <returns>Token id and attention-mask tensors.</returns>
    public TokenizationResult Encode(ReadOnlySpan<byte> utf8Input)
    {
        var text = Encoding.UTF8.GetString(utf8Input);
        var rawIds = tokenizer.EncodeToIds(text);

        var count = maxTokenCount.HasValue
            ? Math.Min(rawIds.Count, maxTokenCount.Value)
            : rawIds.Count;

        var inputIds = new long[count];
        var attentionMask = new long[count];

        for (var i = 0; i < count; i++)
        {
            inputIds[i] = rawIds[i];
            attentionMask[i] = 1L;
        }

        return new TokenizationResult(inputIds, attentionMask);
    }
}
