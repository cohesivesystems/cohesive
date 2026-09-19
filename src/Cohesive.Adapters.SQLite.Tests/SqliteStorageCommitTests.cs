using System.Text.Json;
using Cohesive.Adapters.SQLite;
using Cohesive.Execution;
using Cohesive.Storage.Commits;
using Cohesive.Tests.Storage.Conformance;

namespace Cohesive.Adapters.SQLite.Tests;

public sealed class SqliteStorageCommitTests
{
    [Theory]
    [MemberData(nameof(StorageCommitConformance.Cases), MemberType = typeof(StorageCommitConformance))]
    public async Task Conforms(StorageCommitProbe probe)
    {
        var path = Path.Combine(Path.GetTempPath(), $"cohesive-commit-{Guid.NewGuid():N}.db");
        var database = new SqliteDatabase(new(path, durability: SqliteDurability.Full));
        try
        {
            SqliteStorageCommitExecutor.Schema.Apply(database);
            var executor = new SqliteStorageCommitExecutor(database);
            await StorageCommitConformance.Verify(executor,
                address => executor.ReadAsync(StorageCommitConformance.Context, address), CountActive, probe);
        }
        finally
        {
            database.ClearPool();
            foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix);
        }
        Task<int> CountActive()
        {
            using var connection = database.OpenConnection();
            using var command = database.CreateCommand(connection, null,
                "SELECT payload FROM __cohesive_storage_commits_v1 WHERE kind = 'item'");
            using var reader = command.ExecuteReader();
            var count = 0;
            while (reader.Read())
                if (JsonSerializer.Deserialize<PortableValue>(reader.GetString(0), StorageCommitJson.CreateOptions()) == StorageCommitConformance.Value("active")) count++;
            return Task.FromResult(count);
        }
    }

    [Fact]
    public async Task ConfiguredRowLimitRejectsBeforeCommitAndIncludesOriginalReceipts()
    {
        using var file = new DatabaseFixture();
        var database = new SqliteDatabase(new(file.Path, durability: SqliteDurability.Full));
        SqliteStorageCommitExecutor.Schema.Apply(database);
        var executor = new SqliteStorageCommitExecutor(database, maximumStoredPayloadBytes: 64);
        var intent = StorageCommitConformance.Intent("bounded", new StorageCommitWrite(StorageCommitConformance.Address("state"), StorageCommitConformance.Value("value")));
        Assert.Equal("storage.commit.row-payload-limit", Assert.Single(executor.Validate(intent)!.Diagnostics).Code);
        Assert.Equal(StorageCommitDisposition.Unsupported, (await executor.CommitAsync(StorageCommitConformance.Context, intent)).Disposition);
        Assert.Null(await executor.ReadAsync(StorageCommitConformance.Context, StorageCommitConformance.Address("state")));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SqliteStorageCommitExecutor(database, maximumStoredPayloadBytes: 0));
    }

    [Fact]
    public async Task BoundedPayloadsAlsoReopenFromUtf16Databases()
    {
        using var file = new DatabaseFixture();
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + file.Path))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA encoding = 'UTF-16le'; CREATE TABLE encoding_marker (id INTEGER);";
            command.ExecuteNonQuery();
        }
        var database = new SqliteDatabase(new(file.Path, durability: SqliteDurability.Full));
        SqliteStorageCommitExecutor.Schema.Apply(database);
        var executor = new SqliteStorageCommitExecutor(database, maximumStoredPayloadBytes: 2048);
        var value = StorageCommitConformance.Value(new string('a', 1500));
        var address = StorageCommitConformance.Address("utf16");
        var intent = StorageCommitConformance.Intent("utf16", new StorageCommitWrite(address, value));
        Assert.Equal(StorageCommitDisposition.Committed, (await executor.CommitAsync(StorageCommitConformance.Context, intent)).Disposition);
        Assert.Equal(value, (await executor.ReadAsync(StorageCommitConformance.Context, address))!.Value);
    }

    [Fact]
    public async Task ReopenReconcilesReceiptWithoutChangingLaterState()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cohesive-reopen-{Guid.NewGuid():N}.db");
        var database = new SqliteDatabase(new(path, durability: SqliteDurability.Full));
        try
        {
            SqliteStorageCommitExecutor.Schema.Apply(database);
            var executor = new SqliteStorageCommitExecutor(database);
            var address = StorageCommitConformance.Address("state");
            var intent = StorageCommitConformance.Intent("operation", new StorageCommitWrite(address, StorageCommitConformance.Value("first")));
            var first = await executor.CommitAsync(StorageCommitConformance.Context, intent);
            await executor.CommitAsync(StorageCommitConformance.Context,
                StorageCommitConformance.Intent("later", new StorageCommitWrite(address, StorageCommitConformance.Value("later"), new(intent.Fingerprint))));
            database.ClearPool();
            var reopened = new SqliteStorageCommitExecutor(new(new(path, durability: SqliteDurability.Full)));
            var restored = StorageCommitJson.Deserialize(StorageCommitJson.Serialize(intent));
            Assert.Equal(first.Receipt, (await reopened.ReconcileAsync(StorageCommitConformance.Context, restored.Reference)).Receipt);
            Assert.Equal(first.Receipt, (await reopened.CommitAsync(StorageCommitConformance.Context, restored)).Receipt);
            Assert.Equal(StorageCommitConformance.Value("later"), (await reopened.ReadAsync(StorageCommitConformance.Context, address))!.Value);
        }
        finally
        {
            database.ClearPool();
            foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(path + suffix);
        }
    }
}
