using System.Diagnostics;
using Cohesive.Adapters.SQLite;
using Cohesive.Model;
using Microsoft.Data.Sqlite;

namespace Cohesive.Adapters.SQLite.Tests;

public sealed class SqliteFoundationTests
{
    [Fact]
    public void ConnectionsExposeAndApplyTheirEffectiveProfile()
    {
        using var fixture = new DatabaseFixture();
        var defaults = fixture.Database.Options;
        Assert.Equal<string>([nameof(SqliteDatabaseOptions.Durability), nameof(SqliteDatabaseOptions.BusyTimeoutSeconds), nameof(SqliteDatabaseOptions.Pooling)], defaults.ConventionSuppliedSettings);
        Assert.Equal(SqliteConnectionPooling.Disabled, defaults.Pooling);
        Assert.Equal(1, defaults.MaximumConcurrentWriters);
        Assert.False(defaults.SupportsDistributedTransactions);
        Assert.EndsWith("full/v1", defaults.Target.CapabilityProfile);
        using var connection = fixture.Database.OpenConnection();
        Assert.True(Version.Parse((string)Read(fixture.Database, connection, "SELECT sqlite_version();")!) >= SqliteDatabase.MinimumEngineVersion);
        Assert.Equal("wal", Read(fixture.Database, connection, "PRAGMA journal_mode;"));
        Assert.Equal(1L, Read(fixture.Database, connection, "PRAGMA foreign_keys;"));
        Assert.Equal(2L, Read(fixture.Database, connection, "PRAGMA synchronous;"));
        Assert.Equal(5, connection.DefaultTimeout);

        var normal = new SqliteDatabase(new(fixture.Path, durability: SqliteDurability.Normal, busyTimeoutSeconds: 2,
            pooling: SqliteConnectionPooling.Disabled));
        using var another = normal.OpenConnection();
        Assert.Equal(1L, Read(normal, another, "PRAGMA synchronous;"));
        Assert.Empty(normal.Options.ConventionSuppliedSettings);
        Assert.Equal(2, another.DefaultTimeout);
    }

