using Cohesive.Adapters.Cosmos;

namespace Cohesive.Tests.Cosmos;

public sealed class CosmosClientFactoryTests
{
    const string EmulatorMasterKey = "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==";

    [Fact]
    public void CreateCosmosClient_WithAccountKeyAndDefaultCredentialEnabled_PrefersAccountKeyPath()
    {
        var client = CosmosClientFactory.Shared.CreateCosmosClient(new()
        {
            Endpoint = "https://localhost:8081/",
            AccountKey = EmulatorMasterKey,
            AllowInsecureServerCertificate = true,
            UseDefaultCredential = true
        });

        Assert.NotNull(client);
    }

    [Fact]
    public void CreateCosmosClient_WithAccountKeyAndDefaultCredentialDisabled_UsesAccountKeyPath()
    {
        var client = CosmosClientFactory.Shared.CreateCosmosClient(new()
        {
            Endpoint = "https://localhost:8081/",
            AccountKey = EmulatorMasterKey,
            AllowInsecureServerCertificate = true,
            UseDefaultCredential = false
        });

        Assert.NotNull(client);
    }

    [Fact]
    public void CreateCosmosClient_WithoutAccountKeyOrCredential_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => CosmosClientFactory.Shared.CreateCosmosClient(new()
        {
            Endpoint = "https://localhost:8081/",
            UseDefaultCredential = false
        }));

        Assert.Contains("Cosmos endpoint is not configured", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
