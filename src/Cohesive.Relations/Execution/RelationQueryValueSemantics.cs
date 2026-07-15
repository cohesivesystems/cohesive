using System.Globalization;
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
        if (left.Kind != right.Kind)
        {
            return left.Kind == ObservationValueKind.Int64 && right.Kind == ObservationValueKind.Double
                ? NumericKindsEqual(left.Int64, right.Double)
                : left.Kind == ObservationValueKind.Double && right.Kind == ObservationValueKind.Int64
                    && NumericKindsEqual(right.Int64, left.Double);
        }

        return left.Kind switch
        {
            ObservationValueKind.Undefined or ObservationValueKind.Null => true,
            ObservationValueKind.Int64 => left.Int64 == right.Int64,
            ObservationValueKind.Double => left.Double.Equals(right.Double),
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
            ObservationValueKind.Int64 => HashInt64(value.Int64),
            ObservationValueKind.Double => HashDouble(value.Double),
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

        try
        {
            return value.Kind == ObservationValueKind.Int64
                ? value.Int64
                : Convert.ToDecimal(RequireFiniteDouble(value.Double, operation), CultureInfo.InvariantCulture);
        }
        catch (OverflowException exception)
        {
            throw NumericFailure(
                $"Numeric value '{value}' is outside the supported decimal execution domain for '{operation}'.",
                exception);
        }
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
        if (left.Kind == ObservationValueKind.Int64 && right.Kind == ObservationValueKind.Int64)
            return left.Int64.CompareTo(right.Int64);

        if (left.Kind == ObservationValueKind.Double && right.Kind == ObservationValueKind.Double)
        {
            var leftDouble = RequireFiniteDouble(left.Double, "comparison");
            var rightDouble = RequireFiniteDouble(right.Double, "comparison");
            return leftDouble.CompareTo(rightDouble);
        }

        var integer = left.Kind == ObservationValueKind.Int64 ? left.Int64 : right.Int64;
        var floatingPoint = left.Kind == ObservationValueKind.Double ? left.Double : right.Double;
        RequireFiniteDouble(floatingPoint, "comparison");
        var integerToFloatingPoint = CompareInt64ToDouble(integer, floatingPoint);

        return left.Kind == ObservationValueKind.Int64
            ? integerToFloatingPoint
            : -integerToFloatingPoint;
    }

    static int CompareInt64ToDouble(long integer, double floatingPoint)
    {
        const double Int64Minimum = -9223372036854775808d;
        const double Int64MaximumExclusive = 9223372036854775808d;
        if (floatingPoint < Int64Minimum)
            return 1;
        if (floatingPoint >= Int64MaximumExclusive)
            return -1;
        if (Math.TryGetExactInt64FromDouble(floatingPoint, out var exact))
            return integer.CompareTo(exact);

        var truncated = (long)floatingPoint;
        var compared = integer.CompareTo(truncated);
        if (compared != 0)
            return compared;

        return floatingPoint > truncated ? -1 : 1;
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
        value.Kind is ObservationValueKind.Int64 or ObservationValueKind.Double;

    static bool NumericKindsEqual(long integer, double floatingPoint) =>
        Math.TryGetExactInt64FromDouble(floatingPoint, out var exact) && exact == integer;

    static bool ArraysEqual(
        IReadOnlyList<ObservationValue>? left,
        IReadOnlyList<ObservationValue>? right)
    {
        if (ReferenceEquals(left, right))
            return true;
        if (left is null || right is null || left.Count != right.Count)
            return false;

        for (var index = 0; index < left.Count; index++)
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

    static int HashInt64(long value)
    {
        var low = unchecked((int)value);
        var high = unchecked((int)(value >> 32));
        return Combine(unchecked((int)0x4E554D31), Combine(low, high));
    }

    static int HashDouble(double value)
    {
        if (Math.TryGetExactInt64FromDouble(value, out var integer))
            return HashInt64(integer);
        if (double.IsNaN(value))
            return Combine(unchecked((int)0x4E554D31), unchecked((int)0x7FF80000));

        var bits = value == 0d ? 0L : BitConverter.DoubleToInt64Bits(value);
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

    static int HashArray(IReadOnlyList<ObservationValue>? values)
    {
        var hash = unchecked((int)0x41525231);
        if (values is null)
            return hash;

        foreach (var value in values)
            hash = Combine(hash, GetHashCode(value));
        return Combine(hash, values.Count);
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
