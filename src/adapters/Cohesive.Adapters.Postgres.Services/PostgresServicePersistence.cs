using Cohesive.Api.Execution.Services;
using Cohesive.Model;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.Execution;
using Cohesive.Storage;

namespace Cohesive.Adapters.Postgres.Services;

/// <summary>Attaches native PostgreSQL mappings to application service resolution.</summary>
public static class PostgresServicePersistenceExtensions
{
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
