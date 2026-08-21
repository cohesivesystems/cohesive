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

        var statement = new PostgresSqlSelectBuilder(
                new PostgresSqlQualifiedTable(schema, table),
                source)
            .Select(PostgresSqlExpression.Column(source, column), result)
            .Build();

        Assert.Equal(
            "SELECT \"l\"\" JOIN secrets ON TRUE --\".\"id\"\"; SELECT pg_sleep(10); --\" AS "
            + "\"value\"\" FROM secrets --\" FROM \"public\"\"; DROP SCHEMA public; --\"."
            + "\"loads; DELETE FROM customers\" AS \"l\"\" JOIN secrets ON TRUE --\"",
            statement.Text);
        Assert.Empty(statement.Parameters);
        Assert.Throws<ArgumentException>(() => new PostgresSqlIdentifier("invalid\0identifier"));
    }

    [Fact]
    public void Identifier_RejectsUtf8NamesThatPostgresWouldSilentlyTruncate()
    {
        Assert.Equal(
            new string('a', PostgresSqlIdentifier.StandardMaxUtf8ByteLength),
            new PostgresSqlIdentifier(new string('a', PostgresSqlIdentifier.StandardMaxUtf8ByteLength)).Value);
        Assert.Throws<ArgumentException>(() =>
            new PostgresSqlIdentifier(new string('a', PostgresSqlIdentifier.StandardMaxUtf8ByteLength + 1)));

        Assert.Equal(new string('\u00e9', 31), new PostgresSqlIdentifier(new string('\u00e9', 31)).Value);
        Assert.Throws<ArgumentException>(() => new PostgresSqlIdentifier(new string('\u00e9', 32)));
        Assert.Throws<ArgumentException>(() => new PostgresSqlIdentifier("invalid\ud800"));
    }

    [Fact]
    public void BuildTemplate_DoesNotTreatQuotedIdentifierDigitsAsParameterSlots()
    {
        var template = new PostgresSqlSelectBuilder(
                new PostgresSqlQualifiedTable("public", "loads$1"),
                "l$2")
            .Select(PostgresSqlExpression.Column("l$2", "amount\"$3"), "result$4")
            .BuildTemplate();

        Assert.Empty(template.Parameters);
        Assert.Contains("\"amount\"\"$3\"", template.Text, StringComparison.Ordinal);
        Assert.Equal(template.Text, template.Bind().Text);
    }

    [Fact]
    public void Values_RejectTextOutsideTheExactPostgresUtf8Domain()
    {
        Assert.Throws<ArgumentException>(() => PostgresSqlExpression.Constant("invalid\0text"));
        Assert.Throws<ArgumentException>(() => PostgresSqlExpression.Constant("invalid\ud800"));

        var template = new PostgresSqlSelectBuilder()
            .Select(PostgresSqlExpression.RuntimeParameter("value"), "value")
            .BuildTemplate();
        Assert.Throws<ArgumentException>(() => template.Bind(
            new Dictionary<string, object?> { ["value"] = "invalid\0text" }));
        Assert.Throws<ArgumentException>(() => template.Bind(
            new Dictionary<string, object?> { ["value"] = "invalid\ud800" }));

        var bytes = new byte[] { 0, 255, 0 };
        var bound = template.Bind(new Dictionary<string, object?> { ["value"] = bytes });
        var firstRead = Assert.IsType<byte[]>(Assert.Single(bound.Parameters).Value);
        Assert.Equal(bytes, firstRead);
        Assert.NotSame(bytes, firstRead);
        firstRead[0] = 42;
        Assert.Equal(bytes, Assert.IsType<byte[]>(Assert.Single(bound.Parameters).Value));

        Assert.Throws<ArgumentException>(() => template.Bind(
            new Dictionary<string, object?> { ["value"] = new object() }));
        Assert.Throws<ArgumentException>(() => template.Bind(
            new Dictionary<string, object?> { ["value"] = DateTime.UtcNow }));
        Assert.Throws<ArgumentException>(() => template.Bind(
            new Dictionary<string, object?>
            {
                ["value"] = new DateTime(2026, 7, 18).AddTicks(1)
            }));
        var nonUtcInstant = new DateTimeOffset(2026, 7, 18, 12, 0, 0, TimeSpan.FromHours(2));
        Assert.Throws<ArgumentException>(() => PostgresSqlExpression.Constant(nonUtcInstant));
        Assert.Throws<ArgumentException>(() => template.Bind(
            new Dictionary<string, object?> { ["value"] = nonUtcInstant }));
    }

    [Fact]
    public void PersistedTimestampConstantsRequireCanonicalPostgresTemporalDomain()
    {
        _ = new PostgresSqlConstant(
            PostgresSqlConstantKind.Timestamp,
            "2026-07-18T12:00:00.0000010");
        _ = new PostgresSqlConstant(
            PostgresSqlConstantKind.TimestampWithTimeZone,
            "2026-07-18T12:00:00.0000010+00:00");

        Assert.Throws<ArgumentException>(() => new PostgresSqlConstant(
            PostgresSqlConstantKind.Timestamp,
            "2026-07-18T12:00:00.0000001"));
        Assert.Throws<ArgumentException>(() => new PostgresSqlConstant(
            PostgresSqlConstantKind.TimestampWithTimeZone,
            "2026-07-18T12:00:00.0000001+00:00"));
        Assert.Throws<ArgumentException>(() => new PostgresSqlConstant(
            PostgresSqlConstantKind.TimestampWithTimeZone,
            "2026-07-18T12:00:00.0000010+02:00"));
    }

    [Fact]
    public void BuildTemplate_ParameterizesValuesDeterministicallyAndBindsRuntimeValues()
    {
        var builder = new PostgresSqlSelectBuilder(new PostgresSqlQualifiedTable("public", "loads"), "l")
            .Select(PostgresSqlExpression.Column("l", "id"), "id")
            .Where(PostgresSqlExpression.Binary(
                PostgresSqlBinaryOperator.Equal,
                PostgresSqlExpression.Column("l", "status"),
                PostgresSqlExpression.RuntimeParameter("status")))
            .Where(PostgresSqlExpression.Binary(
                PostgresSqlBinaryOperator.GreaterThan,
                PostgresSqlExpression.Column("l", "amount"),
                PostgresSqlExpression.Constant(10m)))
            .Where(PostgresSqlExpression.Binary(
                PostgresSqlBinaryOperator.NotEqual,
                PostgresSqlExpression.Column("l", "prior_status"),
                PostgresSqlExpression.RuntimeParameter("status")));

        var first = builder.BuildTemplate();
        var second = builder.BuildTemplate();

        Assert.Equal(first.Text, second.Text);
        Assert.Equal(
            "SELECT \"l\".\"id\" AS \"id\" FROM \"public\".\"loads\" AS \"l\" "
            + "WHERE (\"l\".\"status\" = $1) AND (\"l\".\"amount\" > $2) "
            + "AND (\"l\".\"prior_status\" <> $1)",
            first.Text);
        Assert.Equal(
            [
                (1, PostgresSqlParameterBindingKind.Runtime, "status", null),
                (2, PostgresSqlParameterBindingKind.Constant, null, 10m)
            ],
            first.Parameters.Select(static parameter =>
                (parameter.Position, parameter.Kind, parameter.Binding, parameter.ConstantValue)));

        var bound = first.Bind(new Dictionary<string, object?> { ["status"] = "Open" });
        Assert.Equal(first.Text, bound.Text);
        Assert.Equal(
            [(1, "status", (object?)"Open"), (2, null, (object?)10m)],
            bound.Parameters.Select(static parameter =>
                (parameter.Position, parameter.Binding, parameter.Value)));
        Assert.Throws<ArgumentException>(() => first.Bind(new Dictionary<string, object?>()));
        Assert.Throws<ArgumentException>(() => first.Bind(
            new Dictionary<string, object?> { ["status"] = "Open", ["unknown"] = 1 }));
    }

    [Fact]
    public void MutationBuildersShareSafeIdentifiersExpressionsAndParameterTemplates()
    {
        PostgresSqlQualifiedTable table = new("transport", "loads");
        var insert = new PostgresSqlInsertBuilder(table)
            .Value("tenant_id", PostgresSqlExpression.RuntimeParameter("tenant"))
            .Value("load_id", PostgresSqlExpression.RuntimeParameter("id"))
            .Value("status", PostgresSqlExpression.RuntimeParameter("status"))
            .OnConflictDoUpdate(
                conflictColumns: ["tenant_id", "load_id"],
                excludedUpdateColumns: ["status"])
            .Returning(PostgresSqlExpression.UnqualifiedColumn("xmin"), "concurrency_token")
            .BuildTemplate();

        Assert.Equal(
            "INSERT INTO \"transport\".\"loads\" (\"tenant_id\", \"load_id\", \"status\") "
            + "VALUES ($1, $2, $3) ON CONFLICT (\"tenant_id\", \"load_id\") DO UPDATE SET "
            + "\"status\" = EXCLUDED.\"status\" RETURNING \"xmin\" AS \"concurrency_token\"",
            insert.Text);
        Assert.Equal(
            ["tenant", "id", "status"],
            insert.Parameters.Select(static parameter => parameter.Binding));

        var update = new PostgresSqlUpdateBuilder(table)
            .Set("load_id", PostgresSqlExpression.RuntimeParameter("id"))
            .Set("status", PostgresSqlExpression.RuntimeParameter("status"))
            .Where(PostgresSqlExpression.Binary(
                @operator: PostgresSqlBinaryOperator.Equal,
                left: PostgresSqlExpression.UnqualifiedColumn("load_id"),
                right: PostgresSqlExpression.RuntimeParameter("id")))
            .Where(PostgresSqlExpression.Binary(
                @operator: PostgresSqlBinaryOperator.Equal,
                left: PostgresSqlExpression.UnqualifiedColumn("xmin"),
                right: PostgresSqlExpression.RuntimeParameter("expected-concurrency")))
            .Returning(PostgresSqlExpression.UnqualifiedColumn("xmin"), "concurrency_token")
            .BuildTemplate();

        Assert.Equal(
            "UPDATE \"transport\".\"loads\" SET \"load_id\" = $1, \"status\" = $2 "
            + "WHERE (\"load_id\" = $1) AND (\"xmin\" = $3) "
            + "RETURNING \"xmin\" AS \"concurrency_token\"",
            update.Text);
        Assert.Equal(
            ["id", "status", "expected-concurrency"],
            update.Parameters.Select(static parameter => parameter.Binding));

        var delete = new PostgresSqlDeleteBuilder(table)
            .Where(PostgresSqlExpression.Binary(
                @operator: PostgresSqlBinaryOperator.Equal,
                left: PostgresSqlExpression.UnqualifiedColumn("tenant_id"),
                right: PostgresSqlExpression.RuntimeParameter("tenant")))
            .Where(PostgresSqlExpression.Binary(
                @operator: PostgresSqlBinaryOperator.Equal,
                left: PostgresSqlExpression.UnqualifiedColumn("load_id"),
                right: PostgresSqlExpression.RuntimeParameter("id")))
            .Returning(PostgresSqlExpression.UnqualifiedColumn("load_id"), "deleted_id")
            .BuildTemplate();

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
        PostgresSqlQualifiedTable table = new("transport", "loads");
        var unrestricted = new PostgresSqlUpdateBuilder(table)
            .Set("status", PostgresSqlExpression.RuntimeParameter("status"));
        var unrestrictedDelete = new PostgresSqlDeleteBuilder(table);
        var incompleteUpsert = new PostgresSqlInsertBuilder(table)
            .Value("load_id", PostgresSqlExpression.RuntimeParameter("id"))
            .OnConflictDoUpdate(
                conflictColumns: ["tenant_id", "load_id"],
                excludedUpdateColumns: ["status"]);

        Assert.Throws<InvalidOperationException>(unrestricted.BuildTemplate);
        Assert.Throws<InvalidOperationException>(unrestrictedDelete.BuildTemplate);
        Assert.Throws<InvalidOperationException>(incompleteUpsert.BuildTemplate);
    }

    [Fact]
    public void Build_ComposesDerivedJoinAggregateFilterAndNullAwareKeysetPaging()
    {
        var activeLoads = new PostgresSqlSelectBuilder(
                new PostgresSqlQualifiedTable("transport", "loads"),
                "l")
            .Select(PostgresSqlExpression.Column("l", "customer_id"), "customer_id")
            .Select(
                PostgresSqlExpression.Aggregate(
                    PostgresSqlAggregateFunction.Sum,
                    PostgresSqlExpression.Column("l", "amount"),
                    PostgresSqlExpression.Binary(
                        PostgresSqlBinaryOperator.Equal,
                        PostgresSqlExpression.Column("l", "active"),
                        PostgresSqlExpression.Constant(true))),
                "active_total")
            .GroupBy(PostgresSqlExpression.Column("l", "customer_id"))
            .BuildQuery();
        var keyset = PostgresSqlExpression.KeysetAfter(
        [
            new(
                PostgresSqlExpression.Column("c", "name"),
                PostgresSqlExpression.RuntimeParameter("after-name"),
                PostgresSqlSortDirection.Ascending,
                PostgresSqlNullPlacement.Last),
            new(
                PostgresSqlExpression.Column("c", "id"),
                PostgresSqlExpression.RuntimeParameter("after-id"),
                PostgresSqlSortDirection.Ascending,
                PostgresSqlNullPlacement.Last)
        ]);

        var template = new PostgresSqlSelectBuilder(
                new PostgresSqlQualifiedTable("transport", "customers"),
                "c")
            .Select(PostgresSqlExpression.Column("c", "id"), "customer_id")
            .Select(PostgresSqlExpression.Column("t", "active_total"), "active_total")
            .Join(
                activeLoads,
                "t",
                PostgresSqlJoinKind.Left,
                PostgresSqlExpression.Binary(
                    PostgresSqlBinaryOperator.Equal,
                    PostgresSqlExpression.Column("c", "id"),
                    PostgresSqlExpression.Column("t", "customer_id")))
            .Where(keyset)
            .OrderBy(PostgresSqlExpression.Column("c", "name"))
            .OrderBy(PostgresSqlExpression.Column("c", "id"))
            .Limit(25)
            .BuildTemplate();

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
                (PostgresSqlParameterBindingKind.Constant, (string?)null, (object?)true),
                (PostgresSqlParameterBindingKind.Runtime, "after-name", null),
                (PostgresSqlParameterBindingKind.Runtime, "after-id", null)
            ],
            template.Parameters.Select(static parameter =>
                (parameter.Kind, parameter.Binding, parameter.ConstantValue)));

        var bound = template.Bind(new Dictionary<string, object?>
        {
            ["after-name"] = "Acme",
            ["after-id"] = "customer-42"
        });
        Assert.Equal([true, "Acme", "customer-42"], bound.Parameters.Select(static parameter => parameter.Value));
    }
}
