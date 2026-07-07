namespace Cohesive.AI.Text;

/// <summary>
/// Encodes UTF-8 text into tokenizer-specific tensors.
/// </summary>
public interface ITokenizer
{
    /// <summary>
    /// Encodes a UTF-8 input span.
    /// </summary>
    /// <param name="utf8Input">UTF-8 encoded input text.</param>
    /// <returns>Tokenized representation of the input.</returns>
    TokenizationResult Encode(ReadOnlySpan<byte> utf8Input);
}
