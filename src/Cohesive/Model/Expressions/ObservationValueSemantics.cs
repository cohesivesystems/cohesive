using System.Numerics;

namespace Cohesive.Model.Expressions;

/// <summary>
/// Canonical target-independent equality, ordering, and arithmetic semantics for observation values.
/// </summary>
/// <remarks>
/// Expression interpreters share this algebra while retaining their own binding, uncertainty, provenance, and
/// execution-evidence models. Numeric operations use the exact canonical decimal domain and reject non-finite or
/// non-representable binary floating-point values.
/// </remarks>
public static class ObservationValueSemantics
{
    /// <summary>Tests deep semantic equality, including equality across exactly equivalent numeric kinds.</summary>
    /// <param name="left">First value.</param>
    /// <param name="right">Second value.</param>
    /// <returns><see langword="true"/> when both values denote the same observation.</returns>
    public static bool Equals(ObservationValue left, ObservationValue right) => left.Equals(right);

    /// <summary>Computes a hash code compatible with canonical semantic equality.</summary>
    /// <param name="value">Value to hash.</param>
    /// <returns>A hash code equal for every pair accepted by <see cref="Equals(ObservationValue, ObservationValue)"/>.</returns>
    public static int GetHashCode(ObservationValue value) => value.GetHashCode();

    /// <summary>Compares two non-null scalar values using canonical portable ordering.</summary>
    /// <param name="left">First comparable value.</param>
    /// <param name="right">Second comparable value.</param>
    /// <returns>A negative, zero, or positive value according to canonical ordering.</returns>
    /// <exception cref="InvalidOperationException">
    /// Values are nullish, have incompatible kinds, contain invalid temporal data, or are not comparable.
    /// </exception>
    public static int Compare(ObservationValue left, ObservationValue right)
    {
        if (left.Kind is ObservationValueKind.Null or ObservationValueKind.Undefined
            || right.Kind is ObservationValueKind.Null or ObservationValueKind.Undefined)
        {
            throw new InvalidOperationException(
                $"Values of kinds '{left.Kind}' and '{right.Kind}' cannot be compared without a null-placement policy.");
        }

        if (IsNumeric(left) && IsNumeric(right))
            return CompareNumeric(left, right);
        if (left.Kind != right.Kind)
        {
            throw new InvalidOperationException(
                $"Values of kinds '{left.Kind}' and '{right.Kind}' do not share a comparable domain.");
        }

        return left.Kind switch
        {
            ObservationValueKind.String => StringComparer.Ordinal.Compare(left.String, right.String),
            ObservationValueKind.DateTimeOffset when left.TryGetDateTimeOffset(out var leftValue)
                                                     && right.TryGetDateTimeOffset(out var rightValue) =>
                leftValue.CompareTo(rightValue),
            ObservationValueKind.DateOnly when left.TryGetDateOnly(out var leftValue)
                                               && right.TryGetDateOnly(out var rightValue) =>
                leftValue.CompareTo(rightValue),
            ObservationValueKind.TimeOnly when left.TryGetTimeOnly(out var leftValue)
                                               && right.TryGetTimeOnly(out var rightValue) =>
                leftValue.CompareTo(rightValue),
            ObservationValueKind.TimeSpan when left.TryGetTimeSpan(out var leftValue)
                                               && right.TryGetTimeSpan(out var rightValue) =>
                leftValue.CompareTo(rightValue),
            ObservationValueKind.DateTimeOffset
                or ObservationValueKind.DateOnly
                or ObservationValueKind.TimeOnly
                or ObservationValueKind.TimeSpan =>
                throw new InvalidOperationException(
                    $"A {left.Kind} value contains an invalid canonical representation."),
            _ => throw new InvalidOperationException($"Value kind '{left.Kind}' is not comparable.")
        };
    }

    /// <summary>Adds two numeric values in the canonical exact decimal domain.</summary>
    /// <param name="left">Left numeric operand.</param>
    /// <param name="right">Right numeric operand.</param>
    /// <returns>Canonical numeric result.</returns>
    /// <exception cref="InvalidOperationException">An operand is non-numeric or non-finite.</exception>
    /// <exception cref="OverflowException">
    /// An operand has no exact canonical decimal representation or the result exceeds that domain.
    /// </exception>
    public static ObservationValue Add(ObservationValue left, ObservationValue right) =>
        Arithmetic(BinaryOperator.Add, left, right);

    /// <summary>Subtracts two numeric values in the canonical exact decimal domain.</summary>
    /// <param name="left">Left numeric operand.</param>
    /// <param name="right">Right numeric operand.</param>
    /// <returns>Canonical numeric result.</returns>
    /// <exception cref="InvalidOperationException">An operand is non-numeric or non-finite.</exception>
    /// <exception cref="OverflowException">
    /// An operand has no exact canonical decimal representation or the result exceeds that domain.
    /// </exception>
    public static ObservationValue Subtract(ObservationValue left, ObservationValue right) =>
        Arithmetic(BinaryOperator.Sub, left, right);

    /// <summary>Multiplies two numeric values in the canonical exact decimal domain.</summary>
    /// <param name="left">Left numeric operand.</param>
    /// <param name="right">Right numeric operand.</param>
    /// <returns>Canonical numeric result.</returns>
    /// <exception cref="InvalidOperationException">An operand is non-numeric or non-finite.</exception>
    /// <exception cref="OverflowException">
    /// An operand has no exact canonical decimal representation or the result exceeds that domain.
    /// </exception>
    public static ObservationValue Multiply(ObservationValue left, ObservationValue right) =>
        Arithmetic(BinaryOperator.Mul, left, right);

