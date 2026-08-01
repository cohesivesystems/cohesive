using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Acquisition;

namespace Cohesive.Storage.Materialization;

/// <summary>Stable semantic identity of one source change.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct MaterializationChangeId
{
    /// <summary>Creates a source-change identity.</summary>
    /// <param name="value">Non-empty source-stable change identity.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white space.</exception>
    [JsonConstructor]
    public MaterializationChangeId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw stable change identity.</summary>
    public string Value { get; }

    /// <summary>Returns the raw stable change identity.</summary>
    /// <returns>The value supplied at construction.</returns>
    public override string ToString() => Value;
}

/// <summary>Stable identity of one source delivery, including a redelivery of a semantic change.</summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct MaterializationDeliveryId
{
    /// <summary>Creates a source-delivery identity.</summary>
    /// <param name="value">Non-empty source-stable delivery identity.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or white space.</exception>
    [JsonConstructor]
    public MaterializationDeliveryId(string value) => Value = Guard.RequireNotNullOrWhiteSpace(value);

    /// <summary>Raw stable delivery identity.</summary>
    public string Value { get; }

    /// <summary>Returns the raw stable delivery identity.</summary>
    /// <returns>The value supplied at construction.</returns>
    public override string ToString() => Value;
}

/// <summary>
/// Opaque, versioned source position scoped to exactly one materialization source feed.
/// </summary>
/// <remarks>
/// The value is suitable for persistence and equality only. Consumers must not infer ordering or provider semantics
/// from <see cref="Value"/>.
/// </remarks>
public sealed record MaterializationSourcePosition
{
    /// <summary>Creates an opaque source-feed-scoped position.</summary>
    /// <param name="formatVersion">Positive version of the opaque position representation.</param>
    /// <param name="scope">Exact acquisition input, source, partition, and ordering scope that issued the position.</param>
    /// <param name="value">Non-empty opaque position value.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="scope"/> or <paramref name="value"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">An identity is default or <paramref name="value"/> is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="formatVersion"/> is not positive.</exception>
    [JsonConstructor]
    public MaterializationSourcePosition(
        int formatVersion,
        MaterializationSourceScope scope,
        string value)
    {
        if (formatVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(formatVersion), formatVersion, "A source-position version must be positive.");
        }

        Scope = Guard.RequireNotNull(scope);
        FormatVersion = formatVersion;
        Value = Guard.RequireNotNullOrWhiteSpace(value);
    }

    /// <summary>Positive version of the opaque position representation.</summary>
    public int FormatVersion { get; }

    /// <summary>Exact source-feed scope that issued the position.</summary>
    public MaterializationSourceScope Scope { get; }

    /// <summary>Opaque provider-neutral position value.</summary>
    public string Value { get; }
}

/// <summary>Semantic kind of a source change.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MaterializationChangeKind
{
    /// <summary>An observation became present.</summary>
    Create = 0,

    /// <summary>An existing observation changed.</summary>
    Update = 1,

    /// <summary>An observation became absent.</summary>
    Delete = 2,

    /// <summary>An observation is present after the change, but the source cannot distinguish create from update.</summary>
    Upsert = 3
}

