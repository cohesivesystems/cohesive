using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Cohesive.Api.Execution.Services;
using Cohesive.Model;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.Execution;
using Cohesive.Storage;

namespace Cohesive.Adapters.Postgres.Services;

/// <summary>Attaches native PostgreSQL mappings to application service resolution.</summary>
public static class PostgresServicePersistenceExtensions
{
    /// <summary>Prepares model-owned storage using the DI-owned source keyed by the declared database identity.</summary>
    /// <param name="bindings">Frozen storage declaration.</param>
    /// <param name="services">Host services owning the native data source and optional history client.</param>
    /// <param name="authority">Repository provenance authority.</param>
    /// <param name="cancellationToken">Preparation cancellation.</param>
    /// <returns>Prepared persistence borrowing the native clients.</returns>
    /// <exception cref="ArgumentNullException">Bindings or services is null.</exception>
    /// <exception cref="InvalidOperationException">The keyed source is absent or storage admission fails.</exception>
    public static Task<PostgresPreparedPersistence> PrepareAsync(this PostgresPersistenceBindings bindings,
        IServiceProvider services, string authority, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(services);
        return bindings.PrepareAsync(services, services.GetRequiredKeyedService<NpgsqlDataSource>(bindings.Database.Value), authority, cancellationToken);
    }

    /// <summary>Exposes the prepared registration and its model-declared attachments to services.</summary>
    /// <param name="prepared">Prepared persistence borrowing caller-owned native clients.</param>
    /// <returns>A resolver sharing repositories and queries without opening a connection.</returns>
    /// <exception cref="ArgumentNullException">Prepared persistence is null.</exception>
    public static IServicePersistence ForServices(this PostgresPreparedPersistence prepared)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        return prepared.Persistence.ForServices(prepared.Receipts, prepared.History, prepared.Outbox);
    }

    /// <summary>Creates one host-owned repository scope over existing native mappings.</summary>
    /// <param name="persistence">Caller-owned native persistence.</param>
    /// <param name="receipts">Optional validated receipt storage from the same data source.</param>
    /// <param name="stateHistory">Optional schema-validated state authority for all registered entities.</param>
    /// <param name="outbox">Optional validated direct-transition outbox, independent of state authority.</param>
    /// <returns>A resolver sharing repositories by exact observation contract. No connection is opened.</returns>
    public static IServicePersistence ForServices(this PostgresPersistenceRegistration persistence,
        PostgresTransitionReceiptStorage? receipts = null, PostgresEntityHistoryStorage? stateHistory = null, PostgresEntityOutboxStorage? outbox = null)
    {
        ArgumentNullException.ThrowIfNull(persistence);
        return new ServicePersistence(persistence, receipts, stateHistory, outbox);
    }

    sealed class ServicePersistence(PostgresPersistenceRegistration persistence, PostgresTransitionReceiptStorage? receipts, PostgresEntityHistoryStorage? stateHistory, PostgresEntityOutboxStorage? outbox) : IServicePersistence
    {
        readonly Dictionary<ValueContract, IEntityRepository> repositories = [];
        readonly Dictionary<object, Dictionary<(int Rows, long Bytes), object>> queries = new(ReferenceEqualityComparer.Instance);
        public IEntityRepository Repository(ValueContract observation)
        {
            lock (repositories)
            {
                if (!repositories.TryGetValue(observation, out var repository))
                {
                    var native = (PostgresEntityRepository)persistence.Repository(observation, receipts, stateHistory);
                    repository = outbox is null ? native : new PostgresEntityOutboxRepository(native, outbox);
                    repositories.Add(observation, repository);
                }
                return repository;
            }
        }
        public IRelationQueryReader<TInput, TResult> Query<TInput, TResult>(RelationQuery<TInput, TResult> query,
            int maximumRows, long maximumBytes, RelationQueryConsistency consistency)
        {
            ArgumentNullException.ThrowIfNull(query);
            lock (queries)
            {
                if (!queries.TryGetValue(query, out var bounded)) queries.Add(query, bounded = []);
                var key = (maximumRows, maximumBytes);
                if (!bounded.TryGetValue(key, out var reader))
                    bounded.Add(key, reader = persistence.Query(query, maximumRows, maximumBytes));
                return ServiceQueryConsistencyValidation.Require((IRelationQueryReader<TInput, TResult>)reader, consistency);
            }
        }
    }
}
