using AspireFirst.Orders;
using Cohesive.Adapters.Postgres;
using Npgsql;

namespace Cohesive.Adapters.Aspire.Tests;

internal static class PostgresSplitFixture
{
    public static PostgresPersistenceRegistration BindInventory(FulfillmentApplication model, NpgsqlDataSource database) =>
        PostgresPersistenceBindings.Define(new("inventory"), storage =>
            storage.Entity(model.Domain.Inventory, model.Storage.Bindings.Mapping(model.Domain.Inventory)))
        .Bind(new(new("inventory"), database, "test/inventory"));
}
