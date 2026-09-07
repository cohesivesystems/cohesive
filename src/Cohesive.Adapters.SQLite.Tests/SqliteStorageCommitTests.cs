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
