using System.Text;
using AspireFirst.Orders;
using Cohesive.Adapters.Postgres;
using Cohesive.Storage.Materialization;
using Npgsql;

namespace AspireFirst.Materialization;

internal static class ShadowBootstrap
{
    // Session lock covers target writes as well as setup. One shadow writer, including across process restart.
    internal const long WriterLock = 0x434F4853484457;

    internal static async Task InitializeAsync(OrderMaterializationApplication model, NpgsqlDataSource database,
        HttpClient elastic, CancellationToken cancellationToken)
    {
        await model.Orders.Storage.Bindings.PrepareSchemaAsync(database, cancellationToken);
        await model.Storage.OrderDetails.InitializeSourcesAsync(database, cancellationToken);
        await InitializeTargetAsync(model.Materializations.OrderDetails, elastic, cancellationToken);
    }

    internal static async Task InitializeTargetAsync(PreparedMaterialization declaration, HttpClient elastic, CancellationToken cancellationToken)
    {
        using var existing = await elastic.GetAsync("/_index_template/" + ShadowElastic.TemplateName, cancellationToken);
        if (existing.StatusCode != System.Net.HttpStatusCode.NotFound)
        {
            existing.EnsureSuccessStatusCode();
            await ShadowElastic.BindAsync(elastic, declaration, cancellationToken);
        }
        else
        {
            using var response = await elastic.PutAsync("/_index_template/" + ShadowElastic.TemplateName,
            new StringContent(ShadowElastic.Template, Encoding.UTF8, "application/json"), cancellationToken);
            response.EnsureSuccessStatusCode();
        }
    }
}
