using Cohesive.Prelude;

namespace Cohesive.Tests.Prelude;

public sealed class StringExtensionsTests
{
    [Theory]
    [InlineData("A  B", '-', true, "a-b")]
    [InlineData("A  B", '-', false, "A-B")]
    [InlineData("__A---B__", '-', true, "a-b")]
    [InlineData("A/B", '_', true, "a_b")]
    public void ToLettersOrDigitsWithSeparator_NormalizesDelimitedText(
        string value,
        char separator,
        bool lowerCase,
        string expected)
    {
        var result = value.AsSpan().ToLettersOrDigitsWithSeparator(separator, lowerCase);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    [InlineData("\r\n")]
    public void EmptyOrWhiteSpaceAsNull_ForNullOrWhitespace_ReturnsNull(string? value)
    {
        var result = value.EmptyOrWhiteSpaceAsNull();

        Assert.Null(result);
    }

    [Theory]
    [InlineData("a")]
    [InlineData("  a")]
    [InlineData("a  ")]
    [InlineData("  a  ")]
    public void EmptyOrWhiteSpaceAsNull_ForNonWhitespace_ReturnsOriginalValue(string value)
    {
        var result = value.EmptyOrWhiteSpaceAsNull();

        Assert.Equal(value, result);
    }
}
