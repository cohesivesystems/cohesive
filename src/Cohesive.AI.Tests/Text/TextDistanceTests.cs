using Cohesive.AI.Text;

namespace Cohesive.AI.Tests.Text;

public sealed class TextDistanceTests
{
    [Fact]
    public void ComputeLevenshteinDistance_IgnoreCaseTrue_TreatsCaseOnlyDifferenceAsEqual()
    {
        var distance = TextDistance.ComputeLevenshteinDistance("OrderId".AsSpan(), "orderid".AsSpan(), ignoreCase: true);

        Assert.Equal(0, distance);
    }

    [Fact]
    public void ComputeLevenshteinDistance_IgnoreCaseFalse_PreservesCaseDifferences()
    {
        var distance = TextDistance.ComputeLevenshteinDistance("OrderId".AsSpan(), "orderid".AsSpan(), ignoreCase: false);

        Assert.Equal(2, distance);
    }

    [Fact]
    public void ComputeNormalizedLevenshteinSimilarity_ReturnsExpectedScore()
    {
        var similarity = TextDistance.ComputeNormalizedLevenshteinSimilarity("kitten".AsSpan(), "sitting".AsSpan());

        Assert.Equal(0.571f, similarity, precision: 3);
    }

    [Fact]
    public void ComputeNormalizedLevenshteinSimilarity_EmptyInputs_ReturnsExpectedExtremes()
    {
        Assert.Equal(1f, TextDistance.ComputeNormalizedLevenshteinSimilarity(ReadOnlySpan<char>.Empty, ReadOnlySpan<char>.Empty));
        Assert.Equal(0f, TextDistance.ComputeNormalizedLevenshteinSimilarity("abc".AsSpan(), ReadOnlySpan<char>.Empty));
    }
}
