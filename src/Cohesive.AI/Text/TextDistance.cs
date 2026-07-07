using System.Buffers;

namespace Cohesive.AI.Text;

/// <summary>
/// Shared edit-distance helpers for normalized text comparison.
/// </summary>
public static class TextDistance
{
    /// <summary>
    /// Computes Levenshtein edit distance between two character spans.
    /// </summary>
    public static int ComputeLevenshteinDistance(ReadOnlySpan<char> left, ReadOnlySpan<char> right, bool ignoreCase = true)
    {
        if (left.IsEmpty)
            return right.Length;
        if (right.IsEmpty)
            return left.Length;

        var width = right.Length + 1;

        int[]? rentedPrevious = null;
        int[]? rentedCurrent = null;

        var previous = width <= 256
            ? stackalloc int[width]
            : (rentedPrevious = ArrayPool<int>.Shared.Rent(width)).AsSpan(0, width);

        var current = width <= 256
            ? stackalloc int[width]
            : (rentedCurrent = ArrayPool<int>.Shared.Rent(width)).AsSpan(0, width);

        try
        {
            for (var j = 0; j < width; j++)
                previous[j] = j;

            for (var i = 1; i <= left.Length; i++)
            {
                current[0] = i;
                var leftChar = ignoreCase ? char.ToLowerInvariant(left[i - 1]) : left[i - 1];

                for (var j = 1; j <= right.Length; j++)
                {
                    var rightChar = ignoreCase ? char.ToLowerInvariant(right[j - 1]) : right[j - 1];
                    var cost = leftChar == rightChar ? 0 : 1;

                    current[j] = Math.Min(
                        Math.Min(current[j - 1] + 1, previous[j] + 1),
                        previous[j - 1] + cost
                        );
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
    /// Computes normalized Levenshtein similarity in the range [0, 1].
    /// </summary>
    public static float ComputeNormalizedLevenshteinSimilarity(ReadOnlySpan<char> left, ReadOnlySpan<char> right, bool ignoreCase = true)
    {
        if (left.IsEmpty && right.IsEmpty)
            return 1f;
        if (left.IsEmpty || right.IsEmpty)
            return 0f;

        var max = Math.Max(left.Length, right.Length);
        if (max == 0)
            return 1f;

        var distance = ComputeLevenshteinDistance(left, right, ignoreCase);
        return Math.Clamp(1f - ((float)distance / max), 0f, 1f);
    }
}