    [Fact]
    public void UnsupportedOperatingProfilesFailBeforeOpening()
    {
        Assert.Throws<ArgumentException>(() => new SqliteDatabaseOptions(":memory:"));
        Assert.Throws<ArgumentException>(() => new SqliteDatabaseOptions("file:memory?mode=memory"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SqliteDatabaseOptions("test.db", busyTimeoutSeconds: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SqliteDatabaseOptions("test.db", durability: (SqliteDurability)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SqliteDatabaseOptions("test.db", pooling: (SqliteConnectionPooling)99));
    }

    [Fact]
    public void PooledCheckoutRestoresProfileAndRollsBackAbandonedTransaction()
    {
        using var fixture = new DatabaseFixture();
        var database = new SqliteDatabase(new(fixture.Path, pooling: SqliteConnectionPooling.Enabled));
        try
        {
            SQLitePCL.sqlite3? handle;
            using (var first = database.OpenConnection())
            {
                handle = first.Handle;
                Execute(database, first, null, "CREATE TABLE sample (id INTEGER PRIMARY KEY);");
                Execute(database, first, null, "PRAGMA foreign_keys = OFF; PRAGMA synchronous = OFF;");
                var abandoned = first.BeginTransaction();
                Execute(database, first, abandoned, "INSERT INTO sample VALUES (1);");
                // Disposing the logical connection must roll back before returning the handle.
            }
            using (var second = database.OpenConnection())
            {
                Assert.Same(handle, second.Handle);
                Assert.Equal(0L, Read(database, second, "SELECT count(*) FROM sample;"));
                Assert.Equal(1L, Read(database, second, "PRAGMA foreign_keys;"));
                Assert.Equal(2L, Read(database, second, "PRAGMA synchronous;"));
                Assert.Equal("wal", Read(database, second, "PRAGMA journal_mode;"));
                Execute(database, second, null, "PRAGMA query_only = ON;");
            }
            using var third = database.OpenConnection();
            Assert.Equal(0L, Read(database, third, "PRAGMA query_only;"));
            using var transaction = third.BeginTransaction(deferred: false);
            Execute(database, third, transaction, "INSERT INTO sample VALUES (2);");
            transaction.Commit();
        }
        finally { database.ClearPool(); }
    }

    [Fact]
    public void PoolPolicyPreservesConcurrentOwnershipAndReleasesNativeHandlesExplicitly()
    {
        using var fixture = new DatabaseFixture();
        var database = new SqliteDatabase(new(fixture.Path, pooling: SqliteConnectionPooling.Enabled));
        using var first = database.OpenConnection();
        using var second = database.OpenConnection();
        Assert.NotSame(first.Handle, second.Handle);
        var firstHandle = first.Handle!;
        var secondHandle = second.Handle!;
        database.ClearPool();
        Assert.Equal(1L, Read(database, first, "SELECT 1;"));
        Assert.Equal(1L, Read(database, second, "SELECT 1;"));
        first.Dispose();
        second.Dispose();
        Assert.True(firstHandle.IsClosed);
        Assert.True(secondHandle.IsClosed);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => database.OpenConnection(canceled.Token));
        using var reopened = database.OpenConnection();
        Assert.NotSame(firstHandle, reopened.Handle);
        reopened.Dispose();
        database.ClearPool();
    }

    [Fact]
    public void PooledProfilesReapplyDurabilityWhenSharingAConnectionString()
    {
        using var fixture = new DatabaseFixture();
        var full = new SqliteDatabase(new(fixture.Path, pooling: SqliteConnectionPooling.Enabled));
        var normal = new SqliteDatabase(new(fixture.Path, durability: SqliteDurability.Normal,
            pooling: SqliteConnectionPooling.Enabled));
        try
        {
            using (var first = normal.OpenConnection())
                Assert.Equal(1L, Read(normal, first, "PRAGMA synchronous;"));
            using (var second = full.OpenConnection())
                Assert.Equal(2L, Read(full, second, "PRAGMA synchronous;"));
        }
        finally { full.ClearPool(); }
    }

    [Fact]
    public void InvalidTextCannotAliasModuleRevisionsOrTruncateCommands()
    {
        Assert.Throws<ArgumentException>(() => new SqliteSchema("module/\ud800", []));
        Assert.Throws<ArgumentException>(() => new SqliteMigration(1, ["CREATE TABLE \"\ud800\" (value);"]));
        Assert.Throws<ArgumentException>(() => SqliteDatabase.QuoteIdentifier("\ud800"));
        using var fixture = new DatabaseFixture();
        using var connection = fixture.Database.OpenConnection();
        Assert.Throws<ArgumentException>(() => fixture.Database.CreateCommand(connection, null, "SELECT 1;\0 SELECT 2;"));
    }

    [Fact]
    public void SchemaModulesCoexistWithoutTakingOverUserVersion()
    {
        using var fixture = new DatabaseFixture();
        using (var connection = fixture.Database.OpenConnection())
            Execute(fixture.Database, connection, null, "PRAGMA user_version = 7;");
        var core = new SqliteSchema("cohesive/entities", [new(1, ["CREATE TABLE entities (id TEXT PRIMARY KEY) STRICT;"])]);
        var market = new SqliteSchema("ito/market-data", [new(1, ["CREATE TABLE payloads (id TEXT PRIMARY KEY) STRICT;"])]);
        Assert.Equal(1, core.Apply(fixture.Database));
        Assert.Equal(1, market.Apply(fixture.Database));
        Assert.Equal(1, core.Apply(fixture.Database));
        using var read = fixture.Database.OpenConnection();
        Assert.Equal(7L, Read(fixture.Database, read, "PRAGMA user_version;"));
        Assert.Equal(2L, Read(fixture.Database, read, "SELECT count(*) FROM __cohesive_schema_migrations_v1;"));
    }

    [Fact]
    public async Task ConcurrentInitializationCommitsEachMigrationOnce()
    {
        using var fixture = new DatabaseFixture();
        SqliteSchema schema = new("concurrent", [new(1,
        ["CREATE TABLE applied (value INTEGER NOT NULL) STRICT;", "INSERT INTO applied VALUES (1);"])]);
        using var start = new ManualResetEventSlim();
        var tasks = Enumerable.Range(0, 4).Select(_ => Task.Run(() => { start.Wait(); return schema.Apply(fixture.Database); })).ToArray();
        start.Set();
        Assert.All(await Task.WhenAll(tasks), version => Assert.Equal(1, version));
        using var connection = fixture.Database.OpenConnection();
        Assert.Equal(1L, Read(fixture.Database, connection, "SELECT count(*) FROM applied;"));
        Assert.Equal(1L, Read(fixture.Database, connection, "SELECT count(*) FROM __cohesive_schema_migrations_v1;"));
    }

    [Fact]
    public void FailedMigrationRollsBackTheWholeSuffixAndKeepsPriorHistory()
    {
        using var fixture = new DatabaseFixture();
        SqliteMigration first = new(1, ["CREATE TABLE first (id INTEGER PRIMARY KEY) STRICT;"]);
        new SqliteSchema("rollback", [first]).Apply(fixture.Database);
        SqliteSchema failing = new("rollback",
        [first, new(2, ["CREATE TABLE second (id INTEGER PRIMARY KEY) STRICT;"]), new(3, ["INSERT INTO nonexistent VALUES (1);"])]);
        Assert.Throws<SqliteException>(() => failing.Apply(fixture.Database));
        using var connection = fixture.Database.OpenConnection();
        Assert.Equal(1L, Read(fixture.Database, connection, "SELECT max(version) FROM __cohesive_schema_migrations_v1;"));
        Assert.Equal(0L, Read(fixture.Database, connection, "SELECT count(*) FROM sqlite_schema WHERE name = 'second';"));
    }

    [Fact]
    public void FirstMigrationFailureLeavesNeitherSchemaNorHistory()
    {
        using var fixture = new DatabaseFixture();
        SqliteSchema schema = new("first-failure", [new(1,
            ["CREATE TABLE sample (id INTEGER PRIMARY KEY) STRICT;", "INSERT INTO missing VALUES (1);"])]);
        Assert.Throws<SqliteException>(() => schema.Apply(fixture.Database));
        using var connection = fixture.Database.OpenConnection();
        Assert.Equal(0L, Read(fixture.Database, connection,
            "SELECT count(*) FROM sqlite_schema WHERE name IN ('sample', '__cohesive_schema_migrations_v1');"));
    }

    [Fact]
    public void MigrationHistoryRejectsChangedVersionsGapsAndDowngrades()
    {
        using var fixture = new DatabaseFixture();
        SqliteMigration first = new(1, ["CREATE TABLE sample (id INTEGER PRIMARY KEY) STRICT;"]);
        SqliteMigration second = new(2, ["INSERT INTO sample VALUES (1);"]);
        new SqliteSchema("history", [first, second]).Apply(fixture.Database);
        var ahead = Assert.Throws<SqliteSchemaException>(() => new SqliteSchema("history", [first]).Apply(fixture.Database));
        Assert.Equal(SqliteSchemaFailure.AheadOfPlan, ahead.Failure);
        Assert.Equal(2, ahead.Version);
        var changed = Assert.Throws<SqliteSchemaException>(() => new SqliteSchema("history",
            [new(1, ["CREATE TABLE sample (id TEXT PRIMARY KEY) STRICT;"]), second]).Apply(fixture.Database));
        Assert.Equal(SqliteSchemaFailure.ChangedMigration, changed.Failure);
        using (var connection = fixture.Database.OpenConnection())
            Execute(fixture.Database, connection, null, "DELETE FROM __cohesive_schema_migrations_v1 WHERE version = 1;");
        var gap = Assert.Throws<SqliteSchemaException>(() => new SqliteSchema("history", [first, second]).Apply(fixture.Database));
        Assert.Equal(SqliteSchemaFailure.InvalidHistory, gap.Failure);
    }

    [Theory]
    [InlineData("COMMIT;")]
    [InlineData("-- comment\n BEGIN IMMEDIATE;")]
    [InlineData("PRAGMA synchronous = OFF;")]
    [InlineData("ATTACH 'other.db' AS other;")]
    [InlineData("CREATE TABLE example(id); COMMIT;")]
    [InlineData("CREATE TABLE example(id); /* comment */ INSERT INTO example VALUES (1);")]
    [InlineData("/* only a comment */")]
    [InlineData("INSERT INTO example VALUES ('unfinished);")]
    public void MigrationStatementsCannotEscapeTheOwnedTransaction(string sql) =>
        Assert.Throws<ArgumentException>(() => new SqliteMigration(1, [sql]));

    [Fact]
    public void StatementParsingPreservesQuotedValuesCommentsAndDeterministicRevisions()
    {
        using var fixture = new DatabaseFixture();
        const string statement = "/* intro */ INSERT INTO sample VALUES ('a; COMMIT; -- '' quoted'); -- tail";
        var first = new SqliteMigration(1, ["CREATE TABLE sample (value TEXT) STRICT;", statement]);
        Assert.Equal(first.Fingerprint, new SqliteMigration(1, first.Statements).Fingerprint);
        Assert.NotEqual(first.Fingerprint, new SqliteMigration(1, [first.Statements[0], statement + "\n"]).Fingerprint);
        new SqliteSchema("parser", [first]).Apply(fixture.Database);
        using var connection = fixture.Database.OpenConnection();
        Assert.Equal("a; COMMIT; -- ' quoted", Read(fixture.Database, connection, "SELECT value FROM sample;"));
    }

    [Fact]
    public void OnlineBackupRestoresDataAndModuleHistoryToAnotherFile()
    {
        using var source = new DatabaseFixture();
        using var snapshot = new DatabaseFixture();
        SqliteSchema schema = new("backup", [new(1,
            ["CREATE TABLE prices (id TEXT PRIMARY KEY, price TEXT NOT NULL) STRICT;", "INSERT INTO prices VALUES ('a', '123.4500');"])]);
        schema.Apply(source.Database);
        using (var sourceConnection = source.Database.OpenConnection())
        using (var snapshotConnection = snapshot.Database.OpenConnection())
            sourceConnection.BackupDatabase(snapshotConnection);

        // Reopen the standalone snapshot and reconcile the same immutable module revisions before using it.
        Assert.Equal(1, schema.Apply(snapshot.Database));
        using var restored = snapshot.Database.OpenConnection();
        Assert.Equal("123.4500", Read(snapshot.Database, restored, "SELECT price FROM prices WHERE id = 'a';"));
        Assert.Equal("ok", Read(snapshot.Database, restored, "PRAGMA integrity_check;"));
        using var foreignKeys = snapshot.Database.CreateCommand(restored, null, "PRAGMA foreign_key_check;");
        using var violations = foreignKeys.ExecuteReader();
        Assert.False(violations.Read());
    }

    [Fact]
    public void SpecializedPublicationSharesOneCallerOwnedTransaction()
    {
        using var fixture = new DatabaseFixture();
        new SqliteSchema("publication", [new(1,
        [
            "CREATE TABLE payloads (id TEXT PRIMARY KEY, bytes BLOB NOT NULL) STRICT;",
            "CREATE TABLE prices (payload_id TEXT REFERENCES payloads(id), price TEXT NOT NULL) STRICT;",
            "CREATE TABLE checkpoints (id TEXT PRIMARY KEY, version INTEGER NOT NULL) STRICT;",
            "INSERT INTO checkpoints VALUES ('feed', 0);"
        ])]).Apply(fixture.Database);
        using var connection = fixture.Database.OpenConnection();
        Assert.Throws<InvalidOperationException>(() => Publish(commit: false));
        Assert.Equal(0L, Read(fixture.Database, connection, "SELECT count(*) FROM payloads;"));
        Assert.Equal(0L, Read(fixture.Database, connection, "SELECT count(*) FROM prices;"));
        Assert.Equal(0L, Read(fixture.Database, connection, "SELECT version FROM checkpoints;"));
        Publish(commit: true);
        Assert.Equal(1L, Read(fixture.Database, connection, "SELECT count(*) FROM payloads;"));
        Assert.Equal(1L, Read(fixture.Database, connection, "SELECT version FROM checkpoints;"));
        Assert.Equal("123.4500", Read(fixture.Database, connection, "SELECT price FROM prices;"));

        void Publish(bool commit)
        {
            using var transaction = connection.BeginTransaction(deferred: false);
            using (var payload = fixture.Database.CreateCommand(connection, transaction,
                "INSERT INTO payloads VALUES ($id, $bytes);", new SqliteParameter("$id", "payload/1"),
                SqliteScalarCodec.CreateParameter("$bytes", new(new ScalarTypeRef(ScalarTypeKind.Bytes)), ObservationValue.FromBytes(new byte[] { 1, 2, 3 }))))
                payload.ExecuteNonQuery();
            using (var price = fixture.Database.CreateCommand(connection, transaction,
                "INSERT INTO prices VALUES ($id, $price);", new SqliteParameter("$id", "payload/1"),
                SqliteScalarCodec.CreateParameter("$price", new(new ScalarTypeRef(ScalarTypeKind.Decimal)), ObservationValue.FromDecimal(123.4500m))))
                price.ExecuteNonQuery();
            Execute(fixture.Database, connection, transaction, "UPDATE checkpoints SET version = 1 WHERE id = 'feed' AND version = 0;");
            if (!commit) throw new InvalidOperationException("Injected failure before commit.");
            transaction.Commit();
        }
    }

    [Fact]
    public void CommandsRejectForeignTransactionsAndQuoteIdentifiersWithoutInjection()
    {
        using var fixture = new DatabaseFixture();
        using var connection = fixture.Database.OpenConnection();
        using var other = fixture.Database.OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        Assert.Throws<ArgumentException>(() => fixture.Database.CreateCommand(other, transaction, "SELECT 1;"));
        const string name = "price\"; DROP TABLE prices; --";
        Execute(fixture.Database, connection, transaction, $"CREATE TABLE {SqliteDatabase.QuoteIdentifier(name)} (id INTEGER) STRICT;");
        using var query = fixture.Database.CreateCommand(connection, transaction, "SELECT count(*) FROM sqlite_schema WHERE name = $name;", new SqliteParameter("$name", name));
        Assert.Equal(1L, query.ExecuteScalar());
        transaction.Rollback();
    }

    [Fact]
    public void HeldWriterProducesABoundedBusyFailure()
    {
        using var fixture = new DatabaseFixture(timeoutSeconds: 1);
        using var holder = fixture.Database.OpenConnection();
        using var contender = fixture.Database.OpenConnection();
        using var transaction = holder.BeginTransaction(deferred: false);
        var elapsed = Stopwatch.StartNew();
        var error = Assert.Throws<SqliteException>(() => contender.BeginTransaction(deferred: false));
        Assert.True(error.SqliteErrorCode is 5 or 6);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(10), $"Busy failure exceeded the declared test bound: {elapsed.Elapsed}.");
    }

    [Fact]
    public void CancellationBeforeAcquisitionCreatesNoDatabase()
    {
        using var fixture = new DatabaseFixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => fixture.Database.OpenConnection(cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => new SqliteSchema("cancel", [new(1, ["CREATE TABLE canceled(id);"])]).Apply(fixture.Database, cancellation.Token));
        Assert.False(File.Exists(fixture.Path));
    }

    static object? Read(SqliteDatabase database, SqliteConnection connection, string sql)
    {
        using var command = database.CreateCommand(connection, null, sql);
        return command.ExecuteScalar();
    }

    static void Execute(SqliteDatabase database, SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        using var command = database.CreateCommand(connection, transaction, sql);
        command.ExecuteNonQuery();
    }
}

internal sealed class DatabaseFixture : IDisposable
{
    readonly string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cohesive-sqlite-" + Guid.NewGuid().ToString("N"));
    public DatabaseFixture(int? timeoutSeconds = null)
    {
        Directory.CreateDirectory(directory);
        Path = System.IO.Path.Combine(directory, "data.db");
        Database = new(new(Path, busyTimeoutSeconds: timeoutSeconds));
    }
    public string Path { get; }
    public SqliteDatabase Database { get; }
    public void Dispose() => Directory.Delete(directory, recursive: true);
}
