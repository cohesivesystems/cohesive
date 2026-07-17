using System.Diagnostics.Contracts;
using System.Globalization;

namespace Cohesive.Prelude;

/// <summary>
/// Numeric extensions for <see cref="Math"/>.
/// </summary>
public static class MathExtensions
{
    /// <summary>
    /// Static extensions for <see cref="Math"/>.
    /// </summary>
    extension(Math)
    {
        /// <summary>
        /// Tries to convert a floating point value to an integer.
        /// </summary>
        /// <param name="value">The floating point value to convert to an integer.</param>
        /// <param name="result">The resulting integer value, if conversion is successful.</param>
        /// <returns>True if the conversion is exact, false otherwise.</returns>
        public static bool TryGetExactInt64FromDouble(double value, out long result)
        {
            result = 0;

            if (!double.IsFinite(value))
                return false;
            
            if (value < long.MinValue || value > long.MaxValue)
                return false;
            
            var candidate = (long)value;
            // ReSharper disable once CompareOfFloatsByEqualityOperator
            if (candidate != value)
                return false;

            result = candidate;
            return true;
        }

        /// <summary>
        /// Tries to recover the canonical round-trip base-10 representation of a floating-point value.
        /// </summary>
        /// <param name="value">The floating-point value to convert.</param>
        /// <param name="result">The canonical decimal representation when conversion succeeds; otherwise zero.</param>
        /// <returns>
        /// <see langword="true"/> when the finite value's round-trip text is representable by <see cref="decimal"/>
        /// and converts back to the same floating-point value; otherwise <see langword="false"/>.
        /// </returns>
        public static bool TryGetCanonicalDecimalFromDouble(double value, out decimal result)
        {
            if (double.IsFinite(value)
                && decimal.TryParse(
                    value.ToString("R", CultureInfo.InvariantCulture),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out result)
                && (double)result == value)
            {
                return true;
            }

            result = default;
            return false;
        }
        
        /// <summary>
        /// Determines whether two floating point values are approximately equal, within a given tolerance.
        /// </summary>
        /// <param name="a"></param>
        /// <param name="b"></param>
        /// <param name="absoluteTolerance"></param>
        /// <param name="relativeTolerance"></param>
        /// <returns>A value indicating whether the two floating point values are approximately equal.</returns>
        [Pure]
        public static bool ApproximatelyEquals(double a, double b, double absoluteTolerance = 1e-12, double relativeTolerance = 1e-9)
        {
            var diff = Math.Abs(a - b);
            if (diff <= absoluteTolerance)
                return true;

            var scale = Math.Max(Math.Abs(a), Math.Abs(b));
            return diff <= relativeTolerance * scale;
        }
        
        /// <summary>
        /// Determines whether two floating point values are approximately equal, within a given tolerance.
        /// </summary>
        /// <param name="a"></param>
        /// <param name="b"></param>
        /// <param name="absoluteTolerance"></param>
        /// <param name="relativeTolerance"></param>
        /// <returns>A value indicating whether the two floating point values are approximately equal.</returns>
        [Pure]
        public static bool ApproximatelyEquals(float a, float b, float absoluteTolerance = 1e-12f, float relativeTolerance = 1e-9f)
        {
            var diff = Math.Abs(a - b);
            if (diff <= absoluteTolerance)
                return true;

            var scale = Math.Max(Math.Abs(a), Math.Abs(b));
            return diff <= relativeTolerance * scale;
        }
        
        /// <summary>
        /// Computes the logistic sigmoid for one scalar value.
        /// </summary>
        /// <param name="value">Input scalar.</param>
        /// <param name="clampMagnitude">Absolute bound applied before exponentiation to improve numerical stability.</param>
        /// <returns>Sigmoid output in the range (0, 1).</returns>
        /// <exception cref="ArgumentOutOfRangeException"></exception>
        public static double Sigmoid(double value, double clampMagnitude = 20d)
        {
            ArgumentOutOfRangeException.ThrowIfNotFinite(clampMagnitude);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(clampMagnitude);
            var clamped = Math.Clamp(value, -clampMagnitude, clampMagnitude);
            return 1d / (1d + Math.Exp(-clamped));
        }

        /// <summary>
        /// Clamps one <see cref="double"/> to the inclusive range [0, 1].
        /// </summary>
        /// <param name="value">Value to clamp.</param>
        /// <returns><paramref name="value"/> constrained to [0, 1].</returns>
        public static double Clamp01(double value) => Math.Clamp(value, 0d, 1d);

        /// <summary>
        /// Clamps one <see cref="float"/> to the inclusive range [0, 1].
        /// </summary>
        /// <param name="value">Value to clamp.</param>
        /// <returns><paramref name="value"/> constrained to [0, 1].</returns>
        public static float Clamp01(float value) => Math.Clamp(value, 0f, 1f);

        /// <summary>
        /// Computes a convex combination of two values using the given weight.
        /// </summary>
        /// <param name="left">Value for weight 0.</param>
        /// <param name="right">Value for weight 1.</param>
        /// <param name="weight">Blend weight clamped to [0, 1].</param>
        /// <returns>Convex combination constrained to [0, 1].</returns>
        public static double ConvexCombine(double left, double right, double weight)
        {
            var w = Math.Clamp01(weight);
            return Math.Clamp01(((1d - w) * left) + (w * right));
        }
    }
}
