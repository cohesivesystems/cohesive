using AspireFirst.Orders;
using Cohesive.Adapters.AspNet;
using Cohesive.Adapters.AspNet.Services;
using Cohesive.Adapters.Postgres;
using Cohesive.Adapters.Postgres.Services;
using Cohesive.Api.Execution.Services;
using Cohesive.Model;
using Cohesive.Storage;
using Microsoft.AspNetCore.Builder;
using Npgsql;

namespace Cohesive.Adapters.Aspire.Tests;

// Prepare the real application model; tests vary native persistence, never the endpoint declarations.
internal static class OrderHttpTestHost
{
    internal static async Task<ServiceApiBindings> Prepare(WebApplicationBuilder builder, FulfillmentApplication model,
        PostgresPersistenceRegistration persistence, NpgsqlDataSource database,
        Func<IEntityRepository, IEntityRepository>? decorateOrders = null, PostgresEntityHistoryStorage? history = null,
        Func<IServicePersistence, IServicePersistence>? selectReads = null)
    {
        builder.Services.AddRequestOperationContext();
        builder.Services.AddFixedRequestIdentity(FulfillmentDemoIdentity.Context);
        var receipts = new PostgresTransitionReceiptOptions("public", "cohesive_process_receipts", FulfillmentStoragePolicy.LocalPartition);
        await using (var command = database.CreateCommand(receipts.SchemaSql)) await command.ExecuteNonQueryAsync();
        var storage = persistence.ForServices(await receipts.BindAsync(database), history);
        if (decorateOrders is not null)
            storage = new ServicePersistenceBuilder(storage)
                .Repository(decorateOrders(storage.Repository(ValueContract.FromShape(model.Domain.Orders.Definition.Shape)))).Build();
        return model.Endpoints.Build(builder.Services, selectReads is null ? storage : selectReads(storage));
    }
}
