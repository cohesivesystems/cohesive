using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Cohesive.Model;

/// <summary>
/// Compatibility classification for a shape evolution step.
/// </summary>
public enum ShapeCompatibility
{
    /// <summary>
    /// Compatibility has not been classified.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// Existing observations that satisfy the old graph also satisfy the new graph.
    /// </summary>
    BackwardCompatible = 1,

    /// <summary>
    /// New observations can be projected to the old graph without loss.
    /// </summary>
    ForwardCompatible = 2,

    /// <summary>
    /// Compatibility depends on explicit migration/projection logic.
    /// </summary>
    RequiresMigration = 3,

    /// <summary>
    /// The change is known to be breaking without a complete migration.
    /// </summary>
    Breaking = 4
}

/// <summary>
/// One addressable revision of a root shape.
/// </summary>
public sealed record ShapeRevision
{
    /// <summary>
    /// Creates a shape revision.
    /// </summary>
    [JsonConstructor]
    public ShapeRevision(
        string id,
        ShapeId rootShapeId,
        string version,
        GraphId? graphId = null,
        string? baseRevisionId = null,
        ImmutableDictionary<AnnotationKey, AnnotationValue>? annotations = null
        )
    {
        Id = Guard.RequireNotNullOrWhiteSpace(id);
        RootShapeId = rootShapeId;
        Version = Guard.RequireNotNullOrWhiteSpace(version);
        GraphId = graphId;
        BaseRevisionId = string.IsNullOrWhiteSpace(baseRevisionId) ? null : baseRevisionId.Trim();
        Annotations = AnnotationMap.Normalize(annotations);
    }

    /// <summary>
    /// Stable revision identifier.
    /// </summary>
    public string Id { get; init; }

    /// <summary>
    /// Root shape this revision describes.
    /// </summary>
    public ShapeId RootShapeId { get; init; }

    /// <summary>
    /// Version label, for example <c>004010</c> or <c>005030</c>.
    /// </summary>
    public string Version { get; init; }

    /// <summary>
    /// Optional concrete graph id for this revision.
    /// </summary>
    public GraphId? GraphId { get; init; }

    /// <summary>
    /// Optional parent revision id.
    /// </summary>
    public string? BaseRevisionId { get; init; }

    /// <summary>
    /// Optional revision metadata.
    /// </summary>
    public ImmutableDictionary<AnnotationKey, AnnotationValue> Annotations { get; init; }
}

/// <summary>
/// Version-to-version evolution delta for a shape graph.
/// </summary>
public sealed record VersionDelta
{
    /// <summary>
    /// Creates a version delta.
    /// </summary>
    [JsonConstructor]
    public VersionDelta(
        string id,
        ShapeId rootShapeId,
        string fromVersion,
        string toVersion,
        ImmutableArray<GraphDeltaOperation> operations,
        ShapeCompatibility compatibility = ShapeCompatibility.Unknown,
        GraphId? sourceGraphId = null,
        GraphId? targetGraphId = null,
        ImmutableDictionary<AnnotationKey, AnnotationValue>? annotations = null
        )
    {
        Id = Guard.RequireNotNullOrWhiteSpace(id);
        RootShapeId = rootShapeId;
        FromVersion = Guard.RequireNotNullOrWhiteSpace(fromVersion);
        ToVersion = Guard.RequireNotNullOrWhiteSpace(toVersion);
        Operations = operations.IsDefault ? [] : operations;
        Compatibility = compatibility;
        SourceGraphId = sourceGraphId;
        TargetGraphId = targetGraphId;
        Annotations = AnnotationMap.Normalize(annotations);
    }

    /// <summary>
    /// Stable delta identifier.
    /// </summary>
    public string Id { get; init; }

    /// <summary>
    /// Root shape this evolution step applies to.
    /// </summary>
    public ShapeId RootShapeId { get; init; }

    /// <summary>
    /// Source version label.
    /// </summary>
    public string FromVersion { get; init; }

