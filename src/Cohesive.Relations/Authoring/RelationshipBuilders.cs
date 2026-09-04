using System.Linq.Expressions;
using Cohesive.Relations.Model;

namespace Cohesive.Relations.Authoring;

/// <summary>
/// Direct semantic relationship builder awaiting a source reference path.
/// </summary>
public sealed class RelationshipFromBuilder
{
    readonly QualifiedShapeId sourceShape;

    internal RelationshipFromBuilder(QualifiedShapeId sourceShape)
    {
        this.sourceShape = RelationshipAuthoringGuards.RequireQualifiedShape(sourceShape, nameof(sourceShape));
    }

    /// <summary>Declares the field path containing target observation identities.</summary>
    /// <param name="sourceReference">Reference-bearing field path on the source shape.</param>
    /// <returns>A builder that accepts the relationship target.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="sourceReference"/> does not identify exactly one top-level field.
    /// </exception>
    public RelationshipReferenceBuilder Reference(FieldPath sourceReference) =>
        new(sourceShape, sourceReference);
}

/// <summary>
/// Typed relationship builder awaiting a source member selector.
/// </summary>
/// <typeparam name="TSource">CLR type containing the reference member.</typeparam>
public sealed class RelationshipFromBuilder<TSource> where TSource : notnull
{
    readonly QualifiedShapeId sourceShape;
    readonly ClrShapeGraphBuildResult? clrShapes;

    internal RelationshipFromBuilder(
        QualifiedShapeId sourceShape,
        ClrShapeGraphBuildResult? clrShapes = null)
    {
        this.sourceShape = RelationshipAuthoringGuards.RequireQualifiedShape(sourceShape, nameof(sourceShape));
        this.clrShapes = clrShapes;
    }

    /// <summary>
    /// Declares the member containing target observation identities and immediately lowers it to a field path.
    /// </summary>
    /// <typeparam name="TReference">CLR value type of the selected reference member.</typeparam>
    /// <param name="sourceReference">Member path rooted at the source parameter.</param>
    /// <returns>A typed builder that accepts the relationship target.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="sourceReference"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="sourceReference"/> does not identify exactly one top-level member rooted at its lambda parameter.
    /// </exception>
    public RelationshipReferenceBuilder<TSource> Reference<TReference>(
        Expression<Func<TSource, TReference>> sourceReference)
    {
        ArgumentNullException.ThrowIfNull(sourceReference);
        if (clrShapes is not null)
        {
            return new(sourceShape, FieldPath.Capture(sourceReference, clrShapes.ResolveMemberPath));
        }

        var boxedSelector = Expression.Lambda<Func<TSource, object?>>(
            Expression.Convert(sourceReference.Body, typeof(object)),
            sourceReference.Parameters);
        return new(sourceShape, FieldPath.Capture(boxedSelector));
    }
}

/// <summary>
/// Direct semantic relationship builder awaiting a target shape.
/// </summary>
public sealed class RelationshipReferenceBuilder
{
    readonly QualifiedShapeId sourceShape;
    readonly FieldPath sourceReference;

    internal RelationshipReferenceBuilder(QualifiedShapeId sourceShape, FieldPath sourceReference)
    {
        this.sourceShape = RelationshipAuthoringGuards.RequireQualifiedShape(sourceShape, nameof(sourceShape));
        this.sourceReference = RelationshipAuthoringGuards.RequireFieldPath(sourceReference, nameof(sourceReference));
    }

    /// <summary>Completes a relationship targeting observation identity on an explicit shape.</summary>
    /// <param name="targetShape">Graph-qualified target shape.</param>
    /// <param name="id">
    /// Optional explicit semantic identifier. When omitted, <see cref="RelationshipIdConvention"/> derives it.
    /// </param>
    /// <param name="sourceReferenceUniqueness">Global uniqueness guarantee for source reference values.</param>
    /// <returns>The canonical relationship definition.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="targetShape"/> is default or incomplete, or <paramref name="id"/> contains a default identifier.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="sourceReferenceUniqueness"/> is unsupported.
    /// </exception>
    public RelationshipDefinition To(
        QualifiedShapeId targetShape,
        RelationshipId? id = null,
        SourceReferenceUniqueness sourceReferenceUniqueness = SourceReferenceUniqueness.NotGuaranteed) =>
        RelationshipDefinitionFactory.Create(
            sourceShape,
            sourceReference,
            targetShape,
            id,
            sourceReferenceUniqueness);
}

