using Microsoft.Extensions.DependencyInjection;
using Cohesive.Host.Composition;
using AspireFirst.Orders;
using Cohesive.Adapters.Postgres;
using Cohesive.Model;
using Cohesive.Prelude;
using Cohesive.Storage;
using Cohesive.Transitions.Model;
using Npgsql;

namespace Cohesive.Adapters.Aspire.Tests;

public sealed class OrderStorageExampleTests
{
    static readonly AspireFirst.Orders.FulfillmentApplication Model = TestModels.Fulfillment();

    [Fact]
    public void Declared_HTTP_contract_matches_creation_lookup_and_both_conflicts()
    {
        Assert.Equal(Cohesive.Api.ApiResultKind.Created, Model.Api.Create.Operation.PrimaryResult.Kind);
        Assert.Equal(201, Model.Api.Create.Operation.PrimaryResult.Http!.StatusCode);
        Assert.Contains(Model.Api.Get.Operation.Results, result => result.Kind == Cohesive.Api.ApiResultKind.NotFound);
        Assert.Contains(Model.Api.Submit.Operation.Results, result => result.Kind == Cohesive.Api.ApiResultKind.NotFound);
        var conflict = Assert.Single(Model.Api.Submit.Operation.Results, result => result.Kind == Cohesive.Api.ApiResultKind.Conflict);
        Assert.Equal(typeof(Microsoft.AspNetCore.Mvc.ProblemDetails), conflict.BodyType);
    }

    [Fact]
    public void Example_binds_shared_repository_to_the_canonical_entity_without_connecting()
    {
        using var database = NpgsqlDataSource.Create("Host=unreachable.invalid;Database=orders;Username=example;Pooling=false");
        IEntityRepository repository = Model.Storage.Bindings.Bind(database, authority: "test/fulfillment").Repository(Model.Domain.Orders);
        Assert.IsAssignableFrom<IEntityRepository<Order>>(repository);
        Assert.Same(Model.Domain.Orders.Definition, repository.EntityDefinition);
        Assert.Equal("example/order", Model.Domain.Orders.Definition.Name.Value);
        Assert.Equal(new[] { "id", "partition", "status" }, Model.Domain.Orders.Definition.Fields.Select(field => field.Name.Value));
        var write = OrderTestFixture.RegisterOrder(Model.Domain, Guid.Parse("00000000-0000-0000-0000-000000000001"));
        Assert.Equal("00000000-0000-0000-0000-000000000001", write.Entity.EntityId.Value);
        Assert.Equal(write.Entity.EntityId.Value, write.Entity.Observation.GetField(FulfillmentDomain.OrderIdField).GetRequiredString());
        Assert.Equal(FulfillmentStoragePolicy.LocalPartition, write.Entity.Observation.GetField(FulfillmentDomain.PartitionField).GetRequiredString());
        Assert.Equal(1, write.Entity.Version);
        Assert.Null(write.ExpectedConcurrencyToken);
    }

    [Fact]
    public void Canonical_order_rejects_incomplete_state_before_storage()
    {
        Assert.Throws<SemanticRuleViolationException>(() => Model.Domain.Orders.Definition.CreateState("order-1",
            new Dictionary<string, ObservationValue> { [FulfillmentDomain.OrderIdField] = ObservationValue.FromString("order-1") }));
    }

    [Fact]
    public void Poco_state_rejects_null_required_partition()
    {
        Assert.Throws<SemanticRuleViolationException>(() => Model.Domain.Orders.Definition.CreateState("order-1",
            new Order("order-1", null!)));
    }

    [Fact]
    public async Task Repository_operations_observe_cancellation_before_opening_a_connection()
    {
        using var database = NpgsqlDataSource.Create("Host=unreachable.invalid;Database=orders;Username=example;Pooling=false");
        var repository = Model.Storage.Bindings.Bind(database, authority: "test/fulfillment").Repository(Model.Domain.Orders);
        var context = OperationContext.Create(cancellationToken: new CancellationToken(canceled: true));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.Upsert(context, OrderTestFixture.RegisterOrder(Model.Domain, Guid.NewGuid())));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.TryGet(context, "order-1",
            new EntityReadOptions(partitionKey: FulfillmentStoragePolicy.LocalPartition)));
    }
}