    /// <summary>
    /// Target version label.
    /// </summary>
    public string ToVersion { get; init; }

    /// <summary>
    /// Evolution operations.
    /// </summary>
    public ImmutableArray<GraphDeltaOperation> Operations { get; init; }

    /// <summary>
    /// Compatibility classification for this evolution step.
    /// </summary>
    public ShapeCompatibility Compatibility { get; init; }

    /// <summary>
    /// Optional source graph id.
    /// </summary>
    public GraphId? SourceGraphId { get; init; }

    /// <summary>
    /// Optional target graph id.
    /// </summary>
    public GraphId? TargetGraphId { get; init; }

    /// <summary>
    /// Optional evolution metadata.
    /// </summary>
    public ImmutableDictionary<AnnotationKey, AnnotationValue> Annotations { get; init; }

    /// <summary>
    /// Creates a version delta from a general graph delta.
    /// </summary>
    public static VersionDelta FromGraphDelta(
        string id,
        ShapeId rootShapeId,
        string fromVersion,
        string toVersion,
        GraphDelta delta,
        ShapeCompatibility compatibility = ShapeCompatibility.Unknown
        )
    {
        ArgumentNullException.ThrowIfNull(delta);
        return new(
            id: id,
            rootShapeId: rootShapeId,
            fromVersion: fromVersion,
            toVersion: toVersion,
            operations: delta.Operations,
            compatibility: compatibility,
            sourceGraphId: delta.SourceGraphId,
            targetGraphId: delta.TargetGraphId,
            annotations: delta.Annotations);
    }

    /// <summary>
    /// Converts this version delta to the generic graph delta form.
    /// </summary>
    public GraphDelta ToGraphDelta() => new(
        id: Id,
        operations: Operations,
        kind: GraphDeltaKind.Version,
        sourceGraphId: SourceGraphId,
        targetGraphId: TargetGraphId,
        sourceVersion: FromVersion,
        targetVersion: ToVersion,
        annotations: Annotations
        );
}

/// <summary>
/// Version graph for an evolving root shape.
/// </summary>
public sealed record ShapeEvolution
{
    /// <summary>
    /// Creates a shape evolution graph.
    /// </summary>
    [JsonConstructor]
    public ShapeEvolution(
        ShapeId rootShapeId,
        ImmutableArray<ShapeRevision> revisions,
        ImmutableArray<VersionDelta> deltas,
        ImmutableDictionary<AnnotationKey, AnnotationValue>? annotations = null
        )
    {
        RootShapeId = rootShapeId;
        Revisions = revisions.IsDefault ? [] : revisions;
        Deltas = deltas.IsDefault ? [] : deltas;
        Annotations = AnnotationMap.Normalize(annotations);

        var duplicateRevision = Revisions.TryGetDuplicateByKey(x => x.Id, StringComparer.Ordinal);
        if (duplicateRevision is not null)
            throw new ArgumentException($"Shape evolution contains duplicate revision '{duplicateRevision.Id}'.", nameof(revisions));

        if (Revisions.Any(x => x.RootShapeId != RootShapeId))
            throw new ArgumentException("All revisions must target the evolution root shape.", nameof(revisions));

        if (Deltas.Any(x => x.RootShapeId != RootShapeId))
            throw new ArgumentException("All version deltas must target the evolution root shape.", nameof(deltas));
    }

    /// <summary>
    /// Root shape being evolved.
    /// </summary>
    public ShapeId RootShapeId { get; init; }

    /// <summary>
    /// Known revisions.
    /// </summary>
    public ImmutableArray<ShapeRevision> Revisions { get; init; }

    /// <summary>
    /// Directed version deltas between revisions.
    /// </summary>
    public ImmutableArray<VersionDelta> Deltas { get; init; }

    /// <summary>
    /// Optional evolution metadata.
    /// </summary>
    public ImmutableDictionary<AnnotationKey, AnnotationValue> Annotations { get; init; }
}
