using Cohesive.Relations.Acquisition;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.Physical;

namespace Cohesive.Storage;

/// <summary>
/// Associates one graph-qualified entity shape with an exact canonical physical source and offline acquisition contract.
/// </summary>
/// <remarks>
/// The canonical <see cref="RelationQuerySourceInstance"/> remains the source of truth for physical identity,
/// execution domain, target capabilities, and limits. This registration adds only entity-shape affinity and the
/// adapter-interpreted selectors needed to author plan-scoped placement bindings.
/// </remarks>
public class EntityRelationQuerySourceBinding
{
    /// <summary>Conventional selector for <see cref="EntityObservationSnapshot.EntityId"/>.</summary>
    public const string ObservationIdentitySourceSelector =
        RelationQueryPlacementBuilder.FrameworkIdentitySourceSelector;

    /// <summary>Conventional in-memory selector for <see cref="EntityObservationSnapshot.Version"/>.</summary>
    public const string ObservationVersionSourceSelector = "$version";

    /// <summary>In-memory selector for the exact opaque repository snapshot concurrency token.</summary>
    public const string ConcurrencyTokenSourceSelector = "$concurrencyToken";

    /// <summary>Creates immutable entity-source metadata without runtime IO.</summary>
    /// <param name="shape">Exact graph-qualified entity shape supplied by the source.</param>
    /// <param name="source">Canonical physical source instance, capability profile, and limits.</param>
    /// <param name="descriptor">Declared source and partition contract, without a runtime reader.</param>
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
    /// <param name="persistedObservationType">
    /// Exact entity observation retained by the repository, or <see langword="null"/> when it is
    /// <paramref name="shape"/>. Supply this when <paramref name="shape"/> is a derived query source view.
    /// </param>
    /// <param name="concurrencyTokenSemanticPath">Optional field for the opaque token from the acquired snapshot.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="shape"/> is incomplete, <paramref name="identitySourceSelector"/> is empty, or the reader
    /// descriptor does not match <paramref name="source"/>.
    /// </exception>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="source"/> or <paramref name="descriptor"/> is <see langword="null"/>.
    /// </exception>
    public EntityRelationQuerySourceBinding(
        QualifiedShapeId shape,
        RelationQuerySourceInstance source,
        RelationQuerySourceReaderDescriptor descriptor,
        string? identitySourceSelector = null,
        FieldPath? identitySemanticPath = null,
        RelationQueryPlacementFieldSelector? fieldSourceSelector = null,
        RelationQueryPlacementFieldSelector? relationshipKeySourceSelector = null,
        FieldPath? observationVersionSemanticPath = null,
        QualifiedShapeId? persistedObservationType = null,
        FieldPath? concurrencyTokenSemanticPath = null)
    {
        if (string.IsNullOrWhiteSpace(shape.GraphId.Value) || string.IsNullOrWhiteSpace(shape.ShapeId.Value))
            throw new ArgumentException("An entity relation/query source requires a graph-qualified shape.", nameof(shape));

        Source = Guard.RequireNotNull(source);
        Descriptor = Guard.RequireNotNull(descriptor);
        if (descriptor.Source != source.Id
            || descriptor.ExecutionDomain != source.ExecutionDomain
            || !descriptor.TargetProfile.HasSameSemantics(source.TargetProfile))
        {
            throw new ArgumentException(
                "The source descriptor must exactly match the registered source identity, execution domain, and target profile.",
                nameof(descriptor));
        }

        Shape = shape;
        var effectivePersistedObservationType = persistedObservationType ?? shape;
        if (string.IsNullOrWhiteSpace(effectivePersistedObservationType.GraphId.Value)
            || string.IsNullOrWhiteSpace(effectivePersistedObservationType.ShapeId.Value))
        {
            throw new ArgumentException(
                "An entity relation/query source requires a graph-qualified persisted observation type.",
                nameof(persistedObservationType));
        }
        PersistedObservationType = effectivePersistedObservationType;
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
        if (concurrencyTokenSemanticPath is { } tokenPath
            && (tokenPath.Segments.IsDefaultOrEmpty || tokenPath == identitySemanticPath || tokenPath == observationVersionSemanticPath))
            throw new ArgumentException("A concurrency-token path must be nonempty and distinct from identity and version metadata.", nameof(concurrencyTokenSemanticPath));
        ConcurrencyTokenSemanticPath = concurrencyTokenSemanticPath;
        ObservationVersionSemanticPath = observationVersionSemanticPath;
        FieldSourceSelector = fieldSourceSelector ?? SemanticPathSelector;
        RelationshipKeySourceSelector = relationshipKeySourceSelector ?? SemanticPathSelector;
    }

    /// <summary>Exact graph-qualified semantic source-view shape supplied by this registration.</summary>
    public QualifiedShapeId Shape { get; }

    /// <summary>
    /// Exact graph-qualified entity observation retained by the repository. This may differ from
    /// <see cref="Shape"/> when the source projects a derived metadata-enriched view.
    /// </summary>
    public QualifiedShapeId PersistedObservationType { get; }

    /// <summary>Canonical physical source instance, target profile, and execution limits.</summary>
    public RelationQuerySourceInstance Source { get; }

    /// <summary>Declared acquisition contract implementing <see cref="Source"/>.</summary>
    public RelationQuerySourceReaderDescriptor Descriptor { get; }

    /// <summary>Stable physical selector for semantic observation identity.</summary>
    public string IdentitySourceSelector { get; }

    /// <summary>Canonical field path equal to the observation identity, when explicitly evidenced.</summary>
    public FieldPath? IdentitySemanticPath { get; }

    /// <summary>
    /// Canonical field path projected from authoritative repository observation-version metadata, when configured.
    /// </summary>
    public FieldPath? ObservationVersionSemanticPath { get; }

    /// <summary>Optional field projected from the exact opaque repository token, never observation payload.</summary>
    public FieldPath? ConcurrencyTokenSemanticPath { get; }

    /// <summary>Deterministic semantic-to-physical field selector.</summary>
    public RelationQueryPlacementFieldSelector FieldSourceSelector { get; }

    /// <summary>Deterministic semantic-to-physical relationship-reference selector.</summary>
    public RelationQueryPlacementFieldSelector RelationshipKeySourceSelector { get; }

    internal static string SelectSemanticPath(FieldPath path) => SemanticPathSelector(path);
    static readonly RelationQueryPlacementFieldSelector SemanticPathSelector = static path => path.ToString();
}
