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
            new(FulfillmentStoragePolicy.OrdersDatabaseName), storage =>
            {
                storage.Schema(PostgresSchemaInitialization.CreateIfMissing, schema =>
                {
                    mappings.ConfigureOrderSchema(schema);
                    schema.ForeignKey(domain.ReservationItem)
                        .Collation(domain.Inventory, item => item.Sku, "C")
                        .Check(domain.Inventory, item => item.Partition, SqlBinaryOperator.Equal, FulfillmentStoragePolicy.LocalPartition)
                        .Check(domain.Inventory, item => item.Available, SqlBinaryOperator.GreaterThanOrEqual, 0);
                });
                storage.SchemaPreparation(async (database, cancellationToken) =>
                {
                    using var schema = new StreamReader(typeof(FulfillmentStorage).Assembly.GetManifestResourceStream("Orders.schema.sql")
                        ?? throw new InvalidOperationException("The product schema resource is missing."));
                    await using var initialize = database.CreateCommand(await schema.ReadToEndAsync(cancellationToken));
                    await initialize.ExecuteNonQueryAsync(cancellationToken);
                });
                storage.TransitionReceipts(new("public", "cohesive_process_receipts", FulfillmentStoragePolicy.LocalPartition),
                initialization: PostgresSchemaInitialization.CreateIfMissing);
                storage.History(new MartenEntityHistoryConfiguration(history.Authority, history.Reconstitution,
                    initialization: PostgresSchemaInitialization.CreateIfMissing));
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

/// <summary>Shared native table mappings, independent of which entities a composition places in PostgreSQL.</summary>
public sealed class FulfillmentPostgresMappings
{
    readonly FulfillmentDomain domain;
    /// <summary>Authors each immutable mapping once against the shared domain.</summary>
    /// <param name="domain">Canonical entities owned by this application.</param>
    public FulfillmentPostgresMappings(FulfillmentDomain domain)
    {
        this.domain = domain;
        Orders = PostgresEntityRepositoryMapping.For(domain.Orders)
            .Table("public", "cohesive_orders").Identity(x => x.Id, "order_id")
            .Partition(x => x.Partition, "partition_key").Column(x => x.Status, "status").Build();
        Reservations = PostgresEntityRepositoryMapping.For(domain.Reservations)
            .Table("public", "cohesive_reservations").Identity(x => x.Id, "reservation_id")
            .Partition(x => x.Partition, "partition_key").Column(x => x.OrderId, "order_id")
            .Column(x => x.Sku, "sku").Column(x => x.Quantity, "quantity").Build();
        Inventory = PostgresEntityRepositoryMapping.For(domain.Inventory)
            .Table("public", "cohesive_inventory").Identity(x => x.Sku, "sku")
            .Partition(x => x.Partition, "partition_key").Column(x => x.Available, "available").Build();
    }
    /// <summary>Shared constraints for orders/reservations in either database placement.</summary>
    /// <param name="schema">Native schema declaration for the mapped entities.</param>
    public void ConfigureOrderSchema(PostgresEntitySchemaBuilder schema) => schema
        .ForeignKey(domain.ReservationOrder)
        .Collation(domain.Orders, item => item.Id, "C")
        .Collation(domain.Reservations, item => item.Id, "C")
        .Collation(domain.Reservations, item => item.OrderId, "C")
        .Collation(domain.Reservations, item => item.Sku, "C")
        .Check(domain.Reservations, item => item.Partition, SqlBinaryOperator.Equal, FulfillmentStoragePolicy.LocalPartition)
        .Check(domain.Reservations, item => item.Quantity, SqlBinaryOperator.GreaterThan, 0);

    /// <summary>Native order table mapping.</summary>
    public PostgresEntityRepositoryMapping Orders { get; }
    /// <summary>Native reservation table mapping.</summary>
    public PostgresEntityRepositoryMapping Reservations { get; }
    /// <summary>Native inventory mapping for the PostgreSQL-only placement.</summary>
    public PostgresEntityRepositoryMapping Inventory { get; }
}

/// <summary>Shared local storage placement selected by both example compositions; not tenant authorization.</summary>
public static class FulfillmentStoragePolicy
{
    /// <summary>Native orders database connection name.</summary>
    public const string OrdersDatabaseName = "orders";
    /// <summary>Fixed demonstration partition used by query declarations and endpoint bindings.</summary>
    public const string LocalPartition = "local";
    /// <summary>Logical scope shared by the composed acquisition sources.</summary>
    public const string LogicalPartition = "aspire-first/" + LocalPartition;

}

/// <summary>Demo environment selection; switching authority for existing data requires migration.</summary>
public sealed class FulfillmentHistoryPolicy
{
    /// <summary>Authoritative entity representation.</summary>
    public EntityStateAuthority Authority { get; init; } = EntityStateAuthority.State;
    /// <summary>History read policy.</summary>
    public EntityReconstitutionMode Reconstitution { get; init; } = EntityReconstitutionMode.CheckpointAndTail;
}
