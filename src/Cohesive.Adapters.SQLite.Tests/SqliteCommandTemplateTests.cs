using Cohesive.Adapters.Sql;
using Cohesive.Adapters.SQLite;

namespace Cohesive.Adapters.SQLite.Tests;

public sealed class SqliteCommandTemplateTests
{
    [Fact]
    public void SharedTemplateBindsReorderedValuesAndRepeatedReferencesWithoutCopyingPayloads()
    {
        var plan = new SqliteCommandTemplate(new SqlSelectBuilder()
            .Select(SqlExpression.RuntimeParameter("payload"), "first")
            .Select(SqlExpression.RuntimeParameter("payload"), "second")
            .Select(SqlExpression.RuntimeParameter("count"), "count")
            .BuildTemplate(SqliteSqlDialect.Instance));
        using var fixture = new DatabaseFixture();
        using var connection = fixture.Database.OpenConnection();
        byte[] bytes = [0, 1, 255];
        using var command = fixture.Database.CreateCommand(connection, null, plan, ("count", 42L), ("payload", bytes));
        Assert.Equal(2, command.Parameters.Count);
        Assert.Same(bytes, command.Parameters[0].Value);
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(bytes, (byte[])reader.GetValue(0));
        Assert.Equal(bytes, (byte[])reader.GetValue(1));
        Assert.Equal(42L, reader.GetInt64(2));
    }

    [Fact]
    public void CapturedBytesAndProviderParametersAreIsolatedAcrossBindings()
    {
        var plan = new SqliteCommandTemplate(new SqlSelectBuilder()
            .Select(SqlExpression.Constant(new byte[] { 1, 2 }), "value")
            .BuildTemplate(SqliteSqlDialect.Instance));
        using var fixture = new DatabaseFixture();
        using var connection = fixture.Database.OpenConnection();
        using var first = fixture.Database.CreateCommand(connection, null, plan);
        Assert.IsType<byte[]>(first.Parameters[0].Value)[0] = 99;
        using var second = fixture.Database.CreateCommand(connection, null, plan);
        Assert.NotSame(first.Parameters[0], second.Parameters[0]);
        Assert.Equal(new byte[] { 1, 2 }, (byte[])second.ExecuteScalar()!);
    }

    [Fact]
    public void BindingRejectsMissingUnknownDuplicateAndInexactValuesButAcceptsExplicitNull()
    {
        var plan = new SqliteCommandTemplate(new SqlSelectBuilder()
            .Select(SqlExpression.RuntimeParameter("a"), "a")
            .Select(SqlExpression.RuntimeParameter("b"), "b")
            .BuildTemplate(SqliteSqlDialect.Instance));
        using var fixture = new DatabaseFixture();
        using var connection = fixture.Database.OpenConnection();
        Assert.Throws<ArgumentException>(() => fixture.Database.CreateCommand(connection, null, plan, ("a", 1L)));
        Assert.Throws<ArgumentException>(() => fixture.Database.CreateCommand(connection, null, plan, ("a", 1L), ("x", 2L)));
        Assert.Throws<ArgumentException>(() => fixture.Database.CreateCommand(connection, null, plan, ("a", 1L), ("a", 2L)));
        Assert.Throws<ArgumentException>(() => fixture.Database.CreateCommand(connection, null, plan, ("a", 1.25m), ("b", 2L)));
        Assert.Throws<ArgumentException>(() => fixture.Database.CreateCommand(connection, null, plan, ("a", "\ud800"), ("b", 2L)));
        using var command = fixture.Database.CreateCommand(connection, null, plan, ("a", null), ("b", 2L));
        Assert.Equal(DBNull.Value, command.ExecuteScalar());
    }

    [Fact]
    public void ScalarSubqueriesPreserveFirstNonNullBranchAndShareParameterSlots()
    {
        using var fixture = new DatabaseFixture();
        using var connection = fixture.Database.OpenConnection();
        using (var setup = fixture.Database.CreateCommand(connection, null,
            "CREATE TABLE samples (id INTEGER PRIMARY KEY, value TEXT); INSERT INTO samples VALUES (1, 'first'), (2, 'second');"))
            setup.ExecuteNonQuery();
        var first = new SqlSelectBuilder(new SqlQualifiedTable("samples"), "s")
            .Select(SqlExpression.Column("s", "value"), "value")
            .Where(SqlExpression.Binary(SqlBinaryOperator.Equal, SqlExpression.Column("s", "id"), SqlExpression.RuntimeParameter("id")))
            .Limit(1).BuildQuery();
        var second = new SqlSelectBuilder(new SqlQualifiedTable("samples"), "s")
            .Select(SqlExpression.Column("s", "value"), "value")
            .Where(SqlExpression.Binary(SqlBinaryOperator.Equal, SqlExpression.Column("s", "id"), SqlExpression.Constant(2L)))
            .Limit(1).BuildQuery();
        var plan = new SqliteCommandTemplate(new SqlSelectBuilder()
            .Select(SqlExpression.Coalesce(SqlExpression.ScalarSubquery(first), SqlExpression.ScalarSubquery(second)), "value")
            .BuildTemplate(SqliteSqlDialect.Instance));
        using var match = fixture.Database.CreateCommand(connection, null, plan, ("id", 1L));
        Assert.Equal("first", match.ExecuteScalar());
        using var absent = fixture.Database.CreateCommand(connection, null, plan, ("id", 99L));
        Assert.Equal("second", absent.ExecuteScalar());
        Assert.Throws<ArgumentException>(() => SqlExpression.ScalarSubquery(new SqlSelectBuilder()
            .Select(SqlExpression.Constant(1L), "one").BuildQuery()));
        Assert.Throws<ArgumentException>(() => SqlExpression.ScalarSubquery(new SqlSelectBuilder()
            .Select(SqlExpression.Constant(1L), "one").Select(SqlExpression.Constant(2L), "two").Limit(1).BuildQuery()));
    }
}
