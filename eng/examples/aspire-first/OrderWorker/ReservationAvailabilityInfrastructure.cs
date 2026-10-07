using Cohesive.Adapters.Postgres;
using Cohesive.Relations.Execution;
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
        => FulfillmentStorage.Bind(orders).QueryComposed(
            ReservationAvailabilityQuery.Definition,
            ReservationAvailabilityQuery.DemandProjection,
            remote: new PostgresPersistenceRegistration(new(new("inventory"), inventory, "aspire-first/inventory"))
                .Entity(FulfillmentDomain.Inventory, FulfillmentStorage.Inventory),
            policy: new PostgresRelationQuerySourcePolicy(
                maximumBatchKeys: 100, maximumRowsPerRead: 1000,
                maximumPageItems: 1000, maximumPageBytes: 1_000_000,
                partitionScope: new(new("aspire-first/local"), OrderStorage.PartitionField, OrderStorage.LocalPartition)));
}
