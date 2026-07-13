using System.Text.Json.Serialization;

namespace Cohesive.Relations.Model;

/// <summary>
/// Guarantees, if any, that apply to reference values across all source observations.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SourceReferenceUniqueness
{
    /// <summary>Multiple source observations may contain the same reference value.</summary>
    NotGuaranteed = 0,

    /// <summary>At most one source observation may contain a given reference value.</summary>
    GloballyUnique = 1
}

/// <summary>
/// Maximum number of related observations yielded by one semantic traversal.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RelationshipTraversalCardinality
{
    /// <summary>The traversal yields zero or one related observation.</summary>
    AtMostOne = 0,

    /// <summary>The traversal may yield any number of related observations.</summary>
    Many = 1
}

/// <summary>
/// Base semantic key addressed by a relationship reference.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = RelationshipWireNames.TargetKeyDiscriminator)]
[JsonDerivedType(typeof(ObservationIdentityRelationshipTargetKey), RelationshipWireNames.ObservationIdentityTargetKey)]
public abstract record RelationshipTargetKey
{
    private protected RelationshipTargetKey()
    {
    }
}

/// <summary>
/// Indicates that reference values address the stable identity of target observations.
/// </summary>
public sealed record ObservationIdentityRelationshipTargetKey : RelationshipTargetKey
{
    /// <summary>Shared stateless observation-identity target key.</summary>
    public static ObservationIdentityRelationshipTargetKey Instance { get; } = new();

    /// <summary>Creates an observation-identity target key.</summary>
    public ObservationIdentityRelationshipTargetKey()
    {
    }
}

/// <summary>
/// Canonical oriented relationship from a reference-bearing source shape to a target shape.
/// </summary>
/// <remarks>
/// Field cardinality, presence, and nullability remain properties of the source shape. They are
/// deliberately not duplicated here. A required reference requires a key value; it does not
/// guarantee that a matching target observation exists.
/// </remarks>
public sealed record RelationshipDefinition
{
    /// <summary>Creates a canonical relationship definition.</summary>
    /// <param name="id">Stable semantic relationship identifier.</param>
    /// <param name="sourceShape">Graph-qualified shape that contains the reference.</param>
    /// <param name="sourceReference">Reference-bearing field path on <paramref name="sourceShape"/>.</param>
    /// <param name="targetShape">Graph-qualified shape addressed by the reference.</param>
    /// <param name="targetKey">Semantic target key addressed by reference values.</param>
    /// <param name="sourceReferenceUniqueness">Global uniqueness guarantee for source reference values.</param>
    /// <exception cref="ArgumentException">
    /// An identifier is default or <paramref name="sourceReference"/> is default.
    /// </exception>
    /// <exception cref="ArgumentNullException"><paramref name="targetKey"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="sourceReferenceUniqueness"/> is not a supported value.
    /// </exception>
    [JsonConstructor]
    public RelationshipDefinition(
        RelationshipId id,
        QualifiedShapeId sourceShape,
        FieldPath sourceReference,
        QualifiedShapeId targetShape,
        RelationshipTargetKey targetKey,
        SourceReferenceUniqueness sourceReferenceUniqueness = SourceReferenceUniqueness.NotGuaranteed)
    {
        RequireIdentifier(id.Value, nameof(id), "Relationship id");
        RequireQualifiedShape(sourceShape, nameof(sourceShape));
        if (sourceReference.Segments.IsDefaultOrEmpty)
            throw new ArgumentException("A relationship source reference is required.", nameof(sourceReference));
        RequireQualifiedShape(targetShape, nameof(targetShape));
        ArgumentNullException.ThrowIfNull(targetKey);
        if (!Enum.IsDefined(sourceReferenceUniqueness))
            throw new ArgumentOutOfRangeException(nameof(sourceReferenceUniqueness), sourceReferenceUniqueness, "Unsupported source-reference uniqueness value.");

        Id = id;
        SourceShape = sourceShape;
        SourceReference = sourceReference;
        TargetShape = targetShape;
        TargetKey = targetKey;
        SourceReferenceUniqueness = sourceReferenceUniqueness;
    }

    /// <summary>Stable semantic relationship identifier.</summary>
    public RelationshipId Id { get; init; }

    /// <summary>Graph-qualified shape that contains the reference.</summary>
    public QualifiedShapeId SourceShape { get; init; }

    /// <summary>Reference-bearing field path on <see cref="SourceShape"/>.</summary>
    public FieldPath SourceReference { get; init; }

    /// <summary>Graph-qualified shape addressed by reference values.</summary>
    public QualifiedShapeId TargetShape { get; init; }

    /// <summary>Semantic target key addressed by reference values.</summary>
    public RelationshipTargetKey TargetKey { get; init; }

    /// <summary>Global uniqueness guarantee for source reference values.</summary>
    public SourceReferenceUniqueness SourceReferenceUniqueness { get; init; }

    /// <summary>Maximum inverse-traversal cardinality implied by source-reference uniqueness.</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="SourceReferenceUniqueness"/> contains an unsupported value.
    /// </exception>
    [JsonIgnore]
    public RelationshipTraversalCardinality InverseCardinality =>
        SourceReferenceUniqueness switch
        {
            SourceReferenceUniqueness.NotGuaranteed => RelationshipTraversalCardinality.Many,
            SourceReferenceUniqueness.GloballyUnique => RelationshipTraversalCardinality.AtMostOne,
            _ => throw new ArgumentOutOfRangeException(
                nameof(SourceReferenceUniqueness),
                SourceReferenceUniqueness,
                "Unsupported source-reference uniqueness value.")
        };

    /// <summary>Derives forward-traversal cardinality from the supplied source field.</summary>
    /// <param name="sourceReferenceField">Validated source field represented by <see cref="SourceReference"/>.</param>
    /// <returns>Maximum forward-traversal cardinality.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="sourceReferenceField"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="sourceReferenceField"/> declares an unsupported field cardinality.
    /// </exception>
    public RelationshipTraversalCardinality GetForwardCardinality(FieldDefinition sourceReferenceField)
    {
        ArgumentNullException.ThrowIfNull(sourceReferenceField);
        return sourceReferenceField.Cardinality switch
        {
            FieldCardinality.Single => RelationshipTraversalCardinality.AtMostOne,
            FieldCardinality.Many => RelationshipTraversalCardinality.Many,
            _ => throw new ArgumentOutOfRangeException(
                nameof(sourceReferenceField),
                sourceReferenceField.Cardinality,
                "Unsupported source-reference field cardinality.")
        };
    }

    static void RequireIdentifier(string? value, string parameterName, string displayName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{displayName} is required.", parameterName);
    }

    static void RequireQualifiedShape(QualifiedShapeId shape, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(shape.GraphId.Value) || string.IsNullOrWhiteSpace(shape.ShapeId.Value))
            throw new ArgumentException("A graph-qualified shape identifier is required.", parameterName);
    }
}

/// <summary>Stable discriminator property and case names for relationship catalog wire contracts.</summary>
public static class RelationshipWireNames
{
    /// <summary>Discriminator property used by <see cref="RelationshipTargetKey"/>.</summary>
    public const string TargetKeyDiscriminator = "$targetKey";

    /// <summary>Discriminator value for <see cref="ObservationIdentityRelationshipTargetKey"/>.</summary>
    public const string ObservationIdentityTargetKey = "observationIdentity";
}
