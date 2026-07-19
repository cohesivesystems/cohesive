using System.Collections.Immutable;
using Cohesive.Adapters.Cosmos;

namespace Cohesive.Tests.Model;

public sealed class CosmosSqlConstructionTests
{
    [Fact]
    public void Build_HandAuthoredQuery_EmitsSafeDeterministicStatement()
    {
        var hostilePath = new FieldPath(
        [
            FieldPathSegment.ForField("customer\"data"),
            FieldPathSegment.ForField("name\\value")
        ]);
        var builder = new CosmosSqlBuilder("document")
            .Select(CosmosSqlExpression.Property("document", FieldPath.FromField("id")), "f0")
            .Select(CosmosSqlExpression.Property("document", hostilePath), "f1")
            .Where(CosmosSqlExpression.Binary(
                CosmosSqlBinaryOperator.Equal,
                CosmosSqlExpression.Property("document", FieldPath.FromField("status")),
                CosmosSqlExpression.Parameter("open\" OR true")))
            .OrderBy(
                CosmosSqlExpression.Property("document", FieldPath.FromField("id")),
                CosmosSqlSortDirection.Descending)
            .OffsetLimit(offset: 10, limit: 25);

        var first = builder.Build();
        var second = builder.Build();

        Assert.Equal(
            "SELECT document[\"id\"] AS f0, document[\"customer\\u0022data\"][\"name\\\\value\"] AS f1 "
            + "FROM document WHERE (document[\"status\"] = @p0) ORDER BY document[\"id\"] DESC OFFSET 10 LIMIT 25",
            first.Text);
        Assert.Equal(first.Text, second.Text);
        var parameter = Assert.Single(first.Parameters);
        Assert.Equal("@p0", parameter.Name);
        Assert.Equal("open\" OR true", parameter.Value);
        Assert.True(first.Parameters.SequenceEqual(second.Parameters));
        Assert.NotNull(first.ToQueryDefinition());
    }

    [Fact]
    public void Bind_RuntimeParameters_ReusesSlotsAndRejectsInexactBindings()
    {
        var tenant = CosmosSqlExpression.RuntimeParameter("tenant");
        var template = new CosmosSqlBuilder()
            .SelectValue(CosmosSqlExpression.Object(
                new("id", CosmosSqlExpression.Property("c", FieldPath.FromField("id"))),
                new("tenant", tenant)))
            .Where(CosmosSqlExpression.Binary(
                CosmosSqlBinaryOperator.And,
                CosmosSqlExpression.Binary(
                    CosmosSqlBinaryOperator.Equal,
                    CosmosSqlExpression.Property("c", FieldPath.FromField("tenantId")),
                    tenant),
                CosmosSqlExpression.Binary(
                    CosmosSqlBinaryOperator.Equal,
                    CosmosSqlExpression.Property("c", FieldPath.FromField("status")),
                    CosmosSqlExpression.Parameter("open"))))
            .BuildTemplate();

        Assert.Equal(
            "SELECT VALUE { \"id\": c[\"id\"], \"tenant\": @p0 } FROM c "
            + "WHERE ((c[\"tenantId\"] = @p0) AND (c[\"status\"] = @p1))",
            template.Text);
        Assert.Equal(2, template.Parameters.Length);
        Assert.Equal(CosmosSqlParameterBindingKind.Runtime, template.Parameters[0].Kind);
        Assert.Equal("tenant", template.Parameters[0].Binding);
        Assert.Equal(CosmosSqlParameterBindingKind.Constant, template.Parameters[1].Kind);

        var statement = template.Bind(new Dictionary<string, object?>
        {
            ["tenant"] = "tenant-42"
        });

        Assert.Equal("tenant-42", statement.Parameters[0].Value);
        Assert.Equal("open", statement.Parameters[1].Value);
        Assert.Throws<ArgumentException>(() => template.Bind());
        Assert.Throws<ArgumentException>(() => template.Bind(new Dictionary<string, object?>
        {
            ["tenant"] = "tenant-42",
            ["unknown"] = true
        }));
    }