/// <summary>
/// Typed relationship builder awaiting a target CLR type or explicit target shape.
/// </summary>
/// <typeparam name="TSource">CLR type containing the already-lowered source reference.</typeparam>
public sealed class RelationshipReferenceBuilder<TSource> where TSource : notnull
{
    readonly QualifiedShapeId sourceShape;
    readonly FieldPath sourceReference;

    internal RelationshipReferenceBuilder(QualifiedShapeId sourceShape, FieldPath sourceReference)
    {
        this.sourceShape = RelationshipAuthoringGuards.RequireQualifiedShape(sourceShape, nameof(sourceShape));
        this.sourceReference = RelationshipAuthoringGuards.RequireFieldPath(sourceReference, nameof(sourceReference));
    }

    /// <summary>Completes a relationship using deterministic CLR qualification for the target shape.</summary>
    /// <typeparam name="TTarget">CLR type addressed by the reference.</typeparam>
    /// <param name="id">
    /// Optional explicit semantic identifier. When omitted, <see cref="RelationshipIdConvention"/> derives it.
    /// </param>
    /// <param name="sourceReferenceUniqueness">Global uniqueness guarantee for source reference values.</param>
    /// <returns>The canonical relationship definition.</returns>
    /// <exception cref="ArgumentException"><paramref name="id"/> contains a default identifier.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="sourceReferenceUniqueness"/> is unsupported.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The target CLR type's assembly does not expose a stable name.
    /// </exception>
    public RelationshipDefinition To<TTarget>(
        RelationshipId? id = null,
        SourceReferenceUniqueness sourceReferenceUniqueness = SourceReferenceUniqueness.NotGuaranteed)
        where TTarget : notnull =>
        RelationshipDefinitionFactory.Create(
            sourceShape,
            sourceReference,
            ClrRelationshipShapeConvention.GetQualifiedShapeId<TTarget>(),
            id,
            sourceReferenceUniqueness);

    /// <summary>Completes a typed-source relationship using an explicit canonical target shape.</summary>
    /// <param name="targetShape">Graph-qualified target shape.</param>
    /// <param name="id">
    /// Optional explicit semantic identifier. When omitted, <see cref="RelationshipIdConvention"/> derives it.
    /// </param>
    /// <param name="sourceReferenceUniqueness">Global uniqueness guarantee for source reference values.</param>
    /// <returns>The canonical relationship definition.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="targetShape"/> is default or incomplete, or <paramref name="id"/> contains a default identifier.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="sourceReferenceUniqueness"/> is unsupported.
    /// </exception>
    public RelationshipDefinition To(
        QualifiedShapeId targetShape,
        RelationshipId? id = null,
        SourceReferenceUniqueness sourceReferenceUniqueness = SourceReferenceUniqueness.NotGuaranteed) =>
        RelationshipDefinitionFactory.Create(
            sourceShape,
            sourceReference,
            targetShape,
            id,
            sourceReferenceUniqueness);
}

static class RelationshipDefinitionFactory
{
    public static RelationshipDefinition Create(
        QualifiedShapeId sourceShape,
        FieldPath sourceReference,
        QualifiedShapeId targetShape,
        RelationshipId? id,
        SourceReferenceUniqueness sourceReferenceUniqueness)
    {
        targetShape = RelationshipAuthoringGuards.RequireQualifiedShape(targetShape, nameof(targetShape));
        var targetKey = ObservationIdentityRelationshipTargetKey.Instance;
        var effectiveId = id ?? RelationshipIdConvention.Create(
            sourceShape,
            sourceReference,
            targetShape,
            targetKey,
            sourceReferenceUniqueness);

        return new(
            effectiveId,
            sourceShape,
            sourceReference,
            targetShape,
            targetKey,
            sourceReferenceUniqueness);
    }
}

static class RelationshipAuthoringGuards
{
    public static QualifiedShapeId RequireQualifiedShape(QualifiedShapeId shape, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(shape.GraphId.Value) || string.IsNullOrWhiteSpace(shape.ShapeId.Value))
            throw new ArgumentException("A graph-qualified shape identifier is required.", parameterName);

        return shape;
    }

    public static FieldPath RequireFieldPath(FieldPath path, string parameterName)
    {
        if (path.Segments.Length != 1
            || path.Segments[0].Kind != SegmentKind.Field
            || string.IsNullOrWhiteSpace(path.Segments[0].Segment))
        {
            throw new ArgumentException(
                "A relationship source reference must identify exactly one top-level field.",
                parameterName);
        }

        return path;
    }
}
