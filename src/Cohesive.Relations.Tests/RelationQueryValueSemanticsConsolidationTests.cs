using Cohesive.Model.Expressions;

namespace Cohesive.Relations.Tests;

public sealed class RelationQueryValueSemanticsConsolidationTests
{
    [Fact]
    public void RequireDecimal_TranslatesFiniteNonRepresentableValueToRelationNumericFailure()
    {
        var value = ObservationValue.FromDouble(Math.BitIncrement(1e-29));
        Assert.False(value.TryGetCanonicalNumericDecimal(out _));

        _ = Assert.Throws<OverflowException>(() =>
            ObservationValueSemantics.RequireDecimal(value, "test"));
        var exception = Assert.Throws<RelationQueryExpressionEvaluationException>(() =>
            RelationQueryValueSemantics.RequireDecimal(value, "test"));

        Assert.Equal(RelationQueryExpressionEvaluationError.NumericFailure, exception.Error);
        Assert.IsType<OverflowException>(exception.InnerException);
    }
}
