using System.Collections.Concurrent;

namespace Cohesive.Storage;

/// <summary>
/// Maps persisted entity snapshots to contract objects through a compiled observation projection.
/// </summary>
public sealed class EntitySnapshotObjectMapper<TProjection, TResult>(
    EntityReadOptions readOptions,
    Func<TProjection, EntitySnapshot, TResult> map,
    Action<ObservationMaterializerBuilder<TProjection>>? configureProjection = null
    )
{
    readonly Func<TProjection, EntitySnapshot, TResult> map = Guard.RequireNotNull(map);
    readonly ConcurrentDictionary<QualifiedShapeId, ObservationMaterializer<TProjection>> projectionMappers = [];

    /// <summary>
    /// Read options that describe the entity fields needed to map this contract.
    /// </summary>
    public EntityReadOptions ReadOptions { get; } = RequireProjectedReadOptions(readOptions);

    /// <summary>
    /// Maps a snapshot to the configured result contract.
    /// </summary>
    public TResult Map(EntitySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var projection = GetProjectionMapper(snapshot.Entity.Observation.ShapeId)
            .Materialize(snapshot.Entity.Observation);
        return map(projection, snapshot);
    }

    ObservationMaterializer<TProjection> GetProjectionMapper(QualifiedShapeId shape)
    {
        if (projectionMappers.TryGetValue(shape, out var mapper))
            return mapper;

        var builder = ObservationMaterializer.For<TProjection>(shape);
        configureProjection?.Invoke(builder);
        mapper = builder.Compile();
        return projectionMappers.GetOrAdd(shape, mapper);
    }

    static EntityReadOptions RequireProjectedReadOptions(EntityReadOptions readOptions)
    {
        ArgumentNullException.ThrowIfNull(readOptions);
        if (readOptions.Fields is null || readOptions.Fields.Count == 0)
            throw new ArgumentException("Entity snapshot object mappers require a non-empty field projection.", nameof(readOptions));

        return readOptions;
    }

}

/// <summary>
/// Factory methods for <see cref="EntitySnapshotObjectMapper{TProjection,TResult}"/>.
/// </summary>
public static class EntitySnapshotObjectMapper
{
    /// <summary>
    /// Creates a snapshot mapper backed by the observation-object mapper pipeline.
    /// </summary>
    public static EntitySnapshotObjectMapper<TProjection, TResult> Create<TProjection, TResult>(
        EntityReadOptions readOptions,
        Func<TProjection, EntitySnapshot, TResult> map,
        Action<ObservationMaterializerBuilder<TProjection>>? configureProjection = null
        ) =>
        new(readOptions, map, configureProjection);
}