    /// <summary>Divides two numeric values in the canonical exact decimal domain.</summary>
    /// <param name="left">Left numeric operand.</param>
    /// <param name="right">Right numeric operand.</param>
    /// <returns>Canonical numeric result.</returns>
    /// <exception cref="InvalidOperationException">An operand is non-numeric or non-finite.</exception>
    /// <exception cref="DivideByZeroException"><paramref name="right"/> is zero.</exception>
    /// <exception cref="OverflowException">
    /// An operand has no exact canonical decimal representation or the result exceeds that domain.
    /// </exception>
    public static ObservationValue Divide(ObservationValue left, ObservationValue right) =>
        Arithmetic(BinaryOperator.Div, left, right);

    /// <summary>Requires one exact supported numeric value as a decimal.</summary>
    /// <param name="value">Numeric value to project.</param>
    /// <param name="operation">Stable operation name used by failure messages.</param>
    /// <returns>Exact decimal representation.</returns>
    /// <exception cref="InvalidOperationException"><paramref name="value"/> is non-numeric or non-finite.</exception>
    /// <exception cref="OverflowException">
    /// <paramref name="value"/> is numeric and finite but has no exact canonical decimal representation.
    /// </exception>
    public static decimal RequireDecimal(ObservationValue value, string operation)
    {
        if (!IsNumeric(value))
        {
            throw new InvalidOperationException(
                $"Operation '{operation}' requires a numeric value, but received '{value.Kind}'.");
        }
        if (value.Kind == ObservationValueKind.Double && !double.IsFinite(value.Double))
        {
            throw new InvalidOperationException(
                $"Operation '{operation}' does not support non-finite floating-point values.");
        }
        if (value.TryGetCanonicalNumericDecimal(out var exact))
            return exact;

        throw new OverflowException(
            $"Numeric value '{value}' has no exact representation in the canonical decimal domain for '{operation}'.");
    }

    static ObservationValue Arithmetic(
        BinaryOperator operation,
        ObservationValue left,
        ObservationValue right)
    {
        var leftNumber = RequireDecimal(left, operation.ToString());
        var rightNumber = RequireDecimal(right, operation.ToString());
        var result = operation switch
        {
            BinaryOperator.Add => leftNumber + rightNumber,
            BinaryOperator.Sub => leftNumber - rightNumber,
            BinaryOperator.Mul => leftNumber * rightNumber,
            BinaryOperator.Div => leftNumber / rightNumber,
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unsupported arithmetic operation.")
        };
        return ObservationValue.FromDecimal(result);
    }

    static int CompareNumeric(ObservationValue left, ObservationValue right)
    {
        if (left.Kind == ObservationValueKind.Double && !double.IsFinite(left.Double)
            || right.Kind == ObservationValueKind.Double && !double.IsFinite(right.Double))
        {
            throw new InvalidOperationException("Numeric comparison does not support non-finite floating-point values.");
        }

        var leftExact = left.TryGetCanonicalNumericDecimal(out var leftDecimal);
        var rightExact = right.TryGetCanonicalNumericDecimal(out var rightDecimal);
        if (leftExact && rightExact)
            return leftDecimal.CompareTo(rightDecimal);
        if (!leftExact && !rightExact)
            return left.Double.CompareTo(right.Double);

        return leftExact
            ? CompareDecimalToDouble(leftDecimal, right.Double)
            : -CompareDecimalToDouble(rightDecimal, left.Double);
    }

    static int CompareDecimalToDouble(decimal exact, double floatingPoint)
    {
        if (Math.TryGetCanonicalDecimalFromDouble(floatingPoint, out var canonical))
            return exact.CompareTo(canonical);
        if (exact == decimal.Zero)
            return floatingPoint == 0d ? 0 : floatingPoint > 0d ? -1 : 1;
        if (floatingPoint == 0d)
            return exact > decimal.Zero ? 1 : -1;

        var exactNegative = exact < decimal.Zero;
        var floatingPointNegative = floatingPoint < 0d;
        if (exactNegative != floatingPointNegative)
            return exactNegative ? -1 : 1;

        var decimalBits = decimal.GetBits(exact);
        var decimalCoefficient = new BigInteger((uint)decimalBits[0])
                                 | new BigInteger((uint)decimalBits[1]) << 32
                                 | new BigInteger((uint)decimalBits[2]) << 64;
        var decimalScale = (decimalBits[3] >> 16) & 0xFF;
        var doubleBits = BitConverter.DoubleToUInt64Bits(floatingPoint);
        var exponentBits = (int)((doubleBits >> 52) & 0x7FF);
        var significand = doubleBits & 0x000F_FFFF_FFFF_FFFFUL;
        var binaryExponent = exponentBits == 0 ? -1074 : exponentBits - 1023 - 52;
        if (exponentBits != 0)
            significand |= 1UL << 52;

        var decimalScaleFactor = BigInteger.Pow(10, decimalScale);
        BigInteger decimalSide;
        BigInteger doubleSide;
        if (binaryExponent >= 0)
        {
            decimalSide = decimalCoefficient;
            doubleSide = (new BigInteger(significand) << binaryExponent) * decimalScaleFactor;
        }
        else
        {
            decimalSide = decimalCoefficient << -binaryExponent;
            doubleSide = new BigInteger(significand) * decimalScaleFactor;
        }

        var comparison = decimalSide.CompareTo(doubleSide);
        return exactNegative ? -comparison : comparison;
    }

    static bool IsNumeric(ObservationValue value) => value.Kind is
        ObservationValueKind.Int64 or ObservationValueKind.Double or ObservationValueKind.Decimal;
}
