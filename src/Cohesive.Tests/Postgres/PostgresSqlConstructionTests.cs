using Cohesive.Adapters.Sql;
using Cohesive.Adapters.Postgres;

namespace Cohesive.Tests.Postgres;

public sealed class PostgresSqlConstructionTests
{
    [Fact]
    public void Build_QuotesEveryIdentifierAsOneInjectionSafeToken()
    {
        const string schema = "public\"; DROP SCHEMA public; --";
        const string table = "loads; DELETE FROM customers";
        const string source = "l\" JOIN secrets ON TRUE --";
        const string column = "id\"; SELECT pg_sleep(10); --";
        const string result = "value\" FROM secrets --";

        var statement = new SqlSelectBuilder(
                new SqlQualifiedTable(schema, table),
                source)
            .Select(SqlExpression.Column(source, column), result)
            .Build(PostgresSqlDialect.Instance);

        Assert.Equal(
            "SELECT \"l\"\" JOIN secrets ON TRUE --\".\"id\"\"; SELECT pg_sleep(10); --\" AS "
            + "\"value\"\" FROM secrets --\" FROM \"public\"\"; DROP SCHEMA public; --\"."
            + "\"loads; DELETE FROM customers\" AS \"l\"\" JOIN secrets ON TRUE --\"",
            statement.Text);
        Assert.Empty(statement.Parameters);
        Assert.Throws<ArgumentException>(() => PostgresSqlDialect.Identifier("invalid\0identifier"));
    }

    [Fact]
    public void Identifier_RejectsUtf8NamesThatPostgresWouldSilentlyTruncate()
    {
        Assert.Equal(
            new string('a', PostgresSqlDialect.StandardMaxUtf8ByteLength),
            PostgresSqlDialect.Identifier(new string('a', PostgresSqlDialect.StandardMaxUtf8ByteLength)).Value);
        Assert.Throws<ArgumentException>(() =>
            PostgresSqlDialect.Identifier(new string('a', PostgresSqlDialect.StandardMaxUtf8ByteLength + 1)));

        Assert.Equal(new string('\u00e9', 31), PostgresSqlDialect.Identifier(new string('\u00e9', 31)).Value);
        Assert.Throws<ArgumentException>(() => PostgresSqlDialect.Identifier(new string('\u00e9', 32)));
        Assert.Throws<ArgumentException>(() => PostgresSqlDialect.Identifier("invalid\ud800"));
    }

    [Fact]
    public void BuildTemplate_DoesNotTreatQuotedIdentifierDigitsAsParameterSlots()
    {
        var template = new SqlSelectBuilder(
                new SqlQualifiedTable("public", "loads$1"),
                "l$2")
            .Select(SqlExpression.Column("l$2", "amount\"$3"), "result$4")
            .BuildTemplate(PostgresSqlDialect.Instance);

        Assert.Empty(template.Parameters);
        Assert.Contains("\"amount\"\"$3\"", template.Text, StringComparison.Ordinal);
        Assert.Equal(template.Text, template.Bind(PostgresSqlDialect.Instance).Text);
    }

