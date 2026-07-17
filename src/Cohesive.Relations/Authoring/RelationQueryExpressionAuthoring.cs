using System.Collections.Immutable;
using System.Linq.Expressions;
using System.Reflection;
using Cohesive.Model.Serialization;
using Cohesive.Relations.IR;
using Cohesive.Relations.Model;
using Cohesive.Relations.Serialization;

namespace Cohesive.Relations.Authoring;

/// <summary>
/// Authors canonical relations and queries from typed CLR bindings and C# expression trees.
/// </summary>
/// <remarks>
/// This facade is a producer of portable relation/query IR rather than an execution API or an
/// <see cref="IQueryable{T}"/> provider. Every committed operation delegates to
/// <see cref="RelationQueryAuthoringCore"/>, which remains the sole owner of canonical nodes,
/// identities, validation, and snapshots. <see cref="Structural"/> is the unbounded escape hatch
/// for canonical constructs outside the expression translator's supported closure.
/// A session is mutable and is not thread-safe.
/// </remarks>
public sealed partial class RelationQueryExpressionAuthoring
{
    /// <summary>Stable producer identity used by expression-authoring provenance.</summary>
    public const string Producer = "cohesive.relations.csharp-expression/v1";

    readonly RelationQueryAuthoringCore structural = new();
    readonly RelationQueryClrAuthoringContext clr;
    readonly RelationQueryExpressionLowerer lowerer;
    readonly Dictionary<Type, ClrPathRegistration> clrPaths = [];
    readonly HashSet<RelationQueryDefinitionFingerprint> builtQueryFingerprints = [];

    /// <summary>Creates a CLR expression-authoring session.</summary>
    /// <param name="clr">
    /// Optional deterministic CLR shape/member context. A context using the default metadata profile
    /// is created when this value is <see langword="null"/>.
    /// </param>
    public RelationQueryExpressionAuthoring(RelationQueryClrAuthoringContext? clr = null)
    {
        this.clr = clr ?? new RelationQueryClrAuthoringContext();
        lowerer = new(ResolveMemberPath, this);
    }

    /// <summary>
    /// Structural lowering core owned by this session.
    /// </summary>
    /// <remarks>
    /// Developers may use this property to author a canonical operation that is not supported by the
    /// expression frontend, then continue using the resulting structural handles in this session.
    /// </remarks>
    public RelationQueryAuthoringCore Structural => structural;

    /// <summary>Deterministic CLR shape and member-binding context used by this session.</summary>
    public RelationQueryClrAuthoringContext Clr => clr;

    /// <summary>
    /// Exact deterministic shape-graph documents discovered by the session, ordered by graph identity.
    /// </summary>
    public ImmutableArray<ShapeGraphDocument> ShapeDocuments =>
    [
        .. clrPaths.Values
            .GroupBy(static registration => registration.Id.GraphId)
            .OrderBy(static group => group.Key.Value, StringComparer.Ordinal)
            .Select(group => clr.GetShapeDocument(group.First().Id))
    ];

    internal RelationQueryExpressionLowerer ExpressionLowerer => lowerer;

    /// <summary>Declares a source using deterministic CLR shape conventions.</summary>
    /// <typeparam name="T">CLR source type.</typeparam>
    /// <param name="sourceReference">Optional stable producer reference for provenance.</param>
    /// <returns>Typed source-node and source-binding handles.</returns>
    /// <exception cref="InvalidOperationException">
    /// The CLR type cannot be represented by the configured shape metadata profile.
    /// </exception>
    public RelationQueryExpressionBoundNode<SourceQueryNode, T> Source<T>(
        string? sourceReference = null)
        where T : notnull =>
        Source(clr.Shape<T>(), sourceReference);

    /// <summary>Declares a source using an explicit graph-qualified shape identity.</summary>
    /// <typeparam name="T">CLR source type.</typeparam>
    /// <param name="shape">Explicit semantic shape identity, which takes precedence over conventions.</param>
    /// <param name="sourceReference">Optional stable producer reference for provenance.</param>
    /// <returns>Typed source-node and source-binding handles.</returns>
    /// <exception cref="ArgumentException"><paramref name="shape"/> is default or incomplete.</exception>
    /// <exception cref="InvalidOperationException">
    /// The CLR type cannot be represented by the configured shape metadata profile.
    /// </exception>
    public RelationQueryExpressionBoundNode<SourceQueryNode, T> Source<T>(
        QualifiedShapeId shape,
        string? sourceReference = null)
        where T : notnull =>
        Source(clr.Shape<T>(shape), sourceReference);

