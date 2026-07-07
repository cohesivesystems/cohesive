using System.Numerics;

namespace Cohesive.AI.Numerics;

/// <summary>
/// Vector norm helpers for float vectors.
/// </summary>
public static class VectorMath
{
    /// <summary>
    /// Computes the dot product of two equal-length vectors.
    /// </summary>
    /// <param name="x">First input vector.</param>
    /// <param name="w">Second input vector.</param>
    /// <returns>The dot product <c>sum(x[i] * w[i])</c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="x"/> and <paramref name="w"/> have different lengths.</exception>
    public static float Dot(ReadOnlySpan<float> x, ReadOnlySpan<float> w)
    {
        if (x.Length != w.Length)
            throw new ArgumentOutOfRangeException(nameof(w), "Vectors must have equal lengths.");
        if (x.IsEmpty)
            return 0f;

        AccumulateDotAndSquaredNorms(x, w, out var dot, out _, out _);
        return (float)dot;
    }

    /// <summary>
    /// Computes the dot product of two equal-length vectors using higher-precision accumulation.
    /// </summary>
    /// <param name="x">First input vector.</param>
    /// <param name="w">Second input vector.</param>
    /// <returns>The dot product <c>sum(x[i] * w[i])</c> accumulated in <see cref="double"/> precision.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="x"/> and <paramref name="w"/> have different lengths.
    /// </exception>
    public static double DotHighPrecision(scoped ReadOnlySpan<float> x, scoped ReadOnlySpan<float> w)
    {
        if (x.Length != w.Length)
            throw new ArgumentOutOfRangeException(nameof(w), "Vectors must have equal lengths.");

        var n = w.Length;
        var sum = 0d;
        var compensation = 0d;
        for (var i = 0; i < n; i++)
        {
            var product = (double)x[i] * w[i];
            var adjusted = product - compensation;
            var next = sum + adjusted;
            compensation = (next - sum) - adjusted;
            sum = next;
        }

        return sum;
    }

    /// <summary>
    /// Computes the dot product of two equal-length vectors using higher-precision accumulation.
    /// </summary>
    /// <param name="x">First input vector.</param>
    /// <param name="w">Second input vector.</param>
    /// <returns>The dot product <c>sum(x[i] * w[i])</c> accumulated in <see cref="double"/> precision.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="x"/> and <paramref name="w"/> have different lengths.
    /// </exception>
    public static double DotHighPrecision(scoped ReadOnlySpan<double> x, scoped ReadOnlySpan<double> w)
    {
        if (x.Length != w.Length)
            throw new ArgumentOutOfRangeException(nameof(w), "Vectors must have equal lengths.");

        var n = w.Length;
        var sum = 0d;
        var compensation = 0d;
        for (var i = 0; i < n; i++)
        {
            var product = x[i] * w[i];
            var adjusted = product - compensation;
            var next = sum + adjusted;
            compensation = (next - sum) - adjusted;
            sum = next;
        }
        return sum;
    }