    [Fact]
    public void Values_RejectTextOutsideTheExactPostgresUtf8Domain()
    {
        Assert.Throws<ArgumentException>(() => SqlExpression.Constant("invalid\0text"));
        Assert.Throws<ArgumentException>(() => SqlExpression.Constant("invalid\ud800"));

        var template = new SqlSelectBuilder()
            .Select(SqlExpression.RuntimeParameter("value"), "value")
            .BuildTemplate(PostgresSqlDialect.Instance);
        Assert.Throws<ArgumentException>(() => template.Bind(PostgresSqlDialect.Instance,
            new Dictionary<string, object?> { ["value"] = "invalid\0text" }));
        Assert.Throws<ArgumentException>(() => template.Bind(PostgresSqlDialect.Instance,
            new Dictionary<string, object?> { ["value"] = "invalid\ud800" }));

        var bytes = new byte[] { 0, 255, 0 };
        var bound = template.Bind(PostgresSqlDialect.Instance, new Dictionary<string, object?> { ["value"] = bytes });
        var firstRead = Assert.IsType<byte[]>(Assert.Single(bound.Parameters).Value);
        Assert.Equal(bytes, firstRead);
        Assert.NotSame(bytes, firstRead);
        firstRead[0] = 42;
        Assert.Equal(bytes, Assert.IsType<byte[]>(Assert.Single(bound.Parameters).Value));

        Assert.Throws<ArgumentException>(() => template.Bind(PostgresSqlDialect.Instance,
            new Dictionary<string, object?> { ["value"] = new object() }));
        Assert.Throws<ArgumentException>(() => template.Bind(PostgresSqlDialect.Instance,
            new Dictionary<string, object?> { ["value"] = DateTime.UtcNow }));
        Assert.Throws<ArgumentException>(() => template.Bind(PostgresSqlDialect.Instance,
            new Dictionary<string, object?>
            {
                ["value"] = new DateTime(2026, 7, 18).AddTicks(1)
            }));
        var nonUtcInstant = new DateTimeOffset(2026, 7, 18, 12, 0, 0, TimeSpan.FromHours(2));
        Assert.Throws<ArgumentException>(() => new SqlSelectBuilder().Select(SqlExpression.Constant(nonUtcInstant), "value").BuildTemplate(PostgresSqlDialect.Instance));
        Assert.Throws<ArgumentException>(() => template.Bind(PostgresSqlDialect.Instance,
            new Dictionary<string, object?> { ["value"] = nonUtcInstant }));
    }

    [Fact]
    public void PersistedTimestampConstantsRequireCanonicalPostgresTemporalDomain()
    {
        _ = new SqlConstant(
            SqlConstantKind.Timestamp,
            "2026-07-18T12:00:00.0000010");
        _ = new SqlConstant(
            SqlConstantKind.TimestampWithTimeZone,
            "2026-07-18T12:00:00.0000010+00:00");

        Assert.Throws<ArgumentException>(() => PostgresSqlDialect.Instance.ValidateParameter(new SqlConstant(
            SqlConstantKind.Timestamp,
            "2026-07-18T12:00:00.0000001").ToClrValue()));
        Assert.Throws<ArgumentException>(() => PostgresSqlDialect.Instance.ValidateParameter(new SqlConstant(
            SqlConstantKind.TimestampWithTimeZone,
            "2026-07-18T12:00:00.0000001+00:00").ToClrValue()));
        Assert.Throws<ArgumentException>(() => PostgresSqlDialect.Instance.ValidateParameter(new SqlConstant(
            SqlConstantKind.TimestampWithTimeZone,
            "2026-07-18T12:00:00.0000010+02:00").ToClrValue()));
    }

