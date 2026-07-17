namespace Cohesive.Relations.IR;

/// <summary>
/// Shared exact-domain comparison semantics for canonical temporal operands.
/// </summary>
internal static class RelationQueryTemporalValueSemantics
{
    /// <summary>
    /// Compares two values in one exact temporal domain without coercing between Date, DateTime, and Instant.
    /// </summary>
    public static bool TryCompare(
        ScalarTypeKind domain,
        ObservationValue left,
        ObservationValue right,
        out int comparison)
    {
        ValidateDomain(domain);
        if (TryGetOrdinal(domain, left, out var leftOrdinal)
            && TryGetOrdinal(domain, right, out var rightOrdinal))
        {
            comparison = leftOrdinal.CompareTo(rightOrdinal);
            return true;
        }

        comparison = 0;
        return false;
    }

    /// <summary>
    /// Resolves one temporal value to its exact representable-domain ordinal: civil day for
    /// <see cref="ScalarTypeKind.Date"/>, civil tick for <see cref="ScalarTypeKind.DateTime"/>,
    /// or UTC tick for <see cref="ScalarTypeKind.Instant"/>.
    /// </summary>
    public static bool TryGetOrdinal(
        ScalarTypeKind domain,
        ObservationValue value,
        out long ordinal)
    {
        ValidateDomain(domain);
        switch (domain)
        {
            case ScalarTypeKind.Date when value.TryGetDateOnly(out var date):
                ordinal = date.DayNumber;
                return true;
            case ScalarTypeKind.DateTime when value.TryGetDateTimeOffset(out var dateTime):
                ordinal = dateTime.DateTime.Ticks;
                return true;
            case ScalarTypeKind.Instant when value.TryGetInstant(out var instant):
                ordinal = instant.UtcDateTime.Ticks;
                return true;
            default:
                ordinal = 0;
                return false;
        }
    }

    static void ValidateDomain(ScalarTypeKind domain)
    {
        if (domain is not (ScalarTypeKind.Date or ScalarTypeKind.DateTime or ScalarTypeKind.Instant))
        {
            throw new ArgumentOutOfRangeException(
                nameof(domain),
                domain,
                "Temporal matching requires the Date, DateTime, or Instant domain.");
        }
    }
}
