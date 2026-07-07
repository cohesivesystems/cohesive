using System.Collections.Concurrent;
using Cohesive.Relations.Mapping;
using Cohesive.Relations.Model;

namespace Cohesive.Storage;

/// <summary>
/// Maps persisted entity snapshots to contract objects through a compiled observation projection.
/// </summary>
public sealed class EntitySnapshotObjectMapper<TProjection, TResult>(
    EntityReadOptions readOptions,
    Func<TProjection, EntitySnapshot, TResult> map,
    Action<ObservationObjectMapperBuilder<TProjection>>? configureProjection = null,
    ShapeMappingContext? mappingContext = null
    )
{
    readonly Func<TProjection, EntitySnapshot, TResult> map = Guard.RequireNotNull(map);
    readonly ShapeMappingContext mappingContext = mappingContext ?? ShapeMappingContext.Default;
    readonly ConcurrentDictionary<LayoutKey, ObservationObjectMapper<TProjection>> projectionMappers = [];

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
        var projection = GetProjectionMapper(snapshot.Entity.Layout).Map(snapshot.Entity);
        return map(projection, snapshot);
    }

    ObservationObjectMapper<TProjection> GetProjectionMapper(ObservationLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var key = new LayoutKey(layout.Schema.Value, BuildFieldSignature(layout));
        if (projectionMappers.TryGetValue(key, out var mapper))
            return mapper;

        var builder = mappingContext.ForObservationObject<TProjection>(layout);
        configureProjection?.Invoke(builder);
        mapper = builder.Build();
        return projectionMappers.GetOrAdd(key, mapper);
    }

    static EntityReadOptions RequireProjectedReadOptions(EntityReadOptions readOptions)
    {
        ArgumentNullException.ThrowIfNull(readOptions);
        if (readOptions.Fields is null || readOptions.Fields.Count == 0)
            throw new ArgumentException("Entity snapshot object mappers require a non-empty field projection.", nameof(readOptions));

        return readOptions;
    }

    static string BuildFieldSignature(ObservationLayout layout) =>
        string.Join('\u001f', layout.FieldNames);

    readonly record struct LayoutKey(string SchemaId, string FieldSignature);
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
        Action<ObservationObjectMapperBuilder<TProjection>>? configureProjection = null,
        ShapeMappingContext? mappingContext = null
        ) =>
        new(readOptions, map, configureProjection, mappingContext);
}
