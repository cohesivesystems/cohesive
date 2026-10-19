using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Cohesive.Adapters.Postgres.Services;

internal static class PostgresServiceStorageRuntime
{
    internal static Task<PostgresPreparedPersistence> PrepareAsync(PostgresPersistenceBindings bindings,
        IServiceProvider services, string authority, CancellationToken token) =>
        bindings.PrepareAsync(services, services.GetRequiredKeyedService<NpgsqlDataSource>(bindings.Database.Value), authority, token);
}
