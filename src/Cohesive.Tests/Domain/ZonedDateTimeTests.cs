using System.Globalization;
using System.Text.Json;

namespace Cohesive.Tests.Domain;

/// <summary>
/// Tests for <see cref="ZonedDateTime"/>.
/// </summary>
public sealed class ZonedDateTimeTests
{
    [Fact]
    public void ZonedDateTime_Create_AcceptsIanaTimeZoneId()
    {
        var zonedDateTime = ZonedDateTime.Create(
            date: new DateOnly(2026, 2, 11),
            time: new TimeOnly(10, 0, 0),
            ianaTimeZone: "America/New_York"
            );

        Assert.Equal(expected: "America/New_York", actual: zonedDateTime.IanaTimeZone);
    }

    [Fact]
    public void ZonedDateTime_Create_NormalizesWindowsTimeZoneIdToIana()
    {
        var zonedDateTime = ZonedDateTime.Create(
            date: new DateOnly(2026, 2, 11),
            time: new TimeOnly(10, 0, 0),
            ianaTimeZone: "Eastern Standard Time"
            );

        Assert.Equal(expected: "America/New_York", actual: zonedDateTime.IanaTimeZone);
    }

    [Fact]
    public void ZonedDateTime_Create_RejectsUnknownTimeZoneId()
    {
        Assert.Throws<ArgumentException>(
            testCode: () => ZonedDateTime.Create(
                date: new DateOnly(2026, 2, 11),
                time: new TimeOnly(10, 0, 0),
                ianaTimeZone: "Not/ARealTimeZone"
                )
            );
    }

    [Fact]
    public void ZonedDateTime_Constructor_RejectsNonIanaTimeZoneId()
    {
        Assert.Throws<ArgumentException>(
            testCode: () => _ = new ZonedDateTime(
                date: new DateOnly(2026, 2, 11),
                time: new TimeOnly(10, 0, 0),
                ianaTimeZone: "Eastern Standard Time"
                )
            );
    }

    [Fact]
    public void ZonedDateTime_ToString_DefaultFormat_UsesInvariantDiagnosticFormat()
    {
        var zonedDateTime = ZonedDateTime.Create(
            date: new DateOnly(2026, 2, 11),
            time: new TimeOnly(10, 0, 0),
            ianaTimeZone: "America/New_York");

        Assert.Equal(
            expected: "2026-02-11T10:00:00-05:00[America/New_York]",
            actual: zonedDateTime.ToString());
    }

    [Fact]
    public void ZonedDateTime_ToString_InvalidClockTransition_FallsBackWithoutOffset()
    {
        var zonedDateTime = new ZonedDateTime(
            date: new DateOnly(2026, 3, 8),
            time: new TimeOnly(2, 30, 0),
            ianaTimeZone: "America/New_York");

        Assert.Equal(
            expected: "2026-03-08T02:30:00[America/New_York]",
            actual: zonedDateTime.ToString());
    }

    [Fact]
    public void ZonedDateTime_ToString_CanonicalJsonFormat_UsesJSpecifier()
    {
        var zonedDateTime = ZonedDateTime.Create(
            date: new DateOnly(2026, 2, 11),
            time: new TimeOnly(10, 0, 0),
            ianaTimeZone: "America/New_York");

        Assert.Equal(
            expected: "2026-02-11T10:00:00-05:00[America/New_York]",
            actual: zonedDateTime.ToString(format: "J", formatProvider: CultureInfo.InvariantCulture));
    }

    [Fact]
    public void ZonedDateTime_TryFormat_CanonicalJsonFormat_WritesIntoDestination()
    {
        var zonedDateTime = ZonedDateTime.Create(
            date: new DateOnly(2026, 2, 11),
            time: new TimeOnly(10, 0, 0),
            ianaTimeZone: "America/New_York");

        Span<char> buffer = stackalloc char[64];
        var ok = zonedDateTime.TryFormat(
            destination: buffer,
            charsWritten: out var charsWritten,
            format: "J",
            formatProvider: CultureInfo.InvariantCulture);

        Assert.True(condition: ok);
        var text = new string(value: buffer[..charsWritten]);
        Assert.Equal(expected: "2026-02-11T10:00:00-05:00[America/New_York]", actual: text);
    }

    [Fact]
    public void ZonedDateTime_Parse_RoundTripsCanonicalText()
    {
        var text = "2026-02-11T10:00:00-05:00[America/New_York]";
        var parsed = ZonedDateTime.Parse(s: text, provider: CultureInfo.InvariantCulture);

        Assert.Equal(expected: new DateOnly(2026, 2, 11), actual: parsed.Date);
        Assert.Equal(expected: new TimeOnly(10, 0, 0), actual: parsed.Time);
        Assert.Equal(expected: "America/New_York", actual: parsed.IanaTimeZone);
    }

