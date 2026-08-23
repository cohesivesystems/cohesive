using System.Globalization;

namespace Cohesive.Model;

/// <summary>Shared canonical matching between primitive type declarations, literals, and observations.</summary>
public static class PrimitiveTypeSemantics
{
    /// <summary>Tests whether one invariant literal denotes the supplied observation under a primitive type.</summary>
    /// <param name="type">Primitive semantic type used to parse the literal.</param>
    /// <param name="literal">Invariant canonical or accepted literal representation.</param>
    /// <param name="value">Concrete observation to compare.</param>
    /// <returns><see langword="true"/> when the literal and observation denote the same primitive value.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="literal"/> is <see langword="null"/>.</exception>
    public static bool MatchesLiteral(
        PrimitiveType type,
        string literal,
        ObservationValue value)
    {
        ArgumentNullException.ThrowIfNull(literal);
        switch (type)
        {
            case PrimitiveType.Bool:
                return value.Kind == ObservationValueKind.Bool
                    && bool.TryParse(literal, out var boolean)
                    && value.Bool == boolean;
            case PrimitiveType.Int32:
                return IsNumeric(value)
                    && int.TryParse(literal, NumberStyles.Integer, CultureInfo.InvariantCulture, out var int32)
                    && value.TryGetInt32(out var actualInt32)
                    && actualInt32 == int32;
            case PrimitiveType.Int64:
                return IsNumeric(value)
                    && long.TryParse(literal, NumberStyles.Integer, CultureInfo.InvariantCulture, out var int64)
                    && value.TryGetInt64(out var actualInt64)
                    && actualInt64 == int64;
            case PrimitiveType.Decimal:
                return IsNumeric(value)
                    && decimal.TryParse(literal, NumberStyles.Float, CultureInfo.InvariantCulture, out var dec)
                    && value.TryGetDecimal(out var actualDecimal)
                    && actualDecimal == dec;
            case PrimitiveType.String:
                return value.Kind == ObservationValueKind.String
                    && string.Equals(value.String, literal, StringComparison.Ordinal);
            case PrimitiveType.Guid:
                return value.Kind == ObservationValueKind.String
                    && Guid.TryParse(literal, out var guid)
                    && Guid.TryParse(value.String, out var actualGuid)
                    && actualGuid == guid;
            case PrimitiveType.Date:
                return DateOnly.TryParse(literal, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                    && value.TryGetDateOnly(out var actualDate)
                    && actualDate == date;
            case PrimitiveType.DateTime:
                return DateTimeOffset.TryParse(
                        literal,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind,
                        out var dateTime)
                    && value.TryGetDateTimeOffset(out var actualDateTime)
                    && actualDateTime.EqualsExact(dateTime);
            case PrimitiveType.Instant:
                return DateTimeOffset.TryParse(
                        literal,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind,
                        out var instant)
                    && value.TryGetInstant(out var actualInstant)
                    && actualInstant.ToUniversalTime() == instant.ToUniversalTime();
            case PrimitiveType.Bytes:
                if (!value.TryGetBytes(out var actualBytes))
                    return false;
                try
                {
                    return actualBytes.Span.SequenceEqual(Convert.FromBase64String(literal));
                }
                catch (FormatException)
                {
                    return false;
                }
            default:
                return false;
        }
    }

    static bool IsNumeric(ObservationValue value) => value.Kind is
        ObservationValueKind.Int64 or ObservationValueKind.Double or ObservationValueKind.Decimal;
}