    /// <summary>Declares a source from a previously resolved typed CLR shape.</summary>
    /// <typeparam name="T">CLR source type.</typeparam>
    /// <param name="shape">Typed CLR shape resolved by this session's metadata policy.</param>
    /// <param name="sourceReference">Optional stable producer reference for provenance.</param>
    /// <returns>Typed source-node and source-binding handles.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="shape"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// This session already binds <typeparamref name="T"/> to another semantic shape.
    /// </exception>
    public RelationQueryExpressionBoundNode<SourceQueryNode, T> Source<T>(
        RelationQueryClrShape<T> shape,
        string? sourceReference = null)
        where T : notnull
    {
        ArgumentNullException.ThrowIfNull(shape);
        TrackShape(shape);
        var reference = sourceReference ?? $"source/{StableTypeName(typeof(T))}";
        var source = structural.Source(
            shape.Id,
            source: Source(reference, $"CLR source '{StableTypeName(typeof(T))}'."),
            bindingSource: Source(reference + "/binding", $"CLR binding '{StableTypeName(typeof(T))}'."));
        return new(
            source.Node,
            new RelationQueryExpressionValueBinding<T>(
                this,
                source.Binding,
                shape.Type,
                shape.Id,
                shape.ResolveMemberPath,
                shape.ResolveType,
                shape.IdentityOrigin == RelationQueryClrIdentityOrigin.Imported));
    }

    /// <summary>Declares a required or optional typed invocation parameter without a default value.</summary>
    /// <typeparam name="T">Supported CLR parameter type.</typeparam>
    /// <param name="id">Stable canonical parameter identity.</param>
    /// <param name="presence">Whether invocation evidence is required.</param>
    /// <param name="sourceReference">Optional stable producer reference for provenance.</param>
    /// <returns>A typed parameter handle usable inside expression-authoring lambdas.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="id"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="id"/> is empty or repeated.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="presence"/> is unsupported.</exception>
    /// <exception cref="NotSupportedException">
    /// <typeparamref name="T"/> has no metadata-independent portable mapping or requires metadata-aware
    /// object, enum, quantity, entity-reference, or opaque-runtime conversion that the expression
    /// parameter frontend does not perform.
    /// </exception>
    public RelationQueryExpressionParameter<T> Parameter<T>(
        string id,
        FieldPresence presence = FieldPresence.Required,
        string? sourceReference = null)
    {
        id = Guard.RequireNotNullOrWhiteSpace(id);
        var reference = sourceReference ?? $"parameter/{id}";
        var type = ResolveMetadataIndependentParameterType(typeof(T));
        var parameter = structural.Parameter(
            type,
            presence,
            id: new QueryParameterId(id),
            source: Source(reference, $"Typed CLR parameter '{StableTypeName(typeof(T))}'."));
        return new(this, parameter, isProvablyNonNull: presence == FieldPresence.Required);
    }

    /// <summary>Authors a canonical CLR relationship using this session's exact shape/member metadata.</summary>
    /// <typeparam name="TSource">CLR type containing the reference property.</typeparam>
    /// <typeparam name="TReference">CLR type of the reference value.</typeparam>
    /// <typeparam name="TTarget">CLR type addressed by the reference.</typeparam>
    /// <param name="sourceReference">Direct source property containing target observation identities.</param>
    /// <param name="id">
    /// Optional explicit semantic relationship identity; the canonical convention derives one when omitted.
    /// </param>
    /// <param name="sourceReferenceUniqueness">Global uniqueness guarantee for source reference values.</param>
    /// <returns>A typed handle for the canonical relationship and its CLR endpoints.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="sourceReference"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="sourceReference"/> is not one direct readable property rooted at its parameter, or
    /// <paramref name="id"/> is default.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="sourceReferenceUniqueness"/> is unsupported.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// A CLR endpoint cannot be represented by the configured shape metadata profile or is already
    /// bound to an incompatible semantic shape in this session.
    /// </exception>
    public RelationQueryExpressionRelationship<TSource, TTarget> Relationship<TSource, TReference, TTarget>(
        Expression<Func<TSource, TReference>> sourceReference,
        RelationshipId? id = null,
        SourceReferenceUniqueness sourceReferenceUniqueness = SourceReferenceUniqueness.NotGuaranteed)
        where TSource : notnull
        where TTarget : notnull
    {
        ArgumentNullException.ThrowIfNull(sourceReference);
        var sourceShape = clr.Shape<TSource>();
        var targetShape = clr.Shape<TTarget>();
        TrackShape(sourceShape);
        TrackShape(targetShape);
        var path = ResolveSelectorPath(sourceReference, nameof(sourceReference));
        if (path.Segments.Length != 1)
        {
            throw new ArgumentException(
                "A relationship source reference must identify exactly one top-level CLR property.",
                nameof(sourceReference));
        }

        var definition = global::Cohesive.Relations.Authoring.Relationship
            .From(sourceShape.Id)
            .Reference(path)
            .To(targetShape.Id, id, sourceReferenceUniqueness);
        return new(definition);
    }