/// <summary>
/// Canonical source change attributed to one exact Relations acquisition input and graph-qualified shape.
/// </summary>
public sealed record MaterializationChangeEnvelope
{
    /// <summary>Creates a typed materialization change.</summary>
    /// <param name="id">Stable semantic change identity.</param>
    /// <param name="subjectIdentity">Stable identity of the observation affected by the change.</param>
    /// <param name="scope">Exact acquisition input, source, partition, and ordering scope containing the change.</param>
    /// <param name="shape">Stable shape of the affected observation, retained even when no image is available.</param>
    /// <param name="position">
    /// Optional opaque position immediately associated with the change. Positionless leased deliveries retain
    /// provider delivery and attempt identity without inventing a replay cursor.
    /// </param>
    /// <param name="kind">Create, update, delete, or source-ambiguous upsert semantics.</param>
    /// <param name="before">Observation before the change, when required by <paramref name="kind"/>.</param>
    /// <param name="after">Observation after the change, when required by <paramref name="kind"/>.</param>
    /// <param name="occurredAtUtc">UTC time at which the source reports that the change occurred.</param>
    /// <param name="observedAtUtc">UTC time at which the adapter observed the change.</param>
    /// <param name="evidenceReference">Optional opaque source evidence reference.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="id"/> contains a <see langword="null"/> value, or <paramref name="subjectIdentity"/> or
    /// <paramref name="scope"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// An identity, position, observation, time, or create/update/delete invariant is invalid.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is unsupported.</exception>
    [JsonConstructor]
    public MaterializationChangeEnvelope(
        MaterializationChangeId id,
        string subjectIdentity,
        MaterializationSourceScope scope,
        QualifiedShapeId shape,
        MaterializationSourcePosition? position,
        MaterializationChangeKind kind,
        RelationQuerySourceReadObservation? before,
        RelationQuerySourceReadObservation? after,
        DateTimeOffset occurredAtUtc,
        DateTimeOffset observedAtUtc,
        string? evidenceReference = null)
    {
        MaterializationContract.RequireDefinedIdentity(id.Value, nameof(id));
        SubjectIdentity = Guard.RequireNotNullOrWhiteSpace(subjectIdentity);
        Scope = Guard.RequireNotNull(scope);
        if (string.IsNullOrWhiteSpace(shape.GraphId.Value) || string.IsNullOrWhiteSpace(shape.ShapeId.Value))
        {
            throw new ArgumentException("A source change requires a graph-qualified shape.", nameof(shape));
        }

        if (shape != scope.Shape)
        {
            throw new ArgumentException("A source change shape must match its exact Relations placement scope.", nameof(shape));
        }

        if (position is not null && position.Scope != scope)
        {
            throw new ArgumentException(
                "A change position must belong to the exact source-feed scope.",
                nameof(position));
        }
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported materialization change kind.");
        }

        ValidateObservations(kind, SubjectIdentity, shape, before, after);
        MaterializationContract.RequireUtc(occurredAtUtc, nameof(occurredAtUtc));
        MaterializationContract.RequireUtc(observedAtUtc, nameof(observedAtUtc));
        if (observedAtUtc < occurredAtUtc)
        {
            throw new ArgumentException(
                "A change cannot be observed before it occurred.",
                nameof(observedAtUtc));
        }
        if (evidenceReference is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(evidenceReference);
        }

        Id = id;
        Shape = shape;
        Position = position;
        Kind = kind;
        Before = before;
        After = after;
        OccurredAtUtc = occurredAtUtc;
        ObservedAtUtc = observedAtUtc;
        EvidenceReference = evidenceReference;
    }

    /// <summary>Stable semantic change identity.</summary>
    public MaterializationChangeId Id { get; }

    /// <summary>
    /// Stable identity of the affected observation, retained even when a delete source cannot supply a before image.
    /// </summary>
    public string SubjectIdentity { get; }

    /// <summary>Exact Relations acquisition input, source, partition, and ordering scope containing the change.</summary>
    public MaterializationSourceScope Scope { get; }

    /// <summary>Stable shape of the affected observation, including deletes without an image.</summary>
    public QualifiedShapeId Shape { get; }

    /// <summary>
    /// Opaque position immediately associated with the change, or <see langword="null"/> for positionless leased
    /// delivery.
    /// </summary>
    public MaterializationSourcePosition? Position { get; }

    /// <summary>Create, update, delete, or source-ambiguous upsert semantics.</summary>
    public MaterializationChangeKind Kind { get; }

    /// <summary>
    /// Observation before the change, or <see langword="null"/> for a create or delete without before-image evidence.
    /// </summary>
    public RelationQuerySourceReadObservation? Before { get; }

    /// <summary>Observation after the change, or <see langword="null"/> for a delete.</summary>
    public RelationQuerySourceReadObservation? After { get; }

    /// <summary>UTC time at which the source reports that the change occurred.</summary>
    public DateTimeOffset OccurredAtUtc { get; }

    /// <summary>UTC time at which the adapter observed the change.</summary>
    public DateTimeOffset ObservedAtUtc { get; }

    /// <summary>Optional opaque source evidence reference.</summary>
    public string? EvidenceReference { get; }

    static void ValidateObservations(
        MaterializationChangeKind kind,
        string subjectIdentity,
        QualifiedShapeId shape,
        RelationQuerySourceReadObservation? before,
        RelationQuerySourceReadObservation? after)
    {
        switch (kind)
        {
            case MaterializationChangeKind.Create when before is null && after is not null:
                RequireObservation(after, subjectIdentity, shape, nameof(after));
                return;
            case MaterializationChangeKind.Delete when after is null:
                if (before is not null)
                {
                    RequireObservation(before, subjectIdentity, shape, nameof(before));
                }

                return;
            case MaterializationChangeKind.Update when after is not null:
            case MaterializationChangeKind.Upsert when after is not null:
                if (before is not null
                    && (!string.Equals(before.Identity, after.Identity, StringComparison.Ordinal)
                        || before.Shape != after.Shape))
                {
                    throw new ArgumentException(
                        "An update or upsert must retain one observation identity and shape.",
                        nameof(after));
                }
                if (before is not null)
                {
                    RequireObservation(before, subjectIdentity, shape, nameof(before));
                }

                RequireObservation(after, subjectIdentity, shape, nameof(after));
                return;
            default:
                throw new ArgumentException(
                    "Create, update, and upsert changes require an after observation; deletes require no after observation.",
                    nameof(kind));
        }
    }

    static void RequireObservation(
        RelationQuerySourceReadObservation observation,
        string subjectIdentity,
        QualifiedShapeId shape,
        string parameterName)
    {
        if (!string.Equals(observation.Identity, subjectIdentity, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A change observation identity must equal the stable change subject identity.",
                parameterName);
        }
        if (observation.Shape != shape)
        {
            throw new ArgumentException(
                "A change observation shape must equal the stable change shape.",
                parameterName);
        }
    }
}

