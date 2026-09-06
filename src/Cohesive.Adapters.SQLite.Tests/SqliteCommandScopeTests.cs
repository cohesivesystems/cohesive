using System.Data;
using Cohesive.Adapters.Sql;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace Cohesive.Adapters.SQLite.Tests;

public sealed class SqliteCommandScopeTests
{
    static readonly SqliteCommandTemplate Insert = new(new SqlInsertBuilder(new("sample"))
        .Value("id", SqlExpression.RuntimeParameter("id"))
        .Value("payload", SqlExpression.RuntimeParameter("payload"))
        .Value("tag", SqlExpression.Constant(new byte[] { 9, 8 }))
        .BuildTemplate(SqliteSqlDialect.Instance));
    static readonly SqliteCommandTemplate Read = new(new SqlSelectBuilder(new SqlQualifiedTable("sample"), "s")
        .Select(SqlExpression.Column("s", "payload"), "payload")
        .Select(SqlExpression.Column("s", "tag"), "tag")
        .Where(SqlExpression.Binary(SqlBinaryOperator.Equal, SqlExpression.Column("s", "id"), SqlExpression.RuntimeParameter("id")))
        .BuildTemplate(SqliteSqlDialect.Instance));

    [Fact]
    public void RepeatedWritesPrepareOnceAndRetainNullAndConstantSemantics()
    {
        using var fixture = new DatabaseFixture();
        using var connection = fixture.Database.OpenConnection();
        Schema(fixture.Database, connection);
        using var transaction = connection.BeginTransaction(deferred: false);
        var preparations = 0;
        strdelegate_authorizer authorize = (_, operation, _, _, _, _) =>
        {
            if (operation == raw.SQLITE_INSERT) preparations++;
            return raw.SQLITE_OK;
        };
        raw.sqlite3_set_authorizer(connection.Handle, authorize, null!);
        try
        {
            using (var scope = new SqliteCommandScope(fixture.Database, connection, transaction))
            {
                for (var id = 0; id < 8; id++)
                {
                    object? value = id % 2 == 0 ? new byte[] { (byte)id } : null;
                    Assert.Equal(1, scope.ExecuteNonQuery(Insert, default, ("payload", value), ("id", id)));
                    using var reader = scope.ExecuteReader(Read, default, ("id", id));
                    Assert.True(reader.Read());
                    Assert.Equal(value ?? DBNull.Value, reader.GetValue(0));
                    Assert.Equal(new byte[] { 9, 8 }, (byte[])reader.GetValue(1));
                }
                Assert.Equal(1, preparations); // SQLite's authorizer runs when SQL is prepared, not for each execution.
            }
            for (var id = 8; id < 16; id++)
            {
                using var command = fixture.Database.CreateCommand(connection, transaction, Insert,
                    ("id", id), ("payload", null));
                command.ExecuteNonQuery();
            }
            Assert.Equal(9, preparations);
        }
        finally { raw.sqlite3_set_authorizer(connection.Handle, (strdelegate_authorizer)null!, null!); }
        transaction.Rollback();
        using var count = fixture.Database.CreateCommand(connection, null, "SELECT count(*) FROM sample;");
        Assert.Equal(0L, count.ExecuteScalar());
    }

    [Fact]
    public void RejectedOrCanceledRowsNeverExecuteAndLaterCompleteBindingsRecover()
    {
        using var fixture = new DatabaseFixture();
        using var connection = fixture.Database.OpenConnection();
        Schema(fixture.Database, connection);
        using var transaction = connection.BeginTransaction(deferred: false);
        using var scope = new SqliteCommandScope(fixture.Database, connection, transaction);
        scope.ExecuteNonQuery(Insert, default, ("id", 1L), ("payload", "first"));
        Assert.Throws<ArgumentException>(() => scope.ExecuteNonQuery(Insert, default, ("id", 2L)));
        Assert.Throws<ArgumentException>(() => scope.ExecuteNonQuery(Insert, default, ("id", 2L), ("payload", 1.25m)));
        Assert.Throws<ArgumentException>(() => scope.ExecuteNonQuery(Insert, default, ("id", 2L), ("payload", "\ud800")));
        Assert.Throws<ArgumentException>(() => scope.ExecuteNonQuery(Insert, default, ("id", 2L), ("id", 3L)));
        Assert.Throws<ArgumentException>(() => scope.ExecuteNonQuery(Insert, default, ("id", 2L), ("unknown", null)));
        Assert.Throws<OperationCanceledException>(() => scope.ExecuteNonQuery(Insert, new CancellationToken(true),
            ("id", 2L), ("payload", "canceled")));
        Assert.Null(scope.ExecuteScalar(Read, default, ("id", 2L)));
        Assert.Throws<SqliteException>(() => scope.ExecuteNonQuery(Insert, default, ("id", 1L), ("payload", "duplicate")));
        scope.ExecuteNonQuery(Insert, default, ("id", 2L), ("payload", null));
        Assert.Equal("first", scope.ExecuteScalar(Read, default, ("id", 1L)));
        Assert.Equal(DBNull.Value, scope.ExecuteScalar(Read, default, ("id", 2L)));
        scope.Dispose();
        transaction.Commit();
    }

