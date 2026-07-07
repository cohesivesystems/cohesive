using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Cohesive.AI.Text;

/// <summary>
/// Set-based token scoring utilities.
/// </summary>
public static class TokenSetScoring
{
    /// <summary>
    /// Counts the number of shared token ids across two sorted distinct token sets.
    /// This is the cheaper fast path for callers that already provide ascending, duplicate-free inputs.
    /// </summary>
    /// <param name="left">The first ascending, duplicate-free token set.</param>
    /// <param name="right">The second ascending, duplicate-free token set.</param>
    /// <returns>The size of the intersection.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int CountIntersectionSortedDistinct(ReadOnlySpan<int> left, ReadOnlySpan<int> right)
    {
        AssertSortedDistinct(left, nameof(left));
        AssertSortedDistinct(right, nameof(right));

        var i = 0;
        var j = 0;
        var count = 0;

        while (i < left.Length && j < right.Length)
        {
            var a = left[i];
            var b = right[j];
            if (a == b)
            {
                count++;
                i++;
                j++;
            }
            else if (a < b)
            {
                i++;
            }
            else
            {
                j++;
            }
        }

        return count;
    }

    /// <summary>
    /// Computes the Jaccard similarity over two sorted distinct token sets.
    /// This is the cheaper fast path for callers that already provide ascending, duplicate-free inputs.
    /// </summary>
    /// <param name="left">The first ascending, duplicate-free token set.</param>
    /// <param name="right">The second ascending, duplicate-free token set.</param>
    /// <returns>The Jaccard similarity.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float JaccardSortedDistinct(ReadOnlySpan<int> left, ReadOnlySpan<int> right)
    {
        if (left.Length == 0 && right.Length == 0)
            return 1f;

        var intersection = CountIntersectionSortedDistinct(left, right);
        var union = left.Length + right.Length - intersection;
        return union == 0 ? 0f : (float)intersection / union;
    }

    /// <summary>
    /// Computes the overlap coefficient over two sorted distinct token sets.
    /// This is the cheaper fast path for callers that already provide ascending, duplicate-free inputs.
    /// </summary>
    /// <param name="left">The first ascending, duplicate-free token set.</param>
    /// <param name="right">The second ascending, duplicate-free token set.</param>
    /// <returns>The overlap coefficient.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float OverlapCoefficientSortedDistinct(ReadOnlySpan<int> left, ReadOnlySpan<int> right)
    {
        var minLength = Math.Min(left.Length, right.Length);
        if (minLength == 0)
            return 0f;

        var intersection = CountIntersectionSortedDistinct(left, right);
        return (float)intersection / minLength;
    }

    /// <summary>
    /// Computes the containment score of the first token set within the second.
    /// This is the cheaper fast path for callers that already provide ascending, duplicate-free inputs.
    /// </summary>
    /// <param name="left">The ascending, duplicate-free token set being contained.</param>
    /// <param name="right">The ascending, duplicate-free token set providing coverage.</param>
    /// <returns>The fraction of tokens from <paramref name="left"/> found in <paramref name="right"/>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float ContainmentSortedDistinct(ReadOnlySpan<int> left, ReadOnlySpan<int> right)
    {
        if (left.Length == 0)
            return 0f;

        return (float)CountIntersectionSortedDistinct(left, right) / left.Length;
    }

    /// <summary>
    /// Computes overlap statistics for two sorted distinct token sets.
    /// This is the cheaper fast path for callers that already provide ascending, duplicate-free inputs.
    /// </summary>
    /// <param name="left">The first ascending, duplicate-free token set.</param>
    /// <param name="right">The second ascending, duplicate-free token set.</param>
    /// <returns>Overlap statistics for the two sorted distinct token sets.</returns>
    public static TokenOverlapStats ComputeOverlapStatsSortedDistinct(ReadOnlySpan<int> left, ReadOnlySpan<int> right)
    {
        if (left.IsEmpty || right.IsEmpty)
            return TokenOverlapStats.Empty;

        var intersection = CountIntersectionSortedDistinct(left, right);
        var union = left.Length + right.Length - intersection;
        return new(
            Intersection: intersection,
            Union: union,
            LeftUniqueCount: left.Length,
            RightUniqueCount: right.Length
            );
    }

