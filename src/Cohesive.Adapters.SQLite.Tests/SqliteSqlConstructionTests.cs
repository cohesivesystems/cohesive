using System.Collections.Immutable;
using System.Text.Json;
using Cohesive.Adapters.Sql;
using Microsoft.Data.Sqlite;

namespace Cohesive.Adapters.SQLite.Tests;

public sealed class SqliteSqlConstructionTests
{
    [Fact]
    public void IndentedQueriesPreserveParametersQuotedTokensAndNestedExecution()
    {
        var value = SqlExpression.RuntimeParameter("value");
        var child = new SqlSelectBuilder().Select(value, "value  \"$9").Limit(1).BuildQuery();
        var query = new SqlSelectBuilder(child, "candidate")
            .Select(SqlExpression.Column("candidate", "value  \"$9"), "value")
            .Select(SqlExpression.ScalarSubquery(child), "scalar")
            .Select(SqlExpression.Exists(child), "exists")
            .Select(SqlExpression.RowNumber([], [new(value)]), "rank")
            .Select(SqlExpression.Constant(7L), "constant")
            .Where(SqlExpression.Binary(SqlBinaryOperator.Equal,
                SqlExpression.Column("candidate", "value  \"$9"), value))
            .OrderBy(value).BuildQuery();
        var compact = query.ToCommandTemplate(SqliteSqlDialect.Instance);
        var formatted = query.ToCommandTemplate(SqliteSqlDialect.Instance, SqlFormatting.Indented);
        Assert.Equal(JsonSerializer.Serialize(compact.Parameters), JsonSerializer.Serialize(formatted.Parameters));
        Assert.Equal(formatted.Text, query.ToCommandTemplate(SqliteSqlDialect.Instance, SqlFormatting.Indented).Text);
        Assert.DoesNotContain("\r", formatted.Text, StringComparison.Ordinal);
        Assert.Contains("\nFROM (\n    SELECT\n        $1 AS \"value  \"\"$9\"", formatted.Text, StringComparison.Ordinal);
        Assert.Contains("ROW_NUMBER() OVER (\n        ORDER BY $1 ASC NULLS LAST\n    )", formatted.Text, StringComparison.Ordinal);
        Assert.Equal(2, formatted.Parameters.Length);
        var restored = JsonSerializer.Deserialize<SqlCommandTemplate>(JsonSerializer.Serialize(formatted))!;
        using var file = new DatabaseFixture();
        using var connection = file.Database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        foreach (var template in new[] { compact, restored })
        {
            using var scope = new SqliteCommandScope(file.Database, connection, transaction);
            using var reader = scope.ExecuteReader(new(template), CancellationToken.None, ("value", 42L));
            Assert.True(reader.Read());
            Assert.Equal([42L, 42L, 1L, 1L, 7L], Enumerable.Range(0, 5).Select(reader.GetInt64));
            Assert.False(reader.Read());
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => query.ToCommandTemplate(SqliteSqlDialect.Instance, (SqlFormatting)99));
    }

    [Fact]
    public void IndentedSqlHasAStableReviewableLayout()
    {
        var source = new SqlSelectBuilder(new SqlQualifiedTable("candidates"), "candidate")
            .Select(SqlExpression.Column("candidate", "id"), "candidate_id")
            .Select(SqlExpression.RowNumber([SqlExpression.Column("candidate", "category")],
                [new(SqlExpression.Column("candidate", "id"))]), "representative_rank").BuildQuery();
        var template = new SqlSelectBuilder(source, "ranked")
            .Select(SqlExpression.Column("ranked", "candidate_id"), "candidate_id")
            .Where(SqlExpression.Binary(SqlBinaryOperator.Equal, SqlExpression.Column("ranked", "representative_rank"),
                SqlExpression.Constant(1L)))
            .OrderBy(SqlExpression.Column("ranked", "candidate_id"))
            .BuildTemplate(SqliteSqlDialect.Instance, SqlFormatting.Indented);
        Assert.Equal("""
            SELECT
                "ranked"."candidate_id" AS "candidate_id"
            FROM (
                SELECT
                    "candidate"."id" AS "candidate_id",
                    ROW_NUMBER() OVER (
                        PARTITION BY "candidate"."category"
                        ORDER BY "candidate"."id" ASC NULLS LAST
                    ) AS "representative_rank"
                FROM "candidates" AS "candidate"
            ) AS "ranked"
            WHERE ("ranked"."representative_rank" = $1)
            ORDER BY
                "ranked"."candidate_id" ASC NULLS LAST
            """, template.Text);
    }

