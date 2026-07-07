namespace Cohesive.Tests.Domain;

/// <summary>
/// Tests for ISO 8601 UTC parsing helpers on <see cref="DateTime"/> and <see cref="DateTimeOffset"/>.
/// </summary>
public sealed class DateTimeExtensionsTests
{
    [Fact]
    public void DateTime_ParseIso8601Utc_AssumesUtcWhenOffsetMissing()
    {
        var parsed = DateTime.ParseIso8601Utc("2026-02-11T10:00:00");

        Assert.Equal(new(2026, 2, 11, 10, 0, 0, DateTimeKind.Utc), actual: parsed);
        Assert.Equal(DateTimeKind.Utc, parsed.Kind);
    }
    
    [Fact]
    public void DateTime_ParseIso8601Utc_WithZ()
    {
        var parsed = DateTime.ParseIso8601Utc("2026-02-11T10:00:00Z");
        var parsedDto = DateTimeOffset.ParseIso8601Utc("2026-02-11T10:00:00Z");
        Assert.Equal(new(2026, 2, 11, 10, 0, 0, DateTimeKind.Utc), parsed);
        Assert.Equal(new(2026, 2, 11, 10, 0, 0, TimeSpan.Zero), parsedDto);
        Assert.Equal(DateTimeKind.Utc, parsed.Kind);
    }
    
    [Fact]
    public void DateTime_ParseIso8601Utc_WithoutTime()
    {
        var parsed = DateTime.ParseIso8601Utc("2026-02-11");

        Assert.Equal(new(2026, 2, 11, 0, 0, 0, DateTimeKind.Utc), parsed);
        Assert.Equal(DateTimeKind.Utc, parsed.Kind);
    }
    
    [Fact]
    public void DateTime_ParseIso8601Utc_WithoutSeconds()
    {
        var parsed = DateTime.ParseIso8601Utc("2026-02-11T10:00");

        Assert.Equal(new(2026, 2, 11, 10, 0, 0, DateTimeKind.Utc), parsed);
        Assert.Equal(DateTimeKind.Utc, parsed.Kind);
    }

    [Fact]
    public void DateTime_ParseIso8601Utc_NormalizesOffsetToUtc()
    {
        var parsed = DateTime.ParseIso8601Utc("2026-02-11T10:00:00-05:00");

        Assert.Equal(new(2026, 2, 11, 15, 0, 0, DateTimeKind.Utc), parsed);
        Assert.Equal(DateTimeKind.Utc, parsed.Kind);
    }

    [Fact]
    public void DateTimeOffset_ParseIso8601Utc_AssumesUtcWhenOffsetMissing()
    {
        var parsed = DateTimeOffset.ParseIso8601Utc("2026-02-11T10:00:00.1234567");

        Assert.Equal(new DateTimeOffset(2026, 2, 11, 10, 0, 0, 123, TimeSpan.Zero).AddTicks(4567), parsed);
        Assert.Equal(TimeSpan.Zero, actual: parsed.Offset);
    }

    [Fact]
    public void DateTimeOffset_ParseIso8601Utc_NormalizesOffsetToUtc()
    {
        var parsed = DateTimeOffset.ParseIso8601Utc("2026-02-11T10:00:00-05:00");

        Assert.Equal(new(2026, 2, 11, 15, 0, 0, TimeSpan.Zero), parsed);
        Assert.Equal(TimeSpan.Zero, parsed.Offset);
    }

    [Fact]
    public void ParseIso8601Utc_InvalidInput_ThrowsFormatException()
    {
        Assert.Throws<FormatException>(testCode: () => DateTime.ParseIso8601Utc("not-a-datetime"));
        Assert.Throws<FormatException>(testCode: () => DateTime.ParseIso8601Utc("2026-10-11 10:00:00"));
        Assert.Throws<FormatException>(testCode: () => DateTimeOffset.ParseIso8601Utc("not-a-datetime"));
    }
}
