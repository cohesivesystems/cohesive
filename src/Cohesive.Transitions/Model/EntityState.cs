using Cohesive.Relations.Mapping;
using Cohesive.Relations.Model;

namespace Cohesive.Transitions.Model;

/// <summary>
/// Immutable entity state backed by an observation carrying identity, version, and field values.
/// </summary>
public sealed class EntityState
{
    readonly EntityStateLineage lineage;

    /// <summary>
    /// Creates a state snapshot from an observation.
    /// </summary>
    public EntityState(Observation observation)
        : this(observation, new())
    {
    }

    internal EntityState(
        Observation observation,
        EntityStateLineage lineage
        )
    {
        Observation = Guard.RequireNotNull(observation);
        this.lineage = Guard.RequireNotNull(lineage);
        this.lineage.Current = this;
    }

    /// <summary>
    /// Observation carrying identity, version, and field values by canonical field name.
    /// </summary>
    public Observation Observation { get; }

    /// <summary>
    /// Entity identity carried by the underlying observation.
    /// </summary>
    public EntityId EntityId => new(Observation.Id);

    /// <summary>
    /// State version carried by the underlying observation.
    /// </summary>
    public long Version => Observation.Version;

    /// <summary>
    /// Field values keyed by declared field name.
    /// </summary>
    public IReadOnlyDictionary<string, ObservationValue> Fields => Observation.Fields;

    /// <summary>
    /// Materializes the underlying observation into a CLR shape using the shared shape mapper.
    /// </summary>
    public T Populate<T>(ShapeMappingContext? mappingContext = null)
        => Observation.Map<T>(mappingContext);

    /// <summary>
    /// Materializes the underlying observation into a CLR shape using an explicitly configured mapper.
    /// </summary>
    public T Populate<T>(Action<ObservationObjectMapperBuilder<T>> configure, ShapeMappingContext? mappingContext = null)
        => Observation.Map(configure, mappingContext);

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
