using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Cohesive.Adapters.Postgres;
using AspireFirst.Materialization;
using Cohesive.Adapters.Elastic;
using Cohesive.Relations.Execution;

namespace AspireFirst.Orders;

/// <summary>Explicitly selects one generation for order details; all writes and other queries stay native.</summary>
public static class OrderDetailsReadSelection
{
    /// <summary>Prepares the storage-owned serving selection from host-owned clients and operator evidence.</summary>
    /// <param name="services">Native clients and runtime configuration.</param>
    /// <param name="queries">Canonical queries selected by storage.</param>
    /// <param name="storage">Shared source and rebuild declaration.</param>
    /// <param name="databaseIdentity">Existing keyed PostgreSQL resource identity.</param>
    /// <param name="cancellationToken">Cancels artifact and native inspection.</param>
    /// <returns>Bounded-reader factory, prepared once before native source schema preparation.</returns>
    public static async ValueTask<Func<int, long, IRelationQueryReader<string, OrderDetails?>>> PrepareAsync(
        IServiceProvider services, FulfillmentQueries queries, PostgresMaterializationStorage storage,
        string databaseIdentity, CancellationToken cancellationToken)
    {
        var configuration = services.GetRequiredService<IConfiguration>();
        var comparisonPath = configuration["OrderReads:ComparisonFile"]
            ?? throw new InvalidOperationException("OrderReads:ComparisonFile is required for materialized reads.");
        var key = Convert.FromBase64String(configuration["Shadow:PositionKey"]
            ?? throw new InvalidOperationException("Shadow:PositionKey is required for exact read-plan admission."));
        var incarnation = configuration["Shadow:SlotGeneration"]
            ?? throw new InvalidOperationException("Shadow:SlotGeneration is required for exact read-plan admission.");
        var database = services.GetRequiredKeyedService<NpgsqlDataSource>(databaseIdentity);
        return await ElasticMaterializationReadSelection.PrepareAsync(queries.OrderDetails, queries.OrderDetailsRelation,
            storage.Materialization, comparisonPath, async ct =>
            {
                var target = await ShadowElastic.BindAsync(services.GetRequiredKeyedService<HttpClient>(ShadowElastic.Resource),
                    storage.Materialization, ct);
                return await storage.BindAsync(database, services.GetRequiredKeyedService<PostgresLogicalReplicationConnectionFactory>(databaseIdentity),
                    target, key, incarnation, ct);
            }, cancellationToken);
    }
}
