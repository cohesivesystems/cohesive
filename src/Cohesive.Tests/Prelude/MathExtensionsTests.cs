namespace Cohesive.Tests.Prelude;

public sealed class MathExtensionsTests
{
    [Fact]
    public void TryGetExactInt64FromDouble_ForExactInteger_ReturnsTrueAndValue()
    {
        var ok = Math.TryGetExactInt64FromDouble(42d, out var result);

        Assert.True(ok);
        Assert.Equal(42L, result);
    }

    [Fact]
    public void TryGetExactInt64FromDouble_ForFractionalValue_ReturnsFalse()
    {
        var ok = Math.TryGetExactInt64FromDouble(42.5d, out var result);

        Assert.False(ok);
        Assert.Equal(0L, result);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void TryGetExactInt64FromDouble_ForNonFiniteValue_ReturnsFalse(double value)
    {
        var ok = Math.TryGetExactInt64FromDouble(value, out var result);

        Assert.False(ok);
        Assert.Equal(0L, result);
    }

    [Fact]
    public void ApproximatelyEquals_Double_ReturnsTrueWithinAbsoluteTolerance()
    {
        var equal = Math.ApproximatelyEquals(1d, 1d + 5e-13);

        Assert.True(equal);
    }

    [Fact]
    public void ApproximatelyEquals_Double_ReturnsTrueWithinRelativeTolerance()
    {
        var equal = Math.ApproximatelyEquals(1_000_000d, 1_000_000.0005d);

        Assert.True(equal);
    }

    [Fact]
    public void ApproximatelyEquals_Double_ReturnsFalseOutsideTolerance()
    {
        var equal = Math.ApproximatelyEquals(1d, 1.01d);

        Assert.False(equal);
    }

    [Fact]
    public void ApproximatelyEquals_Float_ReturnsTrueWithinSpecifiedRelativeTolerance()
    {
        var equal = Math.ApproximatelyEquals(1_000_000f, 1_000_000.5f, relativeTolerance: 1e-6f);

        Assert.True(equal);
    }

    [Fact]
    public void ApproximatelyEquals_Float_ReturnsFalseOutsideTolerance()
    {
        var equal = Math.ApproximatelyEquals(1f, 1.01f);

        Assert.False(equal);
    }
}
