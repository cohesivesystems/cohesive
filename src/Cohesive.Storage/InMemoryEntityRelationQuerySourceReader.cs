using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Cohesive.Relations.Acquisition;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Model;
using Cohesive.Relations.Physical;
using Cohesive.Relations.Realization;
using Cohesive.Relations.Serialization;

namespace Cohesive.Storage;

/// <summary>
/// Deterministic canonical source reader over one <see cref="InMemoryEntityOutboxRepository"/>.
/// </summary>
/// <remarks>
/// This reader implements only bounded physical acquisition. Canonical filters, projections, joins, aggregations,
/// and paging remain owned by the configured relation/query interpreter. Collection-valued relationship
/// references use the source's maximum batch size as their per-observation normalization boundary and return
/// inconclusive evidence when that boundary is exceeded.
/// </remarks>
public sealed class InMemoryEntityRelationQuerySourceReader : IEntityRelationQuerySourceReader
{
    const string EvidencePrefix = "cohesive.storage.in-memory/entity-source/v1";
    readonly InMemoryEntityOutboxRepository repository;
    readonly RelationQuerySourceInstance source;
    readonly RelationQueryPlacementFieldSelector payloadFieldSourceSelector;

    /// <summary>Conventional physical limits for an in-memory entity source.</summary>
    public static RelationQuerySourcePlacementLimits DefaultLimits { get; } = new(
        maximumBatchSize: 100,
        maximumBufferedRows: 10_000,
        maximumFanOut: 100,
        maximumConcurrency: 4);

    /// <summary>Exact primitive acquisition capabilities implemented by the in-memory entity reader.</summary>
    public static RelationQueryTargetCapabilityProfile TargetProfile { get; } = CreateTargetProfile();

    /// <summary>Creates a deterministic in-memory canonical source reader.</summary>
    /// <param name="shape">Exact graph-qualified semantic source-view shape projected by the reader.</param>
    /// <param name="source">Canonical physical source instance and limits implemented by the reader.</param>
    /// <param name="repository">In-memory repository supplying entity observations.</param>
    /// <param name="logicalPartition">Provider-neutral logical partition implemented by every repository read.</param>
    /// <param name="identitySourceSelector">
    /// Stable physical identity selector, or <see langword="null"/> for the observation-identity convention.
    /// </param>
    /// <param name="fieldSourceSelector">
    /// Semantic-to-physical field selector, or <see langword="null"/> to use semantic path text.
    /// </param>
    /// <param name="relationshipKeySourceSelector">
    /// Semantic-to-physical relationship-reference selector, or <see langword="null"/> to use semantic path text.
    /// </param>
    /// <param name="observationVersionSemanticPath">
    /// Optional semantic path projected from repository snapshot observation-version metadata.
    /// </param>
    /// <param name="persistedObservationType">
    /// Exact entity observation retained by <paramref name="repository"/>, or <see langword="null"/> when it is
    /// <paramref name="shape"/>.
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="shape"/> is incomplete; <paramref name="persistedObservationType"/> does not identify the
    /// repository entity shape; an identity selector is empty; or <paramref name="source"/> does not use
    /// <see cref="TargetProfile"/>.
    /// </exception>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="source"/>, <paramref name="repository"/>, or <paramref name="logicalPartition"/> is
    /// <see langword="null"/>.
    /// </exception>
    public InMemoryEntityRelationQuerySourceReader(
        QualifiedShapeId shape,
        RelationQuerySourceInstance source,
        InMemoryEntityOutboxRepository repository,
        RelationQueryLogicalPartitionIdentity logicalPartition,
        string? identitySourceSelector = null,
        RelationQueryPlacementFieldSelector? fieldSourceSelector = null,
        RelationQueryPlacementFieldSelector? relationshipKeySourceSelector = null,
        FieldPath? observationVersionSemanticPath = null,
        QualifiedShapeId? persistedObservationType = null)
    {
        if (string.IsNullOrWhiteSpace(shape.GraphId.Value) || string.IsNullOrWhiteSpace(shape.ShapeId.Value))
            throw new ArgumentException("An in-memory entity reader requires a graph-qualified shape.", nameof(shape));

        this.source = Guard.RequireNotNull(source);
        this.repository = Guard.RequireNotNull(repository);
        var effectivePersistedObservationType = persistedObservationType ?? shape;
        if (string.IsNullOrWhiteSpace(effectivePersistedObservationType.GraphId.Value)
            || string.IsNullOrWhiteSpace(effectivePersistedObservationType.ShapeId.Value))
        {
            throw new ArgumentException(
                "An in-memory entity reader requires a graph-qualified persisted observation type.",
                nameof(persistedObservationType));
        }
        if (effectivePersistedObservationType.ShapeId != repository.EntityDefinition.Shape.Id)
        {
            throw new ArgumentException(
                $"Persisted observation type '{effectivePersistedObservationType}' does not identify repository entity shape '{repository.EntityDefinition.Shape.Id.Value}'.",
                nameof(persistedObservationType));
        }

        if (!source.TargetProfile.HasSameSemantics(TargetProfile))
        {
            throw new ArgumentException(
                $"In-memory entity readers require target profile '{TargetProfile.Id.Value}'.",
                nameof(source));
        }

        Shape = shape;
        PersistedObservationType = effectivePersistedObservationType;
        Descriptor = new(
            source.Id,
            source.ExecutionDomain,
            source.TargetProfile,
            Guard.RequireNotNull(logicalPartition));
        IdentitySourceSelector = identitySourceSelector is null
            ? EntityRelationQuerySourceRegistration.ObservationIdentitySourceSelector
            : Guard.RequireNotNullOrWhiteSpace(identitySourceSelector);
        if (observationVersionSemanticPath is { Segments.IsDefaultOrEmpty: true })
            throw new ArgumentException("An observation-version semantic path cannot be empty.", nameof(observationVersionSemanticPath));
        if (observationVersionSemanticPath is not null
            && string.Equals(
                IdentitySourceSelector,
                EntityRelationQuerySourceRegistration.ObservationVersionSourceSelector,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Observation identity and observation version cannot use the same physical selector.",
                nameof(identitySourceSelector));
        }
        ObservationVersionSemanticPath = observationVersionSemanticPath;
        payloadFieldSourceSelector = fieldSourceSelector ?? EntityRelationQuerySourceRegistration.SelectSemanticPath;
        FieldSourceSelector = SelectFieldSource;
        RelationshipKeySourceSelector = relationshipKeySourceSelector
            ?? EntityRelationQuerySourceRegistration.SelectSemanticPath;
    }

