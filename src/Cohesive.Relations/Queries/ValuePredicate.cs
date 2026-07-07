namespace Cohesive.Relations.Queries;

/// <summary>
/// Predicate targeting a specific field.
/// </summary>
/// <param name="Field">The field path.</param>
/// <param name="Predicate">The predicate expression.</param>
public sealed record FieldPredicate(FieldPath Field, BoolExpr<ValuePredicate> Predicate);

/// <summary>
/// Field predicate rooted at an optional nested scope.
/// </summary>
/// <param name="Predicate">The predicate expression.</param>
/// <param name="Scope">Optional nested scope.</param>
public sealed record EntityPredicate(BoolExpr<FieldPredicate> Predicate, FieldPath? Scope = null);

/// <summary>
/// Predicate over a primitive or structured field value.
/// </summary>
public abstract record ValuePredicate
{
    /// <summary>
    /// Creates an equality predicate for a supported CLR value.
    /// </summary>
    /// <exception cref="NotSupportedException">If the value type is not supported.</exception>
    public static ValuePredicate EqualTo(object value) => value switch
    {
        string text => new ExactValuePredicate(text),
        DateTime date => new DateValuePredicate(new(date)),
        DateTimeOffset date => new DateValuePredicate(date),
        Int128 integer => new ExactValuePredicate(integer.ToString()),
        decimal number => new DecimalValuePredicate(number),
        double number => new DoubleValuePredicate(number),
        float number => new DoubleValuePredicate(number),
        long number => new LongValuePredicate(number),
        int number => new LongValuePredicate(number),
        short number => new LongValuePredicate(number),
        byte number => new IntValuePredicate(number),
        bool flag => new BoolValuePredicate(flag),
        _ => throw new NotSupportedException($"Equality predicates do not support CLR type '{value.GetType().FullName}'.")
    };

    /// <summary>
    /// Creates an inequality predicate for a supported CLR value.
    /// </summary>
    public static Not<ValuePredicate> NotEqualTo(object value) => new(EqualTo(value));

    /// <summary>
    /// Creates a predicate that matches a missing field or an empty string.
    /// </summary>
    public static BoolExpr<ValuePredicate> NullOrEmptyString() =>
        new Or<ValuePredicate>([new Not<ValuePredicate>(new ExistsValuePredicate()), new ExactValuePredicate("")]);

    /// <summary>
    /// Creates a predicate that matches a present, non-empty string.
    /// </summary>
    public static BoolExpr<ValuePredicate> NonEmpty() => NotEqualTo("");
}

/// <summary>
/// A predicate asserting that a value is equal to the given string.
/// </summary>
/// <param name="Value">The required string value.</param>
/// <param name="CaseSensitive">Whether value comparison is case-sensitive.</param>
public sealed record ExactValuePredicate(string Value, bool CaseSensitive = true) : ValuePredicate;

/// <summary>
/// A predicate asserting that a value starts with the given prefix.
/// </summary>
/// <param name="Prefix">The required prefix.</param>
/// <param name="CaseSensitive">Whether prefix comparison is case-sensitive.</param>
public sealed record PrefixValuePredicate(string Prefix, bool CaseSensitive = true) : ValuePredicate;

/// <summary>
/// A predicate asserting that a value ends with the given suffix.
/// </summary>
/// <param name="Suffix">The required suffix.</param>
/// <param name="CaseSensitive">Whether suffix comparison is case-sensitive.</param>
public sealed record SuffixValuePredicate(string Suffix, bool CaseSensitive = true) : ValuePredicate;

/// <summary>
/// A predicate asserting that a value contains the given substring.
/// </summary>
/// <param name="Value">The required substring.</param>
/// <param name="CaseSensitive">Whether substring comparison is case-sensitive.</param>
public sealed record ContainsValuePredicate(string Value, bool CaseSensitive = true) : ValuePredicate;

/// <summary>
/// A predicate asserting that a value matches the given full-text search query.
/// </summary>
/// <param name="Text"></param>
public sealed record FullTextValuePredicate(string Text) : ValuePredicate;

/// <summary>
/// A predicate asserting that a value is equal to the given boolean value.
/// </summary>
/// <param name="Value"></param>
public sealed record BoolValuePredicate(bool Value) : ValuePredicate;

/// <summary>
/// A predicate asserting that a value is equal to the given date value.
/// </summary>
/// <param name="Value"></param>
public sealed record DateValuePredicate(DateTimeOffset Value) : ValuePredicate;

/// <summary>
/// A predicate asserting that a value is equal to the given integer value.
/// </summary>
/// <param name="Value"></param>
public sealed record IntValuePredicate(int Value) : ValuePredicate;

/// <summary>
/// A predicate asserting that a value is equal to the given long value.
/// </summary>
/// <param name="Value"></param>
public sealed record LongValuePredicate(long Value) : ValuePredicate;

