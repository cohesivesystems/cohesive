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
    [Fact]
    public void Declared_HTTP_contract_matches_creation_lookup_and_both_conflicts()
    {
        Assert.Equal(Cohesive.Api.ApiResultKind.Created, OrderApi.Create.Operation.PrimaryResult.Kind);
        Assert.Equal(201, OrderApi.Create.Operation.PrimaryResult.Http!.StatusCode);
        Assert.Contains(OrderApi.Get.Operation.Results, result => result.Kind == Cohesive.Api.ApiResultKind.NotFound);
        Assert.Contains(OrderApi.Submit.Operation.Results, result => result.Kind == Cohesive.Api.ApiResultKind.NotFound);
        var conflict = Assert.Single(OrderApi.Submit.Operation.Results, result => result.Kind == Cohesive.Api.ApiResultKind.Conflict);
        Assert.Equal(typeof(Microsoft.AspNetCore.Mvc.ProblemDetails), conflict.BodyType);
    }

    [Fact]
    public void Example_binds_shared_repository_to_the_canonical_entity_without_connecting()
    {
        using var database = NpgsqlDataSource.Create("Host=unreachable.invalid;Database=orders;Username=example;Pooling=false");
        IEntityRepository repository = OrderStorage.Bind(database);
        Assert.IsType<PostgresEntityRepository>(repository);
        Assert.Same(OrderStorage.Entity, repository.EntityDefinition);
        Assert.Equal("example/order", OrderStorage.Entity.Name.Value);
        Assert.Equal(new[] { "id", "partition", "status" }, OrderStorage.Entity.Fields.Select(field => field.Name.Value));
        var write = OrderStorage.Register(Guid.Parse("00000000-0000-0000-0000-000000000001"));
        Assert.Equal("00000000-0000-0000-0000-000000000001", write.Entity.EntityId.Value);
        Assert.Equal(write.Entity.EntityId.Value, write.Entity.Observation.GetField(OrderStorage.IdField).GetRequiredString());
        Assert.Equal(OrderStorage.LocalPartition, write.Entity.Observation.GetField(OrderStorage.PartitionField).GetRequiredString());
        Assert.Equal(1, write.Entity.Version);
        Assert.Null(write.ExpectedConcurrencyToken);
    }

    [Fact]
    public void Canonical_order_rejects_incomplete_state_before_storage()
    {
        Assert.Throws<SemanticRuleViolationException>(() => OrderStorage.Entity.CreateState("order-1",
            new Dictionary<string, ObservationValue> { [OrderStorage.IdField] = ObservationValue.FromString("order-1") }));
    }

    [Fact]
    public void Poco_state_rejects_null_required_partition()
    {
        Assert.Throws<SemanticRuleViolationException>(() => OrderStorage.Entity.CreateState("order-1",
            new Order("order-1", null!)));
    }

    [Fact]
    public async Task Repository_operations_observe_cancellation_before_opening_a_connection()
    {
        using var database = NpgsqlDataSource.Create("Host=unreachable.invalid;Database=orders;Username=example;Pooling=false");
        var repository = OrderStorage.Bind(database);
        var context = OperationContext.Create(cancellationToken: new CancellationToken(canceled: true));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.Upsert(context, OrderStorage.Register(Guid.NewGuid())));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.TryGet(context, "order-1",
            new EntityReadOptions(partitionKey: OrderStorage.LocalPartition)));
    }
}
