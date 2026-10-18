using Cohesive.Host.Composition;
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
    static readonly AspireFirst.Orders.FulfillmentApplication Model = ModelActivator.Create<AspireFirst.Orders.FulfillmentApplication>();

    [Fact]
    public void Hosting_requires_receipt_capability_before_build_or_io()
    {
        using var database = NpgsqlDataSource.Create("Host=localhost;Port=1;Database=unused;Username=unused");
        var inventory = Model.Storage.Bindings.Bind(database, authority: "test/fulfillment").Repository(Model.Domain.Inventory);
        var failure = Assert.Throws<ServiceBindingValidationException>(() => Service.Define(new("test"), new("1"), Model.Process.Provenance)
            .Operation("fulfill").Run(Model.Process.Definition, bindings => bindings.Transition(Model.Domain.InventoryTransitions.Reserve, inventory)));
        Assert.Equal("services.binding.receiptCapabilityMissing", Assert.Single(failure.Validation.Diagnostics).Code);
    }

    [Fact]
    public void Duplicate_transition_registration_fails_without_changing_host_associations()
    {
        var inventory = new TypedEntityRepository<InventoryItem>(new InMemoryEntityOutboxRepository(
            Model.Domain.Inventory.Definition, EntityPartitionKeyPolicy.FromField(FulfillmentDomain.PartitionField)), item => item.Sku);
        Assert.Throws<ArgumentException>(() => Service.Define(new("test"), new("1"), Model.Process.Provenance)
            .Operation("fulfill").Run(Model.Process.Definition, bindings => bindings
                .Transition(Model.Domain.InventoryTransitions.Reserve, inventory).Transition(Model.Domain.InventoryTransitions.Reserve, inventory)));
    }
}
