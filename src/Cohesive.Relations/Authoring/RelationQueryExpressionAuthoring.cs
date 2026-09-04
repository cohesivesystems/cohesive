using System.Collections.Immutable;
using System.Linq.Expressions;
using System.Reflection;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Diagnostics;
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
    readonly Dictionary<RelationshipId, RelationshipDefinition> relationships = [];
    readonly HashSet<RelationQueryDefinitionFingerprint> builtDefinitionFingerprints = [];
    readonly Dictionary<object, AuthoredEvaluationSnapshot> authoredEvaluationSnapshots =
        new(ReferenceEqualityComparer.Instance);

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

    /// <summary>Canonical relationships authored by this session in deterministic identity order.</summary>
    public RelationshipCatalog RelationshipCatalog => new(
        [
            .. relationships.Values
                .OrderBy(static relationship => relationship.Id.Value, StringComparer.Ordinal)
        ]);

    /// <summary>Creates a persisted snapshot of the relationships authored by this session.</summary>
    /// <param name="metadata">Optional document provenance and descriptive metadata.</param>
    /// <returns>A current-version relationship catalog document with a deterministic semantic fingerprint.</returns>
    /// <exception cref="ArgumentException">The authored catalog fails catalog-local semantic validation.</exception>
    /// <exception cref="InvalidOperationException">
    /// The catalog contains a value that has no canonical relationship-catalog JSON encoding.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// The catalog contains a runtime type that the canonical JSON serializer does not support.
    /// </exception>
    public RelationshipCatalogDocument CreateRelationshipCatalogDocument(
        RelationshipCatalogDocumentMetadata? metadata = null) =>
        RelationshipCatalogDocument.FromCatalog(RelationshipCatalog, metadata);

    /// <summary>
    /// Begins an evaluation using the exact definition and semantic context captured when this session built the
    /// supplied terminal result.
    /// </summary>
    /// <typeparam name="TDefinition">Canonical relation or query definition type.</typeparam>
    /// <param name="authored">Exact validated terminal result instance produced by this expression-authoring session.</param>
    /// <param name="evaluation">Caller-assigned runtime evaluation identity.</param>
    /// <param name="planReference">Optional exact compiled-plan attribution.</param>
    /// <returns>
    /// An evaluation builder carrying the shape snapshots and relationship catalog captured at terminal-build time.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="authored"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="authored"/> was not successfully produced by this authoring session or
    /// <paramref name="planReference"/> identifies another definition.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// An authored semantic snapshot cannot be represented by canonical serialization.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// An authored semantic snapshot contains a runtime type unsupported by canonical serialization.
    /// </exception>
    public RelationQueryEvaluationBuilder Evaluate<TDefinition>(
        RelationQueryAuthoringResult<TDefinition> authored,
        RelationQueryEvaluationId evaluation,
        RelationQueryCompiledPlanReference? planReference = null)
        where TDefinition : RelationQueryDefinition
    {
        ArgumentNullException.ThrowIfNull(authored);
        if (!authored.Validation.IsValid
            || !authoredEvaluationSnapshots.TryGetValue(authored, out var snapshot))
        {
            throw new ArgumentException(
                "The authored definition was not successfully produced by this expression-authoring session.",
                nameof(authored));
        }

        return new(
            authored.CreateDocument(),
            evaluation,
            snapshot.ShapeDocuments,
            RelationshipCatalogDocument.FromCatalog(snapshot.RelationshipCatalog),
            planReference);
    }

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
        var binding = new RelationQueryExpressionValueBinding<T>(
            this,
            source.Binding,
            shape.Type,
            shape.Id,
            shape.ResolveMemberPath,
            shape.ResolveType,
            shape.IdentityOrigin == RelationQueryClrIdentityOrigin.Imported);
        return new(source.Node, binding, relationRoot: binding);
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
    /// bound to an incompatible semantic shape in this session, or <paramref name="id"/> is already registered
    /// for different relationship semantics.
    /// </exception>
    public RelationQueryExpressionRelationship<TSource, TTarget> Relationship<TSource, TReference, TTarget>(
        Expression<Func<TSource, TReference>> sourceReference,
        RelationshipId? id = null,
        SourceReferenceUniqueness sourceReferenceUniqueness = SourceReferenceUniqueness.NotGuaranteed)
        where TSource : notnull
        where TTarget : notnull
    {
        var definition = CreateRelationshipDefinition<TSource, TReference, TTarget>(
            sourceReference,
            id,
            sourceReferenceUniqueness,
            out var sourceShape,
            out var targetShape);
        var available = RequireRelationshipAvailable(definition);
        TrackShape(sourceShape);
        TrackShape(targetShape);
        CommitRelationship(available);
        return new(available);
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
    /// bound to an incompatible semantic shape in this session, or <paramref name="id"/> is already registered
    /// for different relationship semantics.
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
        {
            return;
        }

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

    /// <summary>Traverses a typed relationship from the focused binding introduced by a bound node.</summary>
    /// <typeparam name="TInput">Canonical type of the input logical node.</typeparam>
    /// <typeparam name="TFrom">CLR type at the relationship source endpoint.</typeparam>
    /// <typeparam name="TRelated">CLR type at the relationship target endpoint.</typeparam>
    /// <param name="input">Logical input and focused source binding from which traversal starts.</param>
    /// <param name="relationship">Typed canonical relationship to traverse.</param>
    /// <param name="joinKind">Behavior when related values are absent.</param>
    /// <param name="requirement">Whether resolving the related value is required.</param>
    /// <param name="sourceReference">Optional stable producer reference for provenance.</param>
    /// <returns>Typed traversal-node and related-binding handles.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="input"/> or <paramref name="relationship"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// A handle belongs to another session or the focused binding is incompatible with the relationship.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="joinKind"/> or <paramref name="requirement"/> is unsupported.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// <typeparamref name="TRelated"/> cannot be mapped to the relationship target shape.
    /// </exception>
    public RelationQueryExpressionBoundNode<TraverseRelationshipQueryNode, TRelated> Traverse<TInput, TFrom, TRelated>(
        RelationQueryExpressionBoundNode<TInput, TFrom> input,
        RelationQueryExpressionRelationship<TFrom, TRelated> relationship,
        JoinKind joinKind = JoinKind.Left,
        QueryInputRequirement requirement = QueryInputRequirement.Required,
        string? sourceReference = null)
        where TInput : LogicalQueryNode
        where TFrom : notnull
        where TRelated : notnull
    {
        ArgumentNullException.ThrowIfNull(input);
        var traversed = Traverse(input.Node, input.Binding, relationship, joinKind, requirement, sourceReference);
        return new(traversed.Node, traversed.Binding, input.RelationRoot);
    }

    /// <summary>Traverses from an explicit earlier binding while retaining a bound input's logical branch.</summary>
    /// <typeparam name="TInput">Canonical type of the input logical node.</typeparam>
    /// <typeparam name="TFocus">CLR type of the input's focused binding.</typeparam>
    /// <typeparam name="TFrom">CLR type at the relationship source endpoint.</typeparam>
    /// <typeparam name="TRelated">CLR type at the relationship target endpoint.</typeparam>
    /// <param name="input">Logical input whose branch and relation-root context are retained.</param>
    /// <param name="from">Visible earlier binding from which traversal starts.</param>
    /// <param name="relationship">Typed canonical relationship to traverse.</param>
    /// <param name="joinKind">Behavior when related values are absent.</param>
    /// <param name="requirement">Whether resolving the related value is required.</param>
    /// <param name="sourceReference">Optional stable producer reference for provenance.</param>
    /// <returns>Typed traversal-node and related-binding handles retaining the input's relation-root context.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="input"/>, <paramref name="from"/>, or <paramref name="relationship"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// A handle belongs to another session, <paramref name="from"/> is not visible in <paramref name="input"/>, or
    /// a shape is incompatible.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="joinKind"/> or <paramref name="requirement"/> is unsupported.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// <typeparamref name="TRelated"/> cannot be mapped to the relationship target shape.
    /// </exception>
    public RelationQueryExpressionBoundNode<TraverseRelationshipQueryNode, TRelated> Traverse<
        TInput,
        TFocus,
        TFrom,
        TRelated>(
        RelationQueryExpressionBoundNode<TInput, TFocus> input,
        RelationQueryExpressionValueBinding<TFrom> from,
        RelationQueryExpressionRelationship<TFrom, TRelated> relationship,
        JoinKind joinKind = JoinKind.Left,
        QueryInputRequirement requirement = QueryInputRequirement.Required,
        string? sourceReference = null)
        where TInput : LogicalQueryNode
        where TFocus : notnull
        where TFrom : notnull
        where TRelated : notnull
    {
        ArgumentNullException.ThrowIfNull(input);
        var traversed = Traverse(input.Node, from, relationship, joinKind, requirement, sourceReference);
        return new(traversed.Node, traversed.Binding, input.RelationRoot);
    }

    /// <summary>Authors and traverses a conventional relationship declared by an inline CLR reference selector.</summary>
    /// <typeparam name="TFrom">CLR type at the relationship source endpoint.</typeparam>
    /// <typeparam name="TRelated">CLR type at the relationship target endpoint.</typeparam>
    /// <param name="input">Logical input and focused source binding from which traversal starts.</param>
    /// <param name="reference">Direct source property containing target observation identities.</param>
    /// <param name="joinKind">Behavior when related values are absent.</param>
    /// <param name="requirement">Whether resolving the related value is required.</param>
    /// <param name="sourceReferenceUniqueness">Global uniqueness guarantee for source reference values.</param>
    /// <param name="relationshipId">Optional explicit relationship identity overriding the semantic convention.</param>
    /// <param name="producerReference">Optional stable producer reference for traversal provenance.</param>
    /// <returns>Typed traversal-node and related-binding handles.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="input"/> or <paramref name="reference"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The input belongs to another session, its focused binding is incompatible with the relationship, or
    /// <paramref name="reference"/> is not one direct readable property rooted at its parameter, or
    /// <paramref name="producerReference"/> is empty or white space.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="joinKind"/>, <paramref name="requirement"/>, or
    /// <paramref name="sourceReferenceUniqueness"/> is unsupported.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// A CLR endpoint cannot be mapped by the configured shape context or <paramref name="relationshipId"/> is
    /// already registered for different relationship semantics.
    /// </exception>
    public RelationQueryExpressionBoundNode<TraverseRelationshipQueryNode, TRelated> Traverse<TFrom, TRelated>(
        RelationQueryExpressionBoundNode<TFrom> input,
        Expression<Func<TFrom, object?>> reference,
        JoinKind joinKind = JoinKind.Left,
        QueryInputRequirement requirement = QueryInputRequirement.Required,
        SourceReferenceUniqueness sourceReferenceUniqueness = SourceReferenceUniqueness.NotGuaranteed,
        RelationshipId? relationshipId = null,
        string? producerReference = null)
        where TFrom : notnull
        where TRelated : notnull
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(reference);
        RequireOwner(input.Binding);
        if (!Enum.IsDefined(joinKind) || joinKind is JoinKind.Right or JoinKind.Full)
        {
            throw new ArgumentOutOfRangeException(
                nameof(joinKind),
                joinKind,
                "A relationship traversal supports only inner or left join semantics.");
        }

        if (!Enum.IsDefined(requirement))
        {
            throw new ArgumentOutOfRangeException(
                nameof(requirement),
                requirement,
                "Unsupported relationship input requirement.");
        }

        if (producerReference is not null)
        {
            _ = Guard.RequireNotNullOrWhiteSpace(producerReference);
        }

        var definition = CreateRelationshipDefinition<TFrom, object?, TRelated>(
            reference,
            relationshipId,
            sourceReferenceUniqueness,
            out _,
            out _);
        var available = RequireRelationshipAvailable(definition);
        var relationship = new RelationQueryExpressionRelationship<TFrom, TRelated>(available);
        var traversed = Traverse(
            input.StructuralNode,
            input.Binding,
            relationship,
            joinKind,
            requirement,
            producerReference);
        CommitRelationship(available);
        return new(traversed.Node, traversed.Binding, input.RelationRoot);
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

    /// <summary>Traverses a typed relationship inversely from the focused binding introduced by a bound node.</summary>
    /// <typeparam name="TInput">Canonical type of the input logical node.</typeparam>
    /// <typeparam name="TSource">CLR type at the inverse traversal result.</typeparam>
    /// <typeparam name="TTarget">CLR type at the relationship target endpoint.</typeparam>
    /// <param name="input">Logical input and focused target binding from which inverse traversal starts.</param>
    /// <param name="relationship">Typed canonical relationship to traverse inversely.</param>
    /// <param name="joinKind">Behavior when related values are absent.</param>
    /// <param name="requirement">Whether resolving related source values is required.</param>
    /// <param name="sourceReference">Optional stable producer reference for provenance.</param>
    /// <returns>Typed traversal-node and source-binding handles.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="input"/> or <paramref name="relationship"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// A handle belongs to another session or the focused binding is incompatible with the relationship.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="joinKind"/> or <paramref name="requirement"/> is unsupported.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// <typeparamref name="TSource"/> cannot be mapped to the relationship source shape.
    /// </exception>
    public RelationQueryExpressionBoundNode<TraverseRelationshipQueryNode, TSource> TraverseInverse<TInput, TSource, TTarget>(
        RelationQueryExpressionBoundNode<TInput, TTarget> input,
        RelationQueryExpressionRelationship<TSource, TTarget> relationship,
        JoinKind joinKind = JoinKind.Left,
        QueryInputRequirement requirement = QueryInputRequirement.Required,
        string? sourceReference = null)
        where TInput : LogicalQueryNode
        where TSource : notnull
        where TTarget : notnull
    {
        ArgumentNullException.ThrowIfNull(input);
        var traversed = TraverseInverse(input.Node, input.Binding, relationship, joinKind, requirement, sourceReference);
        return new(traversed.Node, traversed.Binding, input.RelationRoot);
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
        {
            throw new ArgumentException("Query results cannot contain null entries.", nameof(results));
        }

        foreach (var result in materialized)
        {
            RequireOwner(result);
        }

        var reference = sourceReference ?? $"query/{id.Value}";
        var builtQuery = structural.BuildQuery(
            id,
            name,
            [.. materialized.Select(static result => result.Structural)],
            Source(reference, $"Expression-authored query '{name.Value}'."));
        return CaptureSuccessfulBuild(builtQuery);
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

    /// <summary>Builds a convention-identified relation from an output retaining one originating source root.</summary>
    /// <typeparam name="TOutputNode">Canonical type of the output logical node.</typeparam>
    /// <typeparam name="TOutput">CLR output type.</typeparam>
    /// <param name="output">Typed output retaining its originating source binding.</param>
    /// <param name="mode">Output cardinality relative to each root.</param>
    /// <param name="invariants">Optional already-canonical output invariants.</param>
    /// <param name="id">Optional explicit relation identity overriding the endpoint convention.</param>
    /// <param name="name">Optional explicit relation display name overriding the CLR output-type convention.</param>
    /// <param name="sourceReference">Optional stable producer reference for provenance.</param>
    /// <returns>The canonical relation, validation result, and authoring provenance.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="output"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// The output belongs to another authoring session, exposes more than one visible binding,
    /// <paramref name="invariants"/> contains a null entry, or an endpoint has no graph-qualified shape.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="mode"/> is unsupported.</exception>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="output"/> has no unambiguous originating source. Use the explicit overload accepting a root.
    /// </exception>
    public RelationQueryAuthoringResult<RelationDefinition> BuildRelation<TOutputNode, TOutput>(
        RelationQueryExpressionBoundNode<TOutputNode, TOutput> output,
        RelationOutputMode mode = RelationOutputMode.OnePerRoot,
        ImmutableArray<InvariantDefinition> invariants = default,
        RelationId? id = null,
        RelationName? name = null,
        string? sourceReference = null)
        where TOutputNode : LogicalQueryNode
        where TOutput : notnull
    {
        ArgumentNullException.ThrowIfNull(output);
        var root = RequireRetainedRelationRoot(output);
        var terminal = ResolveRelationTerminal(
            root,
            output.Binding,
            typeof(TOutput),
            mode,
            id,
            name,
            sourceReference);
        var result = BuildRelationCore(
            terminal.Id,
            terminal.Name,
            root,
            output.Node,
            output.Binding,
            mode,
            key: null,
            invariants: invariants,
            sourceReference: terminal.Reference);
        return CaptureSuccessfulBuild(
            WithConventionRelationRoot(WithRelationTerminalProvenance(result, terminal), root, terminal.Source));
    }

    /// <summary>Builds a convention-identified relation without an output key.</summary>
    /// <typeparam name="TRoot">CLR root type.</typeparam>
    /// <typeparam name="TOutputNode">Canonical type of the output logical node.</typeparam>
    /// <typeparam name="TOutput">CLR output type.</typeparam>
    /// <param name="root">Typed source node whose binding is the relation root.</param>
    /// <param name="output">Typed logical node and focused binding producing relation outputs.</param>
    /// <param name="mode">Output cardinality relative to each root.</param>
    /// <param name="invariants">Optional already-canonical output invariants.</param>
    /// <param name="id">Optional explicit relation identity overriding the endpoint convention.</param>
    /// <param name="name">Optional explicit relation display name overriding the CLR output-type convention.</param>
    /// <param name="sourceReference">Optional stable producer reference for provenance.</param>
    /// <returns>The canonical relation, validation result, and authoring provenance.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="root"/> or <paramref name="output"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// A handle belongs to another authoring session, the output exposes more than one visible binding,
    /// <paramref name="invariants"/> contains a null entry, or an endpoint has no graph-qualified shape.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="mode"/> is unsupported.</exception>
    public RelationQueryAuthoringResult<RelationDefinition> BuildRelation<TRoot, TOutputNode, TOutput>(
        RelationQueryExpressionBoundNode<SourceQueryNode, TRoot> root,
        RelationQueryExpressionBoundNode<TOutputNode, TOutput> output,
        RelationOutputMode mode = RelationOutputMode.OnePerRoot,
        ImmutableArray<InvariantDefinition> invariants = default,
        RelationId? id = null,
        RelationName? name = null,
        string? sourceReference = null)
        where TRoot : notnull
        where TOutputNode : LogicalQueryNode
        where TOutput : notnull
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(output);
        var terminal = ResolveRelationTerminal(
            root.Binding,
            output.Binding,
            typeof(TOutput),
            mode,
            id,
            name,
            sourceReference);
        var result = BuildRelationCore(
            terminal.Id,
            terminal.Name,
            root.Binding,
            output.Node,
            output.Binding,
            mode,
            key: null,
            invariants: invariants,
            sourceReference: terminal.Reference);
        return CaptureSuccessfulBuild(WithRelationTerminalProvenance(result, terminal));
    }

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
        return CaptureSuccessfulBuild(
            BuildRelationCore(
                id,
                name,
                root,
                output,
                outputBinding,
                mode,
                key,
                invariants,
                sourceReference));
    }

    RelationQueryAuthoringResult<RelationDefinition> BuildRelationCore<TOutputNode, TOutput>(
        RelationId id,
        RelationName name,
        RelationQueryExpressionValueBinding root,
        RelationQueryNodeHandle<TOutputNode> output,
        RelationQueryExpressionValueBinding<TOutput> outputBinding,
        RelationOutputMode mode,
        Expr? key,
        ImmutableArray<InvariantDefinition> invariants,
        string? sourceReference)
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

    internal RelationQueryAuthoringResult<TDefinition> CaptureSuccessfulBuild<TDefinition>(
        RelationQueryAuthoringResult<TDefinition> authored)
        where TDefinition : RelationQueryDefinition
    {
        if (!authored.Validation.IsValid)
            return authored;

        builtDefinitionFingerprints.Add(RelationQueryDefinitionFingerprinter.Compute(authored.Definition));
        authoredEvaluationSnapshots.Add(
            authored,
            new(
                ShapeDocuments,
                RelationshipCatalog));
        return authored;
    }

    ResolvedRelationTerminal ResolveRelationTerminal(
        RelationQueryExpressionValueBinding root,
        RelationQueryExpressionValueBinding output,
        Type outputType,
        RelationOutputMode mode,
        RelationId? id,
        RelationName? name,
        string? sourceReference)
    {
        RequireOwner(root);
        RequireOwner(output);
        var effectiveId = id ?? RelationQueryExpressionRelationConvention.CreateId(
            RequireShape(root),
            RequireShape(output),
            mode);
        var effectiveName = name ?? RelationQueryExpressionRelationConvention.CreateName(outputType);
        var reference = sourceReference ?? $"relation/{effectiveId.Value}";
        return new(
            effectiveId,
            effectiveName,
            reference,
            Source(reference, $"Expression-authored relation '{effectiveName.Value}'."),
            id is null
                ? RelationQueryAuthoringIdentityOrigin.Convention
                : RelationQueryAuthoringIdentityOrigin.Explicit,
            name is null
                ? RelationQueryAuthoringValueOrigin.Convention
                : RelationQueryAuthoringValueOrigin.Explicit,
            sourceReference is null
                ? RelationQueryAuthoringValueOrigin.Convention
                : RelationQueryAuthoringValueOrigin.Explicit);
    }

    static RelationQueryAuthoringResult<RelationDefinition> WithRelationTerminalProvenance(
        RelationQueryAuthoringResult<RelationDefinition> result,
        ResolvedRelationTerminal terminal) =>
        new(
            result.Definition,
            result.Validation,
            new RelationQueryAuthoringManifest(
                [
                    .. result.Provenance.Identities,
                    new RelationQueryAuthoringIdentityDecision(
                        RelationQueryAuthoringIdentityKind.Relation,
                        terminal.Id.Value,
                        terminal.IdOrigin,
                        terminal.IdOrigin == RelationQueryAuthoringIdentityOrigin.Convention
                            ? RelationQueryExpressionRelationConvention.Version
                            : null,
                        terminal.Source)
                ],
                result.Provenance.Sources,
                [
                    .. result.Provenance.Configuration,
                    new RelationQueryAuthoringConfigurationDecision(
                        terminal.Id.Value,
                        RelationQueryExpressionRelationConvention.NameSetting,
                        terminal.Name.Value,
                        terminal.NameOrigin,
                        terminal.NameOrigin == RelationQueryAuthoringValueOrigin.Convention
                            ? RelationQueryExpressionRelationConvention.Version
                            : null,
                        terminal.Source),
                    new RelationQueryAuthoringConfigurationDecision(
                        terminal.Id.Value,
                        RelationQueryExpressionRelationConvention.SourceReferenceSetting,
                        terminal.Reference,
                        terminal.SourceReferenceOrigin,
                        terminal.SourceReferenceOrigin == RelationQueryAuthoringValueOrigin.Convention
                            ? RelationQueryExpressionRelationConvention.Version
                            : null,
                        terminal.Source)
                ]));

    RelationQueryExpressionValueBinding RequireRetainedRelationRoot<TNode, TOutput>(
        RelationQueryExpressionBoundNode<TNode, TOutput> output)
        where TNode : LogicalQueryNode
        where TOutput : notnull
    {
        var root = output.RelationRoot
            ?? throw new InvalidOperationException(
                "The bound output does not retain one originating source root. Start from a typed Source and use "
                + "the bound-node Traverse and Project overloads, or pass the intended root to BuildRelation explicitly.");
        RequireOwner(root);
        return root;
    }

    static RelationQueryAuthoringResult<RelationDefinition> WithConventionRelationRoot(
        RelationQueryAuthoringResult<RelationDefinition> result,
        RelationQueryExpressionValueBinding root,
        RelationQueryAuthoringSource source) =>
        new(
            result.Definition,
            result.Validation,
            new RelationQueryAuthoringManifest(
                result.Provenance.Identities,
                result.Provenance.Sources,
                [
                    .. result.Provenance.Configuration,
                    new RelationQueryAuthoringConfigurationDecision(
                        result.Definition.Id.Value,
                        RelationQueryExpressionRelationConvention.RootBindingSetting,
                        root.Id.Value,
                        RelationQueryAuthoringValueOrigin.Convention,
                        RelationQueryExpressionRelationConvention.Version,
                        source)
                ]));

    void RequireSingleVisibleBinding<TNode>(
        RelationQueryNodeHandle<TNode> node,
        string parameterName)
        where TNode : LogicalQueryNode
    {
        if (structural.GetVisibleBindingCount(node) == 1)
        {
            return;
        }

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

    internal FieldPath ResolveSelectorPath<T, TValue>(Expression<Func<T, TValue>> selector) =>
        FieldPath.Capture(selector, ResolveMemberPath);

    internal static RelationQueryAuthoringSource Source(
        string reference,
        string? description = null) =>
        new(Producer, Guard.RequireNotNullOrWhiteSpace(reference), description);

    RelationshipDefinition CreateRelationshipDefinition<TSource, TReference, TTarget>(
        Expression<Func<TSource, TReference>> sourceReference,
        RelationshipId? id,
        SourceReferenceUniqueness sourceReferenceUniqueness,
        out RelationQueryClrShape<TSource> sourceShape,
        out RelationQueryClrShape<TTarget> targetShape)
        where TSource : notnull
        where TTarget : notnull
    {
        ArgumentNullException.ThrowIfNull(sourceReference);
        sourceShape = clr.Shape<TSource>();
        targetShape = clr.Shape<TTarget>();
        var path = ResolveSelectorPath(sourceReference);
        if (path.Segments.Length != 1)
        {
            throw new ArgumentException(
                "A relationship source reference must identify exactly one top-level CLR property.",
                nameof(sourceReference));
        }

        return global::Cohesive.Relations.Authoring.Relationship
            .From(sourceShape.Id)
            .Reference(path)
            .To(targetShape.Id, id, sourceReferenceUniqueness);
    }

    RelationshipDefinition RequireRelationshipAvailable(RelationshipDefinition definition)
    {
        if (!relationships.TryGetValue(definition.Id, out var registered))
        {
            return definition;
        }

        return registered == definition
            ? registered
            : throw new InvalidOperationException(
                $"Relationship id '{definition.Id.Value}' is already registered for different relationship semantics.");
    }

    void CommitRelationship(RelationshipDefinition definition) =>
        relationships.TryAdd(definition.Id, definition);

    internal static string StableTypeName(Type type) =>
        type.FullName ?? type.Name;

    internal void RequireOwner(RelationQueryExpressionValueBinding binding)
    {
        if (!ReferenceEquals(binding.Owner, this))
        {
            throw new ArgumentException("The CLR binding belongs to another expression-authoring session.", nameof(binding));
        }
    }

    internal void RequireOwner(RelationQueryExpressionResult result)
    {
        if (!ReferenceEquals(result.Owner, this))
        {
            throw new ArgumentException("The query result belongs to another expression-authoring session.", nameof(result));
        }
    }

    internal void RequireEvaluationDefinition(
        RelationQueryEvaluationBuilder builder,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (!builtDefinitionFingerprints.Contains(builder.DefinitionFingerprint))
        {
            throw new ArgumentException(
                "The typed declaration belongs to an expression-authoring session that did not produce "
                + "the exact canonical query definition being evaluated.",
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

    sealed record AuthoredEvaluationSnapshot(
        ImmutableArray<ShapeGraphDocument> ShapeDocuments,
        RelationshipCatalog RelationshipCatalog);

    sealed record ResolvedRelationTerminal(
        RelationId Id,
        RelationName Name,
        string Reference,
        RelationQueryAuthoringSource Source,
        RelationQueryAuthoringIdentityOrigin IdOrigin,
        RelationQueryAuthoringValueOrigin NameOrigin,
        RelationQueryAuthoringValueOrigin SourceReferenceOrigin);
}
