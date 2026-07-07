using System.Text.Json;
using Cohesive.Adapters.Cosmos;

namespace Cohesive.Tests.Model;

public sealed class CosmosSqlAggregationCompilerTests
{
    [Fact]
    public void Compile_GlobalAggregation_ProducesSingletonQuery()
    {
        var plan = new AggregationPlan(Roots:
        [
            new(Name: "summary",
                Root: new GlobalAggregationPlan("summary"),
                Statistics:
                [
                    new CountAggregationStatistic(),
                    new SumAggregationStatistic("totalAmount", FieldPath.FromField("Amount"))
                ])
        ]);

        var compiled = new CosmosSqlAggregationCompiler().Compile(plan);

        var root = Assert.Single(compiled.Roots);
        Assert.Equal("summary", root.RootName);
        Assert.Equal(
            "SELECT \"summary\" AS key, COUNT(1) AS __docCount, COUNT(1) AS count, SUM(c[\"Amount\"]) AS totalAmount FROM c",
            root.Query.Text
            );
        Assert.Empty(root.Query.Parameters);
    }

    [Fact]
    public void Compile_WithValueRootAndBaseFilter_TargetsNestedObservationDocuments()
    {
        var plan = new AggregationPlan(
            Roots:
            [
                new(Name: "summary",
                    Root: new GlobalAggregationPlan("summary"),
                    Statistics:
                    [
                        new CountAggregationStatistic(),
                        new SumAggregationStatistic("totalAmount", FieldPath.FromField("Amount"))
                    ])
            ],
            Predicate: new(
                new FieldPredicate(
                    FieldPath.FromField("Status"),
                    new ExactValuePredicate("complete"))));

        var compiled = new CosmosSqlAggregationCompiler(new(
            RootAlias: "c",
            ValueRootExpression: "c[\"observation\"]",
            BaseWhereClauses:
            [
                "c[\"documentKind\"] = @entityDocumentKind",
                "c[\"observationType\"] = @observationType",
                "IS_DEFINED(c[\"observation\"])"
            ],
            Parameters: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["@entityDocumentKind"] = "entity",
                ["@observationType"] = "ProcessTask"
            })).Compile(plan);

        var root = Assert.Single(compiled.Roots);
        Assert.Equal(
            "SELECT \"summary\" AS key, COUNT(1) AS __docCount, COUNT(1) AS count, SUM(c[\"observation\"][\"Amount\"]) AS totalAmount " +
            "FROM c WHERE (c[\"documentKind\"] = @entityDocumentKind) AND (c[\"observationType\"] = @observationType) AND " +
            "(IS_DEFINED(c[\"observation\"])) AND (c[\"observation\"][\"Status\"] = @p0)",
            root.Query.Text
            );
        Assert.Equal("entity", root.Query.Parameters["@entityDocumentKind"]);
        Assert.Equal("ProcessTask", root.Query.Parameters["@observationType"]);
        Assert.Equal("complete", root.Query.Parameters["@p0"]);
    }

    [Fact]
    public void Compile_GroupedAggregation_UsesEntityPredicatesForPlanAndStatisticFilters()
    {
        var failed = new EntityPredicate(new FieldPredicate(FieldPath.FromField("Status"), new ExactValuePredicate("failed")));
        var startedAfter = new EntityPredicate(new FieldPredicate( FieldPath.FromField("StartedAt"), new DateRangeValuePredicate(new DateTimeOffset(2026, 05, 01, 0, 0, 0, TimeSpan.Zero), End: null)));
        var plan = new AggregationPlan(
            Roots:
            [
                new(Name: "byStatus",
                    Root: new TermsGroupAggregationPlan(
                        GroupByField: FieldPath.FromField("Status"),
                        Order: new(StatisticName: "count", Descending: true),
                        Take: 10
                        ),
                    Statistics:
                    [
                        new CountAggregationStatistic("count"),
                        new CountIfAggregationStatistic("failedCount", failed),
                        new SumIfAggregationStatistic("failedSeconds", FieldPath.FromField("DurationSeconds"), failed)
                    ])
            ],
            Predicate: startedAfter
            );

        var compiled = new CosmosSqlAggregationCompiler().Compile(plan);

        var root = Assert.Single(compiled.Roots);
        Assert.Equal(
            "SELECT c[\"Status\"] AS key, COUNT(1) AS __docCount, COUNT(1) AS count, " +
            "SUM(IIF(c[\"Status\"] = @p0, 1, 0)) AS failedCount, " +
            "SUM(IIF(c[\"Status\"] = @p1, c[\"DurationSeconds\"], 0)) AS failedSeconds " +
            "FROM c WHERE c[\"StartedAt\"] >= @p2 GROUP BY c[\"Status\"] ORDER BY count DESC OFFSET 0 LIMIT 10",
            root.Query.Text
            );
        Assert.Equal("failed", root.Query.Parameters["@p0"]);
        Assert.Equal("failed", root.Query.Parameters["@p1"]);
        Assert.Equal("2026-05-01T00:00:00.0000000+00:00", root.Query.Parameters["@p2"]);
    }

    [Fact]
    public void Read_BucketedRows_MapsCosmosRowsToAggregationResults()
    {
        var plan = new AggregationPlan(
        [
            new(Name: "byStatus",
                Root: new TermsGroupAggregationPlan(FieldPath.FromField("Status")),
                Statistics:
                [
                    new CountAggregationStatistic(),
                    new SumAggregationStatistic("totalAmount", FieldPath.FromField("Amount"))
                ])
        ]);
        var rowsByRoot = new Dictionary<string, IReadOnlyList<JsonElement>>(StringComparer.Ordinal)
        {
            ["byStatus"] =
            [
                JsonSerializer.SerializeToElement(new
                {
                    key = "complete",
                    __docCount = 3,
                    count = 3,
                    totalAmount = 42.5
                })
            ]
        };

        var results = CosmosSqlAggregationResultReader.Read(rowsByRoot, plan);

        var row = Assert.Single(results["byStatus"].Bucketed().Rows);
        Assert.Equal("complete", row.Key);
        Assert.Equal(3, row.DocCount);
        Assert.Equal(3, row.Statistics["count"]);
        Assert.Equal(42.5, row.Statistics["totalAmount"]);
    }
}
