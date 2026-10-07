using AspireFirst.Orders;
using Cohesive.Adapters.Postgres;
using Npgsql;

namespace Cohesive.Adapters.Aspire.Tests;

public sealed class ComposedPreparationTests
{
    [Theory]
    [InlineData("batch-limit")]
    [InlineData("missing-scope")]
    [InlineData("wrong-selector")]
    [InlineData("missing-mapping")]
    public void Invalid_registration_fails_without_opening_a_database(string failure)
    {
        using var db = NpgsqlDataSource.Create("Host=invalid.invalid;Database=unused;Username=test;Timeout=1");
        var orders = FulfillmentStorage.Bind(db);
        var remote = failure == "missing-mapping"
            ? new PostgresPersistenceRegistration(new(new("inventory"), db, "test"))
            : FulfillmentStorage.BindInventory(db);
        var policy = new PostgresRelationQuerySourcePolicy(failure == "batch-limit" ? 99 : 100, 1000, 1000, 1_000_000,
            partitionScope: failure == "missing-scope" ? null : new(new("local"),
                failure == "wrong-selector" ? "wrong" : FulfillmentDomain.PartitionField, FulfillmentDemo.LocalPartition));
        var error = Record.Exception(() => orders.QueryComposed(FulfillmentQueries.ReservationAvailability,
            FulfillmentQueries.ReservationDemandProjection, remote, policy, ReservationAvailabilityInfrastructure.PlanningPolicy));
        if (failure == "missing-mapping") Assert.IsType<InvalidOperationException>(error);
        else Assert.IsType<ArgumentException>(error);
        Assert.Contains(failure switch { "batch-limit" => "MaximumBatchKeys", "missing-mapping" => "No remote", "wrong-selector" => "selector", _ => "scope" }, error!.Message);
    }
}
