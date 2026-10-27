using Microsoft.Extensions.DependencyInjection;
using Cohesive.Application;
using AspireFirst.Orders;
using Cohesive.Adapters.Postgres;
using Npgsql;
using Cohesive.Relations.Compilation;

namespace Cohesive.Adapters.Aspire.Tests;

public sealed class ComposedPreparationTests
{
    static readonly AspireFirst.Orders.FulfillmentApplication Model = TestModels.Fulfillment();

    [Fact]
    public void Native_prefix_recipe_rejects_queries_without_a_supported_closed_row_boundary()
    {
        using var database = NpgsqlDataSource.Create("Host=invalid.invalid;Database=unused;Username=test");
        var local = Model.Storage.Bindings.Bind(database, authority: "test/fulfillment");
        var remote = PostgresSplitFixture.BindInventory(Model, database);
        var policy = new PostgresRelationQueryComposedPolicy(
            ReservationAvailabilityQueryBindings.PlanningPolicy, 1000, 1000, 1_000_000,
            new(new("local"), FulfillmentDomain.PartitionField, FulfillmentStoragePolicy.LocalPartition));
        var error = Assert.Throws<RelationQueryPreparationException>(() =>
            local.QueryComposed(Model.Queries.OrderDetails, remote, policy));
        Assert.Equal("postgres.composed.splitMissing", error.Code);
    }

    [Fact]
    public void Missing_scope_fails_when_policy_is_declared()
    {
        var error = Assert.Throws<ArgumentNullException>(() => new PostgresRelationQueryComposedPolicy(
            ReservationAvailabilityQueryBindings.PlanningPolicy, 1000, 1000, 1_000_000, null!));
        Assert.Equal("partitionScope", error.ParamName);
    }

    [Theory]
    [InlineData("wrong-selector")]
    [InlineData("missing-mapping")]
    public void Invalid_registration_fails_without_opening_a_database(string failure)
    {
        using var db = NpgsqlDataSource.Create("Host=invalid.invalid;Database=unused;Username=test;Timeout=1");
        var orders = Model.Storage.Bindings.Bind(db, authority: "test/fulfillment");
        var remote = failure == "missing-mapping"
            ? PostgresPersistenceBindings.Define(new("inventory"), _ => { }).Bind(new(new("inventory"), db, "test"))
            : PostgresSplitFixture.BindInventory(Model, db);
        var scope = new PostgresRelationQueryPartitionScope(new("local"),
            failure == "wrong-selector" ? "wrong" : FulfillmentDomain.PartitionField, FulfillmentStoragePolicy.LocalPartition);
        var policy = new PostgresRelationQueryComposedPolicy(
            ReservationAvailabilityQueryBindings.PlanningPolicy, 1000, 1000, 1_000_000, scope);
        var error = Record.Exception(() => orders.QueryComposed(Model.Queries.ReservationAvailability, SubplanTests.DemandProjection(Model.Queries), remote, policy));
        var preparation = Assert.IsType<RelationQueryPreparationException>(error);
        Assert.Equal(failure switch {
            "missing-mapping" => "postgres.composed.remoteMappingMissing",
            _ => "postgres.composed.partitionSelectorMismatch" }, preparation.Code);
        Assert.True(preparation.Compilation.IsSuccessful);
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
        var policy = new PostgresRelationQueryComposedPolicy(physical, 1000, 1000, 1_000_000,
            new(new("local"), FulfillmentDomain.PartitionField, FulfillmentStoragePolicy.LocalPartition));
        Assert.Same(physical, policy.PhysicalPlanningPolicy);
        Assert.Equal(physical.MaximumBatchSize, policy.SourcePolicy.MaximumBatchKeys);
    }

}
