namespace Cohesive.Model;

/// <summary>
/// Immutable versioned state of one identified entity, expressed as an identity-free semantic observation.
/// </summary>
/// <remarks>
/// This type owns semantic entity identity and entity-state version. Storage partition, concurrency token,
/// projection completeness, relation occurrence, and derivation lineage are separate interpretations and are not
/// part of an entity observation snapshot.
/// </remarks>
public sealed record EntityObservationSnapshot
{
    /// <summary>Creates one identified, versioned entity-state snapshot.</summary>
    /// <param name="entityId">Stable identity of the entity whose state was observed.</param>
    /// <param name="version">Non-negative version of the entity state.</param>
    /// <param name="observation">Identity-free concrete state governed by an exact qualified shape.</param>
    /// <exception cref="ArgumentException"><paramref name="entityId"/> is default.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="version"/> is negative.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="observation"/> is <see langword="null"/>.</exception>
    public EntityObservationSnapshot(
        EntityId entityId,
        long version,
        Observation observation)
    {
        if (string.IsNullOrWhiteSpace(entityId.Value))
            throw new ArgumentException("An entity observation snapshot requires an entity identity.", nameof(entityId));
        if (version < 0)
            throw new ArgumentOutOfRangeException(nameof(version), version, "An entity-state version cannot be negative.");

        EntityId = entityId;
        Version = version;
        Observation = Guard.RequireNotNull(observation);
    }

    /// <summary>Gets the stable identity of the entity whose state was observed.</summary>
    public EntityId EntityId { get; }

    /// <summary>Gets the non-negative entity-state version.</summary>
    public long Version { get; }

    /// <summary>Gets the identity-free concrete semantic state.</summary>
    public Observation Observation { get; }
}
