using Cohesive.AI.Numerics;

namespace Cohesive.AI.Tests.Numerics;

/// <summary>
/// Unit tests for shared vector numerics helpers.
/// </summary>
public sealed class NumericsTests
{
    [Fact]
    public void Math_Clamp01_Double_ClampsToUnitInterval()
    {
        Assert.Equal(0d, Math.Clamp01(-0.25d), precision: 12);
        Assert.Equal(0.42d, Math.Clamp01(0.42d), precision: 12);
        Assert.Equal(1d, Math.Clamp01(1.25d), precision: 12);
    }

    [Fact]
    public void Math_Clamp01_Float_ClampsToUnitInterval()
    {
        Assert.Equal(0f, Math.Clamp01(-0.25f), precision: 6);
        Assert.Equal(0.42f, Math.Clamp01(0.42f), precision: 6);
        Assert.Equal(1f, Math.Clamp01(1.25f), precision: 6);
    }

    [Fact]
    public void Math_ConvexCombine_InterpolatesWithClampedWeight()
    {
        Assert.Equal(0.2d, Math.ConvexCombine(0.2d, 0.8d, weight: -2d), precision: 12);
        Assert.Equal(0.5d, Math.ConvexCombine(0.2d, 0.8d, weight: 0.5d), precision: 12);
        Assert.Equal(0.8d, Math.ConvexCombine(0.2d, 0.8d, weight: 2d), precision: 12);
    }

    [Fact]
    public void VectorMath_Dot_KnownVectors_ReturnsExpectedValue()
    {
        var x = new[] { 1f, 2f, 3f };
        var w = new[] { 4f, 5f, 6f };

        var dot = VectorMath.Dot(x, w);

        Assert.Equal(32f, dot, precision: 6);
    }

    [Fact]
    public void VectorMath_DotHighPrecision_CancellationCase_PreservesSignal()
    {
        var x = new[] { 16_777_216f, 1f, -16_777_216f };
        var w = new[] { 1f, 1f, 1f };

        var baseline = VectorMath.Dot(x, w);
        var precise = VectorMath.DotHighPrecision(x, w);

        Assert.Equal(1f, baseline, precision: 6);
        Assert.Equal(1d, precise, precision: 12);
    }

    [Fact]
    public void VectorMath_DotHighPrecision_DoubleVectors_ReturnsExpectedValue()
    {
        var x = new[] { 1d, 2d, 3d };
        var w = new[] { 4d, 5d, 6d };
        var precise = VectorMath.DotHighPrecision(x, w);

        Assert.Equal(32d, precise, precision: 12);
    }

    [Fact]
    public void VectorNorm_ComputeL2_EmptyVector_ReturnsZero()
    {
        var norm = VectorMath.NormL2([]);
        Assert.Equal(0d, norm);
    }

    [Fact]
    public void VectorNorm_ComputeL2_KnownVector_ReturnsExpectedValue()
    {
        var vector = new[] { 3f, 4f };
        var norm = VectorMath.NormL2(vector);
        Assert.Equal(5d, norm, precision: 6);
    }

    [Fact]
    public void VectorNorm_ComputeL2_NonFiniteInput_ReturnsZero()
    {
        var vector = new[] { 1f, float.PositiveInfinity };
        var norm = VectorMath.NormL2(vector);
        Assert.Equal(0d, norm);
    }

    [Fact]
    public void VectorNorm_TryComputeDotAndL2Norms_ValidVectors_ReturnsExpectedValues()
    {
        var left = new[] { 1f, 2f, 3f };
        var right = new[] { 4f, 5f, 6f };

        var success = VectorMath.DotAndNormL2(left, right, out var dot, out var leftNorm, out var rightNorm);

        Assert.True(success);
        Assert.Equal(32d, dot, precision: 6);
        Assert.Equal(Math.Sqrt(14d), leftNorm, precision: 6);
        Assert.Equal(Math.Sqrt(77d), rightNorm, precision: 6);
    }

    [Fact]
    public void VectorNorm_TryComputeDotAndL2Norms_MismatchedLengths_ReturnsFalse()
    {
        var left = new[] { 1f, 2f };
        var right = new[] { 1f };

        var success = VectorMath.DotAndNormL2(left, right, out var dot, out var leftNorm, out var rightNorm);

        Assert.False(success);
        Assert.Equal(0d, dot);
        Assert.Equal(0d, leftNorm);
        Assert.Equal(0d, rightNorm);
    }

    [Fact]
    public void VectorNorm_TryComputeDotAndL2Norms_ZeroNormVector_ReturnsFalse()
    {
        var left = new[] { 0f, 0f, 0f };
        var right = new[] { 1f, 2f, 3f };

        var success = VectorMath.DotAndNormL2(left, right, out _, out _, out _);
        Assert.False(success);
    }

    [Fact]
    public void CosineSimilarity_TryCompute_OrthogonalVectors_ReturnsZeroSimilarity()
    {
        var left = new[] { 1f, 0f, 0f };
        var right = new[] { 0f, 2f, 0f };

        var success = VectorMath.TryCosineSimilarity(left, right, out var similarity);

        Assert.True(success);
        Assert.Equal(0d, similarity, precision: 6);
    }

    [Fact]
    public void CosineSimilarity_TryCompute_CollinearVectors_ReturnsOne()
    {
        var left = new[] { 1f, 2f, 3f };
        var right = new[] { 2f, 4f, 6f };

        var success = VectorMath.TryCosineSimilarity(left, right, out var similarity);

        Assert.True(success);
        Assert.Equal(1d, similarity, precision: 6);
    }

    [Fact]
    public void CosineSimilarity_TryCompute_OppositeVectors_ReturnsNegativeOne()
    {
        var left = new[] { 1f, -2f, 3f };
        var right = new[] { -1f, 2f, -3f };

        var success = VectorMath.TryCosineSimilarity(left, right, out var similarity);

        Assert.True(success);
        Assert.Equal(-1d, similarity, precision: 6);
    }

    [Fact]
    public void CosineSimilarity_ComputeOrZero_InvalidInput_ReturnsZero()
    {
        var similarity = VectorMath.CosineSimilarity(
            left: [1f, 2f],
            right: [1f]);

        Assert.Equal(0d, similarity);
    }
    
    [Fact]
    public void Math_Sigmoid_Zero_ReturnsHalf()
    {
        var value = Math.Sigmoid(0d);
        Assert.Equal(0.5d, value, precision: 6);
    }
    
    [Fact]
    public void Math_Sigmoid_LargeMagnitudeInput_IsStabilizedByClamping()
    {
        var positive = Math.Sigmoid(500d);
        var negative = Math.Sigmoid(-500d);

        Assert.Equal(Math.Sigmoid(20d), positive, precision: 12);
        Assert.Equal(Math.Sigmoid(-20d), negative, precision: 12);
    }
    
    [Fact]
    public void Math_Sigmoid_InvalidClampMagnitude_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Math.Sigmoid(0d, clampMagnitude: 0d));
        Assert.Throws<ArgumentOutOfRangeException>(() => Math.Sigmoid(0d, clampMagnitude: double.PositiveInfinity));
    }
}
