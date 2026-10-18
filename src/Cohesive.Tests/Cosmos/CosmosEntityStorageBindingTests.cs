using System.Text.Json.Serialization;
using Cohesive.Adapters.Cosmos;
using Cohesive.Storage;
using Cohesive.Transitions.Authoring;
using Microsoft.Azure.Cosmos;

namespace Cohesive.Tests.Cosmos;

public sealed class CosmosEntityStorageBindingTests
{
    const string EmulatorKey = "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==";
    public sealed record Stock([property: JsonPropertyName("sku")] string Sku,
        [property: JsonPropertyName("partition")] string Partition);

    [Fact]
    public void Repository_and_acquisition_share_declared_identity_partition_and_container_without_IO()
    {
        var entity = new DomainModelBuilder().Entity<Stock>("test/stock");
        var binding = new CosmosEntityStorageBinding(entity.Definition, "sku", "account", "inventory", "stock",
            new("test/local"), "local", "partition", maximumEnumerationRows: 1000);
        using var client = new CosmosClient("http://127.0.0.1:1", EmulatorKey, new CosmosClientOptions { ConnectionMode = ConnectionMode.Gateway });
        var container = client.GetContainer("inventory", "stock");
        var bound = binding.Bind(container);
        var repository = Assert.IsType<CosmosEntityOutboxRepository>(bound.Repository);
        Assert.Same(entity.Definition, repository.EntityDefinition);
        Assert.Same(container, repository.Container);
        Assert.Equal("sku", repository.IdentityField);
        Assert.Equal(FieldPath.FromField("sku"), bound.Source.IdentitySemanticPath);
        Assert.Same(binding.Query.Binding.Source, bound.Source.Source);
        Assert.Equal(binding.Query.Binding.Descriptor, bound.Source.Descriptor);
        Assert.Equal(new PartitionKey("local"), binding.Query.Policy.FixedPartitionKey);
        Assert.Equal("local", repository.TransitionOperationCapabilities.PartitionKey);
        Assert.Equal("sku-1", EntityRepositoryMappingExtensions.PrepareIdentitySelector<Stock>(repository.IdentityField)(new("sku-1", "local")));
        Assert.Throws<ArgumentException>(() => binding.Bind(client.GetContainer("other", "stock")));
        Assert.Throws<ArgumentException>(() => binding.Bind(client.GetContainer("inventory", "other")));
    }

    [Theory]
    [InlineData("missing", "partition")]
    [InlineData("sku", "missing")]
    public void Invalid_canonical_fields_fail_at_declaration(string identity, string partition)
    {
        var entity = new DomainModelBuilder().Entity<Stock>("test/stock");
        Assert.Throws<ArgumentException>(() => new CosmosEntityStorageBinding(entity.Definition, identity,
            "account", "inventory", "stock", new("test/local"), "local", partition));
    }

    [Fact]
    public void Registration_rejects_a_repository_from_another_entity_or_identity()
    {
        var domain = new DomainModelBuilder();
        var entity = domain.Entity<Stock>("test/stock");
        var other = domain.Entity<Stock>("test/other");
        using var client = new CosmosClient("http://127.0.0.1:1", EmulatorKey, new CosmosClientOptions { ConnectionMode = ConnectionMode.Gateway });
        var container = client.GetContainer("inventory", "stock");
        var binding = new CosmosEntityStorageBinding(entity.Definition, "sku", "account", "inventory", "stock",
            new("test/local"), "local", "partition");
        var source = binding.Bind(container).Source;
        Assert.Throws<ArgumentException>(() => new EntityStorageRegistration(
            new CosmosEntityOutboxRepository(other.Definition, container, identityField: "sku"), source));
        Assert.Throws<ArgumentException>(() => new EntityStorageRegistration(
            new CosmosEntityOutboxRepository(entity.Definition, container, identityField: "partition"), source));
    }
}
