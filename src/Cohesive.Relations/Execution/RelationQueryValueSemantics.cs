using System.Collections.Immutable;
using System.Numerics;
using Cohesive.Relations.IR;

namespace Cohesive.Relations.Execution;

internal static class RelationQueryValueSemantics
{
    public static IEqualityComparer<ObservationValue> EqualityComparer { get; } =
        new ObservationValueEqualityComparer();

    public static IComparer<ObservationValue> Comparer { get; } =
        new ObservationValueComparer();

    public static bool IsNullish(ObservationValue value) =>
        value.Kind is ObservationValueKind.Null or ObservationValueKind.Undefined;

    public static bool Equals(ObservationValue left, ObservationValue right)
    {
        if (IsNumeric(left) && IsNumeric(right))
        {
            var leftIsDecimal = left.TryGetCanonicalNumericDecimal(out var leftDecimal);
            var rightIsDecimal = right.TryGetCanonicalNumericDecimal(out var rightDecimal);
            if (leftIsDecimal || rightIsDecimal)
                return leftIsDecimal && rightIsDecimal && leftDecimal == rightDecimal;

            return left.Kind == ObservationValueKind.Double
                   && right.Kind == ObservationValueKind.Double
                   && left.Double.Equals(right.Double);
        }

        if (left.Kind != right.Kind)
            return false;

        return left.Kind switch
        {
            ObservationValueKind.Undefined or ObservationValueKind.Null => true,
            ObservationValueKind.Bool => left.Bool == right.Bool,
            ObservationValueKind.String
                or ObservationValueKind.DateTimeOffset
                or ObservationValueKind.DateOnly
                or ObservationValueKind.TimeOnly
                or ObservationValueKind.TimeSpan =>
                string.Equals(left.String, right.String, StringComparison.Ordinal),
            ObservationValueKind.Bytes => left.Bytes.Span.SequenceEqual(right.Bytes.Span),
            ObservationValueKind.Array => ArraysEqual(left.Array, right.Array),
            ObservationValueKind.Object => ObjectsEqual(left.Fields, right.Fields),
            _ => false
        };
    }

    public static int GetHashCode(ObservationValue value)
    {
        return value.Kind switch
        {
            ObservationValueKind.Undefined => unchecked((int)0x5F9A43C1),
            ObservationValueKind.Null => unchecked((int)0x4A0F1B77),
            ObservationValueKind.Int64
                or ObservationValueKind.Double
                or ObservationValueKind.Decimal => HashNumeric(value),
            ObservationValueKind.Bool => value.Bool
                ? unchecked((int)0x22E4D5B1)
                : unchecked((int)0x11F1C2A3),
            ObservationValueKind.String => Combine(
                unchecked((int)0x53545231),
                HashString(value.String)),
            ObservationValueKind.DateTimeOffset => Combine(
                unchecked((int)0x44544F31),
                HashString(value.String)),
            ObservationValueKind.DateOnly => Combine(
                unchecked((int)0x444F4E31),
                HashString(value.String)),
            ObservationValueKind.TimeOnly => Combine(
                unchecked((int)0x544F4E31),
                HashString(value.String)),
            ObservationValueKind.TimeSpan => Combine(
                unchecked((int)0x54535031),
                HashString(value.String)),
            ObservationValueKind.Bytes => HashBytes(value.Bytes.Span),
            ObservationValueKind.Object => HashObject(value.Fields),
            ObservationValueKind.Array => HashArray(value.Array),
            _ => 0
        };
    }

    public static int Compare(ObservationValue left, ObservationValue right)
    {
        if (IsNullish(left) || IsNullish(right))
        {
            throw InvalidOperand(
                $"Values of kinds '{left.Kind}' and '{right.Kind}' cannot be compared without an explicit null-placement policy.");
        }

        if (IsNumeric(left) && IsNumeric(right))
            return CompareNumeric(left, right);

        if (left.Kind != right.Kind)
        {
            throw InvalidOperand(
                $"Values of kinds '{left.Kind}' and '{right.Kind}' do not share a comparable domain.");
        }

        return left.Kind switch
        {
            ObservationValueKind.String => StringComparer.Ordinal.Compare(left.String, right.String),
            ObservationValueKind.DateTimeOffset => CompareDateTimeOffset(left, right),
            ObservationValueKind.DateOnly => CompareDateOnly(left, right),
            ObservationValueKind.TimeOnly => CompareTimeOnly(left, right),
            ObservationValueKind.TimeSpan => CompareTimeSpan(left, right),
            _ => throw InvalidOperand($"Value kind '{left.Kind}' is not comparable.")
        };
    }

