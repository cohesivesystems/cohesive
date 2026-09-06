using Cohesive.Adapters.Sql;
using Microsoft.Data.Sqlite;

namespace Cohesive.Adapters.SQLite.Tests;

public sealed class SqlitePublicationConformanceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LateFailureRollsBackPayloadPublicationAndCheckpoint(bool duplicatePublication)
    {
        using var file = new DatabaseFixture();
        var store = new PublicationStore(file.Database);
        store.Initialize();
        using (var connection = file.Database.OpenConnection())
        using (var transaction = connection.BeginTransaction(deferred: false))
        {
            if (duplicatePublication)
                Assert.Throws<SqliteException>(() => store.Publish(connection, transaction, "new-payload", "initial", expectedVersion: 0));
            else
                Assert.Throws<InvalidOperationException>(() => store.Publish(connection, transaction, "new-payload", "new-publication", expectedVersion: 99));
            transaction.Rollback();
        }
        using var reader = new SqliteDatabase(new(file.Path)).OpenConnection();
        Assert.Equal(("initial", "initial", 0L), store.Read(reader));
        Assert.Equal(1L, store.Count(reader, "payloads"));
        Assert.Equal(1L, store.Count(reader, "publications"));
    }

    [Fact]
    public void WalReaderSeesOneCommittedPublicationAndKeepsItsSnapshotUntilRestart()
    {
        using var file = new DatabaseFixture();
        var store = new PublicationStore(file.Database);
        store.Initialize();
        using var writer = file.Database.OpenConnection();
        using var reader = new SqliteDatabase(new(file.Path)).OpenConnection();
        using var readTransaction = reader.BeginTransaction(deferred: true);
        Assert.Equal(("initial", "initial", 0L), store.Read(reader, readTransaction));
        using (var writeTransaction = writer.BeginTransaction(deferred: false))
        {
            store.Publish(writer, writeTransaction, "next-payload", "next-publication", expectedVersion: 0);
            Assert.Equal(("initial", "initial", 0L), store.Read(reader, readTransaction));
            using var independent = file.Database.OpenConnection();
            Assert.Equal(("initial", "initial", 0L), store.Read(independent));
            writeTransaction.Commit();
        }
        Assert.Equal(("initial", "initial", 0L), store.Read(reader, readTransaction));
        readTransaction.Commit();
        Assert.Equal(("next-publication", "next-payload", 1L), store.Read(reader));
        Assert.Equal(2L, store.Count(reader, "payloads"));
        Assert.Equal(2L, store.Count(reader, "publications"));
    }

    // Application-owned semantics with one borrowed SQLite transaction spanning three concrete tables.
    // No generic entity repository or new temporal model is required.
    sealed class PublicationStore(SqliteDatabase database)
    {
        static readonly SqliteSqlDialect Dialect = SqliteSqlDialect.Instance;

        internal void Initialize()
        {
            new SqliteSchema("adoption/publications", [new(1,
            [
                "CREATE TABLE payloads (id TEXT PRIMARY KEY, content BLOB NOT NULL) STRICT",
                "CREATE TABLE publications (id TEXT PRIMARY KEY, payload TEXT NOT NULL REFERENCES payloads(id)) STRICT",
                "CREATE TABLE checkpoints (id TEXT PRIMARY KEY, publication TEXT NOT NULL REFERENCES publications(id), version INTEGER NOT NULL) STRICT"
            ])]).Apply(database);
            using var connection = database.OpenConnection();
            using var transaction = connection.BeginTransaction(deferred: false);
            Insert(connection, transaction, "payloads", ("id", "initial"), ("content", new byte[] { 0, 255 }));
            Insert(connection, transaction, "publications", ("id", "initial"), ("payload", "initial"));
            Insert(connection, transaction, "checkpoints", ("id", "stream"), ("publication", "initial"), ("version", 0L));
            transaction.Commit();
        }

        internal void Publish(SqliteConnection connection, SqliteTransaction transaction, string payload, string publication, long expectedVersion)
        {
            Insert(connection, transaction, "payloads", ("id", payload), ("content", new byte[] { 1, 127, 255 }));
            Insert(connection, transaction, "publications", ("id", publication), ("payload", payload));
            var update = new SqlUpdateBuilder(new("checkpoints"))
                .Set("publication", SqlExpression.Constant(publication)).Set("version", SqlExpression.Constant(expectedVersion + 1))
                .Where(SqliteProofCommands.Equal("id", "stream")).Where(SqliteProofCommands.Equal("version", expectedVersion));
            using var command = SqliteProofCommands.Command(database, connection, transaction, update.BuildTemplate(Dialect).Bind(Dialect));
            if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("Checkpoint CAS failed; the caller must roll back the publication.");
        }

        internal (string Publication, string Payload, long Version) Read(SqliteConnection connection, SqliteTransaction? transaction = null)
        {
            var query = new SqlSelectBuilder(new SqlQualifiedTable("checkpoints"), "c")
                .Join(new SqlQualifiedTable("publications"), "p", SqlJoinKind.Inner, SqlExpression.Binary(SqlBinaryOperator.Equal,
                    SqlExpression.Column("c", "publication"), SqlExpression.Column("p", "id")))
                .Join(new SqlQualifiedTable("payloads"), "b", SqlJoinKind.Inner, SqlExpression.Binary(SqlBinaryOperator.Equal,
                    SqlExpression.Column("p", "payload"), SqlExpression.Column("b", "id")))
                .Select(SqlExpression.Column("p", "id"), "publication").Select(SqlExpression.Column("b", "id"), "payload")
                .Select(SqlExpression.Column("c", "version"), "version");
            using var command = SqliteProofCommands.Command(database, connection, transaction, query.Build(Dialect));
            using var reader = command.ExecuteReader();
            Assert.True(reader.Read());
            var result = (reader.GetString(0), reader.GetString(1), reader.GetInt64(2));
            Assert.False(reader.Read());
            return result;
        }

        internal long Count(SqliteConnection connection, string table)
        {
            var query = new SqlSelectBuilder(new SqlQualifiedTable(table), "r").Select(SqlExpression.UnqualifiedColumn("id"), "id");
            using var command = SqliteProofCommands.Command(database, connection, null, query.Build(Dialect));
            using var reader = command.ExecuteReader();
            long count = 0;
            while (reader.Read()) count++;
            return count;
        }

        void Insert(SqliteConnection connection, SqliteTransaction transaction, string table, params (string Column, object Value)[] values)
        {
            var insert = new SqlInsertBuilder(new(table));
            foreach (var (column, value) in values) insert.Value(column, SqlExpression.Constant(value));
            using var command = SqliteProofCommands.Command(database, connection, transaction, insert.BuildTemplate(Dialect).Bind(Dialect));
            command.ExecuteNonQuery();
        }
    }
}
