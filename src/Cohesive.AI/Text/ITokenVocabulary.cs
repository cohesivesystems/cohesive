using System.Collections.Immutable;
using System.Runtime.CompilerServices;

namespace Cohesive.AI.Text;

/// <summary>
/// Shared token vocabulary used to convert between compact token ids and normalized token text.
/// </summary>
public interface ITokenVocabulary
{
    /// <summary>
    /// Number of interned tokens.
    /// </summary>
    int Count { get; }

    /// <summary>
    /// Resolves an existing token id when present.
    /// </summary>
    bool TryGetId(string token, out int tokenId);

    /// <summary>
    /// Resolves one token id back to token text.
    /// </summary>
    string GetTokenString(int tokenId);
}

/// <summary>
/// Shared token vocabulary used to convert between compact token ids and normalized token text.
/// </summary>
public sealed class TokenVocabulary : ITokenVocabulary
{
    readonly Dictionary<string, int> tokenToId;
    readonly List<string> idToToken;

    /// <summary>
    /// Creates an empty token vocabulary.
    /// </summary>
    public TokenVocabulary(StringComparer? comparer = null)
    {
        tokenToId = new(comparer ?? StringComparer.Ordinal);
        idToToken = [];
    }

    /// <summary>
    /// Number of unique tokens interned in the vocabulary.
    /// </summary>
    public int Count => idToToken.Count;

    /// <summary>
    /// Resolves an existing token id when present.
    /// </summary>
    public bool TryGetId(string token, out int tokenId)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            tokenId = 0;
            return false;
        }

        return tokenToId.TryGetValue(token, out tokenId);
    }

    /// <summary>
    /// Interns one normalized token and returns its compact integer id.
    /// </summary>
    public int GetOrAddId(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        if (tokenToId.TryGetValue(token, out var existing))
            return existing;
        
        var tokenId = idToToken.Count;
        tokenToId[token] = tokenId;
        idToToken.Add(token);
        return tokenId;
    }

    /// <summary>
    /// Resolves one token id back to normalized token text.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public string GetToken(int tokenId) => idToToken[tokenId];

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public string GetTokenString(int tokenId) => GetToken(tokenId);

    /// <summary>
    /// Decodes a token-id span into normalized token text.
    /// </summary>
    public ImmutableArray<string> Decode(ReadOnlySpan<int> tokenIds)
    {
        if (tokenIds.IsEmpty)
            return [];

        var builder = ImmutableArray.CreateBuilder<string>(tokenIds.Length);
        foreach (var tokenId in tokenIds)
            builder.Add(GetToken(tokenId));

        return [.. builder];
    }
}
