namespace Cohesive.Tests.Prelude;

public sealed class ComparableExtensionsTests
{
    [Fact]
    public void MinByOrDefault_ForEmptySequence_ReturnsDefault()
    {
        var result = Array.Empty<Sample>().MinByOrDefault(static x => x.Score);

        Assert.Null(result);
    }

    [Fact]
    public void MinByOrDefault_ReturnsFirstItemWithLowestSelectedValue()
    {
        var firstLowest = new Sample(Name: "beta", Score: 1);
        var source = new[]
        {
            new Sample(Name: "alpha", Score: 3),
            firstLowest,
            new Sample(Name: "gamma", Score: 1),
        };

        var result = source.MinByOrDefault(static x => x.Score);

        Assert.Same(firstLowest, result);
    }

    [Fact]
    public void MaxByOrDefault_ForEmptySequence_ReturnsDefault()
    {
        var result = Array.Empty<Sample>().MaxByOrDefault(static x => x.Score);

        Assert.Null(result);
    }

    [Fact]
    public void MaxByOrDefault_ReturnsFirstItemWithHighestSelectedValue()
    {
        var firstHighest = new Sample(Name: "beta", Score: 5);
        var source = new[]
        {
            new Sample(Name: "alpha", Score: 3),
            firstHighest,
            new Sample(Name: "gamma", Score: 5),
        };

        var result = source.MaxByOrDefault(static x => x.Score);

        Assert.Same(firstHighest, result);
    }

    [Fact]
    public void MinByOrDefault_NullSource_Throws()
    {
        IEnumerable<Sample>? source = null;

        Assert.Throws<ArgumentNullException>(() => source!.MinByOrDefault(static x => x.Score));
    }

    [Fact]
    public void MaxByOrDefault_NullSelector_Throws()
    {
        var source = new[] { new Sample(Name: "alpha", Score: 1) };
        Func<Sample, int> selector = null!;

        Assert.Throws<ArgumentNullException>(() => source.MaxByOrDefault(selector));
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData(null, "b", "b")]
    [InlineData("c", null, "c")]
    [InlineData("c", "b", "b")]
    [InlineData("b", "c", "b")]
    public void Min_ForTwoValues_ReturnsMinimumNonNullValue(string? value, string? other, string? expected)
    {
        var result = value.Min(other);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void Min_ForMultipleValues_ReturnsMinimumNonNullValue()
    {
        string? value = "d";

        Assert.Equal("b", value.Min(null, "b"));
        Assert.Equal("a", value.Min("c", null, "a"));
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData(null, "b", "b")]
    [InlineData("c", null, "c")]
    [InlineData("c", "b", "c")]
    [InlineData("b", "c", "c")]
    public void Max_ForTwoValues_ReturnsMaximumNonNullValue(string? value, string? other, string? expected)
    {
        var result = value.Max(other);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void Max_ForMultipleValues_ReturnsMaximumNonNullValue()
    {
        string? value = "a";

        Assert.Equal("d", value.Max(null, "d"));
        Assert.Equal("e", value.Max("c", null, "e"));
    }

    sealed record Sample(string Name, int Score);
}
