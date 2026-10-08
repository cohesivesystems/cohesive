using AspireFirst.Orders;
using Cohesive.Adapters.Postgres;
using Cohesive.Api.Execution.Services;
using Cohesive.Execution;
using Cohesive.Storage;
using Cohesive.Storage.Processes;
using Npgsql;

namespace Cohesive.Adapters.Aspire.Tests;

public sealed class FulfillmentHostSetupTests
{
    [Fact]
    public void Hosting_requires_receipt_capability_before_build_or_io()
    {
        using var database = NpgsqlDataSource.Create("Host=localhost;Port=1;Database=unused;Username=unused");
        var inventory = FulfillmentStorage.Bind(database).Repository(FulfillmentDomain.Inventory);
        var failure = Assert.Throws<InvalidOperationException>(() => Service.Define(new("test"), new("1"), FulfillmentProcess.Provenance)
            .Operation("fulfill").Run(FulfillmentProcess.Definition, bindings => bindings.Transition(InventoryTransitions.Reserve, inventory)));
        Assert.Contains("atomic state/receipt", failure.Message);
    }

    [Fact]
    public void Duplicate_transition_registration_fails_without_changing_host_associations()
    {
        var inventory = new TypedEntityRepository<InventoryItem>(new InMemoryEntityOutboxRepository(
            FulfillmentDomain.Inventory.Definition, EntityPartitionKeyPolicy.FromField(FulfillmentDomain.PartitionField)), item => item.Sku);
        Assert.Throws<ArgumentException>(() => Service.Define(new("test"), new("1"), FulfillmentProcess.Provenance)
            .Operation("fulfill").Run(FulfillmentProcess.Definition, bindings => bindings
                .Transition(InventoryTransitions.Reserve, inventory).Transition(InventoryTransitions.Reserve, inventory)));
    }
}
