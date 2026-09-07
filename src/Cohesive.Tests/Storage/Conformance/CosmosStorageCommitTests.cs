using System.Text.Json;
using Cohesive.Adapters.Cosmos;
using Cohesive.Execution;
using Cohesive.Storage.Commits;
using Microsoft.Azure.Cosmos;

namespace Cohesive.Tests.Storage.Conformance;

public sealed class CosmosStorageCommitTests
{
    const string ConnectionVariable = "COSMOS_STORAGE_COMMIT_CONNECTION_STRING";

    [CosmosTheory]
    [MemberData(nameof(StorageCommitConformance.Cases), MemberType = typeof(StorageCommitConformance))]
    public async Task Conforms(StorageCommitProbe probe)
    {
        using var client = CreateClient();
        var database = (await client.CreateDatabaseAsync("cohesive-commit-" + Guid.NewGuid().ToString("N"))).Database;
        try
        {
            var container = (await database.CreateContainerAsync(new ContainerProperties("items", "/partitionKey"))).Container;
            var executor = await CosmosStorageCommitExecutor.CreateAsync(client, database.Id, container.Id, StorageCommitConformance.Target);
            await StorageCommitConformance.Verify(executor,
                address => executor.ReadAsync(StorageCommitConformance.Context, address), CountActive, probe);
            async Task<int> CountActive()
            {
                using var iterator = container.GetItemQueryStreamIterator(
                    new QueryDefinition("SELECT c.payload FROM c WHERE STARTSWITH(c.id, @prefix)")
                        .WithParameter("@prefix", "cohesive-commit-v1-item-"),
                    requestOptions: new QueryRequestOptions
                    {
                        PartitionKey = new PartitionKey(StorageCommitConformance.Partition),
                        ConsistencyLevel = executor.Capabilities.SupportsQueryGuards ? ConsistencyLevel.Strong : null
                    });
                var count = 0;
                while (iterator.HasMoreResults)
                {
                    using var response = await iterator.ReadNextAsync();
                    response.EnsureSuccessStatusCode();
                    using var document = await JsonDocument.ParseAsync(response.Content);
                    foreach (var item in document.RootElement.GetProperty("Documents").EnumerateArray())
                        if (item.GetProperty("payload").Deserialize<PortableValue>(StorageCommitJson.CreateOptions()) == StorageCommitConformance.Value("active")) count++;
                }
                return count;
            }
        }
        finally { await database.DeleteAsync(); }
    }

    [CosmosFact]
    public async Task PreflightLimitsAndRestartReconciliation()
    {
        using var client = CreateClient();
        var database = (await client.CreateDatabaseAsync("cohesive-commit-limits-" + Guid.NewGuid().ToString("N"))).Database;
        try
        {
            var container = (await database.CreateContainerAsync(new ContainerProperties("items", "/partitionKey"))).Container;
            var executor = await CosmosStorageCommitExecutor.CreateAsync(client, database.Id, container.Id, StorageCommitConformance.Target);
            var tooMany = StorageCommitConformance.Intent("too-many",
                [.. Enumerable.Range(0, 100).Select(index => new StorageCommitWrite(StorageCommitConformance.Address($"item-{index}"), StorageCommitConformance.Value("v")))]);
            Assert.Equal(StorageCommitDisposition.Unsupported, (await executor.CommitAsync(StorageCommitConformance.Context, tooMany)).Disposition);
            Assert.Null(await executor.ReadAsync(StorageCommitConformance.Context, tooMany.Writes[0].Address));
            var tooLarge = StorageCommitConformance.Intent("too-large", new StorageCommitWrite(StorageCommitConformance.Address("large"),
                StorageCommitConformance.Value(new string('x', CosmosStorageCommitExecutor.MaxSerializedDocumentBytes))));
            Assert.Equal(StorageCommitDisposition.Unsupported, (await executor.CommitAsync(StorageCommitConformance.Context, tooLarge)).Disposition);
            Assert.Null(await executor.ReadAsync(StorageCommitConformance.Context, tooLarge.Writes[0].Address));
            var intent = StorageCommitConformance.Intent("before-restart", new StorageCommitWrite(StorageCommitConformance.Address("restart"), StorageCommitConformance.Value("first")));
            var committed = await executor.CommitAsync(StorageCommitConformance.Context, intent);
            Assert.Equal(StorageCommitDisposition.Committed, committed.Disposition);
            // A different client starts without the first client's session cache.
            using var reopenedClient = CreateClient();
            var reopened = await CosmosStorageCommitExecutor.CreateAsync(reopenedClient, database.Id, container.Id, StorageCommitConformance.Target);
            var restored = StorageCommitJson.Deserialize(StorageCommitJson.Serialize(intent));
            Assert.Equal(committed.Receipt, (await reopened.ReconcileAsync(StorageCommitConformance.Context, restored.Reference)).Receipt);
            Assert.Equal(committed.Receipt, (await reopened.CommitAsync(StorageCommitConformance.Context, restored)).Receipt);
        }
        finally { await database.DeleteAsync(); }
    }

    [CosmosTheory]
    [InlineData("/other", null, false)]
    [InlineData("/partitionKey", 60, false)]
    [InlineData("/partitionKey", null, true)]
    public async Task RejectsIncompatibleNativeContainerProfile(string partitionPath, int? ttl, bool uniqueKey)
    {
        using var client = CreateClient();
        var database = (await client.CreateDatabaseAsync("cohesive-commit-profile-" + Guid.NewGuid().ToString("N"))).Database;
        try
        {
            var properties = new ContainerProperties("items", partitionPath) { DefaultTimeToLive = ttl };
            if (uniqueKey)
            {
                var key = new UniqueKey();
                key.Paths.Add("/external");
                properties.UniqueKeyPolicy.UniqueKeys.Add(key);
            }
            var container = (await database.CreateContainerAsync(properties)).Container;
            await Assert.ThrowsAsync<NotSupportedException>(() => CosmosStorageCommitExecutor.CreateAsync(
                client, database.Id, container.Id, StorageCommitConformance.Target));
        }
        finally { await database.DeleteAsync(); }
    }

    sealed class CosmosFactAttribute : FactAttribute
    {
        public CosmosFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionVariable)))
                Skip = $"Set {ConnectionVariable} to run native Cosmos commit conformance.";
        }
    }

    static CosmosClient CreateClient() => new(Environment.GetEnvironmentVariable(ConnectionVariable), new CosmosClientOptions
    {
        ConnectionMode = ConnectionMode.Gateway,
        HttpClientFactory = static () => new HttpClient(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = static (request, _, _, errors) =>
                errors == System.Net.Security.SslPolicyErrors.None || request.RequestUri?.IsLoopback == true
        })
    });

    sealed class CosmosTheoryAttribute : TheoryAttribute
    {
        public CosmosTheoryAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionVariable)))
                Skip = $"Set {ConnectionVariable} to run native Cosmos commit conformance.";
        }
    }
}
