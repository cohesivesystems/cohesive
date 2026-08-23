using Cohesive.Relations.Acquisition;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.Physical;

namespace Cohesive.Storage;

/// <summary>
/// Associates one graph-qualified entity shape with an exact canonical physical source and reader.
/// </summary>
/// <remarks>
/// The canonical <see cref="RelationQuerySourceInstance"/> remains the source of truth for physical identity,
/// execution domain, target capabilities, and limits. This registration adds only entity-shape affinity and the
/// adapter-interpreted selectors needed to author plan-scoped placement bindings.
/// </remarks>
public sealed class EntityRelationQuerySourceRegistration
{
    /// <summary>Conventional selector for <see cref="Cohesive.Relations.Model.Observation.Id"/>.</summary>
    public const string ObservationIdentitySourceSelector =
        RelationQueryPlacementBuilder.FrameworkIdentitySourceSelector;

    /// <summary>Conventional in-memory selector for <see cref="Cohesive.Relations.Model.Observation.Version"/>.</summary>
    public const string ObservationVersionSourceSelector = "$version";

    /// <summary>Creates one immutable entity-backed canonical source registration.</summary>
    /// <param name="shape">Exact graph-qualified entity shape supplied by the source.</param>
    /// <param name="source">Canonical physical source instance, capability profile, and limits.</param>
    /// <param name="reader">Reader implementing the exact source instance.</param>
    /// <param name="identitySourceSelector">
    /// Stable physical identity selector, or <see langword="null"/> for
    /// <see cref="ObservationIdentitySourceSelector"/>.
    /// </param>
    /// <param name="identitySemanticPath">
    /// Optional canonical field path whose value is exactly the source-native observation identity.
    /// </param>
    /// <param name="fieldSourceSelector">
    /// Semantic-to-physical field selector, or <see langword="null"/> to use canonical semantic path text.
    /// </param>
    /// <param name="relationshipKeySourceSelector">
    /// Semantic-to-physical relationship-reference selector, or <see langword="null"/> to use canonical semantic
    /// path text.
    /// </param>
    /// <param name="observationVersionSemanticPath">
    /// Optional canonical field path whose value is projected from repository snapshot metadata as the exact
    /// observation version rather than read from the observation payload.
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="shape"/> is incomplete, <paramref name="identitySourceSelector"/> is empty, or the reader
    /// descriptor does not match <paramref name="source"/>.
    /// </exception>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="source"/> or <paramref name="reader"/> is <see langword="null"/>.
    /// </exception>
    public EntityRelationQuerySourceRegistration(
        QualifiedShapeId shape,
        RelationQuerySourceInstance source,
        IRelationQuerySourceReader reader,
        string? identitySourceSelector = null,
        FieldPath? identitySemanticPath = null,
        RelationQueryPlacementFieldSelector? fieldSourceSelector = null,
        RelationQueryPlacementFieldSelector? relationshipKeySourceSelector = null,
        FieldPath? observationVersionSemanticPath = null)
    {
        if (string.IsNullOrWhiteSpace(shape.GraphId.Value) || string.IsNullOrWhiteSpace(shape.ShapeId.Value))
            throw new ArgumentException("An entity relation/query source requires a graph-qualified shape.", nameof(shape));

        Source = Guard.RequireNotNull(source);
        Reader = Guard.RequireNotNull(reader);
        var descriptor = reader.Descriptor;
        if (descriptor.Source != source.Id
            || descriptor.ExecutionDomain != source.ExecutionDomain
            || !descriptor.TargetProfile.HasSameSemantics(source.TargetProfile))
        {
            throw new ArgumentException(
                "The source reader descriptor must exactly match the registered source identity, execution domain, and target profile.",
                nameof(reader));
        }

        Shape = shape;
        if (identitySemanticPath is { Segments.IsDefaultOrEmpty: true })
            throw new ArgumentException("An identity semantic path cannot be empty.", nameof(identitySemanticPath));
        if (observationVersionSemanticPath is { Segments.IsDefaultOrEmpty: true })
            throw new ArgumentException("An observation-version semantic path cannot be empty.", nameof(observationVersionSemanticPath));
        if (observationVersionSemanticPath is { } versionPath && identitySemanticPath == versionPath)
        {
            throw new ArgumentException(
                "Observation identity and observation version cannot occupy the same semantic path.",
                nameof(observationVersionSemanticPath));
        }
        IdentitySourceSelector = identitySourceSelector is null
            ? ObservationIdentitySourceSelector
            : Guard.RequireNotNullOrWhiteSpace(identitySourceSelector);
        IdentitySemanticPath = identitySemanticPath;
        ObservationVersionSemanticPath = observationVersionSemanticPath;
        FieldSourceSelector = fieldSourceSelector ?? SemanticPathSelector;
        RelationshipKeySourceSelector = relationshipKeySourceSelector ?? SemanticPathSelector;
    }

    /// <summary>Exact graph-qualified entity shape supplied by this registration.</summary>
    public QualifiedShapeId Shape { get; }

    /// <summary>Canonical physical source instance, target profile, and execution limits.</summary>
    public RelationQuerySourceInstance Source { get; }

    /// <summary>Canonical bounded reader implementing <see cref="Source"/>.</summary>
    public IRelationQuerySourceReader Reader { get; }

    /// <summary>Stable physical selector for semantic observation identity.</summary>
    public string IdentitySourceSelector { get; }

    /// <summary>Canonical field path equal to the observation identity, when explicitly evidenced.</summary>
    public FieldPath? IdentitySemanticPath { get; }

