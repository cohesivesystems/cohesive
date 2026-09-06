using Cohesive.Adapters.Cosmos;
using Cohesive.ExecutionKernel.TestFixtures.Storage;
using Cohesive.Storage;
using Microsoft.Azure.Cosmos;

namespace Cohesive.Tests.Storage.Conformance;

public sealed class CosmosRepositoryConformanceTests
{
    const string ConnectionVariable = "COSMOS_ENTITY_TRANSITION_OPERATION_CONNECTION_STRING";

    [CosmosTheory]
    [MemberData(nameof(EntityRepositoryConformance.AllCases), MemberType = typeof(EntityRepositoryConformance))]
    public async Task Conforms(RepositoryProbe probe)
    {
        var serializerOptions = EntityStorageJson.CreateOptions();
        // Cosmos adds service-owned fields such as _rid and _ts to its outer item document.
        serializerOptions.UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Skip;
        using var client = new CosmosClient(Environment.GetEnvironmentVariable(ConnectionVariable), new CosmosClientOptions
        {
            ConnectionMode = ConnectionMode.Gateway,
            // Explicit storage-only profile: plain observation JSON cannot retain binary values or scalar kinds.
            // Relation bindings that expect raw scalar paths cannot consume these tagged observation bodies.
            Serializer = new CosmosSystemTextJsonSerializer(serializerOptions),
            // Local emulator certificates are self-signed. Remote test services retain normal validation.
            HttpClientFactory = static () => new HttpClient(new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = static (request, _, _, errors) =>
                    errors == System.Net.Security.SslPolicyErrors.None || request.RequestUri?.IsLoopback == true
            })
        });
        var database = (await client.CreateDatabaseAsync("cohesive-conformance-" + Guid.NewGuid().ToString("N"))).Database;
        try
        {
            var container = (await database.CreateContainerAsync(new ContainerProperties("entities", "/partitionKey"))).Container;
            var policy = new EntityPartitionKeyPolicy("adoption tenant",
                static (_, entity) => entity.Observation.GetField(nameof(RunControl.Tenant)).GetRequiredString(),
                static (_, _) => "tenant/a");
            var repository = new CosmosEntityOutboxRepository(RunControlFixture.Entity, container, partitionKeyPolicy: policy,
                itemIdSelector: static entity => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(entity.EntityId.Value))));
            await EntityRepositoryConformance.Verify(repository, probe);
        }
        finally { await database.DeleteAsync(); }
    }

    [Fact]
    public void DefaultProfileRejectsBinaryObservationBodies()
    {
        var serializer = new CosmosSystemTextJsonSerializer();
        Assert.Throws<InvalidOperationException>(() => serializer.ToStream(RunControlFixture.Write(RunControlFixture.Initial()).Entity));
    }

    sealed class CosmosTheoryAttribute : TheoryAttribute
    {
        public CosmosTheoryAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionVariable)))
                Skip = $"Set {ConnectionVariable} to run Cosmos repository conformance.";
        }
    }
}