    /// <summary>Authors a typed canonical CLR relationship using a boxed direct reference selector.</summary>
    /// <typeparam name="TSource">CLR type containing the reference property.</typeparam>
    /// <typeparam name="TTarget">CLR type addressed by the reference.</typeparam>
    /// <param name="sourceReference">Direct source property containing target observation identities.</param>
    /// <param name="id">
    /// Optional explicit semantic relationship identity; the canonical convention derives one when omitted.
    /// </param>
    /// <param name="sourceReferenceUniqueness">Global uniqueness guarantee for source reference values.</param>
    /// <returns>A typed handle for the canonical relationship and its CLR endpoints.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="sourceReference"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="sourceReference"/> is not one direct readable property rooted at its parameter, or
    /// <paramref name="id"/> is default.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="sourceReferenceUniqueness"/> is unsupported.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// A CLR endpoint cannot be represented by the configured shape metadata profile or is already
    /// bound to an incompatible semantic shape in this session.
    /// </exception>
    public RelationQueryExpressionRelationship<TSource, TTarget> Relationship<TSource, TTarget>(
        Expression<Func<TSource, object?>> sourceReference,
        RelationshipId? id = null,
        SourceReferenceUniqueness sourceReferenceUniqueness = SourceReferenceUniqueness.NotGuaranteed)
        where TSource : notnull
        where TTarget : notnull =>
        Relationship<TSource, object?, TTarget>(sourceReference, id, sourceReferenceUniqueness);

    /// <summary>Declares an optional typed invocation parameter with a persisted default value.</summary>
    /// <typeparam name="T">Supported CLR parameter type.</typeparam>
    /// <param name="id">Stable canonical parameter identity.</param>
    /// <param name="defaultValue">
    /// CLR fallback converted to a canonical observation; <see langword="null"/> becomes an explicit null default.
    /// </param>
    /// <param name="sourceReference">Optional stable producer reference for provenance.</param>
    /// <returns>A typed parameter handle usable inside expression-authoring lambdas.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="id"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="id"/> is empty or repeated, or the converted default is incompatible with the
    /// inferred portable parameter type.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// <typeparamref name="T"/> has no metadata-independent portable mapping, requires metadata-aware
    /// conversion that this frontend does not perform, or <paramref name="defaultValue"/> cannot be
    /// represented as a lossless canonical relation/query JSON value.
    /// </exception>
    public RelationQueryExpressionParameter<T> Parameter<T>(
        string id,
        T defaultValue,
        string? sourceReference = null)
    {
        id = Guard.RequireNotNullOrWhiteSpace(id);
        var reference = sourceReference ?? $"parameter/{id}";
        var type = ResolveMetadataIndependentParameterType(typeof(T));

        var observedDefault = ObservationValue.FromObject(defaultValue);
        if (RelationQueryPortableObservationValueSemantics.TryGetCanonicalJsonIssue(
                observedDefault,
                out _,
                out var message))
        {
            throw new NotSupportedException(message);
        }

        var parameter = structural.Parameter(
            type,
            FieldPresence.Optional,
            observedDefault,
            new QueryParameterId(id),
            Source(reference, $"Typed CLR parameter '{StableTypeName(typeof(T))}' with a default."));
        return new(this, parameter, isProvablyNonNull: defaultValue is not null);
    }

    static void RequireMetadataIndependentParameterType(TypeRef type, Type clrType)
    {
        if (IsMetadataIndependentParameterType(type))
            return;

        throw new NotSupportedException(
            $"CLR parameter type '{StableTypeName(clrType)}' maps to semantic type '{type}', which requires "
            + "metadata-aware object, enum, quantity, entity-reference, or opaque-runtime conversion. "
            + "Expression-authored parameters currently support scalar, JSON, and recursively scalar/JSON array types; "
            + "author this parameter structurally when supplying an explicit canonical converter.");
    }

