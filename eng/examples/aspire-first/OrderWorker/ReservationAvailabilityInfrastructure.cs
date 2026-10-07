using Cohesive.Adapters.Postgres;
using Cohesive.Relations.Execution;
using Cohesive.Relations.Physical;

namespace AspireFirst.Orders;

/// <summary>Alternative native placements of the same reservation-availability declaration.</summary>
public static class ReservationAvailabilityInfrastructure
{
    /// <summary>Independent bounds for this example’s composed execution.</summary>
    public static RelationQueryPhysicalPlanningPolicy PlanningPolicy { get; } = new(
        new("aspire-first/availability/v1"), "aspire-first/availability/v1",
        maximumBatchSize: 100, maximumBufferedRows: 1000, maximumLocalRows: 1000,
        maximumFanOut: 100, maximumReferenceKeysPerObservation: 100, maximumConcurrency: 1);

    /// <summary>Runs all joins in one native PostgreSQL statement.</summary>
    /// <param name="persistence">Existing registration containing all three entity mappings.</param>
    /// <returns>A prepared native reader; retain at host lifetime.</returns>
    public static PostgresQueryReader<string, ReservationAvailability[]> BindNative(PostgresPersistenceRegistration persistence) =>
        persistence.Query(FulfillmentQueries.ReservationAvailability, 1000, 1_000_000);

    /// <summary>Runs the order/reservation join in PostgreSQL and enriches its rowset from a separate inventory database.</summary>
    /// <param name="orders">Existing orders database registration.</param>
    /// <param name="inventory">Existing inventory database registration, independently bound and read.</param>
    /// <returns>A prepared composed reader exposing exact split and execution evidence.</returns>
    /// <remarks>No global snapshot is promised across these two data sources. Scope is the example's local partition.</remarks>
    public static RelationQuerySubplanReader<string, ReservationAvailability[]> BindComposed(PostgresPersistenceRegistration orders, PostgresPersistenceRegistration inventory)
        => orders.QueryComposed(
            FulfillmentQueries.ReservationAvailability,
            FulfillmentQueries.ReservationDemandProjection,
            remote: inventory,
            policy: new PostgresRelationQuerySourcePolicy(
                PlanningPolicy, maximumRowsPerRead: 1000,
                maximumPageItems: 1000, maximumPageBytes: 1_000_000,
                partitionScope: new(new("aspire-first/local"), FulfillmentDomain.PartitionField, FulfillmentDemo.LocalPartition)));
}
