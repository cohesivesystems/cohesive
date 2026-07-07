using Cohesive.Storage;

namespace Cohesive.Storage.Seeding;

/// <summary>
/// Result of seeding a repository batch.
/// </summary>
/// <param name="Items">Per-item seed outcomes.</param>
public sealed record RepositorySeedResult(IReadOnlyList<RepositorySeedItemResult> Items)
{
    /// <summary>
    /// Number of items that were written.
    /// </summary>
    public int WrittenCount => Items.Count(static item => item.Status is RepositorySeedItemStatuses.Created or RepositorySeedItemStatuses.Replaced);

    /// <summary>
    /// Number of items skipped because existing entities should be preserved.
    /// </summary>
    public int SkippedCount => Items.Count(static item => item.Status == RepositorySeedItemStatuses.Skipped);
}

/// <summary>
/// Describes the outcome of seeding one entity state.
/// </summary>
/// <param name="Type">Requested entity type or alias.</param>
/// <param name="Id">Seeded entity identifier.</param>
/// <param name="EntityType">Resolved semantic entity type.</param>
/// <param name="EntityShapeId">Resolved entity shape identifier.</param>
/// <param name="Status">Outcome status.</param>
/// <param name="ConcurrencyToken">Concurrency token of the written or existing entity.</param>
public sealed record RepositorySeedItemResult(
    string Type,
    string Id,
    string EntityType,
    string EntityShapeId,
    string Status,
    string? ConcurrencyToken
    );

/// <summary>
/// Seed result status constants.
/// </summary>
public static class RepositorySeedItemStatuses
{
    /// <summary>
    /// The entity did not exist and was created.
    /// </summary>
    public const string Created = "created";

    /// <summary>
    /// The entity existed and was replaced.
    /// </summary>
    public const string Replaced = "replaced";

    /// <summary>
    /// The entity existed and was left unchanged.
    /// </summary>
    public const string Skipped = "skipped";
}

/// <summary>
/// Error raised when a seed plan is structurally invalid before repository persistence.
/// </summary>
public sealed class RepositorySeedException : Exception
{
    /// <summary>
    /// Creates a repository seed exception.
    /// </summary>
    public RepositorySeedException(string message) : base(message)
    {
    }
}

/// <summary>
/// Resolved write request for a concrete entity repository.
/// </summary>
/// <param name="Type">Requested seed type or semantic source label.</param>
/// <param name="Repository">Target entity repository.</param>
/// <param name="Write">Observation write request.</param>
/// <param name="ExistingReadOptions">Optional read options used to check whether the target entity already exists.</param>
public sealed record RepositorySeedWrite(
    string Type,
    IEntityRepository Repository,
    EntityWriteRequest Write,
    EntityReadOptions? ExistingReadOptions = null
    )
{
    /// <summary>
    /// Entity id carried by the write request.
    /// </summary>
    public string Id => Write.Entity.Id;
}

/// <summary>
/// Controls repository seed write behavior.
/// </summary>
/// <param name="SkipExisting">When true, existing entities are left unchanged.</param>
/// <param name="Atomicity">Requested batch atomicity for writes grouped by repository.</param>
public sealed record RepositorySeedWriteOptions(
    bool SkipExisting = false,
    EntityBatchAtomicity Atomicity = EntityBatchAtomicity.None
    );

/// <summary>
/// Shared seed writer for already resolved entity repository writes.
/// </summary>
public sealed class RepositorySeedWriter
{
    /// <summary>
    /// Seeds all writes, grouped by repository, using the repository batch surface where available.
    /// </summary>
    public async Task<RepositorySeedResult> Seed(
        OperationContext context,
        IReadOnlyList<RepositorySeedWrite> writes,
        RepositorySeedWriteOptions? options = null
        )
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(writes);
        context.ThrowIfCancellationRequested();

        options ??= new();
        if (writes.Count == 0)
            return new([]);

        var results = new RepositorySeedItemResult?[writes.Count];
        var groups = GroupByRepository(writes);
        foreach (var group in groups)
        {
            await SeedRepositoryGroup(
                    context,
                    group.Key,
                    group.Value,
                    options,
                    results)
                .ConfigureAwait(false);
        }