    [Fact]
    public void BuildTemplate_ParameterizesValuesDeterministicallyAndBindsRuntimeValues()
    {
        var builder = new SqlSelectBuilder(new SqlQualifiedTable("public", "loads"), "l")
            .Select(SqlExpression.Column("l", "id"), "id")
            .Where(SqlExpression.Binary(
                SqlBinaryOperator.Equal,
                SqlExpression.Column("l", "status"),
                SqlExpression.RuntimeParameter("status")))
            .Where(SqlExpression.Binary(
                SqlBinaryOperator.GreaterThan,
                SqlExpression.Column("l", "amount"),
                SqlExpression.Constant(10m)))
            .Where(SqlExpression.Binary(
                SqlBinaryOperator.NotEqual,
                SqlExpression.Column("l", "prior_status"),
                SqlExpression.RuntimeParameter("status")));

        var first = builder.BuildTemplate(PostgresSqlDialect.Instance);
        var second = builder.BuildTemplate(PostgresSqlDialect.Instance);

        Assert.Equal(first.Text, second.Text);
        Assert.Equal(
            "SELECT \"l\".\"id\" AS \"id\" FROM \"public\".\"loads\" AS \"l\" "
            + "WHERE (\"l\".\"status\" = $1) AND (\"l\".\"amount\" > $2) "
            + "AND (\"l\".\"prior_status\" <> $1)",
            first.Text);
        Assert.Equal(
            [
                (1, SqlParameterBindingKind.Runtime, "status", null),
                (2, SqlParameterBindingKind.Constant, null, 10m)
            ],
            first.Parameters.Select(static parameter =>
                (parameter.Position, parameter.Kind, parameter.Binding, parameter.ConstantValue)));

        var bound = first.Bind(PostgresSqlDialect.Instance, new Dictionary<string, object?> { ["status"] = "Open" });
        Assert.Equal(first.Text, bound.Text);
        Assert.Equal(
            [(1, "status", (object?)"Open"), (2, null, (object?)10m)],
            bound.Parameters.Select(static parameter =>
                (parameter.Position, parameter.Binding, parameter.Value)));
        Assert.Throws<ArgumentException>(() => first.Bind(PostgresSqlDialect.Instance, new Dictionary<string, object?>()));
        Assert.Throws<ArgumentException>(() => first.Bind(PostgresSqlDialect.Instance,
            new Dictionary<string, object?> { ["status"] = "Open", ["unknown"] = 1 }));
    }

    [Fact]
    public void Exists_ComposesCorrelatedQueriesWithOneDeterministicParameterContext()
    {
        var occurrence = new SqlSelectBuilder(
                new SqlQualifiedTable("public", "order_stops"),
                "s")
            .Select(SqlExpression.Constant(1), "match")
            .Where(SqlExpression.Binary(
                @operator: SqlBinaryOperator.Equal,
                left: SqlExpression.Column("s", "order_id"),
                right: SqlExpression.Column("o", "id")))
            .Where(SqlExpression.Binary(
                @operator: SqlBinaryOperator.Equal,
                left: SqlExpression.Column("s", "location_id"),
                right: SqlExpression.RuntimeParameter("location")))
            .BuildQuery();

        var template = new SqlSelectBuilder(
                new SqlQualifiedTable("public", "orders"),
                "o")
            .Select(SqlExpression.Column("o", "id"), "id")
            .Where(SqlExpression.Exists(occurrence))
            .BuildTemplate(PostgresSqlDialect.Instance);

        Assert.Equal(
            "SELECT \"o\".\"id\" AS \"id\" FROM \"public\".\"orders\" AS \"o\" "
            + "WHERE EXISTS (SELECT $1 AS \"match\" FROM \"public\".\"order_stops\" AS \"s\" "
            + "WHERE (\"s\".\"order_id\" = \"o\".\"id\") AND (\"s\".\"location_id\" = $2))",
            template.Text);
        Assert.Equal(2, template.Parameters.Length);
        Assert.Equal("location", template.Parameters[1].Binding);
    }