/// <summary>One attributable delivery of a canonical source change.</summary>
public sealed record MaterializationChangeDelivery
{
    /// <summary>Creates a source-change delivery.</summary>
    /// <param name="id">Stable delivery identity used to recognize redelivery.</param>
    /// <param name="change">Canonical semantic change carried by the delivery.</param>
    /// <param name="deliveredAtUtc">UTC time at which the adapter delivered the change.</param>
    /// <param name="evidenceReference">Optional opaque delivery evidence reference.</param>
    /// <exception cref="ArgumentNullException"><paramref name="change"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An identity or time is invalid, or the delivery predates observation.</exception>
    [JsonConstructor]
    public MaterializationChangeDelivery(
        MaterializationDeliveryId id,
        MaterializationChangeEnvelope change,
        DateTimeOffset deliveredAtUtc,
        string? evidenceReference = null)
    {
        MaterializationContract.RequireDefinedIdentity(id.Value, nameof(id));
        Change = Guard.RequireNotNull(change);
        MaterializationContract.RequireUtc(deliveredAtUtc, nameof(deliveredAtUtc));
        if (deliveredAtUtc < change.ObservedAtUtc)
        {
            throw new ArgumentException("A change cannot be delivered before it was observed.", nameof(deliveredAtUtc));
        }

        if (evidenceReference is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(evidenceReference);
        }

        Id = id;
        DeliveredAtUtc = deliveredAtUtc;
        EvidenceReference = evidenceReference;
    }

    /// <summary>Stable delivery identity used to recognize redelivery.</summary>
    public MaterializationDeliveryId Id { get; }

    /// <summary>Canonical semantic change carried by the delivery.</summary>
    public MaterializationChangeEnvelope Change { get; }

    /// <summary>UTC time at which the adapter delivered the change.</summary>
    public DateTimeOffset DeliveredAtUtc { get; }

    /// <summary>Optional opaque delivery evidence reference.</summary>
    public string? EvidenceReference { get; }
}

/// <summary>Whether one bounded change read reached its current source boundary.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MaterializationChangePageState
{
    /// <summary>The source had no further currently visible provider input for the requested scope.</summary>
    CaughtUp = 0,

    /// <summary>This page returned at least one delivery and another bounded read is required to prove catch-up.</summary>
    MoreAvailable = 1,

    /// <summary>
    /// A bounded provider scan advanced the through-position without producing a delivery, and another bounded read
    /// is required to prove catch-up.
    /// </summary>
    Progressed = 2
}

