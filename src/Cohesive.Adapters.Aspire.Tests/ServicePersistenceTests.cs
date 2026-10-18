using Cohesive.Relations.Execution;
using Cohesive.Adapters.Postgres;
using Cohesive.Host.Composition;
using AspireFirst.Orders;
using Cohesive.Adapters.Postgres.Services;
using Npgsql;

namespace Cohesive.Adapters.Aspire.Tests;

public sealed class ServicePersistenceTests
{
    [Fact]
    public void Unknown_receipt_initialization_fails_during_authoring()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PostgresPersistenceBindings.Define(new("test"), storage =>
            storage.TransitionReceipts(new("public", "receipts", "local"), (PostgresSchemaInitialization)42)));
    }

    [Fact]
    public void Receipt_policy_is_model_owned_and_frozen_with_native_mappings()
    {
        var model = ModelActivator.Create<FulfillmentApplication>();
        Assert.Equal(PostgresSchemaInitialization.CreateIfMissing, model.Storage.Bindings.ReceiptInitialization);
        Assert.IsType<Cohesive.Adapters.Marten.MartenEntityHistoryConfiguration>(model.Storage.Bindings.HistoryConfiguration);
        Assert.NotNull(model.Storage.Bindings.OutboxOptions);
        Assert.Throws<InvalidOperationException>(() => model.Storage.Bindings.History(new Cohesive.Adapters.Marten.MartenEntityHistoryConfiguration()));
        Assert.Throws<InvalidOperationException>(() => model.Storage.Bindings.Outbox(new("public", "other_outbox")));
        Assert.Equal(FulfillmentStoragePolicy.LocalPartition, model.Storage.Bindings.ReceiptOptions!.PartitionKey);
        Assert.Throws<InvalidOperationException>(() => model.Storage.Bindings.TransitionReceipts(
            new("public", "other_receipts", "other")));
        Assert.Same(model.Storage.Bindings, Assert.Single(model.Storage.Composition!.Bindings));
    }

    [Fact]
    public async Task Shared_query_is_prepared_once_per_resolver_and_bound_without_database_io()
    {
        var model = ModelActivator.Create<FulfillmentApplication>();
        await using var database = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=unused;Username=unused");
        var persistence = model.Storage.Bindings.Bind(database, authority: "test/fulfillment").ForServices();
        var offline = model.Storage.Bindings.Prepare(model.Queries.OrderDetails.CompilationRequest);
        var first = persistence.Query(model.Queries.OrderDetails, 1000, 1_000_000, RelationQueryConsistency.SingleSnapshot);
        Assert.Same(offline, Assert.IsType<Cohesive.Adapters.Postgres.PostgresQueryReader<string, OrderDetails?>>(first).Artifact);
        Assert.Same(first, persistence.Query(model.Queries.OrderDetails, 1000, 1_000_000, RelationQueryConsistency.SingleSnapshot));
        Assert.NotSame(first, persistence.Query(model.Queries.OrderDetails, 999, 1_000_000, RelationQueryConsistency.SingleSnapshot));
    }
}