    [Fact]
    public void ZonedDateTime_TryParse_RejectsMismatchedOffsetForZone()
    {
        var ok = ZonedDateTime.TryParse(
            s: "2026-02-11T10:00:00+00:00[America/New_York]",
            provider: CultureInfo.InvariantCulture,
            result: out _);

        Assert.False(condition: ok);
    }

    [Fact]
    public void ZonedDateTime_JsonSerialization_UsesCanonicalString()
    {
        var value = ZonedDateTime.Create(
            date: new DateOnly(2026, 2, 11),
            time: new TimeOnly(14, 30, 45),
            ianaTimeZone: "America/New_York");

        var json = JsonSerializer.Serialize(value: value);

        Assert.Equal(expected: "\"2026-02-11T14:30:45-05:00[America/New_York]\"", actual: json);
    }

    [Fact]
    public void ZonedDateTime_JsonDeserialization_RoundTripsFromCanonicalString()
    {
        var json = "\"2026-02-11T14:30:45-05:00[America/New_York]\"";
        var value = JsonSerializer.Deserialize<ZonedDateTime>(json: json);

        Assert.Equal(
            expected: ZonedDateTime.Create(
                date: new DateOnly(2026, 2, 11),
                time: new TimeOnly(14, 30, 45),
                ianaTimeZone: "America/New_York"),
            actual: value);
    }

    [Fact]
    public void ZonedDateTime_ToInstant_UsesTimeZoneOffset()
    {
        var zonedDateTime = ZonedDateTime.Create(
            date: new DateOnly(2026, 2, 11),
            time: new TimeOnly(10, 0, 0),
            ianaTimeZone: "America/New_York");

        var instant = zonedDateTime.ToInstant();

        Assert.Equal(expected: new DateTimeOffset(2026, 2, 11, 10, 0, 0, TimeSpan.FromHours(-5)), actual: instant);
    }

    [Fact]
    public void ZonedDateTime_TryToInstant_SucceedsForResolvableValue()
    {
        var zonedDateTime = ZonedDateTime.Create(
            date: new DateOnly(2026, 2, 11),
            time: new TimeOnly(10, 0, 0),
            ianaTimeZone: "America/New_York");

        var ok = zonedDateTime.TryToInstant(out var instant);

        Assert.True(condition: ok);
        Assert.Equal(expected: new DateTimeOffset(2026, 2, 11, 10, 0, 0, TimeSpan.FromHours(-5)), actual: instant);
    }

    [Fact]
    public void ZonedDateTime_TryToInstant_ReturnsFalseForInvalidClockTransition()
    {
        var zonedDateTime = new ZonedDateTime(
            date: new DateOnly(2026, 3, 8),
            time: new TimeOnly(2, 30, 0),
            ianaTimeZone: "America/New_York");

        var ok = zonedDateTime.TryToInstant(out var instant);

        Assert.False(condition: ok);
        Assert.Equal(expected: default, actual: instant);
    }

    [Fact]
    public void ZonedDateTime_WithDate_WithTime_WithTimeZone_ReturnUpdatedCopies()
    {
        var original = ZonedDateTime.Create(
            date: new DateOnly(2026, 2, 11),
            time: new TimeOnly(10, 0, 0),
            ianaTimeZone: "America/New_York");

        var withDate = original.WithDate(date: new DateOnly(2026, 2, 12));
        var withTime = original.WithTime(time: new TimeOnly(11, 15, 0));
        var withZone = original.WithTimeZone(ianaTimeZone: "America/Chicago");

        Assert.Equal(expected: new DateOnly(2026, 2, 12), actual: withDate.Date);
        Assert.Equal(expected: original.Time, actual: withDate.Time);
        Assert.Equal(expected: original.IanaTimeZone, actual: withDate.IanaTimeZone);

        Assert.Equal(expected: original.Date, actual: withTime.Date);
        Assert.Equal(expected: new TimeOnly(11, 15, 0), actual: withTime.Time);
        Assert.Equal(expected: original.IanaTimeZone, actual: withTime.IanaTimeZone);

        Assert.Equal(expected: original.Date, actual: withZone.Date);
        Assert.Equal(expected: original.Time, actual: withZone.Time);
        Assert.Equal(expected: "America/Chicago", actual: withZone.IanaTimeZone);
    }

