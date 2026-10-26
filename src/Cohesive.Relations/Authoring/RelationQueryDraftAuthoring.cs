using System.Collections.Immutable;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Drafts;
using Cohesive.Relations.IR;
using Cohesive.Relations.Model;

namespace Cohesive.Relations.Authoring;

public sealed partial class RelationQueryAuthoringCore
{
    /// <summary>
    /// Builds a relation draft whose logical input is the branch authored in this core, and runs the draft validator.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A draft keeps projection alternatives open (candidate expressions and their resolutions) for review, and
    /// <see cref="RelationDraftAcceptor"/> later accepts it into a canonical relation definition. This terminal lets
    /// producers such as inference engines author the draft through the core instead of constructing its input graph
    /// directly: the core selects the input, the projection's identities, and each slot's and candidate's identity.
    /// </para>
    /// <para>
    /// Like the other terminals, it does not add nodes to the core. The projection node and result binding follow the
    /// same identity conventions as <c>Project</c>, must not collide with identities already declared here, and are
    /// recorded in <see cref="RelationDraftAuthoringResult.Provenance"/>. Slot and candidate identities follow
    /// <see cref="RelationDraftIdentityConvention"/>. Parameters referenced by candidates, the output key, or invariants
    /// are carried into the input.
    /// </para>
    /// </remarks>
    /// <typeparam name="TInput">Canonical type of the projection's input node.</typeparam>
    /// <param name="id">Stable identity of the draft across content revisions.</param>
    /// <param name="relationId">Identity of the canonical relation produced when the draft is accepted.</param>
    /// <param name="name">Human-readable name of the canonical relation.</param>
    /// <param name="root">Source binding whose values define rooted execution.</param>
    /// <param name="input">Logical node the draft projection reads from.</param>
    /// <param name="resultShape">Graph-qualified shape produced by the projection.</param>
    /// <param name="assignments">One input per projected target field, with its candidates and resolution.</param>
    /// <param name="projectionId">Optional explicit identity of the projection node.</param>
    /// <param name="resultBindingId">Optional explicit identity of the projection's result binding.</param>
    /// <param name="outputMode">Output cardinality relative to relation roots.</param>
    /// <param name="outputKey">Optional expression defining stable output identity.</param>
    /// <param name="invariants">Optional relation-output invariants.</param>
    /// <param name="source">Optional producer-source attribution for the draft terminal and its projection.</param>
    /// <param name="keySource">Optional producer-source attribution specific to the output-key expression.</param>
    /// <param name="invariantSources">
    /// Optional producer-source attribution for each invariant expression, positionally aligned with
    /// <paramref name="invariants"/>. A null entry falls back to <paramref name="source"/>.
    /// </param>
    /// <returns>The draft, the draft validator's result, and non-semantic authoring provenance.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="relationId"/> or <paramref name="name"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="root"/> or <paramref name="input"/> belongs to another core; <paramref name="assignments"/> or
    /// <paramref name="invariants"/> contains a <see langword="null"/> entry; <paramref name="invariantSources"/> does not
    /// contain one entry per invariant; or the projection node or result-binding identity is already declared here.
    /// </exception>
    public RelationDraftAuthoringResult BuildDraft<TInput>(
        RelationDraftId id,
        RelationId relationId,
        RelationName name,
        RelationQueryBindingHandle root,
        RelationQueryNodeHandle<TInput> input,
        QualifiedShapeId resultShape,
        ImmutableArray<RelationQueryDraftAssignment> assignments,
        QueryNodeId? projectionId = null,
        ValueBindingId? resultBindingId = null,
        RelationOutputMode outputMode = RelationOutputMode.OnePerRoot,
        Expr? outputKey = null,
        ImmutableArray<InvariantDefinition> invariants = default,
        RelationQueryAuthoringSource? source = null,
        RelationQueryAuthoringSource? keySource = null,
        ImmutableArray<RelationQueryAuthoringSource?> invariantSources = default)
        where TInput : LogicalQueryNode
    {
        ArgumentNullException.ThrowIfNull(relationId);
        ArgumentNullException.ThrowIfNull(name);
        RequireBinding(root, nameof(root));
        RequireNode(input, nameof(input));
        assignments = NormalizeEntries(assignments, nameof(assignments), "Draft assignments");
        invariants = NormalizeEntries(invariants, nameof(invariants), "Relation invariants");
        RequireInvariantSources(invariants, invariantSources);

        var projection = SelectNodeId(RelationQueryWireNames.ProjectNode, projectionId, source);
        EnsureAvailable(nodesById.Keys, projection.Id, nameof(projectionId), "logical node");
        var resultBinding = SelectBindingId(projection.Id, "result", resultBindingId, source);
        EnsureAvailable(bindingIds, resultBinding.Id, nameof(resultBindingId), "value binding");

        var slots = assignments.Select(assignment => assignment.ToSlot(resultShape)).ToImmutableArray();
        var selectedBody = CreateBody(
            [input.Id],
            TerminalExpressions(outputKey, invariants, assignments.SelectMany(static assignment => assignment.Candidates)));
        var draft = new RelationDraft(
            id,
            relationId,
            name,
            selectedBody.Body,
            root.Id,
            new RelationDraftProjection(projection.Id, input.Id, resultBinding.Id, resultShape, slots),
            outputMode,
            outputKey,
            invariants);

        var decisions = TerminalSourceDecisions(
            RelationDraftWireNames.Draft,
            id.Value,
            source,
            outputKey,
            keySource,
            invariants,
            invariantSources);
        if (source is not null)
        {
            decisions.Add(new(RelationQueryAuthoringDecisionKind.Node, projection.Id.Value, source));
            decisions.Add(new(RelationQueryAuthoringDecisionKind.Binding, resultBinding.Id.Value, source));
        }
        for (var index = 0; index < assignments.Length; index++)
        {
            if ((assignments[index].Source ?? source) is { } assignmentSource)
                decisions.Add(new(RelationQueryAuthoringDecisionKind.Assignment, slots[index].Id.Value, assignmentSource));
        }

        return new(
            draft,
            RelationDraftValidator.Validate(draft),
            CreateManifest(
                new HashSet<string>(StringComparer.Ordinal),
                selectedBody.ProvenanceTargets,
                [projection.Decision, resultBinding.Decision],
                decisions));
    }
}