    [Theory]
    [InlineData(SqlSortDirection.Ascending, SqlNullPlacement.First, 1L)]
    [InlineData(SqlSortDirection.Descending, SqlNullPlacement.First, 1L)]
    [InlineData(SqlSortDirection.Ascending, SqlNullPlacement.Last, 2L)]
    [InlineData(SqlSortDirection.Descending, SqlNullPlacement.Last, 3L)]
    public void RowNumberPreservesPartitionOrderingAndFiltersWinnersAfterSelection(
        SqlSortDirection direction, SqlNullPlacement nulls, long expected)
    {
        using var file = new DatabaseFixture();
        using var connection = file.Database.OpenConnection();
        using (var setup = file.Database.CreateCommand(connection, null, """
            CREATE TABLE candidates (id INTEGER PRIMARY KEY, category TEXT, preference INTEGER, eligible INTEGER NOT NULL) STRICT;
            INSERT INTO candidates VALUES (1, 'a', NULL, 1), (2, 'a', 5, 1), (3, 'a', 8, 1),
                (4, 'b', 1, 0), (5, 'b', 1, 1);
            """))
            setup.ExecuteNonQuery();

        var ranked = new SqlSelectBuilder(new SqlQualifiedTable("candidates"), "c")
            .Select(SqlExpression.Column("c", "id"), "id")
            .Select(SqlExpression.Column("c", "eligible"), "eligible")
            .Select(SqlExpression.RowNumber([SqlExpression.Column("c", "category")],
                [new(SqlExpression.Column("c", "preference"), direction, nulls),
                 new(SqlExpression.Column("c", "id"))]), "rank")
            .BuildQuery();
        var template = new SqlSelectBuilder(ranked, "winner")
            .Select(SqlExpression.Column("winner", "id"), "id")
            .Where(SqlExpression.Binary(SqlBinaryOperator.Equal,
                SqlExpression.Column("winner", "rank"), SqlExpression.Constant(1L)))
            .Where(SqlExpression.Binary(SqlBinaryOperator.Equal,
                SqlExpression.Column("winner", "eligible"), SqlExpression.RuntimeParameter("eligible")))
            .OrderBy(SqlExpression.Column("winner", "id"))
            .BuildTemplate(SqliteSqlDialect.Instance);
        var restored = JsonSerializer.Deserialize<SqlCommandTemplate>(JsonSerializer.Serialize(template))!;
        using var transaction = connection.BeginTransaction();
        using var scope = new SqliteCommandScope(file.Database, connection, transaction);
        var native = new SqliteCommandTemplate(restored);
        Assert.Equal([expected], Read(eligible: 1L));
        Assert.Equal([4L], Read(eligible: 0L));
        Assert.Equal([expected], Read(eligible: 1L));

        long[] Read(long eligible)
        {
            using var reader = scope.ExecuteReader(native, CancellationToken.None, ("eligible", eligible));
            List<long> ids = [];
            while (reader.Read()) ids.Add(reader.GetInt64(0));
            return [.. ids];
        }
    }

