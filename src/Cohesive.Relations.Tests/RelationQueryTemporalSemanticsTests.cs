using System.Globalization;
using Cohesive.Relations.IR;

namespace Cohesive.Relations.Tests;

public sealed class RelationQueryTemporalSemanticsTests
{
    [Fact]
    public void TryCompare_UsesExactDateCivilDateTimeAndAbsoluteInstantDomains()
    {
        AssertComparison(
            ScalarTypeKind.Date,
            ObservationValue.FromDateOnly(new(2026, 7, 14)),
            ObservationValue.FromDateOnly(new(2026, 7, 15)),
            expectedSign: -1);

        var civilLeft = ObservationValue.FromDateTimeOffset(
            new(2026, 7, 14, 9, 30, 0, TimeSpan.FromHours(-7)));
        var civilRight = ObservationValue.FromDateTimeOffset(
            new(2026, 7, 14, 9, 30, 0, TimeSpan.FromHours(2)));
        AssertComparison(ScalarTypeKind.DateTime, civilLeft, civilRight, expectedSign: 0);
        AssertComparison(ScalarTypeKind.Instant, civilLeft, civilRight, expectedSign: 1);

        var instantLeft = ObservationValue.FromDateTimeOffset(
            new(2026, 7, 14, 16, 30, 0, TimeSpan.Zero));
        AssertComparison(ScalarTypeKind.Instant, instantLeft, civilLeft, expectedSign: 0);
        AssertComparison(ScalarTypeKind.DateTime, instantLeft, civilLeft, expectedSign: 1);

        Assert.False(RelationQueryTemporalSemantics.TryCompare(
            ScalarTypeKind.Instant,
            ObservationValue.FromDateOnly(new(2026, 7, 14)),
            instantLeft,
            out _));
        Assert.False(RelationQueryTemporalSemantics.TryCompare(
            ScalarTypeKind.Instant,
            ObservationValue.FromString("2026-07-14T16:30:00"),
            instantLeft,
            out _));
        AssertComparison(
            ScalarTypeKind.Instant,
            ObservationValue.FromString("2026-07-14T09:30:00-07:00"),
            instantLeft,
            expectedSign: 0);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            RelationQueryTemporalSemantics.TryCompare(
                ScalarTypeKind.String,
                ObservationValue.FromString("a"),
                ObservationValue.FromString("b"),
                out _));
    }

    [Fact]
    public void TryCompare_IsCultureInvariant()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            var turkish = CultureInfo.GetCultureInfo("tr-TR");
            CultureInfo.CurrentCulture = turkish;
            CultureInfo.CurrentUICulture = turkish;

            AssertComparison(
                ScalarTypeKind.Instant,
                ObservationValue.FromString("2026-07-14T09:30:00-07:00"),
                ObservationValue.FromString("2026-07-14T16:30:00Z"),
                expectedSign: 0);
            AssertComparison(
                ScalarTypeKind.Date,
                ObservationValue.FromString("2026-07-14"),
                ObservationValue.FromString("2026-07-15"),
                expectedSign: -1);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    [Theory]
    [InlineData(TemporalBoundaryInclusion.Inclusive, TemporalBoundaryInclusion.Inclusive, true)]
    [InlineData(TemporalBoundaryInclusion.Inclusive, TemporalBoundaryInclusion.Exclusive, false)]
    [InlineData(TemporalBoundaryInclusion.Exclusive, TemporalBoundaryInclusion.Inclusive, false)]
    [InlineData(TemporalBoundaryInclusion.Exclusive, TemporalBoundaryInclusion.Exclusive, false)]
    public void PointInInterval_DistinguishesSingletonAndEmptyEqualBoundIntervals(
        TemporalBoundaryInclusion lowerInclusion,
        TemporalBoundaryInclusion upperInclusion,
        bool expectedSingleton)
    {
        var date = Date(14);
        var interval = Interval(date, lowerInclusion, date, upperInclusion);

        Assert.Equal(
            expectedSingleton
                ? RelationQueryTemporalIntervalKind.NonEmpty
                : RelationQueryTemporalIntervalKind.Empty,
            RelationQueryTemporalSemantics.ClassifyInterval(ScalarTypeKind.Date, interval));
        Assert.Equal(
            expectedSingleton
                ? RelationQueryTemporalEvaluationKind.Match
                : RelationQueryTemporalEvaluationKind.NoMatch,
            RelationQueryTemporalSemantics.PointInInterval(ScalarTypeKind.Date, date, interval));
    }

    [Fact]
    public void PointInInterval_RespectsEveryFiniteBoundaryCombination()
    {
        var lower = Date(10);
        var upper = Date(20);

        Assert.Equal(
            RelationQueryTemporalEvaluationKind.Match,
            RelationQueryTemporalSemantics.PointInInterval(
                ScalarTypeKind.Date,
                lower,
                Interval(
                    lower,
                    TemporalBoundaryInclusion.Inclusive,
                    upper,
                    TemporalBoundaryInclusion.Exclusive)));
        Assert.Equal(
            RelationQueryTemporalEvaluationKind.NoMatch,
            RelationQueryTemporalSemantics.PointInInterval(
                ScalarTypeKind.Date,
                upper,
                Interval(
                    lower,
                    TemporalBoundaryInclusion.Inclusive,
                    upper,
                    TemporalBoundaryInclusion.Exclusive)));
        Assert.Equal(
            RelationQueryTemporalEvaluationKind.NoMatch,
            RelationQueryTemporalSemantics.PointInInterval(
                ScalarTypeKind.Date,
                lower,
                Interval(
                    lower,
                    TemporalBoundaryInclusion.Exclusive,
                    upper,
                    TemporalBoundaryInclusion.Inclusive)));
        Assert.Equal(
            RelationQueryTemporalEvaluationKind.Match,
            RelationQueryTemporalSemantics.PointInInterval(
                ScalarTypeKind.Date,
                upper,
                Interval(
                    lower,
                    TemporalBoundaryInclusion.Exclusive,
                    upper,
                    TemporalBoundaryInclusion.Inclusive)));
    }

    [Fact]
    public void ClassifyInterval_RejectsReversedBoundsWithoutTreatingEmptyAsInvalid()
    {
        var reversed = Interval(
            Date(20),
            TemporalBoundaryInclusion.Inclusive,
            Date(10),
            TemporalBoundaryInclusion.Inclusive);

        Assert.Equal(
            RelationQueryTemporalIntervalKind.InvalidInterval,
            RelationQueryTemporalSemantics.ClassifyInterval(ScalarTypeKind.Date, reversed));
        Assert.Equal(
            RelationQueryTemporalEvaluationKind.InvalidInterval,
            RelationQueryTemporalSemantics.PointInInterval(
                ScalarTypeKind.Date,
                Date(15),
                reversed));
    }

    [Fact]
    public void ClassifyInterval_RecognizesAdjacentExclusiveEndpointsAsEmptyInEveryExactDomain()
    {
        var civil = new DateTimeOffset(2026, 7, 14, 9, 30, 0, TimeSpan.Zero);
        (ScalarTypeKind Domain, ObservationValue Lower, ObservationValue Upper)[] cases =
        [
            (
                ScalarTypeKind.Date,
                ObservationValue.FromDateOnly(new(2026, 7, 14)),
                ObservationValue.FromDateOnly(new(2026, 7, 15))),
            (
                ScalarTypeKind.DateTime,
                ObservationValue.FromDateTimeOffset(civil),
                ObservationValue.FromDateTimeOffset(civil.AddTicks(1))),
            (
                ScalarTypeKind.Instant,
                ObservationValue.FromDateTimeOffset(civil),
                ObservationValue.FromDateTimeOffset(civil.AddTicks(1)))
        ];

        foreach (var (domain, lower, upper) in cases)
        {
            Assert.Equal(
                RelationQueryTemporalIntervalKind.Empty,
                RelationQueryTemporalSemantics.ClassifyInterval(
                    domain,
                    Interval(
                        lower,
                        TemporalBoundaryInclusion.Exclusive,
                        upper,
                        TemporalBoundaryInclusion.Exclusive)));
        }
    }

    [Theory]
    [InlineData(TemporalBoundaryInclusion.Exclusive, TemporalBoundaryInclusion.Inclusive, false)]
    [InlineData(TemporalBoundaryInclusion.Inclusive, TemporalBoundaryInclusion.Exclusive, false)]
    [InlineData(TemporalBoundaryInclusion.Exclusive, TemporalBoundaryInclusion.Exclusive, false)]
    [InlineData(TemporalBoundaryInclusion.Inclusive, TemporalBoundaryInclusion.Inclusive, true)]
    public void IntervalsOverlap_TouchingIntervalsRequireBothBoundariesToIncludeThePoint(
        TemporalBoundaryInclusion leftUpper,
        TemporalBoundaryInclusion rightLower,
        bool expected)
    {
        var touching = Date(20);
        var left = Interval(
            Date(10),
            TemporalBoundaryInclusion.Inclusive,
            touching,
            leftUpper);
        var right = Interval(
            touching,
            rightLower,
            Date(30),
            TemporalBoundaryInclusion.Inclusive);

        Assert.Equal(
            expected
                ? RelationQueryTemporalEvaluationKind.Match
                : RelationQueryTemporalEvaluationKind.NoMatch,
            RelationQueryTemporalSemantics.IntervalsOverlap(ScalarTypeKind.Date, left, right));
    }

    [Fact]
    public void IntervalsOverlap_HandlesUnboundedSingletonEmptyAndDisjointIntervals()
    {
        var allTime = new RelationQueryTemporalIntervalValue(Unbounded(), Unbounded());
        var singleton = Interval(
            Date(20),
            TemporalBoundaryInclusion.Inclusive,
            Date(20),
            TemporalBoundaryInclusion.Inclusive);
        var empty = Interval(
            Date(20),
            TemporalBoundaryInclusion.Inclusive,
            Date(20),
            TemporalBoundaryInclusion.Exclusive);
        var disjoint = Interval(
            Date(21),
            TemporalBoundaryInclusion.Inclusive,
            Date(30),
            TemporalBoundaryInclusion.Inclusive);

        Assert.Equal(
            RelationQueryTemporalEvaluationKind.Match,
            RelationQueryTemporalSemantics.IntervalsOverlap(ScalarTypeKind.Date, allTime, singleton));
        Assert.Equal(
            RelationQueryTemporalEvaluationKind.NoMatch,
            RelationQueryTemporalSemantics.IntervalsOverlap(ScalarTypeKind.Date, allTime, empty));
        Assert.Equal(
            RelationQueryTemporalEvaluationKind.NoMatch,
            RelationQueryTemporalSemantics.IntervalsOverlap(ScalarTypeKind.Date, singleton, disjoint));
    }

    [Fact]
    public void IntervalsOverlap_UsesRepresentablePointsForAdjacentOpenDateIntervals()
    {
        var left = Interval(
            Date(10),
            TemporalBoundaryInclusion.Exclusive,
            Date(12),
            TemporalBoundaryInclusion.Exclusive);
        var right = Interval(
            Date(11),
            TemporalBoundaryInclusion.Exclusive,
            Date(13),
            TemporalBoundaryInclusion.Exclusive);

        Assert.Equal(
            RelationQueryTemporalEvaluationKind.NoMatch,
            RelationQueryTemporalSemantics.IntervalsOverlap(ScalarTypeKind.Date, left, right));
    }

    [Fact]
    public void ResolveBound_PreservesStructuralUnboundedAndRequiresExplicitNullPolicy()
    {
        var structural = new UnboundedTemporalIntervalBound();
        var nullInvalid = new ExpressionTemporalIntervalBound(
            Expr.Const("unused"),
            TemporalBoundaryInclusion.Inclusive,
            TemporalNullBoundBehavior.Invalid);
        var nullUnbounded = new ExpressionTemporalIntervalBound(
            Expr.Const("unused"),
            TemporalBoundaryInclusion.Exclusive,
            TemporalNullBoundBehavior.Unbounded);

        Assert.Equal(
            RelationQueryTemporalBoundValueKind.Unbounded,
            RelationQueryTemporalSemantics.ResolveBound(structural).Kind);
        Assert.Equal(
            RelationQueryTemporalBoundValueKind.Invalid,
            RelationQueryTemporalSemantics.ResolveBound(nullInvalid, ObservationValue.Null).Kind);
        Assert.Equal(
            RelationQueryTemporalBoundValueKind.Unbounded,
            RelationQueryTemporalSemantics.ResolveBound(nullUnbounded, ObservationValue.Null).Kind);
        Assert.Equal(
            RelationQueryTemporalBoundValueKind.Invalid,
            RelationQueryTemporalSemantics.ResolveBound(
                nullUnbounded,
                ObservationValue.Undefined).Kind);

        var finite = RelationQueryTemporalSemantics.ResolveBound(nullInvalid, Date(14));
        Assert.Equal(RelationQueryTemporalBoundValueKind.Finite, finite.Kind);
        Assert.Equal(TemporalBoundaryInclusion.Inclusive, finite.Inclusion);
        Assert.Equal(Date(14), finite.Value);
    }

    [Fact]
    public void NullAsUnbounded_AffectsMembershipOnlyWhenTheBoundOptsIn()
    {
        var lowerDefinition = new ExpressionTemporalIntervalBound(
            Expr.Const("unused"),
            TemporalBoundaryInclusion.Inclusive);
        var invalidUpperDefinition = new ExpressionTemporalIntervalBound(
            Expr.Const("unused"),
            TemporalBoundaryInclusion.Exclusive,
            TemporalNullBoundBehavior.Invalid);
        var unboundedUpperDefinition = new ExpressionTemporalIntervalBound(
            Expr.Const("unused"),
            TemporalBoundaryInclusion.Exclusive,
            TemporalNullBoundBehavior.Unbounded);
        var lower = RelationQueryTemporalSemantics.ResolveBound(lowerDefinition, Date(10));

        Assert.Equal(
            RelationQueryTemporalEvaluationKind.InvalidOperand,
            RelationQueryTemporalSemantics.PointInInterval(
                ScalarTypeKind.Date,
                Date(30),
                new(
                    lower,
                    RelationQueryTemporalSemantics.ResolveBound(
                        invalidUpperDefinition,
                        ObservationValue.Null))));
        Assert.Equal(
            RelationQueryTemporalEvaluationKind.Match,
            RelationQueryTemporalSemantics.PointInInterval(
                ScalarTypeKind.Date,
                Date(30),
                new(
                    lower,
                    RelationQueryTemporalSemantics.ResolveBound(
                        unboundedUpperDefinition,
                        ObservationValue.Null))));
    }

    [Fact]
    public void InvalidMaterializedOperandsRemainDistinctFromConclusiveNonMatches()
    {
        var invalid = new RelationQueryTemporalIntervalValue(
            RelationQueryTemporalBoundValue.Invalid(),
            Unbounded());

        Assert.Equal(
            RelationQueryTemporalIntervalKind.InvalidOperand,
            RelationQueryTemporalSemantics.ClassifyInterval(ScalarTypeKind.Date, invalid));
        Assert.Equal(
            RelationQueryTemporalEvaluationKind.InvalidOperand,
            RelationQueryTemporalSemantics.PointInInterval(
                ScalarTypeKind.Date,
                Date(14),
                invalid));
        Assert.Equal(
            RelationQueryTemporalEvaluationKind.InvalidOperand,
            RelationQueryTemporalSemantics.PointInInterval(
                ScalarTypeKind.Date,
                ObservationValue.Null,
                new(Unbounded(), Unbounded())));
    }

    static void AssertComparison(
        ScalarTypeKind domain,
        ObservationValue left,
        ObservationValue right,
        int expectedSign)
    {
        Assert.True(RelationQueryTemporalSemantics.TryCompare(domain, left, right, out var comparison));
        Assert.Equal(expectedSign, Math.Sign(comparison));
    }

    static RelationQueryTemporalIntervalValue Interval(
        ObservationValue lower,
        TemporalBoundaryInclusion lowerInclusion,
        ObservationValue upper,
        TemporalBoundaryInclusion upperInclusion) =>
        new(
            RelationQueryTemporalBoundValue.Finite(lower, lowerInclusion),
            RelationQueryTemporalBoundValue.Finite(upper, upperInclusion));

    static RelationQueryTemporalBoundValue Unbounded() =>
        RelationQueryTemporalBoundValue.Unbounded();

    static ObservationValue Date(int day) =>
        ObservationValue.FromDateOnly(new(2026, 7, day));
}