/// <summary>Page-budget request for dependency changes in one acquisition feed after an explicit durable boundary.</summary>
/// <remarks>
/// <see cref="MaximumDeliveries"/> and <see cref="MaximumBytes"/> are hard output bounds for ordinary change sources.
/// When the selected <see cref="MaterializationCapabilityKind.SourceChangeDelivery"/> evidence advertises
/// <see cref="MaterializationGuaranteeKind.TransactionAlignedDelivery"/>, they are preferred page budgets instead.
/// Such a source may cross either budget only by retaining one complete source transaction as the final admitted
/// transaction in the page; it must not admit another transaction after crossing a budget. The indivisible
/// transaction itself remains bounded by the evidence's <see cref="MaterializationLimitKind.TransactionItems"/> and
/// <see cref="MaterializationLimitKind.TransactionBytes"/> hard safety limits.
/// </remarks>
public sealed record MaterializationChangeReadRequest
{
    /// <summary>Creates a change request with positive item and encoded-byte page budgets.</summary>
    /// <param name="scope">Exact acquisition input, source, partition, and ordering scope to observe.</param>
    /// <param name="afterPosition">Exclusive durable page boundary previously captured or returned by the source.</param>
    /// <param name="maximumDeliveries">
    /// Positive delivery page budget. This is a hard output bound unless the selected source evidence advertises
    /// transaction-aligned delivery.
    /// </param>
    /// <param name="maximumBytes">
    /// Positive canonical encoded-byte page budget. This is a hard output bound unless the selected source evidence
    /// advertises transaction-aligned delivery.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="scope"/> or <paramref name="afterPosition"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">An identity or position scope is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="maximumDeliveries"/> or <paramref name="maximumBytes"/> is not positive.
    /// </exception>
    [JsonConstructor]
    public MaterializationChangeReadRequest(
        MaterializationSourceScope scope,
        MaterializationSourcePosition afterPosition,
        int maximumDeliveries,
        long maximumBytes)
    {
        Scope = Guard.RequireNotNull(scope);
        AfterPosition = Guard.RequireNotNull(afterPosition);
        if (AfterPosition.Scope != scope)
        {
            throw new ArgumentException(
                "A change-read position must belong to the exact requested source-feed scope.",
                nameof(afterPosition));
        }
        if (maximumDeliveries <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumDeliveries), maximumDeliveries, "A change read must be bounded.");
        }

        if (maximumBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes), maximumBytes, "A change-read byte bound must be positive.");
        }

        MaximumDeliveries = maximumDeliveries;
        MaximumBytes = maximumBytes;
    }

    /// <summary>Exact acquisition input, source, partition, and ordering scope to observe.</summary>
    public MaterializationSourceScope Scope { get; }

    /// <summary>Exclusive durable page boundary previously captured or returned by the source.</summary>
    public MaterializationSourcePosition AfterPosition { get; }

    /// <summary>
    /// Positive delivery page budget; a hard bound except for one final indivisible transaction from a source that
    /// advertises transaction-aligned delivery.
    /// </summary>
    public int MaximumDeliveries { get; }

    /// <summary>
    /// Positive canonical encoded-byte page budget; a hard bound except for one final indivisible transaction from a
    /// source that advertises transaction-aligned delivery.
    /// </summary>
    [JsonConverter(typeof(StringEncodedInt64JsonConverter))]
    public long MaximumBytes { get; }
}