    [Fact]
    public void GlobalRowNumberRequiresOrderingAndAnExplicitDialectCapability()
    {
        var ordering = new SqlOrdering(SqlExpression.Constant(1L));
        Assert.Throws<ArgumentException>(() => SqlExpression.RowNumber([], []));
        Assert.Throws<ArgumentException>(() => SqlExpression.RowNumber([], default));
        Assert.Throws<ArgumentException>(() => SqlExpression.RowNumber([null!], [ordering]));
        Assert.Throws<ArgumentException>(() => SqlExpression.RowNumber([], [null!]));
        Assert.Throws<ArgumentNullException>(() => new SqlOrdering(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SqlOrdering(SqlExpression.Constant(1L), (SqlSortDirection)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SqlOrdering(SqlExpression.Constant(1L), nullPlacement: (SqlNullPlacement)99));
        var query = new SqlSelectBuilder().Select(SqlExpression.RowNumber(default, [ordering]), "rank");
        var error = Assert.Throws<SqlConstructionException>(() => query.BuildTemplate(new RejectFeatureDialect(SqlFeature.RowNumber)));
        Assert.Equal(nameof(SqlFeature.RowNumber), error.Construct);
        var statement = query.Build(SqliteSqlDialect.Instance);
        Assert.DoesNotContain("PARTITION BY", statement.Text, StringComparison.Ordinal);
        using var file = new DatabaseFixture();
        using var connection = file.Database.OpenConnection();
        using var command = file.Database.CreateCommand(connection, null, statement.Text);
        foreach (var parameter in statement.Parameters)
            command.Parameters.AddWithValue(parameter.Placeholder, parameter.Value ?? DBNull.Value);
        Assert.Equal(1L, command.ExecuteScalar());
    }

    [Fact]
    public void SharedMutationAndQueryBuildersExecuteWithExactBindingsAndEscapedNames()
    {
        using var file = new DatabaseFixture();
        using var connection = file.Database.OpenConnection();
        var name = new string('a', 90) + "\";$1";
        var table = new SqlQualifiedTable(name);
        using (var schema = file.Database.CreateCommand(connection, null,
            $"CREATE TABLE {new SqlIdentifier(name).ToSql(SqliteSqlDialect.Instance)} (\"id\" TEXT PRIMARY KEY, \"value\" INTEGER NOT NULL) STRICT;"))
            schema.ExecuteNonQuery();
        var insert = new SqlInsertBuilder(table)
            .Value("id", SqlExpression.RuntimeParameter("id"))
            .Value("value", SqlExpression.RuntimeParameter("value"))
            .OnConflictDoUpdate(["id"], ["value"])
            .Returning(SqlExpression.UnqualifiedColumn("value"), "value")
            .BuildTemplate(SqliteSqlDialect.Instance);
        Assert.Equal(5L, Execute(insert, new() { ["id"] = "x'; DROP TABLE anything; --", ["value"] = 5L }));
        Assert.Equal(7L, Execute(insert, new() { ["id"] = "x'; DROP TABLE anything; --", ["value"] = 7L }));
        var query = new SqlSelectBuilder(table, "row$1")
            .Select(SqlExpression.Column("row$1", "value"), "result$2")
            .Where(Match("id", "key"))
            .Where(Match("id", "key"))
            .Limit(2).BuildTemplate(SqliteSqlDialect.Instance);
        Assert.Single(query.Parameters);
        Assert.Equal(7L, Execute(query, new() { ["key"] = "x'; DROP TABLE anything; --" }));
        var update = new SqlUpdateBuilder(table).Set("value", SqlExpression.Constant(9L))
            .Where(Match("id", "id")).Returning(SqlExpression.UnqualifiedColumn("value"), "value")
            .BuildTemplate(SqliteSqlDialect.Instance);
        Assert.Equal(9L, Execute(update, new() { ["id"] = "x'; DROP TABLE anything; --" }));
        var delete = new SqlDeleteBuilder(table).Where(Match("id", "id"))
            .Returning(SqlExpression.UnqualifiedColumn("value"), "value").BuildTemplate(SqliteSqlDialect.Instance);
        Assert.Equal(9L, Execute(delete, new() { ["id"] = "x'; DROP TABLE anything; --" }));
        Assert.Null(Execute(query, new() { ["key"] = "x'; DROP TABLE anything; --" }));

        object? Execute(SqlCommandTemplate template, Dictionary<string, object?> parameters)
        {
            var statement = template.Bind(SqliteSqlDialect.Instance, parameters);
            using var command = file.Database.CreateCommand(connection, null, statement.Text);
            foreach (var parameter in statement.Parameters)
                command.Parameters.AddWithValue(parameter.Placeholder, parameter.Value ?? DBNull.Value);
            return command.ExecuteScalar();
        }
    }

    [Fact]
    public void DialectConstraintsAreExplicitAndSurviveTemplateSerialization()
    {
        var template = new SqlSelectBuilder().Select(SqlExpression.RuntimeParameter("value"), "value")
            .BuildTemplate(SqliteSqlDialect.Instance);
        var rehydrated = JsonSerializer.Deserialize<SqlCommandTemplate>(JsonSerializer.Serialize(template))!;
        Assert.Equal(template.Dialect, rehydrated.Dialect);
        Assert.Equal(template.Text, rehydrated.Bind(SqliteSqlDialect.Instance, new Dictionary<string, object?> { ["value"] = 5L }).Text);
        Assert.Throws<ArgumentException>(() => rehydrated.Bind(SqliteSqlDialect.Instance, new Dictionary<string, object?> { ["unknown"] = 5L }));
        Assert.Throws<ArgumentException>(() => rehydrated.Bind(SqliteSqlDialect.Instance, new Dictionary<string, object?> { ["value"] = 1m }));
        var otherDialect = new SqlCommandTemplate(template.Text, template.Parameters, "postgres/v1");
        Assert.Throws<ArgumentException>(() => otherDialect.Bind(SqliteSqlDialect.Instance, new Dictionary<string, object?> { ["value"] = 5L }));
        var error = Assert.Throws<SqlConstructionException>(() => new SqlSelectBuilder()
            .Select(SqlExpression.Intrinsic("unsupported.clock/v1"), "now").BuildTemplate(SqliteSqlDialect.Instance));
        Assert.Equal("sql.unsupported-construct", error.Code);
        Assert.Equal("unsupported.clock/v1", error.Construct);
        Assert.Equal(SqliteSqlDialect.Instance.Name, error.Dialect);
        Assert.Throws<SqlConstructionException>(() => new SqlSelectBuilder().Select(SqlExpression.Constant(1L), "one")
            .Offset(1).BuildTemplate(SqliteSqlDialect.Instance));
        Assert.Throws<SqlConstructionException>(() => new SqlSelectBuilder().Select(SqlExpression.EqualAny(SqlExpression.Constant(1L), "items"), "match")
            .BuildTemplate(SqliteSqlDialect.Instance));
        Assert.Throws<ArgumentException>(() => default(SqlIdentifier).ToSql(SqliteSqlDialect.Instance));
        Assert.Throws<ArgumentException>(() => new SqlCommandTemplate("SELECT $1, $2", [
            new(1, SqlParameterBindingKind.Runtime, "duplicate", constant: null),
            new(2, SqlParameterBindingKind.Runtime, "duplicate", constant: null)], SqliteSqlDialect.Instance.Name));
    }

    [Theory]
    [InlineData(SqlBinaryOperator.IsDistinctFrom, 0L)]
    [InlineData(SqlBinaryOperator.IsNotDistinctFrom, 1L)]
    public void DistinctComparisonsRequireCapabilityAndExecuteWithSqliteNullSemantics(SqlBinaryOperator comparison, long expected)
    {
        var query = new SqlSelectBuilder().Select(
            SqlExpression.Binary(comparison, SqlExpression.Constant(null), SqlExpression.Constant(null)), "matches");
        var error = Assert.Throws<SqlConstructionException>(() => query.BuildTemplate(new RejectFeatureDialect(SqlFeature.DistinctComparison)));
        Assert.Equal(nameof(SqlFeature.DistinctComparison), error.Construct);
        using var file = new DatabaseFixture();
        using var connection = file.Database.OpenConnection();
        var statement = query.BuildTemplate(SqliteSqlDialect.Instance).Bind(SqliteSqlDialect.Instance);
        using var command = file.Database.CreateCommand(connection, null, statement.Text);
        foreach (var parameter in statement.Parameters)
            command.Parameters.AddWithValue(parameter.Placeholder, parameter.Value ?? DBNull.Value);
        Assert.Equal(expected, command.ExecuteScalar());
    }

    [Theory]
    [InlineData(SqlJoinKind.Right, SqlFeature.RightJoin, 1)]
    [InlineData(SqlJoinKind.Full, SqlFeature.FullJoin, 2)]
    public void OuterJoinsRequireCapabilityAndPreserveUnmatchedRows(SqlJoinKind kind, SqlFeature feature, int expectedRows)
    {
        var left = new SqlSelectBuilder().Select(SqlExpression.Constant(1L), "id").BuildQuery();
        var right = new SqlSelectBuilder().Select(SqlExpression.Constant(2L), "id").BuildQuery();
        var query = new SqlSelectBuilder(left, "l")
            .Select(SqlExpression.Column("l", "id"), "left")
            .Select(SqlExpression.Column("r", "id"), "right")
            .Join(right, "r", kind, SqlExpression.Binary(SqlBinaryOperator.Equal,
                SqlExpression.Column("l", "id"), SqlExpression.Column("r", "id")));
        var error = Assert.Throws<SqlConstructionException>(() => query.BuildTemplate(new RejectFeatureDialect(feature)));
        Assert.Equal(feature.ToString(), error.Construct);
        using var file = new DatabaseFixture();
        using var connection = file.Database.OpenConnection();
        var statement = query.BuildTemplate(SqliteSqlDialect.Instance).Bind(SqliteSqlDialect.Instance);
        using var command = file.Database.CreateCommand(connection, null, statement.Text);
        foreach (var parameter in statement.Parameters)
            command.Parameters.AddWithValue(parameter.Placeholder, parameter.Value ?? DBNull.Value);
        using var reader = command.ExecuteReader();
        var count = 0;
        while (reader.Read())
        {
            Assert.True(reader.IsDBNull(0) != reader.IsDBNull(1));
            count++;
        }
        Assert.Equal(expectedRows, count);
    }

    [Fact]
    public void ScalarSubqueriesRequireAnExplicitDialectCapability()
    {
        var child = new SqlSelectBuilder().Select(SqlExpression.Constant(1L), "one").Limit(1).BuildQuery();
        var query = new SqlSelectBuilder().Select(SqlExpression.ScalarSubquery(child), "value");
        var error = Assert.Throws<SqlConstructionException>(() =>
            query.BuildTemplate(new RejectFeatureDialect(SqlFeature.ScalarSubquery)));
        Assert.Equal(nameof(SqlFeature.ScalarSubquery), error.Construct);
    }

    sealed class RejectFeatureDialect(SqlFeature rejected) : SqlDialect
    {
        public override string Name => "restricted/v1";
        public override void ValidateIdentifier(SqlIdentifier identifier) => SqliteSqlDialect.Instance.ValidateIdentifier(identifier);
        public override void ValidateParameter(object? value) => SqliteSqlDialect.Instance.ValidateParameter(value);
        public override string FunctionName(SqlFunction function) => SqliteSqlDialect.Instance.FunctionName(function);
        public override string FunctionName(SqlAggregateFunction function) => SqliteSqlDialect.Instance.FunctionName(function);
        public override void Require(SqlFeature feature)
        {
            if (feature == rejected) throw new SqlConstructionException(Name, feature.ToString(), "Select another target.");
            SqliteSqlDialect.Instance.Require(feature);
        }
    }

    static SqlExpression Match(string column, string binding) => SqlExpression.Binary(SqlBinaryOperator.Equal,
        SqlExpression.UnqualifiedColumn(column), SqlExpression.RuntimeParameter(binding));
}
