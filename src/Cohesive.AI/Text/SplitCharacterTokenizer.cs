using System.Text;

namespace Cohesive.AI.Text;

/// <summary>
/// Simple tokenizer that splits text by configurable separator characters over a fixed vocabulary.
/// </summary>
public sealed class SplitCharacterTokenizer : IDecodingTokenizer
{
    readonly HashSet<char> separators;
    readonly IReadOnlyDictionary<string, long> tokenToId;
    readonly IReadOnlyDictionary<long, string> idToToken;
    readonly string unknownToken;
    readonly long unknownTokenId;
    static readonly long[] AttentionMask = [1L];

    /// <summary>
    /// Creates a split-character tokenizer with a fixed token-id vocabulary.
    /// </summary>
    /// <param name="separators">Characters used to split input text into tokens.</param>
    /// <param name="vocabulary">Token to id mapping used during encoding and decoding.</param>
    /// <param name="ignoreCase">When true, token lookup is case-insensitive.</param>
    /// <param name="unknownToken">Text emitted when decoding an unknown token id.</param>
    public SplitCharacterTokenizer(
        IEnumerable<char> separators,
        IReadOnlyDictionary<string, long> vocabulary,
        bool ignoreCase = false,
        string unknownToken = "[UNK]"
        )
    {
        ArgumentNullException.ThrowIfNull(separators);
        ArgumentNullException.ThrowIfNull(vocabulary);
        ArgumentException.ThrowIfNullOrWhiteSpace(unknownToken);

        this.separators = new HashSet<char>(separators);
        if (this.separators.Count == 0)
            throw new ArgumentOutOfRangeException(nameof(separators), "At least one separator character is required.");

        var comparer = ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        this.unknownToken = unknownToken;
        tokenToId = CreateTokenToIdMap(vocabulary, comparer);
        idToToken = CreateIdToTokenMap(tokenToId);
        if (!tokenToId.TryGetValue(unknownToken, out unknownTokenId))
        {
            throw new ArgumentException(
                $"Vocabulary is missing required unknown token '{unknownToken}'.",
                nameof(vocabulary));
        }
    }

    /// <inheritdoc />
    public TokenizationResult Encode(ReadOnlySpan<byte> utf8Input)
    {
        var text = Encoding.UTF8.GetString(utf8Input);
        if (string.IsNullOrEmpty(text))
            return new TokenizationResult(new[] { unknownTokenId }, AttentionMask);

        List<long> ids = [];
        foreach (var token in EnumerateTokens(text))
            ids.Add(ResolveTokenId(token));

        if (ids.Count == 0)
            ids.Add(unknownTokenId);

        var attentionMask = new long[ids.Count];
        Array.Fill(attentionMask, 1L);

        return new TokenizationResult(ids.ToArray(), attentionMask);
    }

    /// <inheritdoc />
    public string DecodeTokenId(long tokenId)
    {
        return idToToken.GetValueOrDefault(tokenId, unknownToken);
    }

    IEnumerable<string> EnumerateTokens(string text)
    {
        var tokenStart = -1;
        for (var i = 0; i < text.Length; i++)
        {
            if (separators.Contains(text[i]))
            {
                if (tokenStart >= 0)
                {
                    var token = text[tokenStart..i];
                    if (!string.IsNullOrWhiteSpace(token))
                        yield return token;
                    tokenStart = -1;
                }

                continue;
            }

            if (tokenStart < 0)
                tokenStart = i;
        }

        if (tokenStart >= 0)
        {
            var token = text[tokenStart..];
            if (!string.IsNullOrWhiteSpace(token))
                yield return token;
        }
    }

    long ResolveTokenId(string token)
    {
        return tokenToId.GetValueOrDefault(token, unknownTokenId);
    }

    static IReadOnlyDictionary<string, long> CreateTokenToIdMap(
        IReadOnlyDictionary<string, long> source,
        StringComparer comparer)
    {
        Dictionary<string, long> map = new(comparer);
        foreach (var (token, id) in source)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(token);
            if (id < 0)
                throw new ArgumentOutOfRangeException(nameof(source), "Token ids must be non-negative.");

            map[token] = id;
        }

        if (map.Count == 0)
            throw new ArgumentOutOfRangeException(nameof(source), "Vocabulary must contain at least one token.");

        return map;
    }

    static IReadOnlyDictionary<long, string> CreateIdToTokenMap(
        IReadOnlyDictionary<string, long> tokenToId)
    {
        Dictionary<long, string> map = new();
        foreach (var (token, id) in tokenToId)
        {
            if (map.TryGetValue(id, out var existingToken))
            {
                throw new ArgumentException(
                    $"Vocabulary contains duplicate token id '{id}' for tokens '{existingToken}' and '{token}'.");
            }

            map[id] = token;
        }

        return map;
    }
}
