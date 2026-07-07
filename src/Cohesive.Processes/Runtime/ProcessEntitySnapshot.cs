namespace Cohesive.Processes.Runtime;

/// <summary>
/// Durable entity snapshot used by process transitions.
/// </summary>
public sealed record ProcessEntitySnapshot
{
    /// <summary>
    /// Creates a durable process-entity snapshot.
    /// </summary>
    /// <param name="entity">Entity reference represented by the snapshot.</param>
    /// <param name="state">Logical entity state.</param>
    /// <param name="concurrencyToken">Storage-specific optimistic concurrency token.</param>
    /// <param name="loadedFields">Loaded field subset, or <see langword="null"/> when the snapshot contains the full entity state.</param>
    public ProcessEntitySnapshot(
        ProcessEntityRef entity,
        EntityState state,
        ProcessEntityConcurrencyToken concurrencyToken,
        IReadOnlySet<string>? loadedFields = null
        )
    {
        Entity = Guard.RequireNotNull(entity);
        State = Guard.RequireNotNull(state);
        ConcurrencyToken = concurrencyToken;
        LoadedFields = loadedFields;
    }

    /// <summary>
    /// Entity reference represented by the snapshot.
    /// </summary>
    public ProcessEntityRef Entity { get; }

    /// <summary>
    /// Logical entity state.
    /// </summary>
    public EntityState State { get; }

    /// <summary>
    /// Storage-specific optimistic concurrency token (e.g. ETag, version)
    /// </summary>
    public ProcessEntityConcurrencyToken ConcurrencyToken { get; }

    /// <summary>
    /// Loaded field subset, or <see langword="null"/> when the snapshot contains the full entity state.
    /// </summary>
    public IReadOnlySet<string>? LoadedFields { get; }

    /// <summary>
    /// Indicates whether the snapshot contains the full entity state.
    /// </summary>
    public bool HasFullState => LoadedFields is null;

    /// <summary>
    /// Logical entity version carried by <see cref="State"/>.
    /// </summary>
    public long Version => State.Version;
}
