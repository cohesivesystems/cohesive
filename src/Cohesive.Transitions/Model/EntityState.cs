namespace Cohesive.Transitions.Model;

/// <summary>
/// Immutable entity state backed by one explicit identity-bearing semantic snapshot.
/// </summary>
public sealed class EntityState
{
    readonly EntityStateLineage lineage;

    /// <summary>
    /// Creates a state from an identified, versioned semantic snapshot.
    /// </summary>
    public EntityState(EntityObservationSnapshot snapshot)
        : this(snapshot, new())
    {
    }

    internal EntityState(
        EntityObservationSnapshot snapshot,
        EntityStateLineage lineage
        )
    {
        Snapshot = Guard.RequireNotNull(snapshot);
        this.lineage = Guard.RequireNotNull(lineage);
        this.lineage.Current = this;
    }

    /// <summary>
    /// Identified and versioned semantic state.
    /// </summary>
    public EntityObservationSnapshot Snapshot { get; }

    /// <summary>Identity-free semantic value carried by <see cref="Snapshot"/>.</summary>
    public Observation Observation => Snapshot.Observation;

    /// <summary>
    /// Entity identity carried by the snapshot.
    /// </summary>
    public EntityId EntityId => Snapshot.EntityId;

    /// <summary>
    /// State version carried by the snapshot.
    /// </summary>
    public long Version => Snapshot.Version;

    /// <summary>
    /// Field values keyed by declared field name.
    /// </summary>
    public IReadOnlyDictionary<string, ObservationValue> Fields => Observation.Fields;

    /// <summary>
    /// Materializes the underlying observation through the deterministic core plan.
    /// </summary>
    public T Populate<T>() => Observation.Materialize<T>();

    /// <summary>
    /// Materializes the underlying observation through an explicitly configured core plan.
    /// </summary>
    public T Populate<T>(Action<ObservationMaterializerBuilder<T>> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var builder = ObservationMaterializer.For<T>(Observation.ShapeId);
        configure(builder);
        return builder.Compile().Materialize(Observation);
    }

    internal EntityStateLineage Lineage => lineage;

    /// <summary>
    /// Attempts to read a field value by field definition.
    /// </summary>
    public bool TryGet(FieldDefinition field, out ObservationValue value)
    {
        ArgumentNullException.ThrowIfNull(field);
        return Observation.TryGetField(field, out value);
    }

    /// <summary>
    /// Returns a field value by field definition.
    /// </summary>
    /// <exception cref="KeyNotFoundException"></exception>
    public ObservationValue Get(FieldDefinition field)
    {
        ArgumentNullException.ThrowIfNull(field);
        return Observation.GetField(field);
    }
}

sealed class EntityStateLineage
{
    public EntityState Current { get; set; } = null!;
}