/// <summary>
/// One projected target field of a relation draft: its candidate expressions and how the choice among them stands.
/// </summary>
/// <remarks>
/// Slot and candidate identities are not supplied here; <see cref="RelationQueryAuthoringCore.BuildDraft{TInput}"/>
/// derives them from the draft's result shape, the target, and each candidate expression, following
/// <see cref="RelationDraftIdentityConvention"/>. Create inputs with <see cref="Selected"/>, <see cref="Ambiguous"/>,
/// <see cref="Unresolved"/>, or <see cref="Omitted"/>.
/// </remarks>
public sealed record RelationQueryDraftAssignment
{
    enum ResolutionKind
    {
        Selected,
        Ambiguous,
        Unresolved,
        Omitted,
    }

    readonly ResolutionKind resolution;
    readonly ImmutableArray<RelationDraftUnresolvedReason> reasons;

    RelationQueryDraftAssignment(
        FieldPath target,
        ImmutableArray<Expr> candidates,
        ResolutionKind resolution,
        ImmutableArray<RelationDraftUnresolvedReason> reasons,
        RelationQueryAuthoringSource? source)
    {
        if (candidates.IsDefault)
            candidates = [];
        if (candidates.Any(static candidate => candidate is null))
            throw new ArgumentException("Draft candidates cannot contain null expressions.", nameof(candidates));
        RequireDistinct(candidates, target);
        Target = target;
        Candidates = candidates;
        this.resolution = resolution;
        this.reasons = reasons.IsDefault ? [] : reasons;
        Source = source;
    }

