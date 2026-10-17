using Cohesive.Adapters.Postgres;
using Npgsql;
using Cohesive.Storage;

namespace AspireFirst.Orders;

/// <summary>Instance-owned native storage attachments to the exact domain supplied by composition.</summary>
public sealed class FulfillmentStorage : StorageModel
{
    /// <summary>Builds immutable offline mappings without opening a connection.</summary>
    /// <param name="domain">Shared canonical entities.</param>
    /// <param name="mappings">Shared native table mappings.</param>
    public FulfillmentStorage(FulfillmentDomain domain, FulfillmentPostgresMappings mappings)
    {
        Bindings = Storage.Define(() => PostgresPersistenceBindings.Define(
            new(FulfillmentResources.DatabaseName), storage =>
            {
                storage.Entity(domain.Orders, mappings.Orders);
                storage.Entity(domain.Reservations, mappings.Reservations);
                storage.Entity(domain.Inventory, mappings.Inventory);
            }));
    }

    /// <summary>Native mappings for the primary database.</summary>
    public PostgresPersistenceBindings Bindings { get; }

    /// <summary>Attaches the configured native data source; ownership remains with the caller.</summary>
    /// <param name="database">Native data source.</param><returns>A runtime persistence registration.</returns>
    public PostgresPersistenceRegistration Bind(NpgsqlDataSource database) =>
        Bindings.Bind(new(new(FulfillmentResources.DatabaseName), database, "aspire-first/apphost"));

}