    /// <summary>
    /// Computes overlap statistics for arbitrary token sequences by deduplicating each side before scoring.
    /// Use this when inputs are not guaranteed to be sorted unique.
    /// </summary>
    /// <param name="left">The first token sequence.</param>
    /// <param name="right">The second token sequence.</param>
    /// <returns>Distinct-token overlap statistics for the two sequences.</returns>
    public static TokenOverlapStats ComputeDistinctOverlapStats(ReadOnlySpan<int> left, ReadOnlySpan<int> right)
    {
        if (left.IsEmpty || right.IsEmpty)
            return TokenOverlapStats.Empty;

        var leftSet = CreateUniqueSet(left);
        var rightSet = CreateUniqueSet(right);
        if (leftSet.Count == 0 || rightSet.Count == 0)
            return TokenOverlapStats.Empty;

        var smaller = leftSet.Count <= rightSet.Count ? leftSet : rightSet;
        var larger = ReferenceEquals(smaller, leftSet) ? rightSet : leftSet;
        var intersection = 0;
        foreach (var tokenId in smaller)
        {
            if (larger.Contains(tokenId))
                intersection++;
        }

        var union = leftSet.Count + rightSet.Count - intersection;
        return new(
            Intersection: intersection,
            Union: union,
            LeftUniqueCount: leftSet.Count,
            RightUniqueCount: rightSet.Count);

        static HashSet<int> CreateUniqueSet(ReadOnlySpan<int> tokens)
        {
            HashSet<int> unique = [];
            foreach (var tokenId in tokens)
                _ = unique.Add(tokenId);

            return unique;
        }
    }
    
    /// <summary>
    /// Computes the Jaccard similarity of two token-sequence prefixes after deduplicating tokens within each prefix.
    /// </summary>
    /// <param name="left">The first token sequence.</param>
    /// <param name="leftLength">The number of leading tokens from <paramref name="left"/> to include.</param>
    /// <param name="right">The second token sequence.</param>
    /// <param name="rightLength">The number of leading tokens from <paramref name="right"/> to include.</param>
    /// <returns>The Jaccard similarity of the distinct token ids contained in the requested prefixes.</returns>
    public static float JaccardDistinctPrefixes(ReadOnlySpan<int> left, int leftLength, ReadOnlySpan<int> right, int rightLength)
    {
        leftLength = Math.Clamp(leftLength, 0, left.Length);
        rightLength = Math.Clamp(rightLength, 0, right.Length);
        if (leftLength == 0 || rightLength == 0)
            return 0f;

        HashSet<int> rightSet = [];
        foreach (var tokenId in right[..rightLength])
            _ = rightSet.Add(tokenId);

        if (rightSet.Count == 0)
            return 0f;

        HashSet<int> leftSeen = [];
        var leftUnique = 0;
        var intersection = 0;
        foreach (var tokenId in left[..leftLength])
        {
            if (!leftSeen.Add(tokenId))
                continue;

            leftUnique++;
            if (rightSet.Contains(tokenId))
                intersection++;
        }

        if (leftUnique == 0)
            return 0f;

        var union = leftUnique + rightSet.Count - intersection;
        return union <= 0
            ? 0f
            : Math.Clamp((float)intersection / union, 0f, 1f);
    }

    /// <summary>
    /// Writes token ids shared by both sequences into the destination span in left-sequence order, omitting duplicates.
    /// </summary>
    /// <param name="left">The source sequence that defines output order.</param>
    /// <param name="right">The sequence providing membership for shared-token detection.</param>
    /// <param name="destination">The span that receives shared distinct token ids.</param>
    /// <returns>The number of token ids written to <paramref name="destination"/>.</returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="destination"/> is too small for the shared distinct token ids.</exception>
    public static int WriteSharedDistinctLeftOrdered(ReadOnlySpan<int> left, ReadOnlySpan<int> right, Span<int> destination)
    {
        if (left.IsEmpty || right.IsEmpty)
            return 0;

        HashSet<int> rightSet = [];
        foreach (var tokenId in right)
            _ = rightSet.Add(tokenId);

        if (rightSet.Count == 0)
            return 0;

        HashSet<int> leftSeen = [];
        var written = 0;
        foreach (var tokenId in left)
        {
            if (!leftSeen.Add(tokenId))
                continue;
            if (!rightSet.Contains(tokenId))
                continue;
            
            if (written >= destination.Length)
                throw new ArgumentException("Destination span is too small for the shared distinct token ids.", nameof(destination));

            destination[written++] = tokenId;
        }

        return written;
    }

    [Conditional("DEBUG")]
    static void AssertSortedDistinct(ReadOnlySpan<int> tokenIds, string paramName)
    {
        for (var i = 1; i < tokenIds.Length; i++)
        {
            Debug.Assert(
                tokenIds[i - 1] < tokenIds[i],
                $"TokenSetScoring fast-path helpers require ascending, duplicate-free token ids. Parameter '{paramName}' violated the precondition at index {i}."
                );
        }
    }
}

