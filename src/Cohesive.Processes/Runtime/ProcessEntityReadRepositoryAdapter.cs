using Cohesive.Relations.Model;

namespace Cohesive.Processes.Runtime;

/// <summary>
/// Adapts point-read process entity repositories to the query projection read-repository contract.
/// </summary>
public sealed class ProcessEntityReadRepositoryAdapter : IReadRepository
{
    readonly IProcessEntityRepository entityRepository;
    readonly string entityType;
    readonly Func<string, string?> partitionKeySelector;

    /// <summary>
    /// Creates an adapter over a process entity repository for one entity type.
    /// </summary>
    public ProcessEntityReadRepositoryAdapter(
        IProcessEntityRepository entityRepository,
        string entityType,
        Func<string, string?>? partitionKeySelector = null
        )
    {
        this.entityRepository = Guard.RequireNotNull(entityRepository);
        this.entityType = Guard.RequireNotNullOrWhiteSpace(entityType);
        this.partitionKeySelector = partitionKeySelector ?? (static _ => null);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, Observation>> GetByIds(OperationContext context, IReadOnlyCollection<string> ids, FieldSelection? options = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(ids);
        context.ThrowIfCancellationRequested();

        Dictionary<string, Observation> result = new(StringComparer.Ordinal);
        var read = ToProcessReadRequest(options);
        foreach (var id in ids.Where(static id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal))
        {
            try
            {
                var snapshot = await entityRepository
                    .Get(
                        context,
                        new(entityType, id, partitionKeySelector(id)),
                        read)
                    .ConfigureAwait(false);

                result[id] = snapshot.State.Observation;
            }
            catch (SemanticRuleViolationException ex) when (IsEntityNotFound(ex))
            {
            }
        }

        return result;
    }

    static ProcessEntityReadOptions? ToProcessReadRequest(FieldSelection? read) =>
        read is null
            ? null
            : new ProcessEntityReadOptions(fieldSelection: read);

    // TODO: make not found semantic exceptions explicit
    static bool IsEntityNotFound(SemanticRuleViolationException ex) =>
        ex.Message.Contains("was not found", StringComparison.Ordinal);
}
