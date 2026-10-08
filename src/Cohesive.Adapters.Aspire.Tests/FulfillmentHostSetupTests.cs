using AspireFirst.Orders;
using Cohesive.Adapters.Postgres;
using Cohesive.Api.Execution.Services;
using Cohesive.Execution;
using Cohesive.Storage;
using Cohesive.Storage.Processes;
using Npgsql;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Cohesive.Transitions.Model;

namespace Cohesive.Adapters.Aspire.Tests;

public sealed class FulfillmentHostSetupTests
{
    [Fact]
    public async Task Mapping_twice_reuses_the_route_and_rejects_another_application()
    {
        var orders = new TypedEntityRepository<Order>(new SetupRepository(FulfillmentDomain.Orders.Definition), item => item.Id);
        var inventory = new TypedEntityRepository<InventoryItem>(new SetupRepository(FulfillmentDomain.Inventory.Definition), item => item.Sku);
        var bindings = new FulfillmentProcessBindings(orders, inventory);
        await using var app = WebApplication.CreateBuilder().Build();
        await using var other = WebApplication.CreateBuilder().Build();
        bindings.Map(app);
        var sources = ((IEndpointRouteBuilder)app).DataSources;
        var endpoints = sources.SelectMany(source => source.Endpoints).ToArray();
        Assert.Single(endpoints);
        bindings.Map(app);
        var route = Assert.IsType<RouteEndpoint>(Assert.Single(sources.SelectMany(source => source.Endpoints)));
        Assert.Equal("/fulfillment", route.RoutePattern.RawText);
        Assert.Throws<InvalidOperationException>(() => bindings.Map(other));
        Assert.Empty(((IEndpointRouteBuilder)other).DataSources);
    }

    sealed class SetupRepository(EntityDefinition definition) : IEntityTransitionOperationRepository
    {
        public EntityDefinition EntityDefinition => definition;
        public string? IdentityField => null;
        public EntityTransitionOperationCapabilities TransitionOperationCapabilities => new(true, "local");
        public Task<EntitySnapshot?> TryGet(OperationContext context, string id, EntityReadOptions? options = null) => throw new InvalidOperationException("No setup IO");
        public Task<EntitySnapshot> Upsert(OperationContext context, EntityWriteRequest write) => throw new InvalidOperationException("No setup IO");
        public Task<EntityTransitionOperationResult> TryGetTransitionOperation(OperationContext context, EntityTransitionOperationRequest request) => throw new InvalidOperationException("No setup IO");
        public Task<EntityTransitionOperationResult> TryGetCreationTransitionOperation(OperationContext context, EntityTransitionOperationRequest request) => throw new InvalidOperationException("No setup IO");
        public Task<EntityTransitionOperationResult> CommitTransitionOperation(OperationContext context, EntityTransitionOperationCommit commit) => throw new InvalidOperationException("No setup IO");
    }

    [Fact]
    public void Hosting_requires_receipt_capability_before_build_or_io()
    {
        using var database = NpgsqlDataSource.Create("Host=localhost;Port=1;Database=unused;Username=unused");
        var inventory = FulfillmentStorage.Bind(database).Repository(FulfillmentDomain.Inventory);
        var failure = Assert.Throws<ServiceBindingValidationException>(() => Service.Define(new("test"), new("1"), FulfillmentProcess.Provenance)
            .Operation("fulfill").Run(FulfillmentProcess.Definition, bindings => bindings.Transition(InventoryTransitions.Reserve, inventory)));
        Assert.Equal("services.binding.receiptCapabilityMissing", Assert.Single(failure.Validation.Diagnostics).Code);
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