    [Fact]
    public void ScopeClosesItsReaderAndCommandsWhilePreservingBorrowedTransactionOwnership()
    {
        using var fixture = new DatabaseFixture();
        using var connection = fixture.Database.OpenConnection();
        Schema(fixture.Database, connection);
        using var transaction = connection.BeginTransaction(deferred: false);
        using var scope = new SqliteCommandScope(fixture.Database, connection, transaction);
        scope.ExecuteNonQuery(Insert, default, ("id", 1), ("payload", "value"));
        using var reader = scope.ExecuteReader(Read, default, ("id", 1));
        Assert.Throws<InvalidOperationException>(() => scope.ExecuteScalar(Read, default, ("id", 1)));
        Assert.Throws<InvalidOperationException>(() => scope.ExecuteNonQuery(Insert, default, ("id", 2), ("payload", null)));
        scope.Dispose();
        Assert.True(reader.IsClosed);
        Assert.Equal(ConnectionState.Open, connection.State);
        Assert.Same(connection, transaction.Connection);
        Assert.Throws<ObjectDisposedException>(() => scope.ExecuteScalar(Read, default, ("id", 1)));
        using var count = fixture.Database.CreateCommand(connection, transaction, "SELECT count(*) FROM sample;");
        Assert.Equal(1L, count.ExecuteScalar());
        transaction.Rollback();
    }

    [Fact]
    public void ScopeCannotOutliveOrChangeItsBorrowedTransaction()
    {
        using var fixture = new DatabaseFixture();
        using var first = fixture.Database.OpenConnection();
        using var second = fixture.Database.OpenConnection();
        using var transaction = first.BeginTransaction(deferred: true);
        Assert.Throws<ArgumentException>(() => new SqliteCommandScope(fixture.Database, second, transaction));
        using var scope = new SqliteCommandScope(fixture.Database, first, transaction);
        transaction.Rollback();
        using var later = first.BeginTransaction(deferred: true);
        Assert.Throws<ArgumentException>(() => scope.ExecuteScalar(Read, default, ("id", 1L)));
    }

    [Fact]
    public async Task IndependentScopesShareOnlyTheImmutableTemplate()
    {
        using var fixture = new DatabaseFixture();
        var template = new SqliteCommandTemplate(new SqlSelectBuilder()
            .Select(SqlExpression.RuntimeParameter("value"), "value").BuildTemplate(SqliteSqlDialect.Instance));
        using (var initialize = fixture.Database.OpenConnection()) { }
        await Task.WhenAll(Enumerable.Range(0, 4).Select(index => Task.Run(() =>
        {
            using var connection = fixture.Database.OpenConnection();
            using var transaction = connection.BeginTransaction(deferred: true);
            using var scope = new SqliteCommandScope(fixture.Database, connection, transaction);
            for (var row = 0; row < 25; row++)
                Assert.Equal((long)(index * 100 + row), scope.ExecuteScalar(template, default, ("value", index * 100 + row)));
        })));
    }

    static void Schema(SqliteDatabase database, SqliteConnection connection)
    {
        using var command = database.CreateCommand(connection, null,
            "CREATE TABLE sample (id INTEGER PRIMARY KEY, payload ANY, tag BLOB NOT NULL) STRICT;");
        command.ExecuteNonQuery();
    }
}
