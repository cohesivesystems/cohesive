namespace Cohesive.AI.Text;

/// <summary>
/// Helpers for projecting tokenization results into decoded token text.
/// </summary>
public static class TokenizationResultExtensions
{
    /// <param name="result">Tokenized model input tensors.</param>
    extension(TokenizationResult result)
    {
        /// <summary>
        /// Converts encoded token ids into decoded token text.
        /// </summary>
        /// <param name="tokenizer">Tokenizer capable of decoding token ids.</param>
        /// <param name="respectAttentionMask">
        /// When true and the attention mask aligns with input ids, only tokens with non-zero mask values are decoded.
        /// </param>
        /// <returns>Decoded token text in token-id order.</returns>
        public IReadOnlyList<string> ToTokens(IDecodingTokenizer tokenizer, bool respectAttentionMask = true)
        {
            ArgumentNullException.ThrowIfNull(tokenizer);

            var inputIds = result.InputIds.Span;
            if (inputIds.IsEmpty)
                return [];

            var attentionMask = result.AttentionMask.Span;
            var useMask = respectAttentionMask && attentionMask.Length == inputIds.Length;
            List<string> tokens = new(inputIds.Length);

            for (var i = 0; i < inputIds.Length; i++)
            {
                if (useMask && attentionMask[i] == 0L)
                    continue;

                tokens.Add(tokenizer.DecodeTokenId(inputIds[i]));
            }

            return tokens;
        }

        /// <summary>
        /// Attempts to decode token ids when the tokenizer supports decoding.
        /// </summary>
        /// <param name="tokenizer">Tokenizer instance to inspect.</param>
        /// <param name="tokens">Decoded tokens when supported; otherwise an empty array.</param>
        /// <param name="respectAttentionMask">When true, non-attended tokens are omitted.</param>
        /// <returns><see langword="true"/> when decoding was available; otherwise <see langword="false"/>.</returns>
        public bool TryToTokens(ITokenizer tokenizer, out IReadOnlyList<string> tokens, bool respectAttentionMask = true)
        {
            ArgumentNullException.ThrowIfNull(tokenizer);

            if (tokenizer is IDecodingTokenizer decodingTokenizer)
            {
                tokens = result.ToTokens(decodingTokenizer, respectAttentionMask);
                return true;
            }

            tokens = [];
            return false;
        }
    }
}
