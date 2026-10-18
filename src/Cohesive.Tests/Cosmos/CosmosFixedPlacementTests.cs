using System.Text.Json.Serialization;
using Cohesive.Adapters.Cosmos;
using Cohesive.Prelude;
using Cohesive.Storage;
using Cohesive.Transitions.Authoring;
using Microsoft.Azure.Cosmos;
namespace Cohesive.Tests.Cosmos;

public sealed class CosmosFixedPlacementTests
{
    sealed record Item([property: JsonPropertyName("sku")] string Sku,
        [property: JsonPropertyName("partition")] string Partition);
    sealed class Model : DomainModel
    {
        public Model() : base(revision: "1") => Items = Entity<Item>("test/item");
        public DomainEntity<Item> Items { get; }
    }

    [Fact]
    public void Fixed_policy_checks_writes_and_exposes_the_same_partition_for_point_reads()
    {
        var model = new Model();
        var policy = EntityPartitionKeyPolicy.Fixed("local", "partition");
        var context = OperationContext.Create();
        Assert.Equal("local", policy.FixedPartitionKey);
        Assert.Equal("local", policy.TryResolvePointReadPartitionKey(context, "book"));
        Assert.Equal("local", policy.ResolveWritePartitionKey(context, model.Items.Definition.CreateState("book", new Item("book", "local"), 1).Snapshot));
        Assert.Throws<ArgumentException>(() => policy.ResolveWritePartitionKey(context,
            model.Items.Definition.CreateState("book", new Item("book", "outside"), 1).Snapshot));
        Assert.Null(EntityPartitionKeyPolicy.ObservationId.FixedPartitionKey);
        Assert.Throws<ArgumentException>(() => EntityPartitionKeyPolicy.Fixed(" local"));
    }

    [Fact]
    public void Cosmos_preserves_explicit_identity_and_fixed_partition_without_inventing_evidence_for_dynamic_policies()
    {
        var model = new Model();
        using var client = new CosmosClient("http://127.0.0.1:1", Convert.ToBase64String(new byte[64]),
            new CosmosClientOptions { ConnectionMode = ConnectionMode.Gateway });
        var container = client.GetContainer("test", "items");
        var repository = new CosmosEntityOutboxRepository(model.Items.Definition, container,
            partitionKeyPolicy: EntityPartitionKeyPolicy.Fixed("local", "partition"), identityField: "sku");
        Assert.Equal("sku", new TypedEntityRepository<Item>(repository).IdentityField);
        Assert.Equal("local", repository.TransitionOperationCapabilities.PartitionKey);
        Assert.True(repository.TransitionOperationCapabilities.SupportsAtomicStateAndReceipt);
        Assert.Null(new CosmosEntityOutboxRepository(model.Items.Definition, container).TransitionOperationCapabilities.PartitionKey);
        Assert.Throws<ArgumentException>(() => new CosmosEntityOutboxRepository(model.Items.Definition, container, identityField: "missing"));
    }
}
