using System.Text;
using AspireFirst.Orders;
using Cohesive.Adapters.Postgres;
using Cohesive.Prelude;
using Npgsql;

namespace AspireFirst.Shadow;

internal static class ShadowBootstrap
{
    // Session lock covers target writes as well as setup. One shadow writer, including across process restart.
    internal const long WriterLock = 0x434F4853484457;

    internal static async Task InitializeAsync(FulfillmentApplication application, NpgsqlDataSource database,
        HttpClient elastic, CancellationToken cancellationToken)
    {
        await application.Storage.Bindings.PrepareSchemaAsync(database, cancellationToken);
        var definition = new FulfillmentShadowDefinition(application.Queries);
        var mappings = new FulfillmentPostgresMappings(application.Domain);
        var tables = new[] { mappings.Orders, mappings.Reservations, mappings.Inventory };
        await using (var connection = await database.OpenConnectionAsync(cancellationToken))
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            using var identifiers = new NpgsqlCommandBuilder();
            var names = tables.Select(mapping => identifiers.QuoteIdentifier(mapping.Table.SchemaName!.Value.Value) + "." +
                identifiers.QuoteIdentifier(mapping.Table.TableName.Value)).ToArray();
            foreach (var name in names)
                await new NpgsqlCommand("ALTER TABLE " + name + " REPLICA IDENTITY FULL", connection, transaction).ExecuteNonQueryAsync(cancellationToken);
            await using var exists = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_publication WHERE pubname = $1)", connection, transaction);
            exists.Parameters.AddWithValue(FulfillmentShadowRuntime.Publication);
            if (!(bool)(await exists.ExecuteScalarAsync(cancellationToken))!)
                await new NpgsqlCommand("CREATE PUBLICATION " + identifiers.QuoteIdentifier(FulfillmentShadowRuntime.Publication) +
                    " FOR TABLE " + string.Join(", ", names) + " WITH (publish = 'insert, update, delete')", connection, transaction).ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            foreach (var input in definition.Document.Definition.Sources)
            {
                await using var slot = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_replication_slots WHERE slot_name = $1)", connection);
                slot.Parameters.AddWithValue(FulfillmentShadowRuntime.Slot(input.Input));
                if (!(bool)(await slot.ExecuteScalarAsync(cancellationToken))!)
                {
                    // An absent slot cannot be reconstructed from an old durable cursor.
                    await using var ledger = new NpgsqlCommand("SELECT to_regclass('cohesive.materialization_state_ledgers') IS NOT NULL", connection);
                    if ((bool)(await ledger.ExecuteScalarAsync(cancellationToken))!)
                    {
                        await using var progress = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM cohesive.materialization_state_ledgers WHERE authority_id LIKE $1)", connection);
                        progress.Parameters.AddWithValue(FulfillmentShadowRuntime.Authority + "/%");
                        if ((bool)(await progress.ExecuteScalarAsync(cancellationToken))!)
                            throw new InvalidOperationException("A shadow replication slot is missing while durable progress exists. Recover the slot or explicitly reset the dedicated shadow generation and its state; initialization cannot repair lost WAL.");
                    }
                    await using var create = new NpgsqlCommand("SELECT pg_create_logical_replication_slot($1, 'pgoutput')", connection);
                    create.Parameters.AddWithValue(FulfillmentShadowRuntime.Slot(input.Input));
                    await create.ExecuteNonQueryAsync(cancellationToken);
                }
            }
        }
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
        await new PostgresMaterializationStateStore(database, new(FulfillmentShadowRuntime.Authority))
            .EnsureCreatedAsync(OperationContext.Create(cancellationToken: cancellationToken));
    }
}