    [Fact]
    public void ZonedDateTime_ToUtcInstant_ConvertsInstantToUtc()
    {
        var zonedDateTime = ZonedDateTime.Create(
            date: new DateOnly(2026, 2, 11),
            time: new TimeOnly(10, 0, 0),
            ianaTimeZone: "America/New_York");

        var utc = zonedDateTime.ToUtcInstant();

        Assert.Equal(expected: TimeSpan.Zero, actual: utc.Offset);
        Assert.Equal(expected: new DateTimeOffset(2026, 2, 11, 15, 0, 0, TimeSpan.Zero), actual: utc);
    }

    [Fact]
    public void ZonedDateTime_GetPossibleOffsets_ReturnsSingleOffsetForNormalTime()
    {
        var zonedDateTime = ZonedDateTime.Create(
            date: new DateOnly(2026, 2, 11),
            time: new TimeOnly(10, 0, 0),
            ianaTimeZone: "America/New_York");

        var offsets = zonedDateTime.GetPossibleOffsets();

        var singleOffset = Assert.Single(collection: offsets);
        Assert.Equal(expected: TimeSpan.FromHours(-5), actual: singleOffset);
    }

    [Fact]
    public void ZonedDateTime_GetPossibleOffsets_ReturnsTwoOffsetsForAmbiguousTime()
    {
        var zonedDateTime = new ZonedDateTime(
            date: new DateOnly(2026, 11, 1),
            time: new TimeOnly(1, 30, 0),
            ianaTimeZone: "America/New_York");

        var offsets = zonedDateTime.GetPossibleOffsets();

        Assert.Equal(expected: 2, actual: offsets.Count);
        Assert.Equal(expected: TimeSpan.FromHours(-4), actual: offsets[0]);
        Assert.Equal(expected: TimeSpan.FromHours(-5), actual: offsets[1]);
    }

    [Fact]
    public void ZonedDateTime_GetPossibleOffsets_ReturnsNoneForInvalidTime()
    {
        var zonedDateTime = new ZonedDateTime(
            date: new DateOnly(2026, 3, 8),
            time: new TimeOnly(2, 30, 0),
            ianaTimeZone: "America/New_York");

        var offsets = zonedDateTime.GetPossibleOffsets();

        Assert.Empty(collection: offsets);
    }

    [Fact]
    public void ZonedDateTime_CompareInstant_ComparesByResolvedInstant()
    {
        var newYork = ZonedDateTime.Create(
            date: new DateOnly(2026, 2, 11),
            time: new TimeOnly(10, 0, 0),
            ianaTimeZone: "America/New_York");
        var chicago = ZonedDateTime.Create(
            date: new DateOnly(2026, 2, 11),
            time: new TimeOnly(9, 0, 0),
            ianaTimeZone: "America/Chicago");
        var later = ZonedDateTime.Create(
            date: new DateOnly(2026, 2, 11),
            time: new TimeOnly(10, 30, 0),
            ianaTimeZone: "America/New_York");

        Assert.Equal(expected: 0, actual: newYork.CompareInstant(other: chicago));
        Assert.True(condition: newYork.CompareInstant(other: later) < 0);
        Assert.True(condition: later.CompareInstant(other: newYork) > 0);
    }

    [Fact]
    public void ZonedDateTime_ToInstant_InvalidClockTransition_Throws()
    {
        var zonedDateTime = new ZonedDateTime(
            date: new DateOnly(2026, 3, 8),
            time: new TimeOnly(2, 30, 0),
            ianaTimeZone: "America/New_York");

        Assert.Throws<InvalidOperationException>(testCode: () => _ = zonedDateTime.ToInstant());
    }

    [Fact]
    public void ZonedDateTime_ToInstant_AmbiguousClockTransition_UsesEarlierOccurrence()
    {
        var zonedDateTime = new ZonedDateTime(
            date: new(year: 2026, month: 11, day: 1),
            time: new(hour: 1, minute: 30, second: 0),
            ianaTimeZone: "America/New_York"
            );

        var instant = zonedDateTime.ToInstant();

        Assert.Equal(expected: TimeSpan.FromHours(-4), actual: instant.Offset);
    }

    [Fact]
    public void ZonedDateTime_Constructor_RejectsUnknownTimeZone()
    {
        Assert.Throws<ArgumentException>(
            testCode: () => _ = new ZonedDateTime(
                date: new DateOnly(2026, 2, 11),
                time: new TimeOnly(10, 0, 0),
                ianaTimeZone: "Not/ARealTimeZone"
                )
            );
    }
}