        var completed = results
            .Select(static result => result ?? throw new InvalidOperationException("Repository seed result was not recorded."))
            .ToArray();
        return new(completed);
    }

    static Dictionary<IEntityRepository, List<IndexedRepositorySeedWrite>> GroupByRepository(IReadOnlyList<RepositorySeedWrite> writes)
    {
        Dictionary<IEntityRepository, List<IndexedRepositorySeedWrite>> groups = new(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < writes.Count; i++)
        {
            var write = ValidateWrite(writes[i]);
            if (!groups.TryGetValue(write.Repository, out var group))
            {
                group = [];
                groups.Add(write.Repository, group);
            }

            group.Add(new(i, write));
        }

        return groups;
    }

    static async Task SeedRepositoryGroup(
        OperationContext context,
        IEntityRepository repository,
        IReadOnlyList<IndexedRepositorySeedWrite> group,
        RepositorySeedWriteOptions options,
        RepositorySeedItemResult?[] results
        )
    {
        List<PendingRepositorySeedWrite> pending = [];
        foreach (var (index, seed) in group)
        {
            var existing = await repository.TryGet(
                context,
                seed.Id,
                seed.ExistingReadOptions ?? EntityReadOptions.Full
                ).ConfigureAwait(false);
            if (existing is not null && options.SkipExisting)
            {
                results[index] = CreateResult(seed, repository, RepositorySeedItemStatuses.Skipped, existing.ConcurrencyToken);
                continue;
            }

            pending.Add(new(index, seed, existing));
        }

        if (pending.Count == 0)
            return;

        var batch = await repository.UpsertBatch(
                context,
                new(
                    Writes: pending.Select(static item => item.Write.Write).ToArray(),
                    Atomicity: options.Atomicity
                    ))
            .ConfigureAwait(false);

        if (batch.Snapshots.Count != pending.Count)
        {
            throw new InvalidOperationException(
                $"Repository '{repository.EntityType}' returned {batch.Snapshots.Count} snapshot(s) for {pending.Count} seed write(s).");
        }

        for (var i = 0; i < pending.Count; i++)
        {
            var item = pending[i];
            var status = item.Existing is null ? RepositorySeedItemStatuses.Created : RepositorySeedItemStatuses.Replaced;
            results[item.Index] = CreateResult(item.Write, repository, status, batch.Snapshots[i].ConcurrencyToken);
        }
    }

    static RepositorySeedWrite ValidateWrite(RepositorySeedWrite write)
    {
        ArgumentNullException.ThrowIfNull(write);
        ArgumentException.ThrowIfNullOrWhiteSpace(write.Type);
        ArgumentNullException.ThrowIfNull(write.Repository);
        ArgumentNullException.ThrowIfNull(write.Write);
        ArgumentNullException.ThrowIfNull(write.Write.Entity);

        if (write.Write.Entity.ShapeId != write.Repository.EntityDefinition.Shape.Id)
        {
            throw new RepositorySeedException(
                $"Seed write '{write.Type}:{write.Write.Entity.Id}' targets shape '{write.Write.Entity.ShapeId.Value}', " +
                $"but repository '{write.Repository.EntityDefinition.Name.Value}' handles shape '{write.Repository.EntityDefinition.Shape.Id.Value}'.");
        }

        return write;
    }

    static RepositorySeedItemResult CreateResult(
        RepositorySeedWrite write,
        IEntityRepository repository,
        string status,
        EntityConcurrencyToken? concurrencyToken
        ) => new(
            Type: write.Type,
            Id: write.Id,
            EntityType: repository.EntityDefinition.Name.Value,
            EntityShapeId: repository.EntityDefinition.Shape.Id.Value,
            Status: status,
            ConcurrencyToken: concurrencyToken?.Value
        );

    readonly record struct IndexedRepositorySeedWrite(
        int Index,
        RepositorySeedWrite Write
        );

    readonly record struct PendingRepositorySeedWrite(
        int Index,
        RepositorySeedWrite Write,
        EntitySnapshot? Existing
        );
}