    [Fact]
    public void MutationBuildersShareSafeIdentifiersExpressionsAndParameterTemplates()
    {
        SqlQualifiedTable table = new("transport", "loads");
        var insert = new SqlInsertBuilder(table)
            .Value("tenant_id", SqlExpression.RuntimeParameter("tenant"))
            .Value("load_id", SqlExpression.RuntimeParameter("id"))
            .Value("status", SqlExpression.RuntimeParameter("status"))
            .OnConflictDoUpdate(
                conflictColumns: ["tenant_id", "load_id"],
                excludedUpdateColumns: ["status"])
            .Returning(SqlExpression.UnqualifiedColumn("xmin"), "concurrency_token")
            .BuildTemplate(PostgresSqlDialect.Instance);

        Assert.Equal(
            "INSERT INTO \"transport\".\"loads\" (\"tenant_id\", \"load_id\", \"status\") "
            + "VALUES ($1, $2, $3) ON CONFLICT (\"tenant_id\", \"load_id\") DO UPDATE SET "
            + "\"status\" = EXCLUDED.\"status\" RETURNING \"xmin\" AS \"concurrency_token\"",
            insert.Text);
        Assert.Equal(
            ["tenant", "id", "status"],
            insert.Parameters.Select(static parameter => parameter.Binding));

        var deduplicatedInsert = new SqlInsertBuilder(table)
            .Value("tenant_id", SqlExpression.RuntimeParameter("tenant"))
            .Value("load_id", SqlExpression.RuntimeParameter("id"))
            .OnConflictDoNothing(["tenant_id", "load_id"])
            .BuildTemplate(PostgresSqlDialect.Instance);
        Assert.Equal(
            "INSERT INTO \"transport\".\"loads\" (\"tenant_id\", \"load_id\") VALUES ($1, $2) "
            + "ON CONFLICT (\"tenant_id\", \"load_id\") DO NOTHING",
            deduplicatedInsert.Text);

        var clock = new SqlSelectBuilder()
            .Select(SqlExpression.Intrinsic(PostgresSqlDialect.ClockTimestampIntrinsic), "provider_now")
            .BuildTemplate(PostgresSqlDialect.Instance);
        Assert.Equal("SELECT CLOCK_TIMESTAMP() AS \"provider_now\"", clock.Text);

        var update = new SqlUpdateBuilder(table)
            .Set("load_id", SqlExpression.RuntimeParameter("id"))
            .Set("status", SqlExpression.RuntimeParameter("status"))
            .Where(SqlExpression.Binary(
                @operator: SqlBinaryOperator.Equal,
                left: SqlExpression.UnqualifiedColumn("load_id"),
                right: SqlExpression.RuntimeParameter("id")))
            .Where(SqlExpression.Binary(
                @operator: SqlBinaryOperator.Equal,
                left: SqlExpression.UnqualifiedColumn("xmin"),
                right: SqlExpression.RuntimeParameter("expected-concurrency")))
            .Returning(SqlExpression.UnqualifiedColumn("xmin"), "concurrency_token")
            .BuildTemplate(PostgresSqlDialect.Instance);

        Assert.Equal(
            "UPDATE \"transport\".\"loads\" SET \"load_id\" = $1, \"status\" = $2 "
            + "WHERE (\"load_id\" = $1) AND (\"xmin\" = $3) "
            + "RETURNING \"xmin\" AS \"concurrency_token\"",
            update.Text);
        Assert.Equal(
            ["id", "status", "expected-concurrency"],
            update.Parameters.Select(static parameter => parameter.Binding));

        var delete = new SqlDeleteBuilder(table)
            .Where(SqlExpression.Binary(
                @operator: SqlBinaryOperator.Equal,
                left: SqlExpression.UnqualifiedColumn("tenant_id"),
                right: SqlExpression.RuntimeParameter("tenant")))
            .Where(SqlExpression.Binary(
                @operator: SqlBinaryOperator.Equal,
                left: SqlExpression.UnqualifiedColumn("load_id"),
                right: SqlExpression.RuntimeParameter("id")))
            .Returning(SqlExpression.UnqualifiedColumn("load_id"), "deleted_id")
            .BuildTemplate(PostgresSqlDialect.Instance);

        Assert.Equal(
            "DELETE FROM \"transport\".\"loads\" WHERE (\"tenant_id\" = $1) AND (\"load_id\" = $2) "
            + "RETURNING \"load_id\" AS \"deleted_id\"",
            delete.Text);
        Assert.Equal(
            ["tenant", "id"],
            delete.Parameters.Select(static parameter => parameter.Binding));
    }

    [Fact]
    public void MutationBuildersRejectIncompleteOrUnrestrictedCommands()
    {
        SqlQualifiedTable table = new("transport", "loads");
        var unrestricted = new SqlUpdateBuilder(table)
            .Set("status", SqlExpression.RuntimeParameter("status"));
        var unrestrictedDelete = new SqlDeleteBuilder(table);
        var incompleteUpsert = new SqlInsertBuilder(table)
            .Value("load_id", SqlExpression.RuntimeParameter("id"))
            .OnConflictDoUpdate(
                conflictColumns: ["tenant_id", "load_id"],
                excludedUpdateColumns: ["status"]);

        Assert.Throws<InvalidOperationException>(() => unrestricted.BuildTemplate(PostgresSqlDialect.Instance));
        Assert.Throws<InvalidOperationException>(() => unrestrictedDelete.BuildTemplate(PostgresSqlDialect.Instance));
        Assert.Throws<InvalidOperationException>(() => incompleteUpsert.BuildTemplate(PostgresSqlDialect.Instance));
    }