    static bool IsMetadataIndependentParameterType(TypeRef type) => type switch
    {
        ScalarTypeRef => true,
        JsonTypeRef => true,
        ArrayTypeRef array => IsMetadataIndependentParameterType(array.ElementType),
        _ => false
    };

    TypeRef ResolveMetadataIndependentParameterType(Type clrType)
    {
        TypeRef type;
        try
        {
            type = clr.GetTypeRef(clrType);
        }
        catch (InvalidOperationException exception)
        {
            throw new NotSupportedException(
                $"CLR parameter type '{StableTypeName(clrType)}' has no metadata-independent portable mapping; metadata-aware conversion is required. "
                + "Expression-authored parameters currently support scalar, JSON, and recursively scalar/JSON array types.",
                exception);
        }

        RequireMetadataIndependentParameterType(type, clrType);
        return type;
    }

    /// <summary>Traverses an explicit canonical relationship from a typed visible binding.</summary>
    /// <typeparam name="TInput">Canonical type of the input logical node.</typeparam>
    /// <typeparam name="TFrom">CLR type at the relationship's selected endpoint.</typeparam>
    /// <typeparam name="TRelated">CLR type at the traversed endpoint.</typeparam>
    /// <param name="input">Logical input containing <paramref name="from"/>.</param>
    /// <param name="from">Typed binding from which traversal starts.</param>
    /// <param name="relationship">Canonical relationship definition to traverse.</param>
    /// <param name="direction">Relationship direction.</param>
    /// <param name="joinKind">Behavior when related values are absent.</param>
    /// <param name="requirement">Whether resolving the related value is required.</param>
    /// <param name="sourceReference">Optional stable producer reference for provenance.</param>
    /// <returns>Typed traversal-node and related-binding handles.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="from"/> or <paramref name="relationship"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="input"/> or <paramref name="from"/> belongs to another session,
    /// <paramref name="from"/> is not visible in <paramref name="input"/>, or its shape does not match
    /// the selected relationship endpoint.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="direction"/>, <paramref name="joinKind"/>, or <paramref name="requirement"/> is unsupported.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// <typeparamref name="TRelated"/> cannot be mapped to the relationship's selected endpoint shape.
    /// </exception>
    public RelationQueryExpressionBoundNode<TraverseRelationshipQueryNode, TRelated> Traverse<TInput, TFrom, TRelated>(
        RelationQueryNodeHandle<TInput> input,
        RelationQueryExpressionValueBinding<TFrom> from,
        RelationshipDefinition relationship,
        RelationshipTraversalDirection direction = RelationshipTraversalDirection.Forward,
        JoinKind joinKind = JoinKind.Left,
        QueryInputRequirement requirement = QueryInputRequirement.Required,
        string? sourceReference = null)
        where TInput : LogicalQueryNode
        where TFrom : notnull
        where TRelated : notnull
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(relationship);
        RequireBindingVisible(input, from, nameof(from));
        var (expectedFrom, expectedRelated) = direction switch
        {
            RelationshipTraversalDirection.Forward => (relationship.SourceShape, relationship.TargetShape),
            RelationshipTraversalDirection.Inverse => (relationship.TargetShape, relationship.SourceShape),
            _ => throw new ArgumentOutOfRangeException(nameof(direction), direction, "Unsupported relationship direction.")
        };
        if (from.Shape != expectedFrom)
        {
            throw new ArgumentException(
                $"Binding shape '{from.Shape}' does not match relationship endpoint '{expectedFrom}' for {direction} traversal.",
                nameof(from));
        }