/// <summary>
/// Distinct-token overlap statistics for two token sequences.
/// </summary>
/// <param name="Intersection">The number of distinct token ids shared by both sides.</param>
/// <param name="Union">The number of distinct token ids across both sides.</param>
/// <param name="LeftUniqueCount">The number of distinct token ids on the left side.</param>
/// <param name="RightUniqueCount">The number of distinct token ids on the right side.</param>
public readonly record struct TokenOverlapStats(int Intersection, int Union, int LeftUniqueCount, int RightUniqueCount)
{
    /// <summary>
    /// Shared empty overlap statistics.
    /// </summary>
    public static TokenOverlapStats Empty { get; } = new(0, 0, 0, 0);

    /// <summary>
    /// Gets the Jaccard similarity implied by the overlap statistics.
    /// </summary>
    public float Jaccard => Union <= 0 ? 0f : Math.Clamp((float)Intersection / Union, 0f, 1f);

    /// <summary>
    /// Gets the containment of the left distinct token set within the right set.
    /// </summary>
    public float LeftContainment => LeftUniqueCount <= 0 ? 0f : Math.Clamp((float)Intersection / LeftUniqueCount, 0f, 1f);

    /// <summary>
    /// Gets the containment of the right distinct token set within the left set.
    /// </summary>
    public float RightContainment => RightUniqueCount <= 0 ? 0f : Math.Clamp((float)Intersection / RightUniqueCount, 0f, 1f);

    /// <summary>
    /// Gets the overlap coefficient implied by the overlap statistics.
    /// </summary>
    public float OverlapCoefficient
    {
        get
        {
            var denominator = Math.Min(LeftUniqueCount, RightUniqueCount);
            return denominator <= 0 ? 0f : Math.Clamp((float)Intersection / denominator, 0f, 1f);
        }
    }
}

/// <summary>
/// Order-sensitive token scoring utilities over base-token sequences.
/// </summary>
public static class OrderedTokenScoring
{
    /// <summary>
    /// Computes the number of matching tokens at the start of two sequences.
    /// </summary>
    /// <param name="left">The first ordered token sequence.</param>
    /// <param name="right">The second ordered token sequence.</param>
    /// <returns>The common ordered prefix length.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int CommonPrefixLength(ReadOnlySpan<int> left, ReadOnlySpan<int> right)
    {
        var count = 0;
        var length = Math.Min(left.Length, right.Length);
        while (count < length && left[count] == right[count])
            count++;
        return count;
    }

    /// <summary>
    /// Determines whether two ordered token sequences start with the same token id.
    /// </summary>
    /// <param name="left">The first ordered token sequence.</param>
    /// <param name="right">The second ordered token sequence.</param>
    /// <returns><see langword="true"/> when both sequences have a first token, and it matches; otherwise <see langword="false"/>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool FirstTokenMatches(ReadOnlySpan<int> left, ReadOnlySpan<int> right)
        => left.Length > 0 && right.Length > 0 && left[0] == right[0];

    /// <summary>
    /// Determines whether two ordered token sequences end with the same token id.
    /// </summary>
    /// <param name="left">The first ordered token sequence.</param>
    /// <param name="right">The second ordered token sequence.</param>
    /// <returns><see langword="true"/> when both sequences have a last token, and it matches; otherwise <see langword="false"/>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool LastTokenMatches(ReadOnlySpan<int> left, ReadOnlySpan<int> right)
        => left.Length > 0 && right.Length > 0 && left[^1] == right[^1];