    public static int CompareForOrdering(
        ObservationValue left,
        ObservationValue right,
        QuerySortDirection direction,
        QueryNullPlacement nullPlacement)
    {
        if (!Enum.IsDefined(direction))
            throw new ArgumentOutOfRangeException(nameof(direction), direction, "Unsupported query sort direction.");
        if (!Enum.IsDefined(nullPlacement))
            throw new ArgumentOutOfRangeException(nameof(nullPlacement), nullPlacement, "Unsupported query null placement.");

        var leftNullish = IsNullish(left);
        var rightNullish = IsNullish(right);
        if (leftNullish || rightNullish)
        {
            if (leftNullish && rightNullish)
                return 0;

            return leftNullish == (nullPlacement == QueryNullPlacement.First) ? -1 : 1;
        }

        var result = Math.Sign(Compare(left, right));
        return direction == QuerySortDirection.Ascending ? result : -result;
    }

    public static ObservationValue Add(ObservationValue left, ObservationValue right) =>
        Arithmetic(BinaryOperator.Add, left, right);

    public static ObservationValue Subtract(ObservationValue left, ObservationValue right) =>
        Arithmetic(BinaryOperator.Sub, left, right);

    public static ObservationValue Multiply(ObservationValue left, ObservationValue right) =>
        Arithmetic(BinaryOperator.Mul, left, right);

    public static ObservationValue Divide(ObservationValue left, ObservationValue right) =>
        Arithmetic(BinaryOperator.Div, left, right);

    internal static decimal RequireDecimal(ObservationValue value, string operation)
    {
        if (!IsNumeric(value))
            throw InvalidOperand($"Operation '{operation}' requires a numeric value, but received '{value.Kind}'.");

        if (value.Kind == ObservationValueKind.Double)
            RequireFiniteDouble(value.Double, operation);
        if (value.TryGetCanonicalNumericDecimal(out var exact))
            return exact;

        var message =
            $"Numeric value '{value}' has no exact representation in the supported decimal execution domain for '{operation}'.";
        throw NumericFailure(message, new OverflowException(message));
    }

