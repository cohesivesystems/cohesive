using Cohesive.Adapters.Postgres;
using Cohesive.Model;
using Cohesive.Relations.Acquisition;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Execution;
using Cohesive.Relations.Physical;
using Npgsql;

namespace AspireFirst.Orders;

/// <summary>Alternative native placements of the same reservation-availability declaration.</summary>
public static class ReservationAvailabilityInfrastructure
{
    /// <summary>Runs all joins in one native PostgreSQL statement.</summary>
    /// <param name="database">Caller-owned database containing all three entity tables.</param>
    /// <returns>A prepared native reader; retain at host lifetime.</returns>
    public static PostgresQueryReader<string, ReservationAvailability[]> BindNative(NpgsqlDataSource database) =>
        FulfillmentStorage.Bind(database)
            .Query(ReservationAvailabilityQuery.Definition, 1000, 1_000_000);

    /// <summary>Runs the order/reservation join in PostgreSQL and enriches its rowset from a separate inventory database.</summary>
    /// <param name="orders">Caller-owned orders database.</param>
    /// <param name="inventory">Caller-owned inventory database, independently bound and read.</param>
    /// <returns>A prepared composed reader exposing exact split and execution evidence.</returns>
    /// <remarks>No global snapshot is promised across these two data sources. Scope is the example's local partition.</remarks>
    public static RelationQuerySubplanReader<string, ReservationAvailability[]> BindComposed(NpgsqlDataSource orders, NpgsqlDataSource inventory)
    {
        var cut = RelationQuerySubplan.Compile(ReservationAvailabilityQuery.Definition.CompilationRequest,
            ReservationAvailabilityQuery.DemandProjection);
        var native = FulfillmentStorage.Bind(orders).Prepare(cut.Prefix.Request, 1000, 1_000_000);
        var builder = RelationQueryPlacement.For(cut.Remainder.Plan!);
        var projected = builder.Source("reservation-demand", RelationQueryProjectedRowset.Profile, new("orders"), limits: new(100, 1000, 100, 1));
        var external = builder.Source("inventory", PostgresRelationQuerySourceTargetProfile.Default, new("inventory"), limits: new(100, 1000, 100, 1));
        foreach (var input in cut.Remainder.Plan!.InputContract.Sources)
        {
            if (input.Node == cut.Cut.Id)
                builder.Place(input, projected).Identity("$row").FieldsBySemanticPath();
            else
                builder.Place(input, external).Identity(FieldPath.FromField(FulfillmentStorage.Inventory.IdentityField), FulfillmentStorage.Inventory.IdentityField)
                    .FieldsBySemanticPath().Partition(FulfillmentStorage.Inventory.PartitionField);
        }
        var placement = builder.Build().RequireValue();
        var storage = PostgresRelationQueryBinding.For(placement).ForSource(external.Id).Database(new("inventory"));
        foreach (var input in placement.Inputs.Where(input => input.Source.Id == external.Id))
            storage.Table(input, FulfillmentStorage.Inventory);
        var bound = storage.Build().RequireValue();
        var policy = new RelationQueryPhysicalPlanningPolicy(new("aspire-first/subplan/v1"), "aspire-first/v1",
            100, 1000, 1000, 100, 100, 1);
        var partition = new RelationQueryLogicalPartitionIdentity("aspire-first/local");
        return new(ReservationAvailabilityQuery.Definition, cut, native, placement.Placement, policy,
            physical => [new PostgresRelationQuerySourceReader(cut.Remainder.Plan!, physical, external.Id,
                bound, inventory, new(new("inventory"), inventory, "aspire-first/inventory"),
                new(100, 1000, 1000, 1_000_000, partitionScope: new(partition, OrderStorage.PartitionField, OrderStorage.LocalPartition)))], partition);
    }

}