    [Fact]
    public void BuildTemplate_CorrelatedCollectionExists_BindsScopedItemAndParametersDeterministically()
    {
        var predicateFactoryCalls = 0;
        var exists = CosmosSqlExpression.CollectionExists(
            CosmosSqlExpression.Property("c", FieldPath.FromField("Stops")),
            item =>
            {
                predicateFactoryCalls++;
                return CosmosSqlExpression.Binary(
                    CosmosSqlBinaryOperator.And,
                    CosmosSqlExpression.Binary(
                        CosmosSqlBinaryOperator.Equal,
                        CosmosSqlExpression.Property(item, FieldPath.FromField("Location")),
                        CosmosSqlExpression.RuntimeParameter("location")),
                    CosmosSqlExpression.Binary(
                        CosmosSqlBinaryOperator.Equal,
                        CosmosSqlExpression.Property(item, FieldPath.FromField("Type")),
                        CosmosSqlExpression.Parameter("Pickup")));
            });
        var builder = new CosmosSqlBuilder()
            .SelectValue(CosmosSqlExpression.Property("c", FieldPath.FromField("Id")))
            .Where(exists);

        var first = builder.BuildTemplate();
        var second = builder.BuildTemplate();

        Assert.Equal(1, predicateFactoryCalls);
        Assert.Equal(
            "SELECT VALUE c[\"Id\"] FROM c WHERE EXISTS (SELECT VALUE e0 FROM e0 IN c[\"Stops\"] "
            + "WHERE ((e0[\"Location\"] = @p0) AND (e0[\"Type\"] = @p1)))",
            first.Text);
        Assert.Equal(first.Text, second.Text);
        Assert.True(first.Parameters.SequenceEqual(second.Parameters));
        Assert.Equal(
            [CosmosSqlParameterBindingKind.Runtime, CosmosSqlParameterBindingKind.Constant],
            first.Parameters.Select(static parameter => parameter.Kind));

        var statement = first.Bind(new Dictionary<string, object?>
        {
            ["location"] = "Seattle"
        });
        Assert.Equal(["Seattle", "Pickup"], statement.Parameters.Select(static parameter => parameter.Value));
        Assert.NotNull(statement.ToQueryDefinition());
    }

    [Fact]
    public void Build_ArrayExpansionGroupingAndAggregate_UsesOneSafeEmitter()
    {
        var statement = new CosmosSqlBuilder()
            .JoinCollection("item", CosmosSqlExpression.Property("c", FieldPath.FromField("items")))
            .Select(CosmosSqlExpression.Property("item", FieldPath.FromField("category")), "f0")
            .Select(
                CosmosSqlExpression.Aggregate(
                    CosmosSqlAggregateFunction.Sum,
                    CosmosSqlExpression.Property("item", FieldPath.FromField("amount"))),
                "f1")
            .Where(CosmosSqlExpression.Function(
                CosmosSqlFunction.IsDefined,
                CosmosSqlExpression.Property("item", FieldPath.FromField("amount"))))
            .GroupBy(CosmosSqlExpression.Property("item", FieldPath.FromField("category")))
            .Build();

        Assert.Equal(
            "SELECT item[\"category\"] AS f0, SUM(item[\"amount\"]) AS f1 FROM c "
            + "JOIN item IN c[\"items\"] WHERE IS_DEFINED(item[\"amount\"]) GROUP BY item[\"category\"]",
            statement.Text);
        Assert.Empty(statement.Parameters);
    }

    [Fact]
    public void Builder_RejectsUnsafeIdentifiersPathsAndInvalidShapes()
    {
        Assert.Throws<ArgumentException>(() => new CosmosSqlBuilder("c; DELETE"));
        Assert.Throws<ArgumentException>(() => new CosmosSqlBuilder().Select(
            CosmosSqlExpression.Alias("c"),
            "bad alias"));
        Assert.Throws<ArgumentException>(() => CosmosSqlExpression.Property(
            "c",
            new FieldPath(ImmutableArray.Create(FieldPathSegment.Element()))));
        Assert.Throws<InvalidOperationException>(() => new CosmosSqlBuilder().Build());
        Assert.Throws<NotSupportedException>(() => CosmosSqlExpression.Parameter(ObservationValue.Undefined));
    }
}
