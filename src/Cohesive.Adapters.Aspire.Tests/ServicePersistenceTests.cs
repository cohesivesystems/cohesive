using Cohesive.Relations.Execution;
using Cohesive.Adapters.Postgres;
using Cohesive.Host.Composition;
using AspireFirst.Orders;
using Cohesive.Adapters.Postgres.Services;
using Npgsql;
using Microsoft.Extensions.DependencyInjection;

namespace Cohesive.Adapters.Aspire.Tests;

public sealed class ServicePersistenceTests
{
    [Fact]
    public async Task Wrong_history_data_source_rejects_before_product_or_attachment_preparation()
    {
        var model = ModelActivator.Create<FulfillmentApplication>();
        await using var database = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=unused;Username=unused");
        await using var other = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=unused;Username=unused");
        using var history = global::Marten.DocumentStore.For(options =>
        {
            options.Connection(other);
            Cohesive.Adapters.Marten.MartenEntityHistoryConfigurationExtensions.UseCohesiveEntityHistory(options);
        });
        var calls = 0;
        var bindings = PostgresPersistenceBindings.Define(new("orders"), storage => storage
            .Entity(model.Domain.Orders, model.Storage.Bindings.Mapping(model.Domain.Orders))
            .Entity(model.Domain.Inventory, model.Storage.Bindings.Mapping(model.Domain.Inventory))
            .Entity(model.Domain.Reservations, model.Storage.Bindings.Mapping(model.Domain.Reservations))
            .SchemaPreparation((_, _) => { calls++; return Task.CompletedTask; })
            .History(new Cohesive.Adapters.Marten.MartenEntityHistoryConfiguration())
            .TransitionReceipts(new("public", "receipts", "local"), PostgresSchemaInitialization.CreateIfMissing));
        await using var provider = new ServiceCollection().AddKeyedSingleton("orders", database)
            .AddKeyedSingleton<global::Marten.IDocumentStore>("orders", history).BuildServiceProvider();
        var composition = new PostgresStorageComposition(bindings, model.Endpoints.Plan);
        await Assert.ThrowsAsync<ArgumentException>(() => composition.BindAsync(provider).AsTask());
        Assert.Equal(0, calls);
    }

    [Fact]
    public void Missing_embedded_migration_fails_when_declared()
    {
        var failure = Assert.Throws<ArgumentException>(() => PostgresPersistenceBindings.Define(new("test"),
            storage => storage.SchemaPreparation(typeof(ServicePersistenceTests).Assembly, "missing.sql")));
        Assert.Contains("missing.sql", failure.Message);
    }

    [Fact]
    public async Task Native_callback_receives_host_services_and_sync_bind_does_not_run_it()
    {
        var model = ModelActivator.Create<FulfillmentApplication>();
        var marker = new object();
        var calls = 0;
        await using var provider = new ServiceCollection().AddSingleton(marker).BuildServiceProvider();
        var bindings = PostgresPersistenceBindings.Define(new("test"), storage => storage
            .Entity(model.Domain.Inventory, model.Storage.Bindings.Mapping(model.Domain.Inventory))
            .SchemaPreparation((services, _, _) =>
            { Assert.Same(marker, services.GetRequiredService<object>()); calls++; return Task.CompletedTask; }));
        await using var database = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=unused;Username=unused");
        _ = bindings.Bind(database, "test");
        Assert.Equal(0, calls);
        await Assert.ThrowsAsync<InvalidOperationException>(() => bindings.PrepareSchemaAsync(database));
        Assert.Equal(0, calls);
        await bindings.PrepareSchemaAsync(database, services: provider);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Product_schema_preparation_is_deferred_and_fails_before_attachment_admission()
    {
        var model = ModelActivator.Create<FulfillmentApplication>();
        var calls = 0;
        var failure = new InvalidOperationException("Explicit migration failed.");
        var bindings = PostgresPersistenceBindings.Define(new(FulfillmentStoragePolicy.OrdersDatabaseName), storage => storage
            .SchemaPreparation((_, _) => { calls++; return Task.FromException(failure); })
            .Entity(model.Domain.Orders, model.Storage.Bindings.Mapping(model.Domain.Orders))
            .Entity(model.Domain.Inventory, model.Storage.Bindings.Mapping(model.Domain.Inventory))
            .Entity(model.Domain.Reservations, model.Storage.Bindings.Mapping(model.Domain.Reservations)));
        var composition = new PostgresStorageComposition(bindings, model.Endpoints.Plan);
        Assert.Equal(0, calls);
        Assert.Throws<InvalidOperationException>(() => bindings.SchemaPreparation((_, _) => Task.CompletedTask));
        await using var database = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=unused;Username=unused");
        await using var provider = new ServiceCollection()
            .AddKeyedSingleton(FulfillmentStoragePolicy.OrdersDatabaseName, database).BuildServiceProvider();
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => composition.BindAsync(provider).AsTask()));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Product_schema_preparation_is_optional_and_observes_cancellation_before_callback()
    {
        await using var database = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=unused;Username=unused");
        var empty = PostgresPersistenceBindings.Define(new("test"), _ => { });
        await empty.PrepareSchemaAsync(database);
        var calls = 0;
        var bindings = PostgresPersistenceBindings.Define(new("test"), storage => storage
            .SchemaPreparation((_, _) => { calls++; return Task.CompletedTask; }));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => bindings.PrepareSchemaAsync(database, canceled.Token));
        Assert.Equal(0, calls);
        Assert.Throws<InvalidOperationException>(() => PostgresPersistenceBindings.Define(new("duplicate"), storage => storage
            .SchemaPreparation((_, _) => Task.CompletedTask).SchemaPreparation((_, _) => Task.CompletedTask)));
    }

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
