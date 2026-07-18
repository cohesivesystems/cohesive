using System.Linq.Expressions;
using Cohesive.Relations.IR;

namespace Cohesive.Relations.Tests;

public sealed class RelationQueryExpressionTemporalAggregateAuthoringTests
{
    [Fact]
    public void Aggregate_RequiresExactSupportedValueAndResultContracts()
    {
        var author = RelationQuery.Expression();
        var rows = author.Source<AggregateRow>();

        var sumMismatch = Assert.Throws<ArgumentException>(() =>
            author.Aggregate<SourceQueryNode, LongSumResult>(
                rows.Node,
                aggregate => aggregate.Value(
                    result => result.Total,
                    AggregateOperator.Sum,
                    (AggregateRow row) => row.Units,
                    rows.Binding)));
        Assert.Contains("same supported exact numeric CLR type", sumMismatch.Message, StringComparison.Ordinal);

        var guidMinimum = Assert.Throws<ArgumentException>(() =>
            author.Aggregate<SourceQueryNode, GuidMinimumResult>(
                rows.Node,
                aggregate => aggregate.Value(
                    result => result.Minimum,
                    AggregateOperator.Min,
                    (AggregateRow row) => row.CorrelationId,
                    rows.Binding)));
        Assert.Contains("numeric or String CLR type", guidMinimum.Message, StringComparison.Ordinal);

        Assert.Throws<ArgumentException>(() =>
            author.Aggregate<SourceQueryNode, TimeMinimumResult>(
                rows.Node,
                aggregate => aggregate.Value(
                    result => result.Minimum,
                    AggregateOperator.Min,
                    (AggregateRow row) => row.Time,
                    rows.Binding)));

        var temporalMinimum = Assert.Throws<ArgumentException>(() =>
            author.Aggregate<SourceQueryNode, TemporalMinimumResult>(
                rows.Node,
                aggregate => aggregate.Value(
                    result => result.Earliest,
                    AggregateOperator.Min,
                    (AggregateRow row) => row.OccurredAt,
                    rows.Binding)));
        Assert.Contains("numeric or String CLR type", temporalMinimum.Message, StringComparison.Ordinal);

        var supported = author.Aggregate<SourceQueryNode, SupportedAggregateResult>(
            rows.Node,
            aggregate => aggregate.Value(
                result => result.Total,
                AggregateOperator.Sum,
                (AggregateRow row) => row.Amount,
                rows.Binding));

        var result = author.Aggregation(supported);
        var query = author.BuildQuery(
            new QueryId("supported-aggregate-contracts"),
            new QueryName("SupportedAggregateContracts"),
            result);
        Assert.True(query.Validation.IsValid);
        Assert.Contains(query.Definition.Body.Nodes, static node => node is AggregateQueryNode);
    }

    [Fact]
    public void Aggregate_AverageAcceptsExactNumericInputOnlyWithDecimalTarget()
    {
        var author = RelationQuery.Expression();
        var rows = author.Source<AggregateRow>();

        var invalidTarget = Assert.Throws<ArgumentException>(() =>
            author.Aggregate<SourceQueryNode, LongAverageResult>(
                rows.Node,
                aggregate => aggregate.Value(
                    result => result.Average,
                    AggregateOperator.Average,
                    (AggregateRow row) => row.Units,
                    rows.Binding)));
        Assert.Contains("requires a Decimal target", invalidTarget.Message, StringComparison.Ordinal);

        var average = author.Aggregate<SourceQueryNode, DecimalAverageResult>(
            rows.Node,
            aggregate => aggregate.Value(
                result => result.Average,
                AggregateOperator.Average,
                (AggregateRow row) => row.Units,
                rows.Binding));

        var query = author.BuildQuery(
            new QueryId("exact-decimal-average"),
            new QueryName("ExactDecimalAverage"),
            author.Aggregation(average));
        Assert.True(query.Validation.IsValid);
        var aggregateNode = Assert.Single(query.Definition.Body.Nodes.OfType<AggregateQueryNode>());
        Assert.Equal(AggregateOperator.Average, Assert.Single(aggregateNode.Aggregates).Operation);
    }

