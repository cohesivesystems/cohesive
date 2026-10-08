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
    public void Receipt_partition_is_inherited_and_an_explicit_mismatch_fails_at_setup()
    {
        using var database = NpgsqlDataSource.Create("Host=localhost;Port=1;Database=unused;Username=unused");
        var persistence = FulfillmentStorage.Bind(database);
        var inventory = persistence.Repository(FulfillmentDomain.Inventory,
            transitionReceipts: new("public", "receipts", "trusted"));
        Assert.True(InteractionContractCatalog.TryCreate([], out var contracts).IsValid);
        var binding = new ProcessTransitionOperationBinding(InventoryTransitions.Reserve.Compile().Plan!, inventory, contracts!);
        Assert.Equal("trusted", binding.PartitionKey);
        Assert.Throws<ArgumentException>(() => new ProcessTransitionOperationBinding(
            binding.Plan, inventory, contracts!, partitionKey: "wrong"));
    }

    [Fact]
    public void Hosting_requires_receipt_capability_before_build_or_io()
    {
        using var database = NpgsqlDataSource.Create("Host=localhost;Port=1;Database=unused;Username=unused");
        var inventory = FulfillmentStorage.Bind(database).Repository(FulfillmentDomain.Inventory);
        var failure = Assert.Throws<ServiceBindingValidationException>(() => Service.Host(FulfillmentProcess.Definition)
            .Transition(InventoryTransitions.Reserve, inventory));
        Assert.Equal(EntityTransitionOperationDiagnosticCodes.CapabilityInsufficient, Assert.Single(failure.Validation.Diagnostics).Code);
    }

    [Fact]
    public void Duplicate_transition_registration_fails_without_changing_host_associations()
    {
        using var database = NpgsqlDataSource.Create("Host=localhost;Port=1;Database=unused;Username=unused");
        var inventory = FulfillmentStorage.Bind(database).Repository(FulfillmentDomain.Inventory,
            transitionReceipts: new("public", "receipts", "trusted"));
        var host = Service.Host(FulfillmentProcess.Definition).Transition(InventoryTransitions.Reserve, inventory);
        Assert.Throws<ArgumentException>(() => host.Transition(InventoryTransitions.Reserve, inventory));
    }
}
