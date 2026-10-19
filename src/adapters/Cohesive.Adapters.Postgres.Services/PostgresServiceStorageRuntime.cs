using Cohesive.Api.Execution.Services;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Cohesive.Adapters.Postgres.Services;

internal sealed record PostgresServiceStorageRuntime(PostgresPersistenceRegistration Persistence,
    PostgresTransitionReceiptStorage? Receipts, PostgresEntityHistoryStorage? History, PostgresEntityOutboxStorage? Outbox)
{
    internal IServicePersistence Services => Persistence.ForServices(Receipts, History, Outbox);
    internal static async Task<PostgresServiceStorageRuntime> PrepareAsync(PostgresPersistenceBindings bindings,
        IServiceProvider services, string authority, CancellationToken token)
    {
        var database = services.GetRequiredKeyedService<NpgsqlDataSource>(bindings.Database.Value);
        bindings.HistoryConfiguration?.ValidateNativeResources(services, bindings.Database.Value, database);
        await bindings.PrepareSchemaAsync(database, token, services).ConfigureAwait(false);
        var history = bindings.HistoryConfiguration is { } configuration
            ? await configuration.PrepareAsync(services, bindings.Database.Value, database, token).ConfigureAwait(false) : null;
        var receipts = await bindings.PrepareReceiptsAsync(database, token).ConfigureAwait(false);
        var outbox = await bindings.PrepareOutboxAsync(database, token).ConfigureAwait(false);
        return new(bindings.Bind(database, authority), receipts, history, outbox);
    }
}