    /// <summary>
    /// Computes cosine similarity for two vectors and returns <c>0</c> when the vectors are invalid.
    /// </summary>
    /// <param name="left">First vector.</param>
    /// <param name="right">Second vector.</param>
    /// <returns>Cosine similarity score in [-1, 1], or <c>0</c> when inputs are invalid.</returns>
    public static double CosineSimilarity(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
        => TryCosineSimilarity(left, right, out var similarity) ? similarity : 0d;

    /// <summary>
    /// Attempts to compute cosine similarity for two vectors.
    /// </summary>
    /// <param name="left">First vector.</param>
    /// <param name="right">Second vector.</param>
    /// <param name="similarity">Computed cosine similarity when vectors are valid; otherwise <c>0</c>.</param>
    /// <returns>
    /// <see langword="true"/> when both vectors are non-empty, have equal dimensionality, and non-zero norms;
    /// otherwise <see langword="false"/>.
    /// </returns>
    public static bool TryCosineSimilarity(ReadOnlySpan<float> left, ReadOnlySpan<float> right, out double similarity)
    {
        if (!DotAndNormL2(left, right, out var dot, out var leftNorm, out var rightNorm))
        {
            similarity = 0d;
            return false;
        }

        var score = dot / (leftNorm * rightNorm);
        if (!double.IsFinite(score))
        {
            similarity = 0d;
            return false;
        }

        similarity = score;
        return true;
    }
    
    /// <summary>
    /// Computes the L2 norm (Euclidean magnitude) for one vector.
    /// </summary>
    /// <param name="vector">Input vector.</param>
    /// <returns>L2 norm, or <c>0</c> when the computed norm is not finite.</returns>
    public static double NormL2(scoped ReadOnlySpan<float> vector)
    {
        var squaredSum = ComputeSquaredSum(vector);
        if (squaredSum <= 0d || !double.IsFinite(squaredSum))
            return 0d;

        var norm = Math.Sqrt(squaredSum);
        return double.IsFinite(norm) ? norm : 0d;
    }

    /// <summary>
    /// Attempts to compute dot product and L2 norms for two vectors in one pass.
    /// </summary>
    /// <param name="left">First vector.</param>
    /// <param name="right">Second vector.</param>
    /// <param name="dotProduct">Computed dot product.</param>
    /// <param name="leftNorm">L2 norm of <paramref name="left"/>.</param>
    /// <param name="rightNorm">L2 norm of <paramref name="right"/>.</param>
    /// <returns>
    /// <see langword="true"/> when inputs are non-empty, equally sized, and both norms are non-zero finite values;
    /// otherwise <see langword="false"/>.
    /// </returns>
    public static bool DotAndNormL2(scoped ReadOnlySpan<float> left, scoped ReadOnlySpan<float> right, out double dotProduct, out double leftNorm, out double rightNorm)
    {
        if (left.IsEmpty || right.IsEmpty || left.Length != right.Length)
        {
            dotProduct = 0d;
            leftNorm = 0d;
            rightNorm = 0d;
            return false;
        }

        AccumulateDotAndSquaredNorms(left, right, out var dotSquared, out var leftSquared, out var rightSquared);

        if (!double.IsFinite(dotSquared) || !double.IsFinite(leftSquared) || !double.IsFinite(rightSquared))
        {
            dotProduct = 0d;
            leftNorm = 0d;
            rightNorm = 0d;
            return false;
        }

        if (leftSquared <= double.Epsilon || rightSquared <= double.Epsilon)
        {
            dotProduct = 0d;
            leftNorm = 0d;
            rightNorm = 0d;
            return false;
        }

        dotProduct = dotSquared;
        leftNorm = Math.Sqrt(leftSquared);
        rightNorm = Math.Sqrt(rightSquared);

        if (!double.IsFinite(leftNorm) || !double.IsFinite(rightNorm))
        {
            dotProduct = 0d;
            leftNorm = 0d;
            rightNorm = 0d;
            return false;
        }

        return true;
    }

    static void AccumulateDotAndSquaredNorms(scoped ReadOnlySpan<float> left, scoped ReadOnlySpan<float> right, out double dot, out double leftSquared, out double rightSquared)
    {
        var length = left.Length;
        var width = Vector<float>.Count;

        var dotVector = Vector<float>.Zero;
        var leftSquaredVector = Vector<float>.Zero;
        var rightSquaredVector = Vector<float>.Zero;

        var i = 0;
        for (; i <= length - width; i += width)
        {
            var leftChunk = new Vector<float>(left.Slice(i, width));
            var rightChunk = new Vector<float>(right.Slice(i, width));
            dotVector += leftChunk * rightChunk;
            leftSquaredVector += leftChunk * leftChunk;
            rightSquaredVector += rightChunk * rightChunk;
        }

        dot = 0d;
        leftSquared = 0d;
        rightSquared = 0d;
        for (var j = 0; j < width; j++)
        {
            dot += dotVector[j];
            leftSquared += leftSquaredVector[j];
            rightSquared += rightSquaredVector[j];
        }

        for (; i < length; i++)
        {
            var leftValue = (double)left[i];
            var rightValue = (double)right[i];

            dot += leftValue * rightValue;
            leftSquared += leftValue * leftValue;
            rightSquared += rightValue * rightValue;
        }
    }

    static double ComputeSquaredSum(scoped in ReadOnlySpan<float> vector)
    {
        var length = vector.Length;
        if (length == 0)
            return 0d;

        var width = Vector<float>.Count;
        var squaredVector = Vector<float>.Zero;

        var i = 0;
        for (; i <= length - width; i += width)
        {
            var values = new Vector<float>(vector.Slice(i, width));
            squaredVector += values * values;
        }

        var sum = 0d;
        for (var j = 0; j < width; j++)
            sum += squaredVector[j];

        for (; i < length; i++)
            sum += vector[i] * vector[i];

        return sum;
    }
}
