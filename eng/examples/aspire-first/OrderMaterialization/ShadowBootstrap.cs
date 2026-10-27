using System.Text;
using AspireFirst.Orders;
using Cohesive.Adapters.Postgres;
using Cohesive.Prelude;
using Npgsql;

namespace AspireFirst.Materialization;

internal static class ShadowBootstrap
{
    // Session lock covers target writes as well as setup. One shadow writer, including across process restart.
    internal const long WriterLock = 0x434F4853484457;

    internal static async Task InitializeAsync(FulfillmentApplication application, FulfillmentMaterializations definition, NpgsqlDataSource database,
        HttpClient elastic, CancellationToken cancellationToken)
    {
        await application.Storage.Bindings.PrepareSchemaAsync(database, cancellationToken);
        var bindings = application.Storage.Bindings;
        var tables = new[] { bindings.Mapping(application.Domain.Orders).Table,
            bindings.Mapping(application.Domain.Reservations).Table, bindings.Mapping(application.Domain.Inventory).Table };
        var state = new PostgresMaterializationStateStore(database, new(FulfillmentMaterializationBindings.Authority));
        await PostgresLogicalReplicationPreparation.InitializeAsync(database, tables,
            FulfillmentMaterializationBindings.Publication, definition.Document.Definition.Sources.Select(input => FulfillmentMaterializationBindings.Slot(input.Input)),
            state, cancellationToken);
        using var existing = await elastic.GetAsync("/_index_template/" + ShadowElastic.TemplateName, cancellationToken);
        if (existing.StatusCode != System.Net.HttpStatusCode.NotFound)
        {
            existing.EnsureSuccessStatusCode();
            await ShadowElastic.BindAsync(elastic, definition, cancellationToken);
        }
        else
        {
            using var response = await elastic.PutAsync("/_index_template/" + ShadowElastic.TemplateName,
            new StringContent(ShadowElastic.Template, Encoding.UTF8, "application/json"), cancellationToken);
            response.EnsureSuccessStatusCode();
        }
        await state.EnsureCreatedAsync(OperationContext.Create(cancellationToken: cancellationToken));
    }
}