    [Fact]
    public void Build_ComposesDerivedJoinAggregateFilterAndNullAwareKeysetPaging()
    {
        var activeLoads = new SqlSelectBuilder(
                new SqlQualifiedTable("transport", "loads"),
                "l")
            .Select(SqlExpression.Column("l", "customer_id"), "customer_id")
            .Select(
                SqlExpression.Aggregate(
                    SqlAggregateFunction.Sum,
                    SqlExpression.Column("l", "amount"),
                    SqlExpression.Binary(
                        SqlBinaryOperator.Equal,
                        SqlExpression.Column("l", "active"),
                        SqlExpression.Constant(true))),
                "active_total")
            .GroupBy(SqlExpression.Column("l", "customer_id"))
            .BuildQuery();
        var keyset = SqlExpression.KeysetAfter(
        [
            new(
                SqlExpression.Column("c", "name"),
                SqlExpression.RuntimeParameter("after-name"),
                SqlSortDirection.Ascending,
                SqlNullPlacement.Last),
            new(
                SqlExpression.Column("c", "id"),
                SqlExpression.RuntimeParameter("after-id"),
                SqlSortDirection.Ascending,
                SqlNullPlacement.Last)
        ]);

        var template = new SqlSelectBuilder(
                new SqlQualifiedTable("transport", "customers"),
                "c")
            .Select(SqlExpression.Column("c", "id"), "customer_id")
            .Select(SqlExpression.Column("t", "active_total"), "active_total")
            .Join(
                activeLoads,
                "t",
                SqlJoinKind.Left,
                SqlExpression.Binary(
                    SqlBinaryOperator.Equal,
                    SqlExpression.Column("c", "id"),
                    SqlExpression.Column("t", "customer_id")))
            .Where(keyset)
            .OrderBy(SqlExpression.Column("c", "name"))
            .OrderBy(SqlExpression.Column("c", "id"))
            .Limit(25)
            .BuildTemplate(PostgresSqlDialect.Instance);

        Assert.Contains(
            "LEFT JOIN (SELECT \"l\".\"customer_id\" AS \"customer_id\", "
            + "SUM(\"l\".\"amount\") FILTER (WHERE (\"l\".\"active\" = $1)) AS \"active_total\"",
            template.Text,
            StringComparison.Ordinal);
        Assert.Contains("GROUP BY \"l\".\"customer_id\") AS \"t\" ON", template.Text, StringComparison.Ordinal);
        Assert.Contains("IS NOT DISTINCT FROM", template.Text, StringComparison.Ordinal);
        Assert.EndsWith(
            "ORDER BY \"c\".\"name\" ASC NULLS LAST, \"c\".\"id\" ASC NULLS LAST LIMIT 25",
            template.Text,
            StringComparison.Ordinal);
        Assert.Equal(
            [
                (SqlParameterBindingKind.Constant, (string?)null, (object?)true),
                (SqlParameterBindingKind.Runtime, "after-name", null),
                (SqlParameterBindingKind.Runtime, "after-id", null)
            ],
            template.Parameters.Select(static parameter =>
                (parameter.Kind, parameter.Binding, parameter.ConstantValue)));

        var bound = template.Bind(PostgresSqlDialect.Instance, new Dictionary<string, object?>
        {
            ["after-name"] = "Acme",
            ["after-id"] = "customer-42"
        });
        Assert.Equal([true, "Acme", "customer-42"], bound.Parameters.Select(static parameter => parameter.Value));
    }
}
