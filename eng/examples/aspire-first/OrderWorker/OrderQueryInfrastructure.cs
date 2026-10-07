using Cohesive.Adapters.Postgres;
using Npgsql;

namespace AspireFirst.Orders;

/// <summary>Native PostgreSQL composition, separate from the domain query and HTTP binding.</summary>
public static class OrderQueryInfrastructure
{
    /// <summary>Registers existing native mappings and prepares the query once for this host.</summary>
    /// <param name="database">Caller-owned Aspire data source.</param>
    /// <param name="maximumRows">Complete-result bound; excess rows fail the request.</param>
    /// <returns>A typed reader retaining its native prepared artifact.</returns>
    public static PostgresQueryReader<string, OrderDetails?> Bind(NpgsqlDataSource database, int maximumRows = 1000) =>
        new PostgresQueryRegistration(new(new("orders"), database, "aspire-first/apphost"))
            .Entity(FulfillmentDomain.Orders, OrderStorage.Mapping)
            .Entity(FulfillmentDomain.Reservations, FulfillmentStorage.Reservations)
            .Entity(FulfillmentDomain.Inventory, FulfillmentStorage.Inventory)
            .Register(OrderDetailsQuery.Definition, maximumRows, maximumBytes: 1_000_000);
}
