using System.Collections.Immutable;
using System.Text.Json;
using Cohesive.Adapters.Sql;
using Microsoft.Data.Sqlite;

namespace Cohesive.Adapters.SQLite.Tests;

public sealed class SqliteSqlConstructionTests
{
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
