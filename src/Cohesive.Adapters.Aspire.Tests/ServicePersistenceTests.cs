using Cohesive.Host.Composition;
using AspireFirst.Orders;
using Cohesive.Adapters.Postgres.Services;
using Npgsql;

namespace Cohesive.Adapters.Aspire.Tests;

public sealed class ServicePersistenceTests
{
    [Fact]
    public async Task Shared_query_is_prepared_once_per_resolver_and_bound_without_database_io()
    {
        var model = ModelActivator.Create<FulfillmentApplication>();
        await using var database = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=unused;Username=unused");
        var persistence = model.Storage.Bindings.Bind(database, authority: "test/fulfillment").ForServices();
        var first = persistence.Query(model.Queries.OrderDetails, 1000, 1_000_000);
        Assert.Same(first, persistence.Query(model.Queries.OrderDetails, 1000, 1_000_000));
        Assert.NotSame(first, persistence.Query(model.Queries.OrderDetails, 999, 1_000_000));
    }
}
