using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Cohesive.Model;

/// <summary>
/// Selects the context where a shape profile or overlay applies.
/// </summary>
public sealed record ShapeProfileSelector
{
    /// <summary>
    /// Creates a shape profile selector.
    /// </summary>
    [JsonConstructor]
    public ShapeProfileSelector(
        ShapeId shapeId,
        string? standard = null,
        string? transactionSet = null,
        string? release = null,
        string? tradingPartnerId = null,
        string? customerId = null,
        string? senderId = null,
        string? receiverId = null
        )
    {
        ShapeId = shapeId;
        Standard = Normalize(standard);
        TransactionSet = Normalize(transactionSet);
        Release = Normalize(release);
        TradingPartnerId = Normalize(tradingPartnerId);
        CustomerId = Normalize(customerId);
        SenderId = Normalize(senderId);
        ReceiverId = Normalize(receiverId);
    }

    /// <summary>
    /// Root shape this selector applies to.
    /// </summary>
    public ShapeId ShapeId { get; init; }

    /// <summary>
    /// EDI or schema standard identifier, for example <c>x12</c>.
    /// </summary>
    public string? Standard { get; init; }

    /// <summary>
    /// Transaction set or document family, for example <c>204</c>.
    /// </summary>
    public string? TransactionSet { get; init; }

    /// <summary>
    /// Standard release/version, for example <c>004010</c>.
    /// </summary>
    public string? Release { get; init; }

    /// <summary>
    /// Trading partner identity.
    /// </summary>
    public string? TradingPartnerId { get; init; }

    /// <summary>
    /// Customer or shipper identity.
    /// </summary>
    public string? CustomerId { get; init; }

    /// <summary>
    /// EDI sender identity.
    /// </summary>
    public string? SenderId { get; init; }

    /// <summary>
    /// EDI receiver identity.
    /// </summary>
    public string? ReceiverId { get; init; }

    static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>
/// Party/profile-specific delta over a base graph.
/// </summary>
public sealed record OverlayDelta
{
    /// <summary>
    /// Creates an overlay delta.
    /// </summary>
    [JsonConstructor]
    public OverlayDelta(
        string id,
        ShapeProfileSelector appliesTo,
        ImmutableArray<GraphDeltaOperation> operations,
        ImmutableArray<string> extends = default,
        ImmutableDictionary<AnnotationKey, AnnotationValue>? annotations = null)
    {
        Id = Guard.RequireNotNullOrWhiteSpace(id);
        AppliesTo = Guard.RequireNotNull(appliesTo);
        Operations = operations.IsDefault ? [] : operations;
        Extends = NormalizeExtends(extends);
        Annotations = AnnotationMap.Normalize(annotations);
    }

    /// <summary>
    /// Stable overlay identifier.
    /// </summary>
    public string Id { get; init; }

    /// <summary>
    /// Matching selector for this overlay.
    /// </summary>
    public ShapeProfileSelector AppliesTo { get; init; }

    /// <summary>
    /// Base overlay/profile ids that should be applied before this overlay.
    /// </summary>
    public ImmutableArray<string> Extends { get; init; }

    /// <summary>
    /// Overlay operations.
    /// </summary>
    public ImmutableArray<GraphDeltaOperation> Operations { get; init; }

    /// <summary>
    /// Optional overlay metadata.
    /// </summary>
    public ImmutableDictionary<AnnotationKey, AnnotationValue> Annotations { get; init; }

    /// <summary>
    /// Creates an overlay delta from a general graph delta.
    /// </summary>
    public static OverlayDelta FromGraphDelta(string id, ShapeProfileSelector appliesTo, GraphDelta delta, ImmutableArray<string> extends = default)
    {
        ArgumentNullException.ThrowIfNull(delta);
        return new(
            id: id,
            appliesTo: appliesTo,
            operations: delta.Operations,
            extends: extends,
            annotations: delta.Annotations
            );
    }

    /// <summary>
    /// Converts this overlay to the generic graph delta form.
    /// </summary>
    public GraphDelta ToGraphDelta(GraphId? sourceGraphId = null, GraphId? targetGraphId = null) =>
        new(id: Id,
            operations: Operations,
            kind: GraphDeltaKind.Overlay,
            sourceGraphId: sourceGraphId,
            targetGraphId: targetGraphId,
            sourceVersion: AppliesTo.Release,
            targetVersion: AppliesTo.Release,
            annotations: Annotations
            );

    static ImmutableArray<string> NormalizeExtends(ImmutableArray<string> values)
    {
        if (values.IsDefaultOrEmpty)
            return [];

        return
        [
            .. values
                .WhereNotNullOrWhiteSpace()
                .Select(x => x.Trim())
                .Distinct(StringComparer.Ordinal)
        ];
    }
}

/// <summary>
/// Named shape profile that carries party-specific overlay semantics.
/// </summary>
public sealed record ShapeProfile
{
    /// <summary>
    /// Creates a shape profile.
    /// </summary>
    [JsonConstructor]
    public ShapeProfile(
        string id,
        ShapeProfileSelector appliesTo,
        OverlayDelta delta,
        ImmutableArray<string> extends = default,
        ImmutableArray<InvariantDefinition> invariants = default,
        ImmutableDictionary<AnnotationKey, AnnotationValue>? annotations = null
        )
    {
        Id = Guard.RequireNotNullOrWhiteSpace(id);
        AppliesTo = Guard.RequireNotNull(appliesTo);
        Delta = Guard.RequireNotNull(delta);
        Extends = extends.IsDefault ? [] : extends;
        Invariants = invariants.IsDefault ? [] : invariants;
        Annotations = AnnotationMap.Normalize(annotations);
    }

    /// <summary>
    /// Stable profile identifier.
    /// </summary>
    public string Id { get; init; }

    /// <summary>
    /// Matching selector for the profile.
    /// </summary>
    public ShapeProfileSelector AppliesTo { get; init; }

    /// <summary>
    /// Profile ids applied before this profile.
    /// </summary>
    public ImmutableArray<string> Extends { get; init; }

    /// <summary>
    /// Overlay delta represented by this profile.
    /// </summary>
    public OverlayDelta Delta { get; init; }

    /// <summary>
    /// Additional profile invariants.
    /// </summary>
    public ImmutableArray<InvariantDefinition> Invariants { get; init; }

    /// <summary>
    /// Optional profile metadata.
    /// </summary>
    public ImmutableDictionary<AnnotationKey, AnnotationValue> Annotations { get; init; }
}