    [Fact]
    public void Aggregate_RejectsBindingsOutsideItsInputBranch()
    {
        var author = RelationQuery.Expression();
        var included = author.Source<AggregateRow>(sourceReference: "included");
        var unrelated = author.Source<AggregateRow>(sourceReference: "unrelated");

        var exception = Assert.Throws<ArgumentException>(() =>
            author.Aggregate<SourceQueryNode, SupportedAggregateResult>(
                included.Node,
                aggregate => aggregate.Value(
                    result => result.Total,
                    AggregateOperator.Sum,
                    (AggregateRow row) => row.Amount,
                    unrelated.Binding)));

        Assert.Contains("not visible in the aggregate input", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TemporalOrderDistinctAndGroupingKeys_RequireExplicitCanonicalNormalization()
    {
        var author = RelationQuery.Expression();
        var rows = author.Source<TemporalKeyRow>();

        AssertTemporalKeyFailure(
            () => author.Order(rows.Node, (TemporalKeyRow row) => row.CalendarDate, rows.Binding),
            "ordering");
        AssertTemporalKeyFailure(
            () => author.Order(rows.Node, (TemporalKeyRow row) => row.WallClock, rows.Binding),
            "ordering");
        AssertTemporalKeyFailure(
            () => author.Order(rows.Node, (TemporalKeyRow row) => row.Instant, rows.Binding),
            "ordering");
        AssertTemporalKeyFailure(
            () => author.Order(rows.Node, (TemporalKeyRow row) => (object)row.Instant, rows.Binding),
            "ordering");
        AssertTemporalKeyFailure(
            () => author.Order(
                rows.Node,
                author.Ordering((TemporalKeyRow row) => row.Instant, rows.Binding)),
            "ordering");

        AssertTemporalKeyFailure(
            () => author.Distinct(rows.Node, (TemporalKeyRow row) => row.CalendarDate, rows.Binding),
            "distinct");
        AssertTemporalKeyFailure(
            () => author.Distinct(rows.Node, (TemporalKeyRow row) => row.WallClock, rows.Binding),
            "distinct");
        AssertTemporalKeyFailure(
            () => author.Distinct(rows.Node, (TemporalKeyRow row) => row.Instant, rows.Binding),
            "distinct");
        AssertTemporalKeyFailure(
            () => author.Distinct(rows.Node, (TemporalKeyRow row) => row.OptionalInstant, rows.Binding),
            "distinct");
        AssertTemporalKeyFailure(
            () => author.Distinct(
                rows.Node,
                (TemporalKeyRow row) => new TemporalCompositeKey(row.Instant),
                rows.Binding),
            "distinct");
        Expression<Func<TemporalKeyRow, IComparable>> conditional =
            row => row.Flag ? (IComparable)row.CalendarDate : (IComparable)row.Instant;
        AssertTemporalKeyFailure(
            () => author.Distinct(rows.Node, [conditional], [rows.Binding]),
            "distinct");

        AssertTemporalKeyFailure(
            () => author.Aggregate<SourceQueryNode, TemporalKeyResult>(
                rows.Node,
                aggregate => aggregate
                    .Group(result => result.CalendarDate, row => row.CalendarDate, rows.Binding)
                    .Count(result => result.Count)),
            "grouping");
        AssertTemporalKeyFailure(
            () => author.Aggregate<SourceQueryNode, CompositeKeyResult>(
                rows.Node,
                aggregate => aggregate
                    .Group(
                        result => result.Key,
                        row => new TemporalCompositeKey(row.Instant),
                        rows.Binding)
                    .Count(result => result.Count)),
            "grouping");

        AssertTemporalKeyFailure(
            () => author.BuildRelation(
                new RelationId("temporal-scalar-key"),
                new RelationName("TemporalScalarKey"),
                rows.Binding,
                rows.Node,
                rows.Binding,
                (TemporalKeyRow row) => row.Instant),
            "relation output");
        AssertTemporalKeyFailure(
            () => author.BuildRelation(
                new RelationId("temporal-composite-key"),
                new RelationName("TemporalCompositeKey"),
                rows.Binding,
                rows.Node,
                rows.Binding,
                (TemporalKeyRow row) => new TemporalCompositeKey(row.Instant)),
            "relation output");
        AssertTemporalKeyFailure(
            () => author.Aggregate<SourceQueryNode, TemporalKeyResult>(
                rows.Node,
                aggregate => aggregate
                    .Group(result => result.WallClock, row => row.WallClock, rows.Binding)
                    .Count(result => result.Count)),
            "grouping");
        AssertTemporalKeyFailure(
            () => author.Aggregate<SourceQueryNode, TemporalKeyResult>(
                rows.Node,
                aggregate => aggregate
                    .Group(result => result.Instant, row => row.Instant, rows.Binding)
                    .Count(result => result.Count)),
            "grouping");

        var ordered = author.Order(
            rows.Node,
            (TemporalKeyRow row) => row.NormalizedInstant,
            rows.Binding);
        var distinct = author.Distinct(
            ordered,
            (TemporalKeyRow row) => row.NormalizedInstant,
            rows.Binding);
        var aggregate = author.Aggregate<DistinctQueryNode, NormalizedKeyResult>(
            distinct,
            builder => builder
                .Group(
                    result => result.NormalizedInstant,
                    row => row.NormalizedInstant,
                    rows.Binding)
                .Count(result => result.Count));
        var query = author.BuildQuery(
            new QueryId("normalized-temporal-keys"),
            new QueryName("NormalizedTemporalKeys"),
            author.Aggregation(aggregate));

        Assert.True(query.Validation.IsValid);
    }

    [Fact]
    public void DynamicAndWholeRowKeys_FailBeforeCanonicalNodeCommit()
    {
        var author = RelationQuery.Expression();
        var rows = author.Source<TemporalKeyRow>();

        AssertTemporalKeyFailure(
            () => author.Distinct(
                rows.Node,
                (TemporalKeyRow row) => row.Dynamic,
                rows.Binding,
                sourceReference: "distinct/retry"),
            "distinct");
        var retried = author.Distinct(
            rows.Node,
            (TemporalKeyRow row) => row.NormalizedInstant,
            rows.Binding,
            sourceReference: "distinct/retry");

        var wholeRow = Assert.Throws<RelationQueryExpressionAuthoringException>(() =>
            author.Distinct(retried, sourceReference: "distinct/whole-row"));
        Assert.Equal(
            RelationQueryExpressionDiagnosticCodes.KeyDomainUnsupported,
            Assert.Single(wholeRow.Diagnostics).Code);
    }

    [Fact]
    public void TemporalJoin_RejectsRawMatchesAndForeignIntervalComponents()
    {
        var author = RelationQuery.Expression();
        var left = author.Source<TemporalLeft>(sourceReference: "left");
        var right = author.Source<TemporalRight>(sourceReference: "right");

        var rawMatch = new TemporalPointInIntervalMatch(
            Expr.Const(DateTimeOffset.UnixEpoch),
            new TemporalInterval(
                new UnboundedTemporalIntervalBound(),
                new UnboundedTemporalIntervalBound()));
        var matchException = Assert.Throws<ArgumentException>(() => author.TemporalJoin(
            left.Node,
            right.Node,
            JoinKind.Inner,
            (TemporalLeft l, TemporalRight r) => l.Id == r.Id,
            left.Binding,
            right.Binding,
            _ => rawMatch));
        Assert.Contains("must return a match created", matchException.Message, StringComparison.Ordinal);

        var intervalException = Assert.Throws<ArgumentException>(() => author.TemporalJoin(
            left.Node,
            right.Node,
            JoinKind.Inner,
            (TemporalLeft l, TemporalRight r) => l.Id == r.Id,
            left.Binding,
            right.Binding,
            match => match.PointInInterval(
                (TemporalLeft l) => l.OccurredAt,
                left.Binding,
                match.Interval(
                    new UnboundedTemporalIntervalBound(),
                    match.Unbounded()))));
        Assert.Contains("same active temporal-match builder", intervalException.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TemporalJoin_EnforcesOperandSidesAndExactTemporalDomains()
    {
        var author = RelationQuery.Expression();
        var left = author.Source<TemporalLeft>(sourceReference: "left");
        var right = author.Source<TemporalRight>(sourceReference: "right");

        var wrongPointSide = Assert.Throws<ArgumentException>(() => author.TemporalJoin(
            left.Node,
            right.Node,
            JoinKind.Inner,
            (TemporalLeft l, TemporalRight r) => l.Id == r.Id,
            left.Binding,
            right.Binding,
            match => match.PointInInterval(
                (TemporalRight r) => r.ValidFrom,
                right.Binding,
                RightInterval(match, right.Binding))));
        Assert.Contains("left-side point", wrongPointSide.Message, StringComparison.Ordinal);

        var wrongIntervalSide = Assert.Throws<ArgumentException>(() => author.TemporalJoin(
            left.Node,
            right.Node,
            JoinKind.Inner,
            (TemporalLeft l, TemporalRight r) => l.Id == r.Id,
            left.Binding,
            right.Binding,
            match => match.PointInInterval(
                (TemporalLeft l) => l.OccurredAt,
                left.Binding,
                match.Interval(
                    match.Bound(
                        (TemporalLeft l) => l.OccurredAt,
                        left.Binding,
                        TemporalBoundaryInclusion.Inclusive),
                    match.Unbounded()))));
        Assert.Contains("right-side interval", wrongIntervalSide.Message, StringComparison.Ordinal);

        var domainMismatch = Assert.Throws<ArgumentException>(() => author.TemporalJoin(
            left.Node,
            right.Node,
            JoinKind.Inner,
            (TemporalLeft l, TemporalRight r) => l.Id == r.Id,
            left.Binding,
            right.Binding,
            match => match.PointInInterval(
                (TemporalLeft l) => l.OccurredAt,
                left.Binding,
                match.Interval(
                    match.Bound(
                        (TemporalRight r) => r.ValidDate,
                        right.Binding,
                        TemporalBoundaryInclusion.Inclusive),
                    match.Unbounded()))));
        Assert.Contains("exact canonical domain", domainMismatch.Message, StringComparison.Ordinal);

        var unsupportedType = Assert.Throws<ArgumentException>(() => author.TemporalJoin(
            left.Node,
            right.Node,
            JoinKind.Inner,
            (TemporalLeft l, TemporalRight r) => l.Id == r.Id,
            left.Binding,
            right.Binding,
            match => match.PointInInterval(
                (TemporalLeft l) => l.Time,
                left.Binding,
                RightInterval(match, right.Binding))));
        Assert.Contains("must return DateOnly, DateTime, or DateTimeOffset", unsupportedType.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TemporalMatchBuilder_ExpiresWhenItsCallbackCompletes()
    {
        var author = RelationQuery.Expression();
        var left = author.Source<TemporalLeft>(sourceReference: "left");
        var right = author.Source<TemporalRight>(sourceReference: "right");
        RelationQueryExpressionTemporalMatchBuilder? captured = null;

        _ = author.TemporalJoin(
            left.Node,
            right.Node,
            JoinKind.Inner,
            (TemporalLeft l, TemporalRight r) => l.Id == r.Id,
            left.Binding,
            right.Binding,
            match =>
            {
                captured = match;
                return match.PointInInterval(
                    (TemporalLeft l) => l.OccurredAt,
                    left.Binding,
                    RightInterval(match, right.Binding));
            });

        Assert.NotNull(captured);
        Assert.Throws<InvalidOperationException>(() => captured.Unbounded());
    }

    static TemporalInterval RightInterval(
        RelationQueryExpressionTemporalMatchBuilder match,
        RelationQueryExpressionValueBinding<TemporalRight> right) =>
        match.Interval(
            match.Bound(
                (TemporalRight value) => value.ValidFrom,
                right,
                TemporalBoundaryInclusion.Inclusive),
            match.Bound(
                (TemporalRight value) => value.ValidTo,
                right,
                TemporalBoundaryInclusion.Exclusive,
                TemporalNullBoundBehavior.Unbounded));

    static void AssertTemporalKeyFailure(Action action, string keyRole)
    {
        var exception = Assert.Throws<RelationQueryExpressionAuthoringException>(action);
        var diagnostic = Assert.Single(exception.Diagnostics);
        Assert.Equal(RelationQueryExpressionDiagnosticCodes.KeyDomainUnsupported, diagnostic.Code);
        Assert.Contains(keyRole, diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("normalized canonical String or Int64", diagnostic.Suggestion, StringComparison.Ordinal);
    }

    sealed class AggregateRow
    {
        public int Units { get; init; }

        public decimal Amount { get; init; }

        public Guid CorrelationId { get; init; }

        public TimeOnly Time { get; init; }

        public DateTimeOffset OccurredAt { get; init; }
    }

    sealed class LongSumResult
    {
        public long Total { get; init; }
    }

    sealed class LongAverageResult
    {
        public long Average { get; init; }
    }

    sealed class DecimalAverageResult
    {
        public decimal Average { get; init; }
    }

    sealed class GuidMinimumResult
    {
        public Guid Minimum { get; init; }
    }

    sealed class TimeMinimumResult
    {
        public TimeOnly Minimum { get; init; }
    }

    sealed class SupportedAggregateResult
    {
        public decimal Total { get; init; }
    }

    sealed class TemporalMinimumResult
    {
        public DateTimeOffset Earliest { get; init; }
    }

    sealed class TemporalKeyRow
    {
        public DateOnly CalendarDate { get; init; }

        public DateTime WallClock { get; init; }

        public DateTimeOffset Instant { get; init; }

        public DateTimeOffset? OptionalInstant { get; init; }

        public long NormalizedInstant { get; init; }

        public bool Flag { get; init; }

        public ObservationValue Dynamic { get; init; } = ObservationValue.Null;
    }

    sealed class TemporalKeyResult
    {
        public DateOnly CalendarDate { get; init; }

        public DateTime WallClock { get; init; }

        public DateTimeOffset Instant { get; init; }

        public long Count { get; init; }
    }

    sealed class NormalizedKeyResult
    {
        public long NormalizedInstant { get; init; }

        public long Count { get; init; }
    }

    sealed record TemporalCompositeKey(DateTimeOffset Instant);

    sealed class CompositeKeyResult
    {
        public TemporalCompositeKey Key { get; init; } = new(default);

        public long Count { get; init; }
    }

    sealed class TemporalLeft
    {
        public string Id { get; init; } = string.Empty;

        public DateTimeOffset OccurredAt { get; init; }

        public TimeOnly Time { get; init; }
    }

    sealed class TemporalRight
    {
        public string Id { get; init; } = string.Empty;

        public DateTimeOffset ValidFrom { get; init; }

        public DateTimeOffset? ValidTo { get; init; }

        public DateOnly ValidDate { get; init; }
    }
}