        var relatedShape = clr.Shape<TRelated>(expectedRelated);
        TrackShape(relatedShape);
        var reference = sourceReference ?? $"traverse/{relationship.Id.Value}";
        var traversed = structural.Traverse(
            input,
            from.Structural,
            relationship.Id,
            direction,
            joinKind,
            requirement,
            source: Source(reference, $"Traversal of relationship '{relationship.Id.Value}'."),
            bindingSource: Source(reference + "/binding", $"Related CLR binding '{StableTypeName(typeof(TRelated))}'."));
        return new(
            traversed.Node,
            new RelationQueryExpressionValueBinding<TRelated>(
                this,
                traversed.Binding,
                relatedShape.Type,
                relatedShape.Id,
                relatedShape.ResolveMemberPath,
                relatedShape.ResolveType,
                relatedShape.IdentityOrigin == RelationQueryClrIdentityOrigin.Imported));
    }

    /// <summary>Traverses a typed expression-authored relationship from its source endpoint.</summary>
    /// <typeparam name="TInput">Canonical type of the input logical node.</typeparam>
    /// <typeparam name="TFrom">CLR type at the relationship source endpoint.</typeparam>
    /// <typeparam name="TRelated">CLR type at the relationship target endpoint.</typeparam>
    /// <param name="input">Logical input containing <paramref name="from"/>.</param>
    /// <param name="from">Typed source binding from which traversal starts.</param>
    /// <param name="relationship">Typed canonical relationship to traverse.</param>
    /// <param name="joinKind">Behavior when related values are absent.</param>
    /// <param name="requirement">Whether resolving the related value is required.</param>
    /// <param name="sourceReference">Optional stable producer reference for provenance.</param>
    /// <returns>Typed traversal-node and related-binding handles.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="from"/> or <paramref name="relationship"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// A handle belongs to another session, <paramref name="from"/> is not visible in
    /// <paramref name="input"/>, or a shape is incompatible.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="joinKind"/> or <paramref name="requirement"/> is unsupported.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// <typeparamref name="TRelated"/> cannot be mapped to the relationship target shape.
    /// </exception>
    public RelationQueryExpressionBoundNode<TraverseRelationshipQueryNode, TRelated> Traverse<TInput, TFrom, TRelated>(
        RelationQueryNodeHandle<TInput> input,
        RelationQueryExpressionValueBinding<TFrom> from,
        RelationQueryExpressionRelationship<TFrom, TRelated> relationship,
        JoinKind joinKind = JoinKind.Left,
        QueryInputRequirement requirement = QueryInputRequirement.Required,
        string? sourceReference = null)
        where TInput : LogicalQueryNode
        where TFrom : notnull
        where TRelated : notnull
    {
        ArgumentNullException.ThrowIfNull(relationship);
        return Traverse<TInput, TFrom, TRelated>(
            input,
            from,
            relationship.Definition,
            RelationshipTraversalDirection.Forward,
            joinKind,
            requirement,
            sourceReference);
    }

    /// <summary>Traverses a typed expression-authored relationship from its target to its source endpoint.</summary>
    /// <typeparam name="TInput">Canonical type of the input logical node.</typeparam>
    /// <typeparam name="TSource">CLR type at the inverse traversal result.</typeparam>
    /// <typeparam name="TTarget">CLR type at the relationship target endpoint.</typeparam>
    /// <param name="input">Logical input containing <paramref name="from"/>.</param>
    /// <param name="from">Typed target binding from which inverse traversal starts.</param>
    /// <param name="relationship">Typed canonical relationship to traverse inversely.</param>
    /// <param name="joinKind">Behavior when related values are absent.</param>
    /// <param name="requirement">Whether resolving related source values is required.</param>
    /// <param name="sourceReference">Optional stable producer reference for provenance.</param>
    /// <returns>Typed traversal-node and source-binding handles.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="from"/> or <paramref name="relationship"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// A handle belongs to another session, <paramref name="from"/> is not visible in
    /// <paramref name="input"/>, or a shape is incompatible.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="joinKind"/> or <paramref name="requirement"/> is unsupported.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// <typeparamref name="TSource"/> cannot be mapped to the relationship source shape.
    /// </exception>
    public RelationQueryExpressionBoundNode<TraverseRelationshipQueryNode, TSource> TraverseInverse<TInput, TSource, TTarget>(
        RelationQueryNodeHandle<TInput> input,
        RelationQueryExpressionValueBinding<TTarget> from,
        RelationQueryExpressionRelationship<TSource, TTarget> relationship,
        JoinKind joinKind = JoinKind.Left,
        QueryInputRequirement requirement = QueryInputRequirement.Required,
        string? sourceReference = null)
        where TInput : LogicalQueryNode
        where TSource : notnull
        where TTarget : notnull
    {
        ArgumentNullException.ThrowIfNull(relationship);
        return Traverse<TInput, TTarget, TSource>(
            input,
            from,
            relationship.Definition,
            RelationshipTraversalDirection.Inverse,
            joinKind,
            requirement,
            sourceReference);
    }

    /// <summary>Declares a typed named row result over an expression-authored branch.</summary>
    /// <typeparam name="TNode">Canonical type of the branch input node.</typeparam>
    /// <typeparam name="T">CLR type represented by each row.</typeparam>
    /// <param name="input">Typed projected branch.</param>
    /// <param name="id">Optional explicit result identity.</param>
    /// <param name="sourceReference">Optional stable producer reference for provenance.</param>
    /// <returns>A typed named row-result handle.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="input"/> belongs to another session, exposes more than one visible binding, or
    /// <paramref name="id"/> is invalid.
    /// </exception>
    /// <exception cref="InvalidOperationException">The row binding has no graph-qualified result shape.</exception>
    public RelationQueryExpressionRowsResult<T> Rows<TNode, T>(
        RelationQueryExpressionBoundNode<TNode, T> input,
        string? id = null,
        string? sourceReference = null)
        where TNode : LogicalQueryNode
        where T : notnull
    {
        ArgumentNullException.ThrowIfNull(input);
        RequireBindingVisible(input.Node, input.Binding, nameof(input));
        RequireSingleVisibleBinding(input.Node, nameof(input));
        var shape = RequireShape(input.Binding);
        var reference = sourceReference ?? $"result/{id ?? "rows"}";
        var result = structural.Rows(
            input.Node,
            id is null ? null : new QueryResultId(Guard.RequireNotNullOrWhiteSpace(id)),
            Source(reference, $"Rows of '{StableTypeName(typeof(T))}'."));
        return new(this, result, shape);
    }

    /// <summary>Declares a typed named row result over a logical branch and visible CLR binding.</summary>
    /// <typeparam name="TNode">Canonical type of the branch input node.</typeparam>
    /// <typeparam name="T">CLR type represented by each row.</typeparam>
    /// <param name="input">Logical branch producing rows.</param>
    /// <param name="binding">Typed binding that describes each produced row and its semantic shape.</param>
    /// <param name="id">Optional explicit result identity.</param>
    /// <param name="sourceReference">Optional stable producer reference for provenance.</param>
    /// <returns>A typed named row-result handle.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="binding"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="input"/> or <paramref name="binding"/> belongs to another session, or
    /// <paramref name="binding"/> is not visible in <paramref name="input"/>, or <paramref name="id"/>
    /// is invalid, or <paramref name="input"/> exposes more than one visible binding.
    /// </exception>
    /// <exception cref="InvalidOperationException"><paramref name="binding"/> has no graph-qualified result shape.</exception>
    public RelationQueryExpressionRowsResult<T> Rows<TNode, T>(
        RelationQueryNodeHandle<TNode> input,
        RelationQueryExpressionValueBinding<T> binding,
        string? id = null,
        string? sourceReference = null)
        where TNode : LogicalQueryNode
        where T : notnull
    {
        ArgumentNullException.ThrowIfNull(binding);
        RequireBindingVisible(input, binding, nameof(binding));
        RequireSingleVisibleBinding(input, nameof(input));
        var shape = RequireShape(binding);
        var reference = sourceReference ?? $"result/{id ?? "rows"}";
        var result = structural.Rows(
            input,
            id is null ? null : new QueryResultId(Guard.RequireNotNullOrWhiteSpace(id)),
            Source(reference, $"Rows of '{StableTypeName(typeof(T))}'."));
        return new(this, result, shape);
    }

    /// <summary>Declares a typed named aggregation result over an aggregate branch.</summary>
    /// <typeparam name="T">CLR type represented by each aggregation row.</typeparam>
    /// <param name="input">Typed aggregate branch.</param>
    /// <param name="id">Optional explicit result identity.</param>
    /// <param name="sourceReference">Optional stable producer reference for provenance.</param>
    /// <returns>A typed named aggregation-result handle.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="input"/> belongs to another session or <paramref name="id"/> is invalid.</exception>
    public RelationQueryExpressionAggregationResult<T> Aggregation<T>(
        RelationQueryExpressionBoundNode<AggregateQueryNode, T> input,
        string? id = null,
        string? sourceReference = null)
        where T : notnull
    {
        ArgumentNullException.ThrowIfNull(input);
        RequireOwner(input.Binding);
        var reference = sourceReference ?? $"result/{id ?? "aggregation"}";
        var result = structural.Aggregation(
            input.Node,
            id is null ? null : new QueryResultId(Guard.RequireNotNullOrWhiteSpace(id)),
            Source(reference, $"Aggregation rows of '{StableTypeName(typeof(T))}'."));
        return new(this, result, RequireShape(input.Binding));
    }

    /// <summary>Builds and validates a canonical query from typed named results.</summary>
    /// <param name="id">Stable canonical query identity.</param>
    /// <param name="name">Human-readable canonical query name.</param>
    /// <param name="results">Non-empty named row and aggregation results owned by this session.</param>
    /// <param name="sourceReference">Optional stable producer reference for provenance.</param>
    /// <returns>The canonical query, validation result, and authoring provenance.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="results"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="results"/> is empty, contains <see langword="null"/>, repeats an identity, or contains
    /// a result owned by another session.
    /// </exception>
    public RelationQueryAuthoringResult<QueryDefinition> BuildQuery(
        QueryId id,
        QueryName name,
        IEnumerable<RelationQueryExpressionResult> results,
        string? sourceReference = null)
    {
        ArgumentNullException.ThrowIfNull(results);
        var materialized = results.ToImmutableArray();
        if (materialized.Any(static result => result is null))
            throw new ArgumentException("Query results cannot contain null entries.", nameof(results));
        foreach (var result in materialized)
            RequireOwner(result);

        var reference = sourceReference ?? $"query/{id.Value}";
        var builtQuery = structural.BuildQuery(
            id,
            name,
            [.. materialized.Select(static result => result.Structural)],
            Source(reference, $"Expression-authored query '{name.Value}'."));
        if (builtQuery.Validation.IsValid)
            builtQueryFingerprints.Add(RelationQueryDefinitionFingerprinter.Compute(builtQuery.Definition));
        return builtQuery;
    }

    /// <summary>Builds and validates a canonical query from typed named results.</summary>
    /// <param name="id">Stable canonical query identity.</param>
    /// <param name="name">Human-readable canonical query name.</param>
    /// <param name="results">Non-empty named row and aggregation results owned by this session.</param>
    /// <returns>The canonical query, validation result, and authoring provenance.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="results"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="results"/> is empty, contains <see langword="null"/>, repeats an identity, or contains
    /// a result owned by another session.
    /// </exception>
    public RelationQueryAuthoringResult<QueryDefinition> BuildQuery(
        QueryId id,
        QueryName name,
        params RelationQueryExpressionResult[] results) =>
        BuildQuery(id, name, (IEnumerable<RelationQueryExpressionResult>)results);

    /// <summary>
    /// Builds and validates a canonical relation when its optional key and invariants are already canonical.
    /// </summary>
    /// <typeparam name="TRoot">CLR root type.</typeparam>
    /// <typeparam name="TOutputNode">Canonical type of the output logical node.</typeparam>
    /// <typeparam name="TOutput">CLR output type.</typeparam>
    /// <param name="id">Stable canonical relation identity.</param>
    /// <param name="name">Human-readable canonical relation name.</param>
    /// <param name="root">Root source binding.</param>
    /// <param name="output">Logical node producing relation outputs.</param>
    /// <param name="outputBinding">Typed projected or aggregate output binding.</param>
    /// <param name="mode">Output cardinality relative to each root.</param>
    /// <param name="key">Optional already-canonical output-key expression.</param>
    /// <param name="invariants">Optional already-canonical output invariants.</param>
    /// <param name="sourceReference">Optional stable producer reference for provenance.</param>
    /// <returns>The canonical relation, validation result, and authoring provenance.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="root"/> or <paramref name="outputBinding"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// A handle belongs to another authoring session, <paramref name="outputBinding"/> is not visible in
    /// <paramref name="output"/>, <paramref name="output"/> exposes more than one visible binding, or
    /// <paramref name="invariants"/> contains a null entry.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="mode"/> is unsupported.</exception>
    public RelationQueryAuthoringResult<RelationDefinition> BuildRelation<TRoot, TOutputNode, TOutput>(
        RelationId id,
        RelationName name,
        RelationQueryExpressionValueBinding<TRoot> root,
        RelationQueryNodeHandle<TOutputNode> output,
        RelationQueryExpressionValueBinding<TOutput> outputBinding,
        RelationOutputMode mode = RelationOutputMode.OnePerRoot,
        Expr? key = null,
        ImmutableArray<InvariantDefinition> invariants = default,
        string? sourceReference = null)
        where TRoot : notnull
        where TOutputNode : LogicalQueryNode
        where TOutput : notnull
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(outputBinding);
        RequireOwner(root);
        RequireBindingVisible(output, outputBinding, nameof(outputBinding));
        RequireSingleVisibleBinding(output, nameof(output));
        var reference = sourceReference ?? $"relation/{id.Value}";
        return structural.BuildRelation(
            id,
            name,
            root.Structural,
            output,
            RequireShape(outputBinding),
            mode,
            key,
            invariants,
            Source(reference, $"Expression-authored relation '{name.Value}'."),
            key is null ? null : Source(reference + "/key", "Relation output key."));
    }

    void RequireSingleVisibleBinding<TNode>(
        RelationQueryNodeHandle<TNode> node,
        string parameterName)
        where TNode : LogicalQueryNode
    {
        if (structural.GetVisibleBindingCount(node) == 1)
            return;

        throw new ArgumentException(
            $"Logical node '{node.Id.Value}' exposes multiple value bindings, but the canonical terminal does not "
            + "persist a selected binding. Project the intended value into one output binding before declaring "
            + "a rows result or relation terminal.",
            parameterName);
    }

    internal FieldPath ResolveMemberPath(Type rootType, IReadOnlyList<PropertyInfo> members) =>
        clrPaths.TryGetValue(Nullable.GetUnderlyingType(rootType) ?? rootType, out var registration)
            ? registration.Resolve(members)
            : clr.ResolveMemberPath(rootType, members);

    internal RelationQueryClrShape<T> TrackShape<T>(RelationQueryClrShape<T> shape)
        where T : notnull
    {
        ArgumentNullException.ThrowIfNull(shape);
        if (!ReferenceEquals(shape.AuthoringContext, clr))
        {
            throw new InvalidOperationException(
                "A CLR shape handle must be created by this expression-authoring session's CLR context. "
                + "Register or import the shape through the session's Clr property before using it.");
        }

        var type = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        if (clrPaths.TryGetValue(type, out var existing))
        {
            if (existing.Id != shape.Id)
            {
                throw new InvalidOperationException(
                    $"CLR type '{StableTypeName(type)}' is already bound to semantic shape '{existing.Id}' in this authoring session; " +
                    $"it cannot also bind to '{shape.Id}'. Use a separate CLR type or authoring session for another semantic profile.");
            }

            return shape;
        }

        clrPaths.Add(type, new(shape.Id, shape.ResolveMemberPath));
        return shape;
    }

    internal FieldPath ResolveSelectorPath<T, TValue>(
        Expression<Func<T, TValue>> selector,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(selector);
        Expression current = selector.Body;
        while (current is UnaryExpression
            {
                NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked
            } conversion
               && (conversion.Type == typeof(object) || conversion.Type == conversion.Operand.Type))
        {
            current = conversion.Operand;
        }

        List<PropertyInfo> reversed = [];
        while (current is MemberExpression member)
        {
            if (member.Member is not PropertyInfo property)
                throw new ArgumentException("A semantic field selector must use readable CLR properties.", parameterName);
            reversed.Add(property);
            current = member.Expression
                ?? throw new ArgumentException("A semantic field selector cannot use a static member.", parameterName);
        }

        if (!ReferenceEquals(current, selector.Parameters[0]) || reversed.Count == 0)
        {
            throw new ArgumentException(
                "A semantic field selector must be a direct or nested property chain rooted at the selector parameter.",
                parameterName);
        }

        reversed.Reverse();
        return ResolveMemberPath(typeof(T), reversed);
    }

    internal static RelationQueryAuthoringSource Source(
        string reference,
        string? description = null) =>
        new(Producer, Guard.RequireNotNullOrWhiteSpace(reference), description);

    internal static string StableTypeName(Type type) =>
        type.FullName ?? type.Name;

    internal void RequireOwner(RelationQueryExpressionValueBinding binding)
    {
        if (!ReferenceEquals(binding.Owner, this))
            throw new ArgumentException("The CLR binding belongs to another expression-authoring session.", nameof(binding));
    }

    internal void RequireOwner(RelationQueryExpressionResult result)
    {
        if (!ReferenceEquals(result.Owner, this))
            throw new ArgumentException("The query result belongs to another expression-authoring session.", nameof(result));
    }

    internal void RequireInvocationDefinition(
        RelationQueryInvocationBuilder builder,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (!builtQueryFingerprints.Contains(builder.DefinitionFingerprint))
        {
            throw new ArgumentException(
                "The typed declaration belongs to an expression-authoring session that did not produce "
                + "the exact canonical query definition being invoked.",
                parameterName);
        }
    }

    internal static QualifiedShapeId RequireShape(RelationQueryExpressionValueBinding binding) =>
        binding.Shape ?? throw new ArgumentException(
            $"Binding '{binding.Id.Value}' has semantic type '{binding.Type}' but no root semantic shape.",
            nameof(binding));

    sealed record ClrPathRegistration(
        QualifiedShapeId Id,
        Func<IReadOnlyList<PropertyInfo>, FieldPath> Resolve);
}