/// <summary>
/// A predicate asserting that a value is equal to the given double value.
/// </summary>
/// <param name="Value"></param>
public sealed record DoubleValuePredicate(double Value) : ValuePredicate;

/// <summary>
/// A predicate asserting that a value is equal to the given decimal value.
/// </summary>
/// <param name="Value"></param>
public sealed record DecimalValuePredicate(decimal Value) : ValuePredicate;

/// <summary>
/// A predicate asserting that a value exists.
/// </summary>
public sealed record ExistsValuePredicate() : ValuePredicate;

/// <summary>
/// A predicate asserting that a value is within the given date range.
/// </summary>
/// <param name="Start"></param>
/// <param name="End"></param>
/// <param name="StartExclusive"></param>
/// <param name="EndExclusive"></param>
public sealed record DateRangeValuePredicate(
    DateTimeOffset? Start,
    DateTimeOffset? End,
    bool? StartExclusive = null,
    bool? EndExclusive = null
    ) : ValuePredicate;

/// <summary>
/// A predicate asserting that a value is within the given numeric range.
/// </summary>
/// <param name="Start"></param>
/// <param name="End"></param>
/// <param name="StartExclusive"></param>
/// <param name="EndExclusive"></param>
public sealed record NumberRangeValuePredicate(
    double? Start,
    double? End,
    bool? StartExclusive = null,
    bool? EndExclusive = null
    ) : ValuePredicate
{
    /// <summary>
    /// Creates a numeric range predicate.
    /// </summary>
    /// <exception cref="NotSupportedException">The given value type is not supported for numeric range predicates.</exception>
    public static NumberRangeValuePredicate Between(
        object? start,
        object? end,
        bool? startExclusive = true,
        bool? endExclusive = null
        ) => new(ToDouble(start), ToDouble(end), startExclusive, endExclusive);

    /// <summary>
    /// Creates a greater-than-or-equal predicate.
    /// </summary>
    /// <exception cref="NotSupportedException">The given value type is not supported for numeric range predicates.</exception>
    public static NumberRangeValuePredicate GreaterThanOrEqual(object? value) =>
        new(ToDouble(value), End: null, StartExclusive: false);

    /// <summary>
    /// Creates a greater-than predicate.
    /// </summary>
    /// <exception cref="NotSupportedException">The given value type is not supported for numeric range predicates.</exception>
    public static NumberRangeValuePredicate GreaterThan(object? value) =>
        new(ToDouble(value), End: null, StartExclusive: true);

    /// <summary>
    /// Creates a less-than-or-equal predicate.
    /// </summary>
    /// <exception cref="NotSupportedException">The given value type is not supported for numeric range predicates.</exception>
    public static NumberRangeValuePredicate LessThanOrEqual(object? value) =>
        new(Start: null, ToDouble(value), EndExclusive: false);

    /// <summary>
    /// Creates a less-than predicate.
    /// </summary>
    /// <exception cref="NotSupportedException">The given value type is not supported for numeric range predicates.</exception>
    public static NumberRangeValuePredicate LessThan(object? value) =>
        new(Start: null, ToDouble(value), EndExclusive: true);

    /// <summary>
    /// Converts a CLR value to a double.
    /// </summary>
    /// <param name="value"></param>
    /// <returns></returns>
    /// <exception cref="NotSupportedException">The given value type is not supported for numeric range predicates.</exception>
    static double? ToDouble(object? value) => value switch
    {
        null => null,
        byte number => number,
        short number => number,
        int number => number,
        long number => number,
        float number => number,
        double number => number,
        decimal number => (double)number,
        _ => throw new NotSupportedException($"Numeric range predicates do not support CLR type '{value.GetType().FullName}'.")
    };
}

/// <summary>
/// A predicate asserting that a value is within the given set of values.
/// </summary>
/// <param name="Values"></param>
public sealed record InValuePredicate(IReadOnlyCollection<object> Values) : ValuePredicate;

/// <summary>
/// A predicate asserting that any value in a collection matches the given value predicate.
/// </summary>
/// <param name="Predicate">Predicate evaluated against each collection item as the current value.</param>
public sealed record AnyValuePredicate(BoolExpr<ValuePredicate> Predicate) : ValuePredicate;

/// <summary>
/// A predicate asserting that any object in a collection matches the given field predicate.
/// </summary>
/// <param name="Predicate">Predicate evaluated against each collection item as the current object.</param>
public sealed record AnyFieldPredicate(BoolExpr<FieldPredicate> Predicate) : ValuePredicate;

/// <summary>
/// A predicate asserting that a value is within the given from the given geographic location.
/// </summary>
/// <param name="Latitude">The latitude of the geographic location.</param>
/// <param name="Longitude">The longitude of the geographic location.</param>
/// <param name="DistanceMi">The search radius in miles around the geographic location.</param>
public sealed record GeoDistanceValuePredicate(double Latitude, double Longitude, double DistanceMi) : ValuePredicate;