    /// <summary>
    /// Canonical field path projected from authoritative repository observation-version metadata, when configured.
    /// </summary>
    public FieldPath? ObservationVersionSemanticPath { get; }

    /// <summary>Deterministic semantic-to-physical field selector.</summary>
    public RelationQueryPlacementFieldSelector FieldSourceSelector { get; }

    /// <summary>Deterministic semantic-to-physical relationship-reference selector.</summary>
    public RelationQueryPlacementFieldSelector RelationshipKeySourceSelector { get; }

    /// <summary>Creates an in-memory entity source registration using deterministic conventions.</summary>
    /// <param name="shape">Exact graph-qualified shape represented by <paramref name="repository"/>.</param>
    /// <param name="repository">In-memory entity repository supplying observations.</param>
    /// <param name="logicalPartition">Provider-neutral logical partition implemented by the registration.</param>
    /// <param name="source">Explicit source identity, or <see langword="null"/> for a shape-derived identity.</param>
    /// <param name="executionDomain">
    /// Explicit execution domain, or <see langword="null"/> for a shape-derived domain that does not imply a
    /// cross-repository consistent snapshot.
    /// </param>
    /// <param name="limits">Explicit physical limits, or <see langword="null"/> for in-memory defaults.</param>
    /// <param name="identitySourceSelector">Explicit identity selector, or <see langword="null"/> for the observation-identity convention.</param>
    /// <param name="identitySemanticPath">Optional canonical field path exactly equal to observation identity.</param>
    /// <param name="fieldSourceSelector">Explicit field selector policy, or <see langword="null"/> for semantic paths.</param>
    /// <param name="relationshipKeySourceSelector">
    /// Explicit relationship-reference selector policy, or <see langword="null"/> for semantic paths.
    /// </param>
    /// <param name="observationVersionSemanticPath">
    /// Optional canonical field path projected from <see cref="Cohesive.Relations.Model.Observation.Version"/>.
    /// </param>
    /// <returns>A registration whose source, reader, limits, profile, and selector policies agree exactly.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="repository"/> or <paramref name="logicalPartition"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="shape"/> does not identify the repository entity shape, or an explicit identity or selector
    /// is invalid.
    /// </exception>
    public static EntityRelationQuerySourceRegistration InMemory(
        QualifiedShapeId shape,
        InMemoryEntityOutboxRepository repository,
        RelationQueryLogicalPartitionIdentity logicalPartition,
        RelationQuerySourceInstanceId? source = null,
        RelationQueryExecutionDomainId? executionDomain = null,
        RelationQuerySourcePlacementLimits? limits = null,
        string? identitySourceSelector = null,
        FieldPath? identitySemanticPath = null,
        RelationQueryPlacementFieldSelector? fieldSourceSelector = null,
        RelationQueryPlacementFieldSelector? relationshipKeySourceSelector = null,
        FieldPath? observationVersionSemanticPath = null)
    {
        ArgumentNullException.ThrowIfNull(repository);
        if (shape.ShapeId != repository.EntityDefinition.Shape.Id)
        {
            throw new ArgumentException(
                $"Qualified shape '{shape}' does not identify repository entity shape '{repository.EntityDefinition.Shape.Id.Value}'.",
                nameof(shape));
        }

        if (observationVersionSemanticPath is { Segments.IsDefaultOrEmpty: true })
            throw new ArgumentException("An observation-version semantic path cannot be empty.", nameof(observationVersionSemanticPath));
        if (observationVersionSemanticPath is not null
            && string.Equals(identitySourceSelector, ObservationVersionSourceSelector, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Observation identity and observation version cannot use the same physical selector.",
                nameof(identitySourceSelector));
        }

        var shapeKey = ShapeKey(shape);
        var sourceKey = observationVersionSemanticPath is null
            ? shapeKey
            : string.Concat(
                shapeKey,
                "/metadata/observation-version/",
                Uri.EscapeDataString(observationVersionSemanticPath.Value.ToString()));
        var effectiveSource = source ?? new RelationQuerySourceInstanceId(
            $"source/cohesive.storage.in-memory/{sourceKey}");
        var effectiveDomain = executionDomain ?? new RelationQueryExecutionDomainId(
            $"domain/cohesive.storage.in-memory/{shapeKey}");
        var effectiveLimits = limits ?? InMemoryEntityRelationQuerySourceReader.DefaultLimits;
        var sourceInstance = new RelationQuerySourceInstance(
            effectiveSource,
            effectiveDomain,
            InMemoryEntityRelationQuerySourceReader.TargetProfile,
            effectiveLimits);
        var reader = new InMemoryEntityRelationQuerySourceReader(
            shape,
            sourceInstance,
            repository,
            logicalPartition,
            identitySourceSelector,
            fieldSourceSelector,
            relationshipKeySourceSelector,
            observationVersionSemanticPath);
        return new(
            shape,
            sourceInstance,
            reader,
            reader.IdentitySourceSelector,
            identitySemanticPath,
            reader.FieldSourceSelector,
            reader.RelationshipKeySourceSelector,
            observationVersionSemanticPath);
    }

    internal static string SelectSemanticPath(FieldPath path) => SemanticPathSelector(path);

    static readonly RelationQueryPlacementFieldSelector SemanticPathSelector =
        static semanticPath => semanticPath.ToString();

    static string ShapeKey(QualifiedShapeId shape) => string.Concat(
        Uri.EscapeDataString(shape.GraphId.Value),
        "/",
        Uri.EscapeDataString(shape.ShapeId.Value));

}