    /// <summary>
    /// Computes the longest common subsequence length across two ordered token sequences.
    /// </summary>
    /// <param name="left">The first ordered token sequence.</param>
    /// <param name="right">The second ordered token sequence.</param>
    /// <returns>The length of the longest common subsequence.</returns>
    public static int LongestCommonSubsequenceLength(ReadOnlySpan<int> left, ReadOnlySpan<int> right)
    {
        if (left.IsEmpty || right.IsEmpty)
            return 0;

        var cols = right.Length + 1;

        int[]? rentedPrevious = null;
        int[]? rentedCurrent = null;

        var previous = cols <= 128
            ? stackalloc int[cols]
            : (rentedPrevious = ArrayPool<int>.Shared.Rent(cols)).AsSpan(0, cols);

        var current = cols <= 128
            ? stackalloc int[cols]
            : (rentedCurrent = ArrayPool<int>.Shared.Rent(cols)).AsSpan(0, cols);

        previous.Clear();
        current.Clear();

        try
        {
            for (var i = 1; i <= left.Length; i++)
            {
                current[0] = 0;
                for (var j = 1; j <= right.Length; j++)
                {
                    if (left[i - 1] == right[j - 1])
                    {
                        current[j] = previous[j - 1] + 1;
                    }
                    else
                    {
                        current[j] = Math.Max(previous[j], current[j - 1]);
                    }
                }

                var temp = previous;
                previous = current;
                current = temp;
            }

            return previous[right.Length];
        }
        finally
        {
            if (rentedPrevious is not null)
                ArrayPool<int>.Shared.Return(rentedPrevious, clearArray: true);

            if (rentedCurrent is not null)
                ArrayPool<int>.Shared.Return(rentedCurrent, clearArray: true);
        }
    }

    /// <summary>
    /// Computes a normalized longest-common-subsequence similarity across two ordered token sequences.
    /// </summary>
    /// <param name="left">The first ordered token sequence.</param>
    /// <param name="right">The second ordered token sequence.</param>
    /// <returns>
    /// The longest common subsequence length divided by the longer input sequence length.
    /// Returns <c>0</c> when both sequences are empty.
    /// </returns>
    public static float NormalizedLongestCommonSubsequence(ReadOnlySpan<int> left, ReadOnlySpan<int> right)
    {
        var maxLength = Math.Max(left.Length, right.Length);
        if (maxLength == 0)
            return 0f;

        return Math.Clamp((float)LongestCommonSubsequenceLength(left, right) / maxLength, 0f, 1f);
    }

    /// <summary>
    /// Computes the Levenshtein edit distance across two ordered token sequences.
    /// </summary>
    /// <param name="left">The first ordered token sequence.</param>
    /// <param name="right">The second ordered token sequence.</param>
    /// <returns>The minimum number of insertions, deletions, and substitutions needed to transform one sequence into the other.</returns>
    public static int LevenshteinDistance(ReadOnlySpan<int> left, ReadOnlySpan<int> right)
    {
        if (left.IsEmpty)
            return right.Length;
        if (right.IsEmpty)
            return left.Length;

        var cols = right.Length + 1;

        int[]? rentedPrevious = null;
        int[]? rentedCurrent = null;

        var previous = cols <= 128
            ? stackalloc int[cols]
            : (rentedPrevious = ArrayPool<int>.Shared.Rent(cols)).AsSpan(0, cols);

        var current = cols <= 128
            ? stackalloc int[cols]
            : (rentedCurrent = ArrayPool<int>.Shared.Rent(cols)).AsSpan(0, cols);

        try
        {
            for (var j = 0; j < cols; j++)
                previous[j] = j;

            for (var i = 1; i <= left.Length; i++)
            {
                current[0] = i;
                for (var j = 1; j <= right.Length; j++)
                {
                    var cost = left[i - 1] == right[j - 1] ? 0 : 1;
                    current[j] = Math.Min(
                        Math.Min(previous[j] + 1, current[j - 1] + 1),
                        previous[j - 1] + cost);
                }

                var temp = previous;
                previous = current;
                current = temp;
            }

            return previous[right.Length];
        }
        finally
        {
            if (rentedPrevious is not null)
                ArrayPool<int>.Shared.Return(rentedPrevious, clearArray: true);

            if (rentedCurrent is not null)
                ArrayPool<int>.Shared.Return(rentedCurrent, clearArray: true);
        }
    }

    /// <summary>
    /// Computes a normalized Levenshtein similarity across two ordered token sequences.
    /// </summary>
    /// <param name="left">The first ordered token sequence.</param>
    /// <param name="right">The second ordered token sequence.</param>
    /// <returns>
    /// <c>1 - distance / max(left.Length, right.Length)</c>.
    /// Returns <c>1</c> when both sequences are empty and <c>0</c> when exactly one sequence is empty.
    /// </returns>
    public static float NormalizedLevenshteinSimilarity(ReadOnlySpan<int> left, ReadOnlySpan<int> right)
    {
        if (left.IsEmpty && right.IsEmpty)
            return 1f;
        if (left.IsEmpty || right.IsEmpty)
            return 0f;

        var maxLength = Math.Max(left.Length, right.Length);
        if (maxLength == 0)
            return 1f;

        return Math.Clamp(1f - ((float)LevenshteinDistance(left, right) / maxLength), 0f, 1f);
    }
}
