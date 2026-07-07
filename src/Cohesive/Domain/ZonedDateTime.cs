using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cohesive.Domain;

/// <summary>
/// Represents a local date and time in a specific IANA time zone.
/// </summary>
[JsonConverter(typeof(ZonedDateTimeJsonConverter))]
public readonly record struct ZonedDateTime : ISpanParsable<ZonedDateTime>, ISpanFormattable
{
    const string DateFormat = "yyyy-MM-dd";
    const string TimeFormat = "HH:mm:ss";

    /// <summary>
    /// Creates a zoned local date/time value and normalizes recognized Windows time zone identifiers to IANA.
    /// </summary>
    /// <exception cref="ArgumentException">The time zone is not a recognized IANA or Windows time zone identifier.</exception>
    public static ZonedDateTime Create(DateOnly date, TimeOnly time, string ianaTimeZone)
    {
        var normalizedIanaTimeZone = NormalizeToIanaTimeZoneId(timeZone: ianaTimeZone, paramName: nameof(ianaTimeZone));
        return new(date: date, time: time, ianaTimeZone: normalizedIanaTimeZone);
    }

    /// <summary>
    /// Creates a zoned local date/time value.
    /// </summary>
    /// <exception cref="ArgumentException">The timezone is not a valid IANA timezone.</exception>
    [JsonConstructor]
    public ZonedDateTime(DateOnly date, TimeOnly time, string ianaTimeZone)
    {
        Date = date;
        Time = time;
        IanaTimeZone = RequireIanaTimeZoneId(timeZone: ianaTimeZone, paramName: nameof(ianaTimeZone));
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
    /// IANA time zone identifier (for example, "America/New_York").
    /// </summary>
    public string IanaTimeZone { get; }

    /// <summary>
    /// Returns an invariant diagnostic representation:
    /// yyyy-MM-ddTHH:mm:ss+/-HH:mm[IANA/Zone] when the offset can be resolved.
    /// Falls back to yyyy-MM-ddTHH:mm:ss[IANA/Zone] if offset resolution is not possible.
    /// </summary>
    public override string ToString() => ToString(format: "G", formatProvider: CultureInfo.InvariantCulture);

    /// <summary>
    /// Formats the value using a single-character invariant format token.
    /// </summary>
    /// <param name="format">
    /// Supported format tokens:<br />
    /// <c>G</c> (default) = diagnostic form <c>yyyy-MM-ddTHH:mm:ss+/-HH:mm[IANA/Zone]</c> when offset is resolvable,
    /// otherwise <c>yyyy-MM-ddTHH:mm:ss[IANA/Zone]</c>;<br />
    /// <c>J</c> = canonical JSON round-trip form <c>yyyy-MM-ddTHH:mm:ss+/-HH:mm[IANA/Zone]</c>;<br />
    /// <c>L</c> = local form without offset <c>yyyy-MM-ddTHH:mm:ss[IANA/Zone]</c>.
    /// </param>
    /// <param name="formatProvider">Ignored. Formatting is culture-invariant.</param>
    /// <exception cref="FormatException"></exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown for <c>J</c> when the local value cannot be resolved to an offset.
    /// </exception>
    public string ToString(string? format, IFormatProvider? formatProvider)
    {
        var formatToken = NormalizeFormat(format: format);
        return formatToken switch
        {
            'G' => BuildDiagnosticText(),
            'J' => BuildCanonicalJsonText(),
            'L' => BuildLocalText(),
            _ => throw new FormatException(message: $"The format string '{format}' is not supported for {nameof(ZonedDateTime)}."),
        };
    }

    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? formatProvider)
    {
        charsWritten = 0;
        string formatted;
        try
        {
            formatted = ToString(
                format: format.Length == 0 ? null : new string(value: format),
                formatProvider: formatProvider);
        }
        catch (FormatException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        if (!formatted.AsSpan().TryCopyTo(destination: destination))
            return false;

        charsWritten = formatted.Length;
        return true;
    }

    public static ZonedDateTime Parse(string s, IFormatProvider? provider)
    {
        if (TryParse(s: s, provider: provider, result: out var value))
            return value;

        throw new FormatException(message: $"Input string was not in a correct format for {nameof(ZonedDateTime)}.");
    }

    public static ZonedDateTime Parse(ReadOnlySpan<char> s, IFormatProvider? provider)
    {
        if (TryParse(s: s, provider: provider, result: out var value))
            return value;

        throw new FormatException(message: $"Input span was not in a correct format for {nameof(ZonedDateTime)}.");
    }

    public static bool TryParse(string? s, IFormatProvider? provider, out ZonedDateTime result)
    {
        if (s is null)
        {
            result = default;
            return false;
        }

        return TryParse(s: s.AsSpan(), provider: provider, result: out result);
    }

    public static bool TryParse(ReadOnlySpan<char> s, IFormatProvider? provider, out ZonedDateTime result)
    {
        result = default;
        var span = s.Trim();
        if (span.IsEmpty)
            return false;

        var openBracket = span.LastIndexOf(value: '[');
        if (openBracket <= 0 || span[^1] != ']')
            return false;

        var localAndOffset = span[..openBracket];
        var zoneSpan = span[(openBracket + 1)..^1];
        if (zoneSpan.IsEmpty)
            return false;

        var zoneId = new string(value: zoneSpan);
        if (!TryParseLocalDateTimeAndOffset(localAndOffset: localAndOffset, out var date, out var time, out var parsedOffset))
            return false;

        ZonedDateTime parsed;
        try
        {
            parsed = Create(date: date, time: time, ianaTimeZone: zoneId);
        }
        catch (ArgumentException)
        {
            return false;
        }

        if (parsedOffset is not null && !parsed.IsOffsetCompatible(expectedOffset: parsedOffset.Value))
            return false;

        result = parsed;
        return true;
    }

    /// <summary>
    /// Converts this local zoned value to a resolved <see cref="DateTimeOffset"/> instant.
    /// </summary>
    /// <exception cref="TimeZoneNotFoundException"></exception>
    /// <exception cref="InvalidTimeZoneException"></exception>
    /// <exception cref="InvalidOperationException"></exception>
    public DateTimeOffset ToInstant()
    {
        var timeZone = ResolveTimeZoneInfo(ianaTimeZone: IanaTimeZone);
        var localDateTime = Date.ToDateTime(time: Time, kind: DateTimeKind.Unspecified);

        if (timeZone.IsInvalidTime(dateTime: localDateTime))
            throw new InvalidOperationException(message: $"Local date/time '{localDateTime:O}' is invalid in IANA time zone '{IanaTimeZone}' due to a clock transition.");

        if (timeZone.IsAmbiguousTime(dateTime: localDateTime))
        {
            var offsets = timeZone.GetAmbiguousTimeOffsets(dateTime: localDateTime);
            var earlierOffset = offsets.Max();
            return new(dateTime: localDateTime, offset: earlierOffset);
        }

        var offset = timeZone.GetUtcOffset(dateTime: localDateTime);
        return new(dateTime: localDateTime, offset: offset);
    }

    /// <summary>
    /// Attempts to convert this local zoned value to a resolved <see cref="DateTimeOffset"/> instant.
    /// </summary>
    public bool TryToInstant(out DateTimeOffset value)
    {
        try
        {
            value = ToInstant();
            return true;
        }
        catch (TimeZoneNotFoundException)
        {
            value = default;
            return false;
        }
        catch (InvalidTimeZoneException)
        {
            value = default;
            return false;
        }
        catch (InvalidOperationException)
        {
            value = default;
            return false;
        }
    }

    /// <summary>
    /// Returns a copy with a different local date.
    /// </summary>
    public ZonedDateTime WithDate(DateOnly date)
        => new(date: date, time: Time, ianaTimeZone: IanaTimeZone);

    /// <summary>
    /// Returns a copy with a different local time.
    /// </summary>
    public ZonedDateTime WithTime(TimeOnly time)
        => new(date: Date, time: time, ianaTimeZone: IanaTimeZone);

    /// <summary>
    /// Returns a copy with a different IANA time zone identifier.
    /// </summary>
    public ZonedDateTime WithTimeZone(string ianaTimeZone)
        => Create(date: Date, time: Time, ianaTimeZone: ianaTimeZone);

    /// <summary>
    /// Converts the value to a UTC instant.
    /// </summary>
    public DateTimeOffset ToUtcInstant() => ToInstant().ToUniversalTime();

    /// <summary>
    /// Returns possible offsets for this local date/time in the configured time zone.
    /// Returns no values for invalid local times (for example, DST gap).
    /// </summary>
    public IReadOnlyList<TimeSpan> GetPossibleOffsets()
    {
        var timeZone = ResolveTimeZoneInfo(ianaTimeZone: IanaTimeZone);
        var localDateTime = Date.ToDateTime(time: Time, kind: DateTimeKind.Unspecified);

        if (timeZone.IsInvalidTime(dateTime: localDateTime))
            return [];

        if (timeZone.IsAmbiguousTime(dateTime: localDateTime))
            return timeZone.GetAmbiguousTimeOffsets(dateTime: localDateTime)
                .OrderDescending()
                .ToArray();

        return [timeZone.GetUtcOffset(dateTime: localDateTime)];
    }

    /// <summary>
    /// Compares two values by resolved instant, not by local wall-clock components.
    /// </summary>
    public int CompareInstant(ZonedDateTime other)
        => ToInstant().CompareTo(other.ToInstant());

    static TimeZoneInfo ResolveTimeZoneInfo(string ianaTimeZone)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id: ianaTimeZone);
        }
        catch (TimeZoneNotFoundException) when (TryResolveWindowsTimeZone(ianaTimeZone: ianaTimeZone, out var windowsTimeZone))
        {
            return windowsTimeZone;
        }
        catch (TimeZoneNotFoundException exception)
        {
            throw new TimeZoneNotFoundException(
                message: $"IANA time zone '{ianaTimeZone}' could not be resolved on this system.",
                innerException: exception
            );
        }
    }

    static bool TryResolveWindowsTimeZone(string ianaTimeZone, out TimeZoneInfo windowsTimeZone)
    {
        windowsTimeZone = null!;
        if (!OperatingSystem.IsWindows())
            return false;

        if (!TimeZoneInfo.TryConvertIanaIdToWindowsId(ianaId: ianaTimeZone, windowsId: out var windowsId))
            return false;

        try
        {
            windowsTimeZone = TimeZoneInfo.FindSystemTimeZoneById(id: windowsId);
            return true;
        }
        catch (TimeZoneNotFoundException)
        {
            return false;
        }
    }

    internal static string NormalizeToIanaTimeZoneId(string timeZone, string paramName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: timeZone, paramName: paramName);

        if (IsIanaTimeZoneId(timeZone: timeZone))
            return timeZone;

        if (TimeZoneInfo.TryConvertWindowsIdToIanaId(windowsId: timeZone, ianaId: out var ianaTimeZone))
            return ianaTimeZone;

        throw new ArgumentException(
            message: $"Time zone id '{timeZone}' is not a recognized IANA or Windows time zone identifier.",
            paramName: paramName
        );
    }

    internal static string RequireIanaTimeZoneId(string timeZone, string paramName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: timeZone, paramName: paramName);

        if (IsIanaTimeZoneId(timeZone: timeZone))
            return timeZone;

        throw new ArgumentException(
            message: $"Time zone id '{timeZone}' is not a recognized IANA time zone identifier.",
            paramName: paramName
        );
    }

    internal static bool IsIanaTimeZoneId(string timeZone)
        => TimeZoneInfo.TryConvertIanaIdToWindowsId(ianaId: timeZone, windowsId: out _);

    static bool TryParseLocalDateTimeAndOffset(ReadOnlySpan<char> localAndOffset, out DateOnly date, out TimeOnly time, out TimeSpan? parsedOffset)
    {
        date = default;
        time = default;
        parsedOffset = null;

        if (localAndOffset.Length != 19 && localAndOffset.Length != 25)
            return false;

        if (localAndOffset[10] != 'T')
            return false;

        if (!DateOnly.TryParseExact(
                s: localAndOffset[..10],
                format: DateFormat,
                provider: CultureInfo.InvariantCulture,
                style: DateTimeStyles.None,
                result: out date))
            return false;

        if (!TimeOnly.TryParseExact(
                s: localAndOffset.Slice(start: 11, length: 8),
                format: TimeFormat,
                provider: CultureInfo.InvariantCulture,
                style: DateTimeStyles.None,
                result: out time))
            return false;

        if (localAndOffset.Length == 19)
            return true;

        var sign = localAndOffset[19];
        if (sign is not ('+' or '-'))
            return false;

        if (!TimeSpan.TryParseExact(
                input: localAndOffset[20..],
                format: "hh\\:mm",
                formatProvider: CultureInfo.InvariantCulture,
                result: out var absoluteOffset))
            return false;

        parsedOffset = sign == '-' ? -absoluteOffset : absoluteOffset;
        return true;
    }

    static char NormalizeFormat(string? format)
    {
        if (string.IsNullOrWhiteSpace(value: format))
            return 'G';

        if (format.Length != 1)
            throw new FormatException(message: $"The format string '{format}' is not supported for {nameof(ZonedDateTime)}.");

        return char.ToUpperInvariant(c: format[0]);
    }

    string BuildLocalText()
        => string.Create(CultureInfo.InvariantCulture, $"{Date:yyyy-MM-dd}T{Time:HH:mm:ss}[{IanaTimeZone}]");

    string BuildDiagnosticText()
    {
        var localText = BuildLocalText();
        if (!TryResolveOffsetText(out var offsetText))
            return localText;

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{localText[..19]}{offsetText}[{IanaTimeZone}]"
            );
    }

    string BuildCanonicalJsonText()
    {
        var resolved = ToInstant();
        var sign = resolved.Offset < TimeSpan.Zero ? "-" : "+";
        var absolute = resolved.Offset.Duration();
        var offsetText = string.Create(CultureInfo.InvariantCulture, $"{sign}{absolute:hh\\:mm}");
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{Date:yyyy-MM-dd}T{Time:HH:mm:ss}{offsetText}[{IanaTimeZone}]"
            );
    }

    bool IsOffsetCompatible(TimeSpan expectedOffset)
    {
        try
        {
            var timeZone = ResolveTimeZoneInfo(ianaTimeZone: IanaTimeZone);
            var localDateTime = Date.ToDateTime(time: Time, kind: DateTimeKind.Unspecified);

            if (timeZone.IsInvalidTime(dateTime: localDateTime))
                return false;

            if (timeZone.IsAmbiguousTime(dateTime: localDateTime))
                return timeZone.GetAmbiguousTimeOffsets(dateTime: localDateTime).Contains(value: expectedOffset);

            return timeZone.GetUtcOffset(dateTime: localDateTime) == expectedOffset;
        }
        catch (TimeZoneNotFoundException)
        {
            return false;
        }
        catch (InvalidTimeZoneException)
        {
            return false;
        }
    }

    bool TryResolveOffsetText(out string offsetText)
    {
        offsetText = string.Empty;
        try
        {
            var timeZone = ResolveTimeZoneInfo(ianaTimeZone: IanaTimeZone);
            var localDateTime = Date.ToDateTime(time: Time, kind: DateTimeKind.Unspecified);

            if (timeZone.IsInvalidTime(dateTime: localDateTime))
                return false;

            var offset = timeZone.IsAmbiguousTime(dateTime: localDateTime)
                ? timeZone.GetAmbiguousTimeOffsets(dateTime: localDateTime).Max()
                : timeZone.GetUtcOffset(dateTime: localDateTime);

            var sign = offset < TimeSpan.Zero ? "-" : "+";
            var absolute = offset.Duration();
            offsetText = string.Create(CultureInfo.InvariantCulture, $"{sign}{absolute:hh\\:mm}");
            return true;
        }
        catch (TimeZoneNotFoundException)
        {
            return false;
        }
        catch (InvalidTimeZoneException)
        {
            return false;
        }
    }
}

/// <summary>
/// A JSON converter for <see cref="ZonedDateTime"/> that encodes the value as a 'J' formatted string.
/// </summary>
public sealed class ZonedDateTimeJsonConverter : JsonConverter<ZonedDateTime>
{
    public override ZonedDateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException(message: $"Expected JSON string when reading {nameof(ZonedDateTime)}.");

        var text = reader.GetString();
        if (text is null || !ZonedDateTime.TryParse(s: text, provider: CultureInfo.InvariantCulture, result: out var value))
            throw new JsonException(message: $"JSON value is not a valid {nameof(ZonedDateTime)}.");

        return value;
    }

    public override void Write(Utf8JsonWriter writer, ZonedDateTime value, JsonSerializerOptions options)
    {
        try
        {
            writer.WriteStringValue(value: value.ToString(format: "J", formatProvider: CultureInfo.InvariantCulture));
        }
        catch (InvalidOperationException exception)
        {
            throw new JsonException(message: $"Unable to serialize {nameof(ZonedDateTime)} as canonical JSON string.", innerException: exception);
        }
    }
}
