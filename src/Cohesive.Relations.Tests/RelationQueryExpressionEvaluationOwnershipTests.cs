using Cohesive.Model.Serialization;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.IR;
using Cohesive.Relations.Serialization;

namespace Cohesive.Relations.Tests;

public sealed class RelationQueryExpressionEvaluationOwnershipTests
{
    [Fact]
    public void Evaluate_RejectsSemanticallyIdenticalResultFromForeignSession()
    {
        var local = CreateFixture("shared-query");
        var foreign = CreateFixture("shared-query");

        Assert.Equal(
            RelationQueryDefinitionFingerprinter.Compute(local.Query.Definition),
            RelationQueryDefinitionFingerprinter.Compute(foreign.Query.Definition));

        var exception = Assert.Throws<ArgumentException>(() =>
            local.Author.Evaluate(foreign.Query, new("ownership/foreign-query")));

        Assert.Contains("this expression-authoring session", exception.Message, StringComparison.Ordinal);
        Assert.NotNull(local.Author.Evaluate(local.Query, new("ownership/local-query")));
    }

    [Fact]
    public void Evaluate_UsesSemanticContextCapturedWhenTheTerminalWasBuilt()
    {
        var fixture = CreateFixture("snapshot-query");
        var expectedShapeIds = GetShapeIds(fixture.Author.ShapeDocuments);
        var expectedRelationshipCount = fixture.Author.RelationshipCatalog.Count;

        fixture.Author.Relationship<UnrelatedSource, UnrelatedTarget>(source => source.TargetId);

        Assert.True(GetShapeIds(fixture.Author.ShapeDocuments).Length > expectedShapeIds.Length);
        Assert.True(fixture.Author.RelationshipCatalog.Count > expectedRelationshipCount);

        var evaluation = fixture.Author
            .Evaluate(fixture.Query, new("ownership/snapshot"))
            .Set(fixture.Parameter, "local")
            .Select(fixture.Rows)
            .Build();

        Assert.Equal(expectedShapeIds, GetShapeIds(evaluation.Compilation.ShapeDocuments));
        Assert.Equal(
            expectedRelationshipCount,
            Assert.IsType<RelationshipCatalogDocument>(evaluation.Compilation.RelationshipCatalogDocument)
                .Catalog.Count);
    }

    [Fact]
    public void Evaluate_AcceptsExactKeyedRelationAndRejectsForeignEquivalent()
    {
        var local = CreateRelationFixture();
        var foreign = CreateRelationFixture();

        Assert.Equal(
            RelationQueryDefinitionFingerprinter.Compute(local.Relation.Definition),
            RelationQueryDefinitionFingerprinter.Compute(foreign.Relation.Definition));

        Assert.NotNull(local.Author.Evaluate(local.Relation, new("ownership/local-relation")));
        Assert.Throws<ArgumentException>(() =>
            local.Author.Evaluate(foreign.Relation, new("ownership/foreign-relation")));
    }

    [Fact]
    public void TypedParameterOperations_RejectForeignCollidingHandleBeforeMutation()
    {
        var local = CreateFixture("local-parameter-query");
        var foreign = CreateFixture("foreign-parameter-query");
        Func<RelationQueryEvaluationBuilder, RelationQueryEvaluationBuilder>[] foreignOperations =
        [
            builder => builder.Set(foreign.Parameter, "foreign"),
            builder => builder.SetNull(foreign.Parameter),
            builder => builder.SetMissing(foreign.Parameter),
            builder => builder.SetFailed(foreign.Parameter, "tests/foreign-failure"),
            builder => builder.Omit(foreign.Parameter)
        ];

        foreach (var foreignOperation in foreignOperations)
        {
            var builder = local.Query.Definition.Evaluate(new("ownership/parameter"));

            var exception = Assert.Throws<ArgumentException>(() => foreignOperation(builder));
            Assert.Contains("exact canonical query definition", exception.Message, StringComparison.Ordinal);

            var evaluation = builder.Set(local.Parameter, "local").Build();
            Assert.Equal(ObservationValue.FromString("local"), Assert.Single(evaluation.Parameters).Value);
        }
    }

    [Fact]
    public void TypedResultOperations_RejectForeignCollidingHandleBeforeMutation()
    {
        var local = CreateFixture("local-result-query");
        var foreign = CreateFixture("foreign-result-query");
        Action<RelationQueryEvaluationBuilder>[] foreignOperations =
        [
            builder => builder.Select(foreign.Rows),
            builder => builder.Select(foreign.Rows, row => row.Name),
            builder => builder.Select(foreign.Aggregation),
            builder => builder.Select(foreign.Aggregation, row => row.Count)
        ];

        foreach (var foreignOperation in foreignOperations)
        {
            var builder = local.Query.Definition.Evaluate(new("ownership/result"));

            var exception = Assert.Throws<ArgumentException>(() => foreignOperation(builder));
            Assert.Contains("exact canonical query definition", exception.Message, StringComparison.Ordinal);

            var evaluation = builder.Select(local.Rows).Build();
            Assert.Equal(local.Rows.Id, Assert.Single(evaluation.Demand.QueryResults).Result);
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
        return new(author, query, parameter, rows, aggregation);
    }

    static RelationFixture CreateRelationFixture()
    {
        var author = RelationQuery.Expression();
        var source = author.Source<Row>();
        var projected = author.Project(
            source,
            (Row row) => new RelationRow { Name = row.Name });
        var relation = projected.BuildRelation(
            (RelationRow row) => row.Name,
            id: new("ownership-relation"),
            name: new("OwnershipRelation"));
        Assert.True(relation.Validation.IsValid);
        return new(author, relation);
    }

    static string[] GetShapeIds(IEnumerable<ShapeGraphDocument> documents) =>
    [
        .. documents
            .SelectMany(static document => document.Graph.Shapes.Select(
                shape => $"{document.Graph.Id.Value}/{shape.Id.Value}"))
            .Order(StringComparer.Ordinal)
    ];

    sealed record Row(string Name);

    sealed record CountRow(long Count);

    sealed class RelationRow
    {
        public required string Name { get; init; }
    }

    sealed record UnrelatedSource(string Id, string TargetId);

    sealed record UnrelatedTarget(string Id);

    sealed record Fixture(
        RelationQueryExpressionAuthoring Author,
        RelationQueryAuthoringResult<QueryDefinition> Query,
        RelationQueryExpressionParameter<string> Parameter,
        RelationQueryExpressionRowsResult<Row> Rows,
        RelationQueryExpressionAggregationResult<CountRow> Aggregation);

    sealed record RelationFixture(
        RelationQueryExpressionAuthoring Author,
        RelationQueryAuthoringResult<RelationDefinition> Relation);
}
