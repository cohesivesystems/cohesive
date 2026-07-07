namespace Cohesive.AI.Text;

/// <summary>
/// Extends <see cref="ITokenizer"/> with token-id decoding support.
/// </summary>
public interface IDecodingTokenizer : ITokenizer
{
    /// <summary>
    /// Decodes one token identifier into a token string.
    /// </summary>
    /// <param name="tokenId">Encoded token identifier.</param>
    /// <returns>Decoded token text.</returns>
    string DecodeTokenId(long tokenId);
}
