using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Cohesive.Adapters.Elastic;
using AspireFirst.Materialization;
using Cohesive.Api.Execution.Services;
using Cohesive.Relations.Execution;
using Cohesive.Adapters.Postgres;
using Cohesive.Storage;
using Cohesive.Adapters.Sql;
using Cohesive.Adapters.Marten;
using Cohesive.Adapters.Postgres.Services;

namespace AspireFirst.Orders;

/// <summary>Instance-owned native storage attachments to the exact domain supplied by composition.</summary>
public sealed class FulfillmentStorage : StorageModel
{
    /// <summary>Builds immutable offline mappings without opening a connection.</summary>
    /// <param name="domain">Shared canonical entities.</param>
    /// <param name="history">Environment-selected authority and reconstitution, captured by this model.</param>
    /// <param name="mappings">Shared native table mappings.</param>
    /// <param name="queries">Shared query declarations eligible for serving selection.</param>
    /// <param name="reads">Explicit live or materialized serving policy.</param>
    public FulfillmentStorage(FulfillmentDomain domain, FulfillmentPostgresMappings mappings, FulfillmentHistoryPolicy history,
        FulfillmentQueries queries, RelationQueryReadPolicy? reads = null)
    {
        reads ??= new();
        reads.Validate();
        Bindings = Storage.Define(() => PostgresPersistenceBindings.Define(
            new(history.DatabaseName), storage =>
            {
                storage.Schema(PostgresSchemaInitialization.CreateIfMissing, schema =>
                {
                    mappings.ConfigureOrderSchema(schema);
                    schema.ForeignKey(domain.ReservationItem)
                        .Collation(domain.Inventory, item => item.Sku, "C")
                        .Check(domain.Inventory, item => item.Partition, SqlBinaryOperator.Equal, FulfillmentStoragePolicy.LocalPartition)
                        .Check(domain.Inventory, item => item.Available, SqlBinaryOperator.GreaterThanOrEqual, 0);
                });
                storage.SchemaPreparation(typeof(FulfillmentStorage).Assembly, "Orders.schema.sql");
                storage.TransitionReceipts(new("public", "cohesive_process_receipts", FulfillmentStoragePolicy.LocalPartition),
                initialization: PostgresSchemaInitialization.CreateIfMissing);
                storage.History(history.History);
                storage.Outbox(new("public", "cohesive_transition_outbox"),
                    initialization: PostgresSchemaInitialization.CreateIfMissing);
                storage.Entity(domain.Orders, mappings.Orders);
                storage.Entity(domain.Reservations, mappings.Reservations);
                storage.Entity(domain.Inventory, mappings.Inventory);
            }));
        Native = new PostgresStorageComposition(Bindings);
        Storage.Compose(() =>
        {
            if (reads.Source == RelationQueryReadSource.Live) return Native;
            var materializations = new FulfillmentMaterializations(queries);
            var materializationStorage = new FulfillmentMaterializationStorage(this, materializations);
            return Native.SelectQuery(queries.OrderDetails,
                new("Elasticsearch", ShadowElastic.Resource, RelationQueryConsistency.Eventual),
                async (services, ct) =>
                {
                    var configuration = services.GetRequiredService<IConfiguration>();
                    var comparisonFile = configuration["OrderReads:ComparisonFile"]
                        ?? throw new InvalidOperationException("OrderReads:ComparisonFile is required for materialized reads.");
                    var key = Convert.FromBase64String(configuration["Shadow:PositionKey"]
                        ?? throw new InvalidOperationException("Shadow:PositionKey is required for read-plan admission."));
                    var incarnation = configuration["Shadow:SlotGeneration"]
                        ?? throw new InvalidOperationException("Shadow:SlotGeneration is required for read-plan admission.");
                    return await ElasticMaterializationReadSelection.PrepareAsync(queries.OrderDetails, queries.OrderDetailsRelation,
                        materializations.OrderDetails, comparisonFile, async token =>
                        {
                            var target = await ShadowElastic.BindAsync(services.GetRequiredKeyedService<HttpClient>(ShadowElastic.Resource),
                                materializations.OrderDetails, token);
                            return await materializationStorage.OrderDetails.BindAsync(services, target, key, incarnation, token);
                        }, ct);
                });
        });
    }

    /// <summary>Canonical source placement and live rollback interpretation, also used for infrastructure evidence.</summary>
    public PostgresStorageComposition Native { get; }

    /// <summary>Native mappings for the primary database.</summary>
    public PostgresPersistenceBindings Bindings { get; }


}
