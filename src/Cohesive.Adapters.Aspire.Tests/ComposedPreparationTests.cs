using AspireFirst.Orders;
using Cohesive.Adapters.Postgres;
using Npgsql;
using Cohesive.Relations.Compilation;

namespace Cohesive.Adapters.Aspire.Tests;

public sealed class ComposedPreparationTests
{
    [Theory]
    [InlineData("missing-physical-policy")]
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
        var scope = failure == "missing-scope" ? null : new PostgresRelationQueryPartitionScope(new("local"),
            failure == "wrong-selector" ? "wrong" : FulfillmentDomain.PartitionField, FulfillmentDemo.LocalPartition);
        var policy = failure == "missing-physical-policy"
            ? new PostgresRelationQuerySourcePolicy(100, 1000, 1000, 1_000_000, partitionScope: scope)
            : new PostgresRelationQuerySourcePolicy(ReservationAvailabilityInfrastructure.PlanningPolicy, 1000, 1000, 1_000_000, scope);
        var error = Record.Exception(() => orders.QueryComposed(FulfillmentQueries.ReservationAvailability,
            FulfillmentQueries.ReservationDemandProjection, remote, policy));
        if (failure == "missing-mapping")
        {
            Assert.IsType<InvalidOperationException>(error);
            Assert.Contains("No remote", error!.Message);
        }
        else
        {
            var preparation = Assert.IsType<RelationQueryPreparationException>(error);
            Assert.Equal(failure switch {
                "missing-physical-policy" => "postgres.composed.physicalPolicyMissing",
                "wrong-selector" => "postgres.composed.partitionSelectorMismatch",
                _ => "postgres.composed.partitionScopeMissing" }, preparation.Code);
            Assert.True(preparation.Compilation.IsSuccessful);
        }
    }
    [Theory]
    [InlineData(7)]
    [InlineData(1000)]
    public void Composed_source_batch_bound_is_derived_from_its_retained_physical_policy(int batchSize)
    {
        var physical = new Cohesive.Relations.Physical.RelationQueryPhysicalPlanningPolicy(
            new("test/composed"), "test/composed", maximumBatchSize: batchSize,
            maximumBufferedRows: 1000, maximumLocalRows: 1000, maximumFanOut: 100,
            maximumReferenceKeysPerObservation: 100, maximumConcurrency: 1);
        var policy = new PostgresRelationQuerySourcePolicy(physical, 1000, 1000, 1_000_000,
            new(new("local"), FulfillmentDomain.PartitionField, FulfillmentDemo.LocalPartition));
        Assert.Same(physical, policy.PhysicalPlanningPolicy);
        Assert.Equal(physical.MaximumBatchSize, policy.MaximumBatchKeys);
    }

}
