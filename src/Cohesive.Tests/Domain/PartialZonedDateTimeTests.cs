using System.Text.Json;

namespace Cohesive.Tests.Domain;

/// <summary>
/// Tests for <see cref="PartialZonedDateTime"/>.
/// </summary>
public sealed class PartialZonedDateTimeTests
{
    [Fact]
    public void PartialZonedDateTime_UnZoned_HasNoTimeZone()
    {
        var value = PartialZonedDateTime.UnZoned(
            date: new DateOnly(2026, 2, 11),
            time: new TimeOnly(10, 0, 0));

        Assert.False(value.HasTimeZone);
        Assert.Null(value.IanaTimeZone);
    }

    [Fact]
    public void PartialZonedDateTime_Zoned_ValidatesIanaTimeZone()
    {
        var value = PartialZonedDateTime.Zoned(
            date: new DateOnly(2026, 2, 11),
            time: new TimeOnly(10, 0, 0),
            ianaTimeZone: "America/New_York");

        Assert.True(value.HasTimeZone);
        Assert.Equal("America/New_York", value.IanaTimeZone);
    }

    [Fact]
    public void PartialZonedDateTime_Zoned_RejectsNonIanaTimeZone()
    {
        Assert.Throws<ArgumentException>(
            testCode: () => PartialZonedDateTime.Zoned(
                date: new DateOnly(2026, 2, 11),
                time: new TimeOnly(10, 0, 0),
                ianaTimeZone: "Eastern Standard Time"));
    }

    [Fact]
    public void PartialZonedDateTime_Create_NormalizesWindowsTimeZoneIdToIana()
    {
        var value = PartialZonedDateTime.Create(
            date: new DateOnly(2026, 2, 11),
            time: new TimeOnly(10, 0, 0),
            timeZone: "Eastern Standard Time");

        Assert.True(value.HasTimeZone);
        Assert.Equal(expected: "America/New_York", actual: value.IanaTimeZone);
    }

    [Fact]
    public void PartialZonedDateTime_Create_AcceptsMissingTimeZone()
    {
        var value = PartialZonedDateTime.Create(
            date: new DateOnly(2026, 2, 11),
            time: new TimeOnly(10, 0, 0));

        Assert.False(value.HasTimeZone);
        Assert.Null(@object: value.IanaTimeZone);
    }

    [Fact]
    public void PartialZonedDateTime_TryToZonedDateTime_ReturnsFalseWhenZoneMissing()
    {
        var value = PartialZonedDateTime.UnZoned(
            date: new DateOnly(2026, 2, 11),
            time: new TimeOnly(10, 0, 0));

        var ok = value.TryToZonedDateTime(out var zoned);

        Assert.False(condition: ok);
        Assert.Equal(expected: default, actual: zoned);
    }

    [Fact]
    public void PartialZonedDateTime_TryToZonedDateTime_ReturnsTrueWhenZonePresent()
    {
        var value = PartialZonedDateTime.Zoned(
            date: new DateOnly(2026, 2, 11),
            time: new TimeOnly(10, 0, 0),
            ianaTimeZone: "America/New_York");

        var ok = value.TryToZonedDateTime(out var zoned);

        Assert.True(condition: ok);
        Assert.Equal(expected: "America/New_York", actual: zoned.IanaTimeZone);
    }

    [Fact]
    public void PartialZonedDateTime_ToZonedDateTime_UsesFallbackWhenZoneMissing()
    {
        var value = PartialZonedDateTime.UnZoned(
            date: new DateOnly(2026, 2, 11),
            time: new TimeOnly(10, 0, 0));

        var zoned = value.ToZonedDateTime(fallbackIanaTimeZone: "America/Chicago");

        Assert.Equal(expected: "America/Chicago", actual: zoned.IanaTimeZone);
    }

    [Fact]
    public void PartialZonedDateTime_ToZonedDateTime_PrefersEmbeddedZoneOverFallback()
    {
        var value = PartialZonedDateTime.Zoned(
            date: new DateOnly(2026, 2, 11),
            time: new TimeOnly(10, 0, 0),
            ianaTimeZone: "America/New_York"
            );

        var zoned = value.ToZonedDateTime(fallbackIanaTimeZone: "America/Chicago");

        Assert.Equal(expected: "America/New_York", actual: zoned.IanaTimeZone);
    }

    [Fact]
    public void PartialZonedDateTime_WithTimeZone_WithoutTimeZone_UpdateZoneState()
    {
        var unZoned = PartialZonedDateTime.UnZoned(
            date: new DateOnly(2026, 2, 11),
            time: new TimeOnly(10, 0, 0)
            );

        var zoned = unZoned.WithTimeZone(ianaTimeZone: "America/New_York");
        var backToUnZoned = zoned.WithoutTimeZone();

        Assert.True(condition: zoned.HasTimeZone);
        Assert.Equal(expected: "America/New_York", actual: zoned.IanaTimeZone);
        Assert.False(condition: backToUnZoned.HasTimeZone);
        Assert.Null(backToUnZoned.IanaTimeZone);
    }

    [Fact]
    public void PartialZonedDateTime_JsonSerialization_RoundTrips()
    {
        var value = PartialZonedDateTime.Zoned(
            date: new DateOnly(2026, 2, 11),
            time: new TimeOnly(10, 0, 0),
            ianaTimeZone: "America/New_York"
            );

        var json = JsonSerializer.Serialize(value: value);
        var roundTripped = JsonSerializer.Deserialize<PartialZonedDateTime>(json: json);

        Assert.Equal(expected: value, actual: roundTripped);
    }
}
