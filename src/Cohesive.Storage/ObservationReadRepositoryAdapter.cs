using Cohesive.Relations.Model;
using Cohesive.Relations.Queries;

namespace Cohesive.Storage;

/// <summary>
/// Adapts point-read observation repositories to the temporary legacy read-repository contract.
/// </summary>
/// <remarks>
/// Retained only for Cosmos compatibility until the legacy Relations query namespace is removed. Canonical
/// integrations implement <see cref="Cohesive.Relations.Acquisition.IRelationQuerySourceReader"/> instead.
/// </remarks>
/// <param name="repository">Point-read entity repository to adapt.</param>
/// <exception cref="ArgumentNullException"><paramref name="repository"/> is <see langword="null"/>.</exception>
public sealed class ObservationReadRepositoryAdapter(IEntityRepository repository) : IReadRepository
{
    readonly IEntityRepository repository = Guard.RequireNotNull(repository);

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, Observation>> GetByIds(OperationContext context, IReadOnlyCollection<string> ids, FieldSelection? options = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(ids);
        context.ThrowIfCancellationRequested();

        Dictionary<string, Observation> result = new(StringComparer.Ordinal);
        var readOptions = ToObservationReadOptions(options);
        
        foreach (var id in ids.Where(static id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal))
        {
            var snapshot = await repository.TryGet(context, id, readOptions).ConfigureAwait(false);
            if (snapshot is not null)
                result[id] = snapshot.Entity;
        }

        return result;
    }

    static EntityReadOptions? ToObservationReadOptions(FieldSelection? fields) => fields is null ? null : new EntityReadOptions(fieldSelection: fields);
}