    static ObservationValue Arithmetic(
        BinaryOperator operation,
        ObservationValue left,
        ObservationValue right)
    {
        var leftNumber = RequireDecimal(left, operation.ToString());
        var rightNumber = RequireDecimal(right, operation.ToString());
        try
        {
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
        catch (DivideByZeroException exception)
        {
            throw NumericFailure("Division by zero is not defined.", exception);
        }
        catch (OverflowException exception)
        {
            throw NumericFailure($"Arithmetic operation '{operation}' overflowed.", exception);
        }
    }

    static int CompareNumeric(ObservationValue left, ObservationValue right)
    {
        if (left.Kind == ObservationValueKind.Double)
            RequireFiniteDouble(left.Double, "comparison");
        if (right.Kind == ObservationValueKind.Double)
            RequireFiniteDouble(right.Double, "comparison");

        var leftIsDecimal = left.TryGetCanonicalNumericDecimal(out var leftDecimal);
        var rightIsDecimal = right.TryGetCanonicalNumericDecimal(out var rightDecimal);
        if (leftIsDecimal && rightIsDecimal)
            return leftDecimal.CompareTo(rightDecimal);

        if (!leftIsDecimal && !rightIsDecimal)
            return left.Double.CompareTo(right.Double);

        return leftIsDecimal
            ? CompareDecimalToDouble(leftDecimal, right.Double)
            : -CompareDecimalToDouble(rightDecimal, left.Double);
    }

    static int CompareDateTimeOffset(ObservationValue left, ObservationValue right)
    {
        if (left.TryGetDateTimeOffset(out var leftValue)
            && right.TryGetDateTimeOffset(out var rightValue))
        {
            return leftValue.CompareTo(rightValue);
        }

        throw InvalidOperand("A DateTimeOffset value contains an invalid canonical representation.");
    }

    static int CompareDateOnly(ObservationValue left, ObservationValue right)
    {
        if (left.TryGetDateOnly(out var leftValue) && right.TryGetDateOnly(out var rightValue))
            return leftValue.CompareTo(rightValue);

        throw InvalidOperand("A DateOnly value contains an invalid canonical representation.");
    }

    static int CompareTimeOnly(ObservationValue left, ObservationValue right)
    {
        if (left.TryGetTimeOnly(out var leftValue) && right.TryGetTimeOnly(out var rightValue))
            return leftValue.CompareTo(rightValue);

        throw InvalidOperand("A TimeOnly value contains an invalid canonical representation.");
    }

    static int CompareTimeSpan(ObservationValue left, ObservationValue right)
    {
        if (left.TryGetTimeSpan(out var leftValue) && right.TryGetTimeSpan(out var rightValue))
            return leftValue.CompareTo(rightValue);

        throw InvalidOperand("A TimeSpan value contains an invalid canonical representation.");
    }

    static double RequireFiniteDouble(double value, string operation)
    {
        if (double.IsFinite(value))
            return value;

        throw InvalidOperand(
            $"Operation '{operation}' does not support non-finite floating-point values.");
    }

    static bool IsNumeric(ObservationValue value) =>
        value.Kind is ObservationValueKind.Int64
            or ObservationValueKind.Double
            or ObservationValueKind.Decimal;

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
        var binaryExponent = exponentBits == 0
            ? -1074
            : exponentBits - 1023 - 52;
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

    static bool ArraysEqual(
        ImmutableArray<ObservationValue> left,
        ImmutableArray<ObservationValue> right)
    {
        if (left.IsDefault || right.IsDefault)
            return left.IsDefault == right.IsDefault;
        if (left.Length != right.Length)
            return false;

        for (var index = 0; index < left.Length; index++)
        {
            if (!Equals(left[index], right[index]))
                return false;
        }

        return true;
    }

    static bool ObjectsEqual(
        IReadOnlyDictionary<string, ObservationValue>? left,
        IReadOnlyDictionary<string, ObservationValue>? right)
    {
        if (ReferenceEquals(left, right))
            return true;
        if (left is null || right is null || left.Count != right.Count)
            return false;

        foreach (var (leftKey, leftValue) in left)
        {
            var found = false;
            foreach (var (rightKey, rightValue) in right)
            {
                if (!string.Equals(leftKey, rightKey, StringComparison.Ordinal))
                    continue;

                if (!Equals(leftValue, rightValue))
                    return false;
                found = true;
                break;
            }

            if (!found)
                return false;
        }

        return true;
    }

    static int HashNumeric(ObservationValue value)
    {
        if (value.TryGetCanonicalNumericDecimal(out var exact))
            return Combine(unchecked((int)0x4E554D31), exact.GetHashCode());

        var floatingPoint = value.Double;
        if (double.IsNaN(floatingPoint))
            return Combine(unchecked((int)0x4E554D31), unchecked((int)0x7FF80000));

        var bits = floatingPoint == 0d ? 0L : BitConverter.DoubleToInt64Bits(floatingPoint);
        return Combine(
            unchecked((int)0x4E554D31),
            Combine(unchecked((int)bits), unchecked((int)(bits >> 32))));
    }

    static int HashString(string? value)
    {
        unchecked
        {
            var hash = (int)2166136261;
            if (value is null)
                return hash;
            foreach (var character in value)
                hash = (hash ^ character) * 16777619;
            return hash;
        }
    }

    static int HashBytes(ReadOnlySpan<byte> value)
    {
        unchecked
        {
            var hash = (int)2166136261;
            foreach (var item in value)
                hash = (hash ^ item) * 16777619;
            return Combine(unchecked((int)0x42595445), hash);
        }
    }

    static int HashArray(ImmutableArray<ObservationValue> values)
    {
        var hash = unchecked((int)0x41525231);
        if (values.IsDefault)
            return hash;

        foreach (var value in values)
            hash = Combine(hash, GetHashCode(value));
        return Combine(hash, values.Length);
    }

    static int HashObject(IReadOnlyDictionary<string, ObservationValue>? values)
    {
        var seed = unchecked((int)0x4F424A31);
        if (values is null || values.Count == 0)
            return seed;

        unchecked
        {
            var xor = 0;
            var sum = 0;
            var product = 1;
            foreach (var (key, value) in values)
            {
                var entry = Combine(HashString(key), GetHashCode(value));
                xor ^= entry;
                sum += entry;
                product *= entry | 1;
            }

            return Combine(Combine(Combine(Combine(seed, values.Count), xor), sum), product);
        }
    }

    static int Combine(int seed, int value)
    {
        unchecked
        {
            return (seed * 16777619) ^ value;
        }
    }

    static RelationQueryExpressionEvaluationException InvalidOperand(string message) =>
        new(RelationQueryExpressionEvaluationError.InvalidOperand, message);

    static RelationQueryExpressionEvaluationException NumericFailure(
        string message,
        Exception innerException) =>
        new(RelationQueryExpressionEvaluationError.NumericFailure, message, innerException);

    sealed class ObservationValueEqualityComparer : IEqualityComparer<ObservationValue>
    {
        public bool Equals(ObservationValue x, ObservationValue y) =>
            RelationQueryValueSemantics.Equals(x, y);

        public int GetHashCode(ObservationValue obj) =>
            RelationQueryValueSemantics.GetHashCode(obj);
    }

    sealed class ObservationValueComparer : IComparer<ObservationValue>
    {
        public int Compare(ObservationValue x, ObservationValue y) =>
            RelationQueryValueSemantics.Compare(x, y);
    }
}
