using System.Buffers;
using System.Runtime.CompilerServices;

namespace Cohesive.AI.Text;

/// <summary>
/// Incrementally builds a <see cref="TokenTable"/>.
/// </summary>
public sealed class TokenTableBuilder
{
    readonly ArrayBufferWriter<int> buffer = new();

    /// <summary>
    /// Appends one token-id sequence and returns its compact span.
    /// </summary>
    public TokenSpan Add(ReadOnlySpan<int> tokenIds)
    {
        if (tokenIds.IsEmpty)
            return TokenSpan.Empty;

        var span = new TokenSpan(buffer.WrittenCount, tokenIds.Length);
        buffer.Write(tokenIds);
        return span;
    }

    /// <summary>
    /// Returns a span from the in-progress backing buffer.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<int> GetSpan(TokenSpan span) =>
        span.IsEmpty ? [] : buffer.WrittenSpan.Slice(span.Offset, span.Length);

    /// <summary>
    /// Builds the immutable token table.
    /// </summary>
    public TokenTable Build() =>
        buffer.WrittenCount == 0 ? TokenTable.Empty : new([.. buffer.WrittenSpan]);
}

/// <summary>
/// Helpers for manipulating token-id spans in a shared token table.
/// </summary>
public static class TokenTableBuilderExtensions
{
    /// <param name="tokenTableBuilder">The shared token table builder that owns the input spans and receives the merged output.</param>
    extension(TokenTableBuilder tokenTableBuilder)
    {
        /// <summary>
        /// Writes a sorted, unique expanded token-id set into the shared token table.
        /// </summary>
        public TokenSpan WriteExpandedTokens(TokenSpan baseTokens, ITokenExpansionLexicon lexicon)
        {
            ArgumentNullException.ThrowIfNull(lexicon);
            ArgumentNullException.ThrowIfNull(tokenTableBuilder);

            if (baseTokens.IsEmpty)
                return TokenSpan.Empty;

            HashSet<int> seen = [];
            List<int> expanded = [];
            foreach (var tokenId in tokenTableBuilder.GetSpan(baseTokens))
            {
                if (seen.Add(tokenId))
                    expanded.Add(tokenId);

                foreach (var expansionTokenId in lexicon.GetExpansionTokenIds(tokenId))
                {
                    if (!seen.Add(expansionTokenId))
                        continue;

                    expanded.Add(expansionTokenId);
                }
            }

            if (expanded.Count == 0)
                return TokenSpan.Empty;

            expanded.Sort();
            return tokenTableBuilder.Add([..expanded]);
        }

        /// <summary>
        /// Builds one ordered union of token spans and appends it to the shared token table.
        /// </summary>
        /// <param name="spans">The token spans to union in order.</param>
        /// <returns>
        /// A span referencing the merged token ids in insertion order, with duplicates removed.
        /// Returns <see cref="TokenSpan.Empty"/> when every input span is empty.
        /// </returns>
        public TokenSpan BuildUnionTokenSpan(ReadOnlySpan<TokenSpan> spans) =>
            CollectDistinct(tokenTableBuilder, spans);

        /// <summary>
        /// Merges multiple token spans into one ordered distinct span in the shared token table.
        /// </summary>
        /// <param name="spans">The spans to merge in order.</param>
        /// <returns>
        /// A span containing the first occurrence of each token id across the supplied spans.
        /// Returns <see cref="TokenSpan.Empty"/> when no token ids are present.
        /// </returns>
        public TokenSpan MergeDistinct(ReadOnlySpan<TokenSpan> spans) =>
            CollectDistinct(tokenTableBuilder, spans);

        /// <summary>
        /// Writes a sorted, duplicate-free token-id sequence into the shared token table.
        /// </summary>
        /// <param name="tokenIds">The token ids to sort and deduplicate.</param>
        /// <param name="tokenVocabulary">The vocabulary used to impose deterministic lexical ordering.</param>
        /// <returns>
        /// A span referencing the sorted unique token ids.
        /// Returns <see cref="TokenSpan.Empty"/> when the input sequence contains no token ids.
        /// </returns>
        public TokenSpan WriteSortedDistinct(IEnumerable<int> tokenIds, TokenVocabulary tokenVocabulary)
        {
            var ordered = tokenIds
                .Distinct()
                .OrderBy(tokenVocabulary.GetToken, StringComparer.Ordinal)
                .ToArray();

            return ordered.Length == 0 ? TokenSpan.Empty : tokenTableBuilder.Add(ordered);
        }
        
        /// <summary>
        /// Re-encodes a token sequence into a target vocabulary and appends the remapped ids to the shared token table.
        /// </summary>
        /// <param name="sourceTokens">
        /// The source token sequence to re-encode. Token ids are decoded through its current vocabulary and re-interned
        /// into <paramref name="targetVocabulary"/> while preserving the original token order.
        /// </param>
        /// <param name="targetVocabulary">The vocabulary that receives the re-interned token strings and defines the output token ids.</param>
        /// <returns>
        /// A span referencing the re-encoded token ids in the shared token table.
        /// Returns <see cref="TokenSpan.Empty"/> when <paramref name="sourceTokens"/> is empty.
        /// </returns>
        public TokenSpan Reindex(IndexedTokenSequence sourceTokens, TokenVocabulary targetVocabulary)
        {
            if (sourceTokens.IsEmpty)
                return TokenSpan.Empty;

            var tokenIds = sourceTokens.TokenIds;
            var encoded = new int[tokenIds.Length];
            for (var i = 0; i < tokenIds.Length; i++)
                encoded[i] = targetVocabulary.GetOrAddId(sourceTokens.Vocabulary.GetToken(tokenIds[i]));

            return tokenTableBuilder.Add(encoded);
        }
    }

    static TokenSpan CollectDistinct(TokenTableBuilder tokenTableBuilder, ReadOnlySpan<TokenSpan> spans)
    {
        ArgumentNullException.ThrowIfNull(tokenTableBuilder);

        Span<int> distinctTokenIds = stackalloc int[16];
        int[]? rented = null;
        var count = 0;

        try
        {
            foreach (var span in spans)
            {
                if (span.IsEmpty)
                    continue;

                foreach (var tokenId in tokenTableBuilder.GetSpan(span))
                {
                    if (Contains(distinctTokenIds[..count], tokenId))
                        continue;

                    if (count == distinctTokenIds.Length)
                    {
                        var grown = ArrayPool<int>.Shared.Rent(distinctTokenIds.Length * 2);
                        distinctTokenIds[..count].CopyTo(grown);
                        if (rented is not null)
                            ArrayPool<int>.Shared.Return(rented);

                        rented = grown;
                        distinctTokenIds = grown;
                    }

                    distinctTokenIds[count++] = tokenId;
                }
            }

            return count == 0
                ? TokenSpan.Empty
                : tokenTableBuilder.Add(distinctTokenIds[..count]);
        }
        finally
        {
            if (rented is not null)
                ArrayPool<int>.Shared.Return(rented);
        }

        static bool Contains(ReadOnlySpan<int> tokenIds, int tokenId)
        {
            for (var i = 0; i < tokenIds.Length; i++)
            {
                if (tokenIds[i] == tokenId)
                    return true;
            }

            return false;
        }
    }
}