    /// <summary>Exact graph-qualified shape returned by this reader.</summary>
    public QualifiedShapeId Shape { get; }

    /// <summary>Exact graph-qualified entity observation retained by the repository.</summary>
    public QualifiedShapeId PersistedObservationType { get; }

    /// <inheritdoc />
    public RelationQuerySourceReaderDescriptor Descriptor { get; }

    /// <summary>Stable physical selector interpreted as <see cref="Observation.Id"/>.</summary>
    public string IdentitySourceSelector { get; }

    /// <summary>Semantic field projected from authoritative observation-version metadata, when configured.</summary>
    public FieldPath? ObservationVersionSemanticPath { get; }

    /// <summary>Deterministic semantic-to-physical field selector.</summary>
    public RelationQueryPlacementFieldSelector FieldSourceSelector { get; }

    /// <summary>Deterministic semantic-to-physical relationship-reference selector.</summary>
    public RelationQueryPlacementFieldSelector RelationshipKeySourceSelector { get; }

    /// <inheritdoc />
    public ValueTask<RelationQuerySourceReadResult> ReadAsync(
        RelationQuerySourceReadRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            if (ValidateRequest(request) is { } invalid)
                return ValueTask.FromResult(Failed(request, invalid));

            if (BatchBoundaryExceeded(request.Constraint))
                return ValueTask.FromResult(Inconclusive(request, "batch-boundary-exceeded"));

            var (snapshots, version) = repository.CaptureRelationQuerySnapshot();
            cancellationToken.ThrowIfCancellationRequested();
            HashSet<string> identities = new(snapshots.Length, StringComparer.Ordinal);
            foreach (var snapshot in snapshots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (snapshot.Entity.ShapeId != PersistedObservationType.ShapeId)
                    return ValueTask.FromResult(Failed(request, "repository-shape-mismatch", version));
                if (!identities.Add(snapshot.Entity.Id))
                    return ValueTask.FromResult(Failed(request, "duplicate-observation-identity", version));
            }

            Array.Sort(
                snapshots,
                static (left, right) => string.CompareOrdinal(left.Entity.Id, right.Entity.Id));
            cancellationToken.ThrowIfCancellationRequested();
            var result = request.Constraint switch
            {
                RelationQueryBoundedEnumeration enumeration => ReadEnumeration(
                    request,
                    snapshots,
                    version,
                    enumeration,
                    cancellationToken),
                RelationQueryIdentityBatchLookup identity => ReadIdentityBatch(
                    request,
                    snapshots,
                    version,
                    identity,
                    cancellationToken),
                RelationQueryRelationshipKeyBatchLookup relationship => ReadRelationshipBatch(
                    request,
                    snapshots,
                    version,
                    relationship,
                    cancellationToken),
                _ => Failed(request, "unsupported-read-constraint", version)
            };
            return ValueTask.FromResult(result);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            return ValueTask.FromResult(Failed(request, "reader-failure"));
        }
    }

    RelationQuerySourceReadResult ReadEnumeration(
        RelationQuerySourceReadRequest request,
        EntitySnapshot[] snapshots,
        long version,
        RelationQueryBoundedEnumeration enumeration,
        CancellationToken cancellationToken)
    {
        var maximumRows = Math.Min(
            enumeration.MaximumRows,
            Math.Min(request.MaximumBufferedRows, source.Limits.MaximumBufferedRows));
        var count = checked((int)Math.Min(maximumRows, snapshots.Length));
        var selected = snapshots.AsSpan(0, count);
        var rows = Project(request, selected, version, cancellationToken);
        return new(
            snapshots.Length > count ? RelationQuerySourceReadState.Partial : RelationQuerySourceReadState.Complete,
            rows,
            Evidence(request, snapshots.Length > count ? "enumeration-partial" : "enumeration-complete", version));
    }

    RelationQuerySourceReadResult ReadIdentityBatch(
        RelationQuerySourceReadRequest request,
        EntitySnapshot[] snapshots,
        long version,
        RelationQueryIdentityBatchLookup lookup,
        CancellationToken cancellationToken)
    {
        HashSet<string> identities = new(lookup.Identities, StringComparer.Ordinal);
        List<EntitySnapshot> selected = new(Math.Min(identities.Count, snapshots.Length));
        foreach (var snapshot in snapshots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (identities.Contains(snapshot.Entity.Id))
                selected.Add(snapshot);
        }

        if (selected.Count == 0)
            return NotFound(request, "identity-not-found", version);
        if ((long)selected.Count > MaximumBufferedRows(request))
            return Inconclusive(request, "identity-buffer-boundary-exceeded", version);

        return new(
            RelationQuerySourceReadState.Complete,
            Project(request, CollectionsMarshal.AsSpan(selected), version, cancellationToken),
            Evidence(request, "identity-complete", version));
    }

    RelationQuerySourceReadResult ReadRelationshipBatch(
        RelationQuerySourceReadRequest request,
        EntitySnapshot[] snapshots,
        long version,
        RelationQueryRelationshipKeyBatchLookup lookup,
        CancellationToken cancellationToken)
    {
        HashSet<string> requestedKeys = new(lookup.Keys, StringComparer.Ordinal);
        Dictionary<string, long> fanOutByKey = new(requestedKeys.Count, StringComparer.Ordinal);
        List<EntitySnapshot> selected = [];
        FieldPath relationshipSourcePath;
        try
        {
            relationshipSourcePath = FieldPath.Parse(lookup.SourceSelector);
        }
        catch (Exception exception) when (exception is not OperationCanceledException
                                          and not OutOfMemoryException
                                          and not StackOverflowException)
        {
            return Failed(request, "relationship-reference-path-invalid", version);
        }
        foreach (var snapshot in snapshots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsFieldAuthoritative(snapshot, relationshipSourcePath))
                return Inconclusive(request, "relationship-reference-inconclusive", version);

            ObservationValue reference;
            try
            {
                if (!snapshot.Entity.TryGetField(relationshipSourcePath, out reference)
                    || reference.Kind is ObservationValueKind.Null or ObservationValueKind.Undefined)
                {
                    continue;
                }
            }
            catch (NotSupportedException)
            {
                return Failed(request, "relationship-reference-path-unsupported", version);
            }

            var extraction = RelationQueryReferenceKeyExtractor.Extract(
                reference,
                maximumKeys: source.Limits.MaximumBatchSize,
                cancellationToken,
                out var referenceKeys);
            if (extraction == RelationQueryReferenceKeyExtractionState.BoundaryExceeded)
                return Inconclusive(request, "relationship-reference-key-boundary-exceeded", version);
            if (extraction == RelationQueryReferenceKeyExtractionState.Invalid)
                return Failed(request, "relationship-reference-invalid", version);

            var matched = false;
            foreach (var key in referenceKeys)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!requestedKeys.Contains(key))
                    continue;

                matched = true;
                var fanOut = fanOutByKey.GetValueOrDefault(key) + 1;
                if (fanOut > source.Limits.MaximumFanOut)
                    return Inconclusive(request, "relationship-fan-out-boundary-exceeded", version);
                fanOutByKey[key] = fanOut;
            }
            if (!matched)
                continue;

            selected.Add(snapshot);
            if ((long)selected.Count > MaximumBufferedRows(request))
                return Inconclusive(request, "relationship-buffer-boundary-exceeded", version);
        }

        if (selected.Count == 0)
            return NotFound(request, "relationship-not-found", version);
        return new(
            RelationQuerySourceReadState.Complete,
            Project(request, CollectionsMarshal.AsSpan(selected), version, cancellationToken),
            Evidence(request, "relationship-complete", version));
    }

    ImmutableArray<RelationQuerySourceReadObservation> Project(
        RelationQuerySourceReadRequest request,
        ReadOnlySpan<EntitySnapshot> snapshots,
        long version,
        CancellationToken cancellationToken)
    {
        var rows = ImmutableArray.CreateBuilder<RelationQuerySourceReadObservation>(snapshots.Length);
        foreach (ref readonly var snapshot in snapshots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ImmutableArray<RelationQuerySourceReadFieldResult>.Builder fields =
                ImmutableArray.CreateBuilder<RelationQuerySourceReadFieldResult>(request.Fields.Length);
            foreach (var field in request.Fields)
            {
                cancellationToken.ThrowIfCancellationRequested();
                fields.Add(ProjectField(request, snapshot, field, version));
            }
            rows.Add(new(snapshot.Entity.Id, request.Shape, fields.MoveToImmutable()));
        }
        return rows.MoveToImmutable();
    }

    RelationQuerySourceReadFieldResult ProjectField(
        RelationQuerySourceReadRequest request,
        EntitySnapshot snapshot,
        RelationQuerySourceReadField field,
        long version)
    {
        var evidence = Evidence(request, $"field/{Uri.EscapeDataString(field.SemanticPath.ToString())}", version);
        if (field.SemanticPath == ObservationVersionSemanticPath)
        {
            return new(
                field,
                RelationQuerySourceReadFieldState.Value,
                ObservationValue.FromInt64(snapshot.Entity.Version),
                evidence);
        }
        FieldPath sourcePath;
        try
        {
            sourcePath = FieldPath.Parse(field.SourceSelector);
        }
        catch (Exception exception) when (exception is not OperationCanceledException
                                          and not OutOfMemoryException
                                          and not StackOverflowException)
        {
            return new(
                field,
                RelationQuerySourceReadFieldState.Failed,
                evidenceReference: evidence);
        }
        if (!IsFieldAuthoritative(snapshot, sourcePath))
        {
            return new(
                field,
                RelationQuerySourceReadFieldState.Inconclusive,
                evidenceReference: evidence);
        }

        ObservationValue value;
        try
        {
            if (!snapshot.Entity.TryGetField(sourcePath, out value)
                || value.Kind == ObservationValueKind.Undefined)
            {
                return new(
                    field,
                    RelationQuerySourceReadFieldState.Missing,
                    evidenceReference: evidence);
            }
        }
        catch (NotSupportedException)
        {
            return new(
                field,
                RelationQuerySourceReadFieldState.Failed,
                evidenceReference: evidence);
        }

        return value.Kind == ObservationValueKind.Null
            ? new(field, RelationQuerySourceReadFieldState.Null, evidenceReference: evidence)
            : new(field, RelationQuerySourceReadFieldState.Value, value, evidence);
    }

    string? ValidateRequest(RelationQuerySourceReadRequest request)
    {
        if (request.Source != source.Id)
            return "source-mismatch";
        if (request.Shape != Shape)
            return "shape-mismatch";
        if (!string.Equals(request.IdentitySelector, IdentitySourceSelector, StringComparison.Ordinal))
            return "identity-selector-mismatch";

        try
        {
            foreach (var field in request.Fields)
            {
                var valid = field.Purpose switch
                {
                    RelationQuerySourceReadFieldPurpose.SemanticInput =>
                        string.Equals(
                            field.SourceSelector,
                            FieldSourceSelector(field.SemanticPath),
                            StringComparison.Ordinal),
                    RelationQuerySourceReadFieldPurpose.Correlation =>
                        string.Equals(
                            field.SourceSelector,
                            RelationshipKeySourceSelector(field.SemanticPath),
                            StringComparison.Ordinal),
                    RelationQuerySourceReadFieldPurpose.SemanticInputAndCorrelation =>
                        string.Equals(
                            field.SourceSelector,
                            FieldSourceSelector(field.SemanticPath),
                            StringComparison.Ordinal)
                        && string.Equals(
                            field.SourceSelector,
                            RelationshipKeySourceSelector(field.SemanticPath),
                            StringComparison.Ordinal),
                    _ => false
                };
                if (!valid)
                    return "field-selector-mismatch";
            }

            if (request.Constraint is RelationQueryRelationshipKeyBatchLookup relationship
                && !string.Equals(
                    relationship.SourceSelector,
                    RelationshipKeySourceSelector(relationship.RelationshipReference),
                    StringComparison.Ordinal))
            {
                return "relationship-selector-mismatch";
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException
                                          and not OutOfMemoryException
                                          and not StackOverflowException)
        {
            return "selector-policy-failed";
        }

        return null;
    }

    string SelectFieldSource(FieldPath semanticPath) => semanticPath == ObservationVersionSemanticPath
        ? EntityRelationQuerySourceRegistration.ObservationVersionSourceSelector
        : payloadFieldSourceSelector(semanticPath);

    bool BatchBoundaryExceeded(RelationQuerySourceReadConstraint constraint) => constraint switch
    {
        RelationQueryIdentityBatchLookup identity =>
            (long)identity.Identities.Length > source.Limits.MaximumBatchSize,
        RelationQueryRelationshipKeyBatchLookup relationship =>
            (long)relationship.Keys.Length > source.Limits.MaximumBatchSize,
        _ => false
    };

    long MaximumBufferedRows(RelationQuerySourceReadRequest request) =>
        Math.Min(request.MaximumBufferedRows, source.Limits.MaximumBufferedRows);

    static bool IsFieldAuthoritative(EntitySnapshot snapshot, FieldPath path)
    {
        if (snapshot.LoadedFields is null)
            return true;
        var first = path.Segments[0];
        return first.Kind == SegmentKind.Field
            && first.Segment is { } field
            && snapshot.LoadedFields.Contains(field);
    }

    RelationQuerySourceReadResult Failed(
        RelationQuerySourceReadRequest request,
        string reason,
        long? version = null) => new(
        RelationQuerySourceReadState.Failed,
        evidenceReference: Evidence(request, reason, version));

    RelationQuerySourceReadResult Inconclusive(
        RelationQuerySourceReadRequest request,
        string reason,
        long? version = null) => new(
        RelationQuerySourceReadState.Inconclusive,
        evidenceReference: Evidence(request, reason, version));

    RelationQuerySourceReadResult NotFound(
        RelationQuerySourceReadRequest request,
        string reason,
        long version) => new(
        RelationQuerySourceReadState.NotFound,
        evidenceReference: Evidence(request, reason, version));

    string Evidence(RelationQuerySourceReadRequest request, string reason, long? version) => string.Concat(
        EvidencePrefix,
        "/source/",
        Uri.EscapeDataString(source.Id.Value),
        "/stage/",
        Uri.EscapeDataString(request.Stage.Value),
        version is { } snapshotVersion ? $"/snapshot/{snapshotVersion}" : string.Empty,
        "/",
        reason);

    static RelationQueryTargetCapabilityProfile CreateTargetProfile()
    {
        RelationQueryPrimitiveCapabilityKind[] capabilities =
        [
            RelationQueryPrimitiveCapabilityKind.BatchedKeyLookup,
            RelationQueryPrimitiveCapabilityKind.BatchedPredicateLookup,
            RelationQueryPrimitiveCapabilityKind.CompleteSetEnumeration,
            RelationQueryPrimitiveCapabilityKind.FieldProjection,
            RelationQueryPrimitiveCapabilityKind.ObservationIdentityRead,
            RelationQueryPrimitiveCapabilityKind.RelationshipReferenceRead
        ];
        return new(
            new("cohesive.storage.in-memory-entity-source"),
            new("cohesive.storage.in-memory-entity-source/v1"),
            [RelationQueryDocument.CurrentSchemaVersion],
            [RelationQueryCompilationProvenance.CurrentCompilerProfile],
            [
                .. capabilities.Select(static capability => new RelationQueryTargetCapabilityEvidence(
                    new($"cohesive.storage.in-memory-entity-source/capability/{(int)capability}"),
                    new PrimitiveRelationQueryCapability(capability)))
            ],
            description: "Bounded canonical acquisition over one in-memory entity repository.");
    }
}
