using Cohesive.AI.Text;

namespace Cohesive.AI.Tests.Text;

public sealed class TokenScoringTests
{
    [Fact]
    public void CommonPrefixLength_CountsMatchingLeadingTokens()
    {
        var length = OrderedTokenScoring.CommonPrefixLength(
            left: [10, 20, 30, 40],
            right: [10, 20, 90]);

        Assert.Equal(2, length);
    }

    [Fact]
    public void FirstTokenMatches_RequiresNonEmptySequencesAndMatchingHead()
    {
        Assert.True(OrderedTokenScoring.FirstTokenMatches(
            left: [7, 8, 9],
            right: [7, 1]));

        Assert.False(OrderedTokenScoring.FirstTokenMatches(
            left: [],
            right: [7, 1]));
    }

    [Fact]
    public void LastTokenMatches_RequiresNonEmptySequencesAndMatchingTail()
    {
        Assert.True(OrderedTokenScoring.LastTokenMatches(
            left: [7, 8, 9],
            right: [1, 9]));

        Assert.False(OrderedTokenScoring.LastTokenMatches(
            left: [7, 8],
            right: [1, 9]));
    }

    [Fact]
    public void LongestCommonSubsequenceLength_ComputesOrderedOverlap()
    {
        var length = OrderedTokenScoring.LongestCommonSubsequenceLength(
            left: [1, 3, 4, 1, 2, 8],
            right: [3, 4, 1, 2, 1, 7]);

        Assert.Equal(4, length);
    }

    [Fact]
    public void NormalizedLongestCommonSubsequence_ReturnsZeroWhenBothSequencesAreEmpty()
    {
        var similarity = OrderedTokenScoring.NormalizedLongestCommonSubsequence(
            left: [],
            right: []);

        Assert.Equal(0f, similarity);
    }

    [Fact]
    public void NormalizedLongestCommonSubsequence_NormalizesByLongerSequence()
    {
        var similarity = OrderedTokenScoring.NormalizedLongestCommonSubsequence(
            left: [1, 2, 3, 4],
            right: [2, 3]);

        Assert.Equal(0.5f, similarity, precision: 3);
    }

    [Fact]
    public void LevenshteinDistance_ComputesTokenEditDistance()
    {
        var distance = OrderedTokenScoring.LevenshteinDistance(
            left: [1, 2, 3],
            right: [1, 4, 3, 5]);

        Assert.Equal(2, distance);
    }

    [Fact]
    public void NormalizedLevenshteinSimilarity_HandlesEmptyAndPartialMatches()
    {
        Assert.Equal(1f, OrderedTokenScoring.NormalizedLevenshteinSimilarity(
            left: [],
            right: []));

        Assert.Equal(0f, OrderedTokenScoring.NormalizedLevenshteinSimilarity(
            left: [],
            right: [1, 2]));

        Assert.Equal(0.75f, OrderedTokenScoring.NormalizedLevenshteinSimilarity(
            left: [1, 2, 3, 4],
            right: [1, 2, 9, 4]),
            precision: 3);
    }

    [Fact]
    public void JaccardDistinctPrefixes_DeduplicatesWithinPrefixes()
    {
        var score = TokenSetScoring.JaccardDistinctPrefixes(
            left: [1, 2, 2, 3],
            leftLength: 4,
            right: [2, 2, 3, 4],
            rightLength: 4);

        Assert.Equal(0.5f, score, precision: 3);
    }

    [Fact]
    public void WriteSharedDistinctLeftOrdered_PreservesLeftOrder()
    {
        Span<int> destination = stackalloc int[5];

        var written = TokenSetScoring.WriteSharedDistinctLeftOrdered(
            left: [5, 3, 5, 2, 1],
            right: [2, 2, 5, 9],
            destination: destination);

        Assert.Equal(2, written);
        Assert.Equal([5, 2], destination[..written].ToArray());
    }

    [Fact]
    public void ComputeOverlapStatsSortedDistinct_UsesSortedDistinctFastPath()
    {
        var stats = TokenSetScoring.ComputeOverlapStatsSortedDistinct(
            left: [1, 2, 3],
            right: [2, 3, 4]);

        Assert.Equal(2, stats.Intersection);
        Assert.Equal(4, stats.Union);
        Assert.Equal(3, stats.LeftUniqueCount);
        Assert.Equal(3, stats.RightUniqueCount);
        Assert.Equal(0.5f, stats.Jaccard, precision: 3);
        Assert.Equal(0.667f, stats.LeftContainment, precision: 3);
        Assert.Equal(0.667f, stats.RightContainment, precision: 3);
        Assert.Equal(0.667f, stats.OverlapCoefficient, precision: 3);
    }
}
