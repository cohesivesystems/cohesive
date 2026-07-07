namespace Cohesive.Adapters.ONNX;

/// <summary>
/// Shared validation helpers for ONNX feature-vector model inputs.
/// </summary>
static class OnnxFeatureVectorValidation
{
    /// <summary>
    /// Validates that vectors are non-empty, aligned to the same length, and finite.
    /// </summary>
    /// <param name="featureVectors">Feature vectors to validate.</param>
    /// <param name="paramName">Parameter name used for thrown exceptions.</param>
    /// <returns>The shared feature count for all vectors.</returns>
    internal static int ValidateAlignedFiniteVectors(IReadOnlyList<ReadOnlyMemory<float>> featureVectors, string paramName = "featureVectors")
    {
        ArgumentNullException.ThrowIfNull(featureVectors, paramName);
        if (featureVectors.Count == 0)
            throw new ArgumentException("At least one feature vector is required.", paramName);

        var featureCount = featureVectors[0].Length;
        if (featureCount <= 0)
            throw new ArgumentOutOfRangeException(paramName, "Feature vectors must contain at least one value.");

        for (var vectorIndex = 0; vectorIndex < featureVectors.Count; vectorIndex++)
        {
            var vector = featureVectors[vectorIndex].Span;
            if (vector.Length != featureCount)
            {
                throw new ArgumentException(
                    $"Feature vector at index {vectorIndex} has length {vector.Length}, expected {featureCount}.",
                    paramName);
            }

            for (var featureIndex = 0; featureIndex < vector.Length; featureIndex++)
            {
                var value = vector[featureIndex];
                if (float.IsNaN(value) || float.IsInfinity(value))
                {
                    throw new ArgumentOutOfRangeException(
                        paramName,
                        $"Feature value at [{vectorIndex}, {featureIndex}] must be finite.");
                }
            }
        }

        return featureCount;
    }
}
