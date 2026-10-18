using Npgsql;

namespace Cohesive.Adapters.Postgres;

/// <summary>Offline history policy whose adapter borrows native clients during storage preparation.</summary>
public interface IPostgresEntityHistoryConfiguration
{
    /// <summary>Prepares history against the exact relational data source without owning its lifetime.</summary>
    /// <param name="services">Host provider owning native services.</param>
    /// <param name="database">Existing infrastructure database identity used for keyed client selection.</param>
    /// <param name="dataSource">Exact relational source required for atomic commit.</param>
    /// <param name="cancellationToken">Cancellation of preparation.</param>
    /// <returns>Validated history attachment.</returns>
    ValueTask<PostgresEntityHistoryStorage> PrepareAsync(IServiceProvider services, string database,
        NpgsqlDataSource dataSource, CancellationToken cancellationToken = default);
}
