using Cohesive.Relations.Compilation;
using Cohesive.Relations.IR;

namespace Cohesive.Relations.Tests;

public sealed class RelationQueryExpressionInvocationOwnershipTests
{
    [Fact]
    public void TypedParameterOperations_RejectForeignCollidingHandleBeforeMutation()
    {
        var local = CreateFixture("local-parameter-query");
        var foreign = CreateFixture("foreign-parameter-query");
        Func<RelationQueryInvocationBuilder, RelationQueryInvocationBuilder>[] foreignOperations =
        [
            builder => builder.Set(foreign.Parameter, "foreign"),
            builder => builder.SetNull(foreign.Parameter),
            builder => builder.SetMissing(foreign.Parameter),
            builder => builder.Omit(foreign.Parameter)
        ];

        foreach (var foreignOperation in foreignOperations)
        {
            var builder = local.Query.Definition.Invoke(new("ownership/parameter"));

            var exception = Assert.Throws<ArgumentException>(() => foreignOperation(builder));
            Assert.Contains("exact canonical query definition", exception.Message, StringComparison.Ordinal);

            var invocation = builder.Set(local.Parameter, "local").Build();
            Assert.Equal(ObservationValue.FromString("local"), Assert.Single(invocation.Parameters).Value);
        }
    }

    [Fact]
    public void TypedResultOperations_RejectForeignCollidingHandleBeforeMutation()
    {
        var local = CreateFixture("local-result-query");
        var foreign = CreateFixture("foreign-result-query");
        Action<RelationQueryInvocationBuilder>[] foreignOperations =
        [
            builder => builder.Select(foreign.Rows),
            builder => builder.Select(foreign.Rows, row => row.Name),
            builder => builder.Select(foreign.Aggregation),
            builder => builder.Select(foreign.Aggregation, row => row.Count)
        ];

        foreach (var foreignOperation in foreignOperations)
        {
            var builder = local.Query.Definition.Invoke(new("ownership/result"));

            var exception = Assert.Throws<ArgumentException>(() => foreignOperation(builder));
            Assert.Contains("exact canonical query definition", exception.Message, StringComparison.Ordinal);

            var invocation = builder.Select(local.Rows).Build();
            Assert.Equal(local.Rows.Id, Assert.Single(invocation.Demand.QueryResults).Result);
        }
    }

    static Fixture CreateFixture(string queryId)
    {
        var author = RelationQuery.Expression();
        var source = author.Source<Row>();
        var parameter = author.Parameter<string>("value");
        var filtered = author.Filter(
            source.Node,
            (Row row) => row.Name == parameter.Value,
            source.Binding);
        var rows = author.Rows(filtered, source.Binding, id: "rows");
        var aggregate = author.Aggregate<FilterQueryNode, CountRow>(
            filtered,
            builder => builder.Count(result => result.Count));
        var aggregation = author.Aggregation(aggregate, id: "aggregation");
        var query = author.BuildQuery(
            new(queryId),
            new(queryId),
            rows,
            aggregation);
        Assert.True(query.Validation.IsValid);
        return new(query, parameter, rows, aggregation);
    }

    sealed record Row(string Name);

    sealed record CountRow(long Count);

    sealed record Fixture(
        RelationQueryAuthoringResult<QueryDefinition> Query,
        RelationQueryExpressionParameter<string> Parameter,
        RelationQueryExpressionRowsResult<Row> Rows,
        RelationQueryExpressionAggregationResult<CountRow> Aggregation);
}
