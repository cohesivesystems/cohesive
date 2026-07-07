using System.Diagnostics;
using System.Text.Json.Serialization;

namespace Cohesive.Domain;

/// <summary>
/// Represents a local date and time wherein the IANA time zone may be unknown.
/// </summary>
[DebuggerDisplay("{Date} {Time} [{IanaTimeZone}]")]
public readonly record struct PartialZonedDateTime
{
    /// <summary>
    /// Creates a local date/time with an optional IANA time zone identifier.
    /// </summary>
    [JsonConstructor]
    public PartialZonedDateTime(DateOnly date, TimeOnly time, string? ianaTimeZone = null)
    {
        Date = date;
        Time = time;
        IanaTimeZone = NormalizeIanaTimeZone(ianaTimeZone: ianaTimeZone);
    }

    /// <summary>
    /// Local date component.
    /// </summary>
    public DateOnly Date { get; }

    /// <summary>
    /// Local time component.
    /// </summary>
    public TimeOnly Time { get; }

    /// <summary>
    /// Optional IANA time zone identifier.
    /// </summary>
    public string? IanaTimeZone { get; }

    /// <summary>
    /// Indicates whether this value has a zone identifier.
    /// </summary>
    public bool HasTimeZone => !string.IsNullOrWhiteSpace(value: IanaTimeZone);

    /// <summary>
    /// Creates a value without a time zone.
    /// </summary>
    public static PartialZonedDateTime UnZoned(DateOnly date, TimeOnly time)
        => new(date: date, time: time, ianaTimeZone: null);

    /// <summary>
    /// Creates a value with an optional time zone and normalizes recognized Windows time zone identifiers to IANA.
    /// </summary>
    public static PartialZonedDateTime Create(DateOnly date, TimeOnly time, string? timeZone = null)
    {
        var normalizedIanaTimeZone = string.IsNullOrWhiteSpace(value: timeZone)
            ? null
            : ZonedDateTime.NormalizeToIanaTimeZoneId(timeZone: timeZone, paramName: nameof(timeZone));
        return new(date: date, time: time, ianaTimeZone: normalizedIanaTimeZone);
    }

    /// <summary>
    /// Creates a value with a validated IANA time zone.
    /// </summary>
    public static PartialZonedDateTime Zoned(DateOnly date, TimeOnly time, string ianaTimeZone)
        => new(date: date, time: time, ianaTimeZone: ValidateIanaTimeZone(ianaTimeZone: ianaTimeZone));

    /// <summary>
    /// Returns a copy with a different local date.
    /// </summary>
    public PartialZonedDateTime WithDate(DateOnly date)
        => new(date: date, time: Time, ianaTimeZone: IanaTimeZone);

    /// <summary>
    /// Returns a copy with a different local time.
    /// </summary>
    public PartialZonedDateTime WithTime(TimeOnly time)
        => new(date: Date, time: time, ianaTimeZone: IanaTimeZone);

    /// <summary>
    /// Returns a copy with a validated IANA time zone.
    /// </summary>
    public PartialZonedDateTime WithTimeZone(string ianaTimeZone)
        => new(date: Date, time: Time, ianaTimeZone: ValidateIanaTimeZone(ianaTimeZone: ianaTimeZone));

    /// <summary>
    /// Returns a copy without a time zone.
    /// </summary>
    public PartialZonedDateTime WithoutTimeZone()
        => new(date: Date, time: Time, ianaTimeZone: null);

    /// <summary>
    /// Converts to <see cref="ZonedDateTime"/> when a zone is present.
    /// </summary>
    public bool TryToZonedDateTime(out ZonedDateTime value)
    {
        if (!HasTimeZone)
        {
            value = default;
            return false;
        }

        value = ZonedDateTime.Create(date: Date, time: Time, ianaTimeZone: IanaTimeZone!);
        return true;
    }

    /// <summary>
    /// Converts to <see cref="ZonedDateTime"/> or throws when no zone is present.
    /// </summary>
    /// <exception cref="InvalidOperationException">Time zone is missing</exception>
    public ZonedDateTime ToZonedDateTime()
    {
        if (TryToZonedDateTime(out var value))
            return value;

        throw new InvalidOperationException(message: $"Cannot convert {nameof(PartialZonedDateTime)} to {nameof(ZonedDateTime)} when time zone is missing.");
    }

    /// <summary>
    /// Converts to <see cref="ZonedDateTime"/>, using this value's zone when present,
    /// otherwise using a validated fallback IANA zone from context.
    /// </summary>
    public ZonedDateTime ToZonedDateTime(string fallbackIanaTimeZone)
    {
        var zoneToUse = HasTimeZone
            ? IanaTimeZone!
            : ValidateIanaTimeZone(ianaTimeZone: fallbackIanaTimeZone);
        return ZonedDateTime.Create(date: Date, time: Time, ianaTimeZone: zoneToUse);
    }

    static string? NormalizeIanaTimeZone(string? ianaTimeZone)
    {
        if (string.IsNullOrWhiteSpace(value: ianaTimeZone))
            return null;

        return ValidateIanaTimeZone(ianaTimeZone: ianaTimeZone);
    }

    static string ValidateIanaTimeZone(string ianaTimeZone)
        => ZonedDateTime.RequireIanaTimeZoneId(timeZone: ianaTimeZone, paramName: nameof(ianaTimeZone));
}