    /// <summary>Target field path in the draft's result shape.</summary>
    public FieldPath Target { get; }

    /// <summary>Candidate expressions; for a selected assignment the first is the selection.</summary>
    public ImmutableArray<Expr> Candidates { get; }

    /// <summary>Optional producer-source attribution for this assignment.</summary>
    public RelationQueryAuthoringSource? Source { get; }

    /// <summary>Creates an assignment whose candidate <paramref name="value"/> is selected.</summary>
    /// <param name="target">Target field path in the draft's result shape.</param>
    /// <param name="value">Selected candidate expression.</param>
    /// <param name="alternatives">Optional alternatives kept for review alongside the selection.</param>
    /// <param name="source">Optional producer-source attribution.</param>
    /// <returns>A selected draft assignment.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="alternatives"/> contains a <see langword="null"/> entry, repeats an expression, or repeats
    /// <paramref name="value"/>.
    /// </exception>
    public static RelationQueryDraftAssignment Selected(
        FieldPath target,
        Expr value,
        ImmutableArray<Expr> alternatives = default,
        RelationQueryAuthoringSource? source = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(target, [value, .. alternatives.IsDefault ? [] : alternatives], ResolutionKind.Selected, [], source);
    }

    /// <summary>Creates an assignment whose candidates remain undecided between several alternatives.</summary>
    /// <param name="target">Target field path in the draft's result shape.</param>
    /// <param name="candidates">Competing candidate expressions.</param>
    /// <param name="source">Optional producer-source attribution.</param>
    /// <returns>An ambiguous draft assignment.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="candidates"/> has fewer than two entries, contains a <see langword="null"/> entry, or repeats an
    /// expression.
    /// </exception>
    public static RelationQueryDraftAssignment Ambiguous(
        FieldPath target,
        ImmutableArray<Expr> candidates,
        RelationQueryAuthoringSource? source = null)
    {
        if (candidates.IsDefault || candidates.Length < 2)
        {
            throw new ArgumentException(
                $"An ambiguous assignment to '{target}' needs at least two distinct candidates.",
                nameof(candidates));
        }

        return new(target, candidates, ResolutionKind.Ambiguous, [], source);
    }

    /// <summary>Creates an assignment that has no acceptable candidate yet.</summary>
    /// <param name="target">Target field path in the draft's result shape.</param>
    /// <param name="reasons">Structured reasons the assignment is unresolved.</param>
    /// <param name="candidates">Optional rejected or partial candidates kept for review.</param>
    /// <param name="source">Optional producer-source attribution.</param>
    /// <returns>An unresolved draft assignment.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="reasons"/> is empty, contains an undefined value, or repeats a reason; or
    /// <paramref name="candidates"/> contains a <see langword="null"/> entry or repeats an expression.
    /// </exception>
    public static RelationQueryDraftAssignment Unresolved(
        FieldPath target,
        ImmutableArray<RelationDraftUnresolvedReason> reasons,
        ImmutableArray<Expr> candidates = default,
        RelationQueryAuthoringSource? source = null)
    {
        if (reasons.IsDefaultOrEmpty)
            throw new ArgumentException($"An unresolved assignment to '{target}' needs at least one reason.", nameof(reasons));
        if (reasons.Any(static reason => !Enum.IsDefined(reason)))
            throw new ArgumentException($"An unresolved assignment to '{target}' has an undefined reason.", nameof(reasons));
        if (reasons.Distinct().Count() != reasons.Length)
            throw new ArgumentException($"An unresolved assignment to '{target}' repeats a reason.", nameof(reasons));
        return new(target, candidates, ResolutionKind.Unresolved, reasons, source);
    }

