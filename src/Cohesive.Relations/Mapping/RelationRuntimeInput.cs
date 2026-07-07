namespace Cohesive.Relations.Mapping;

/// <summary>
/// Runtime input item for relation execution, allowing per-input mapping overrides.
/// </summary>
public sealed record RelationRuntimeInput
{
    /// <summary>
    /// Creates a runtime input.
    /// </summary>
    public RelationRuntimeInput(
        object value,
        ShapeId? schemaId = null,
        ObjectObservationMetadata? metadata = null)
    {
        Value = Guard.RequireNotNull(value);
        SchemaId = schemaId;
        Metadata = metadata;
    }

    /// <summary>
    /// CLR DTO instance or pre-built observed shape.
    /// </summary>
    public object Value { get; init; }

    /// <summary>
    /// Optional schema override when mapping CLR DTO inputs.
    /// </summary>
    public ShapeId? SchemaId { get; init; }

    /// <summary>
    /// Optional observed-shape metadata override when mapping CLR DTO inputs.
    /// </summary>
    public ObjectObservationMetadata? Metadata { get; init; }

    /// <summary>
    /// Creates a runtime input with optional mapping overrides.
    /// </summary>
    public static RelationRuntimeInput From(
        object value,
        ShapeId? schemaId = null,
        ObjectObservationMetadata? metadata = null)
        => new(value, schemaId, metadata);
}