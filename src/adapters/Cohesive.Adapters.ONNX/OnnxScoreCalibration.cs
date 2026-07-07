using Cohesive.AI.Numerics;

namespace Cohesive.Adapters.ONNX;

/// <summary>
/// Shared score calibration helpers for ONNX classifier outputs.
/// </summary>
static class OnnxScoreCalibration
{
    /// <summary>
    /// Calibrates one scalar model output to a [0, 1] score.
    /// </summary>
    internal static float CalibrateSingleLabelScore(float rawScore)
    {
        if (float.IsNaN(rawScore) || float.IsInfinity(rawScore))
            return 0f;
        if (rawScore is >= 0f and <= 1f)
            return rawScore;

        return Math.Clamp01((float)Math.Sigmoid(rawScore));
    }

    /// <summary>
    /// Calibrates a multi-class output row and returns the positive-class score (last index).
    /// </summary>
    internal static float CalibrateMultiClassPositiveClassScore(ReadOnlySpan<float> classValues)
    {
        if (LooksLikeProbabilityDistribution(classValues))
            return Math.Clamp01(classValues[^1]);
        var positiveIndex = classValues.Length - 1;
        var max = float.NegativeInfinity;
        for (var i = 0; i < classValues.Length; i++)
        {
            if (classValues[i] > max)
                max = classValues[i];
        }
        var denominator = 0d;
        for (var i = 0; i < classValues.Length; i++)
            denominator += Math.Exp(classValues[i] - max);
        var positive = Math.Exp(classValues[positiveIndex] - max);
        return denominator <= double.Epsilon
            ? 0f
            : Math.Clamp01((float)(positive / denominator));
    }

    /// <summary>
    /// Returns true when values appear to be a valid probability distribution.
    /// </summary>
    internal static bool LooksLikeProbabilityDistribution(ReadOnlySpan<float> values)
    {
        var sum = 0d;
        for (var i = 0; i < values.Length; i++)
        {
            var value = values[i];
            if (value is < 0f or > 1f || float.IsNaN(value) || float.IsInfinity(value))
                return false;
            sum += value;
        }
        return Math.Abs(sum - 1d) <= 0.05d;
    }
}