    /// <summary>Creates an assignment that is deliberately left out of the accepted relation.</summary>
    /// <param name="target">Target field path in the draft's result shape.</param>
    /// <param name="candidates">Optional candidates kept for review.</param>
    /// <param name="source">Optional producer-source attribution.</param>
    /// <returns>An omitted draft assignment.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="candidates"/> contains a <see langword="null"/> entry or repeats an expression.
    /// </exception>
    public static RelationQueryDraftAssignment Omitted(
        FieldPath target,
        ImmutableArray<Expr> candidates = default,
        RelationQueryAuthoringSource? source = null) =>
        new(target, candidates, ResolutionKind.Omitted, [], source);

    // Two candidates are the same exactly when they would receive the same candidate identity, so duplicates are
    // rejected here rather than surfacing later as duplicate-identity diagnostics from the draft validator.
    static void RequireDistinct(ImmutableArray<Expr> candidates, FieldPath target)
    {
        HashSet<string> keys = new(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            if (!keys.Add(RelationDraftIdentityConvention.CanonicalExpressionKey(candidate)))
                throw new ArgumentException($"The assignment to '{target}' repeats a candidate expression.", "candidates");
        }
    }

    internal RelationDraftAssignmentSlot ToSlot(QualifiedShapeId resultShape)
    {
        var slotId = RelationDraftIdentityConvention.CreateAssignmentSlotId(resultShape, Target);
        var candidates = Candidates
            .Select(value => new RelationDraftCandidate(RelationDraftIdentityConvention.CreateCandidateId(slotId, value), value))
            .ToImmutableArray();
        RelationDraftAssignmentResolution slotResolution = resolution switch
        {
            ResolutionKind.Selected => new SelectedRelationDraftAssignmentResolution(candidates[0].Id),
            ResolutionKind.Ambiguous => new AmbiguousRelationDraftAssignmentResolution(
                [.. candidates.Select(static candidate => candidate.Id)]),
            ResolutionKind.Unresolved => new UnresolvedRelationDraftAssignmentResolution(reasons),
            ResolutionKind.Omitted => OmittedRelationDraftAssignmentResolution.Instance,
            _ => throw new InvalidOperationException($"Unsupported draft resolution '{resolution}'."),
        };
        return new(slotId, Target, candidates, slotResolution);
    }
}

/// <summary>
/// Relation draft, draft validation, and non-semantic authoring attribution produced by
/// <see cref="RelationQueryAuthoringCore.BuildDraft{TInput}"/>.
/// </summary>
public sealed class RelationDraftAuthoringResult
{
    internal RelationDraftAuthoringResult(
        RelationDraft draft,
        DocumentValidationResult validation,
        RelationQueryAuthoringManifest provenance)
    {
        Draft = draft;
        Validation = validation;
        Provenance = provenance;
    }

    /// <summary>Relation draft whose input, projection, slots, and candidates were authored through the core.</summary>
    public RelationDraft Draft { get; }

    /// <summary>Result from the authoritative draft validator.</summary>
    public DocumentValidationResult Validation { get; }

    /// <summary>Returns the draft, or throws when the draft validator reported diagnostics.</summary>
    /// <remarks>
    /// Use this unless the caller deliberately keeps invalid drafts, for example to show their diagnostics. Unresolved
    /// and ambiguous slots are valid draft state and do not cause a failure; acceptance decides whether they block.
    /// </remarks>
    /// <returns>The validated <see cref="Draft"/>.</returns>
    /// <exception cref="InvalidOperationException">
    /// <see cref="Validation"/> is not valid; the message lists each diagnostic code and message.
    /// </exception>
    public RelationDraft EnsureValid()
    {
        if (Validation.IsValid)
            return Draft;

        throw new InvalidOperationException(
            $"Relation draft '{Draft.Id.Value}' is invalid: "
            + string.Join("; ", Validation.Diagnostics.Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
    }

    /// <summary>
    /// Identity and producer-source attribution for the draft's input graph, its projection node and result binding,
    /// and any attributed terminal, key, invariant, and assignment decisions.
    /// </summary>
    public RelationQueryAuthoringManifest Provenance { get; }
}
