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
    /// <param name="endpoints">Shared endpoint selections.</param>
    /// <param name="history">Environment-selected authority and reconstitution, captured by this model.</param>
    /// <param name="mappings">Shared native table mappings.</param>
    public FulfillmentStorage(FulfillmentDomain domain, FulfillmentPostgresMappings mappings, OrderEndpoints endpoints, FulfillmentHistoryPolicy history)
    {
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
        Storage.Compose(() => new PostgresStorageComposition(Bindings, endpoints.Plan));
    }

    /// <summary>Native mappings for the primary database.</summary>
    public PostgresPersistenceBindings Bindings { get; }


}