/// <summary>One source-ordered bounded page of change deliveries.</summary>
public sealed record MaterializationChangePage
{
    /// <summary>Creates a bounded change page.</summary>
    /// <param name="deliveries">Deliveries retained in source order.</param>
    /// <param name="throughPosition">
    /// Opaque boundary after the provider input examined for this page and at or after every returned delivery.
    /// </param>
    /// <param name="state">Whether the source is caught up or another bounded read is required to prove catch-up.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="deliveries"/> contains a null entry or duplicate delivery identity, a delivery belongs to a
    /// different scope or has no position, an empty page claims that deliveries are available, or a progressed page
    /// contains a delivery.
    /// </exception>
    /// <exception cref="ArgumentNullException"><paramref name="throughPosition"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="state"/> is unsupported.</exception>
    [JsonConstructor]
    public MaterializationChangePage(
        ImmutableArray<MaterializationChangeDelivery> deliveries,
        MaterializationSourcePosition throughPosition,
        MaterializationChangePageState state)
    {
        ThroughPosition = Guard.RequireNotNull(throughPosition);
        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(state), state, "Unsupported materialization change-page state.");
        }

        var normalized = deliveries.IsDefault ? [] : deliveries;
        if (state == MaterializationChangePageState.MoreAvailable && normalized.IsDefaultOrEmpty)
        {
            throw new ArgumentException("A page with more available changes must return at least one delivery.", nameof(deliveries));
        }
        if (state == MaterializationChangePageState.Progressed && !normalized.IsDefaultOrEmpty)
        {
            throw new ArgumentException("A progressed change page must be empty.", nameof(deliveries));
        }

        HashSet<MaterializationDeliveryId> ids = [];
        foreach (var delivery in normalized)
        {
            if (delivery is null)
            {
                throw new ArgumentException("Change pages cannot contain null deliveries.", nameof(deliveries));
            }

            if (!ids.Add(delivery.Id))
            {
                throw new ArgumentException("Change pages cannot repeat a delivery identity.", nameof(deliveries));
            }

            if (delivery.Change.Scope != throughPosition.Scope)
            {
                throw new ArgumentException("Every change delivery must belong to the page boundary scope.", nameof(deliveries));
            }

            if (delivery.Change.Position is null)
            {
                throw new ArgumentException(
                    "A positioned pull page cannot contain a positionless leased delivery.",
                    nameof(deliveries));
            }
        }

        Deliveries = normalized;
        State = state;
    }

    /// <summary>Deliveries retained in source order.</summary>
    public ImmutableArray<MaterializationChangeDelivery> Deliveries { get; }

    /// <summary>Opaque resumable boundary after the provider input examined for this page.</summary>
    public MaterializationSourcePosition ThroughPosition { get; }

    /// <summary>Whether the source is caught up or another bounded read is required to prove catch-up.</summary>
    public MaterializationChangePageState State { get; }

    /// <summary>
    /// Requires exact durable application-checkpoint evidence before a managed provider may settle this page.
    /// </summary>
    /// <param name="progress">Exact definition, generation, and source-feed progress key supplied to the handler.</param>
    /// <param name="result">Durable progress mutation result returned by the handler.</param>
    /// <returns>The exact durable change checkpoint that authorizes provider settlement.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="progress"/> or <paramref name="result"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The mutation was not applied or replayed, the snapshot belongs to another progress key, the latest checkpoint
    /// is not an incremental-change checkpoint through this page, or its applied delivery identities do not exactly equal
    /// this page's delivery set.
    /// </exception>
    public MaterializationApplicationCheckpoint RequireDurableCheckpointForSettlement(
        MaterializationProgressKey progress,
        MaterializationProgressMutationResult result)
    {
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentNullException.ThrowIfNull(result);
        if (result.Disposition is not (MaterializationProgressMutationDisposition.Applied
            or MaterializationProgressMutationDisposition.Replayed))
        {
            throw new InvalidOperationException(
                $"Provider settlement requires applied or replayed application progress; the handler returned '{result.Disposition}'.");
        }

        var snapshot = result.Snapshot
            ?? throw new InvalidOperationException("Provider settlement requires a durable progress snapshot.");
        if (snapshot.Key != progress)
        {
            throw new InvalidOperationException(
                "Provider settlement requires progress from the exact materialization definition, generation, and source-feed scope supplied to the handler.");
        }

        var checkpoint = snapshot.LatestCheckpoint
            ?? throw new InvalidOperationException("Provider settlement requires a durable application checkpoint.");
        if (checkpoint.Kind != MaterializationCheckpointKind.ChangeProgress)
        {
            throw new InvalidOperationException(
                $"Provider settlement requires a '{MaterializationCheckpointKind.ChangeProgress}' checkpoint; the latest checkpoint is '{checkpoint.Kind}'.");
        }

        if (!checkpoint.CoversReplayPosition(ThroughPosition))
        {
            throw new InvalidOperationException(
                "Provider settlement requires a durable application checkpoint through the exact delivered page position.");
        }

        if (!HasExactAppliedDeliverySet(checkpoint.AppliedDeliveries))
        {
            throw new InvalidOperationException(
                "Provider settlement requires a durable application checkpoint covering exactly the delivered page identities.");
        }

        return checkpoint;
    }

    bool HasExactAppliedDeliverySet(ImmutableArray<MaterializationDeliveryId> appliedDeliveries)
    {
        if (appliedDeliveries.Length != Deliveries.Length)
        {
            return false;
        }

        foreach (var delivery in Deliveries)
        {
            if (!ContainsCanonical(appliedDeliveries, delivery.Id))
            {
                return false;
            }
        }

        return true;
    }

    static bool ContainsCanonical(
        ImmutableArray<MaterializationDeliveryId> deliveries,
        MaterializationDeliveryId sought)
    {
        var lower = 0;
        var upper = deliveries.Length - 1;
        while (lower <= upper)
        {
            var middle = lower + ((upper - lower) / 2);
            var comparison = StringComparer.Ordinal.Compare(deliveries[middle].Value, sought.Value);
            if (comparison == 0)
            {
                return true;
            }

            if (comparison < 0)
            {
                lower = middle + 1;
            }
            else
            {
                upper = middle - 1;
            }
        }

        return false;
    }
}
