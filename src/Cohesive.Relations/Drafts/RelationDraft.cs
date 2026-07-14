using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Cohesive.Relations.IR;
using Cohesive.Relations.Model;

namespace Cohesive.Relations.Drafts;

/// <summary>
/// Portable, potentially incomplete semantic draft of a canonical relation definition.
/// </summary>
/// <remarks>
/// The draft carries canonical pre-projection query nodes and semantic projection alternatives.
/// Producer-specific scoring, evidence, review workflow, and user-interface state do not belong
/// to this model.
/// </remarks>
public sealed record RelationDraft
{
    /// <summary>Creates a portable relation draft.</summary>
    /// <param name="id">Stable identity of the draft across content revisions.</param>
    /// <param name="relationId">Identity of the canonical relation produced when the draft is accepted.</param>
    /// <param name="name">Human-readable name of the canonical relation.</param>
    /// <param name="input">Canonical logical query fragment evaluated before the draft projection.</param>
    /// <param name="rootBinding">Source binding whose values define rooted relation execution.</param>
    /// <param name="projection">Potentially incomplete terminal projection.</param>
    /// <param name="outputMode">Output cardinality relative to relation roots.</param>
    /// <param name="outputKey">Optional expression defining stable output identity.</param>
    /// <param name="invariants">Invariants applied to accepted relation outputs.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="relationId"/>, <paramref name="name"/>, <paramref name="input"/>, or
    /// <paramref name="projection"/> is <see langword="null"/>.
    /// </exception>
    [JsonConstructor]
    public RelationDraft(
        RelationDraftId id,
        RelationId relationId,
        RelationName name,
        LogicalQueryDefinition input,
        ValueBindingId rootBinding,
        RelationDraftProjection projection,
        RelationOutputMode outputMode = RelationOutputMode.OnePerRoot,
        Expr? outputKey = null,
        ImmutableArray<InvariantDefinition> invariants = default)
    {
        Id = id;
        RelationId = Guard.RequireNotNull(relationId);
        Name = Guard.RequireNotNull(name);
        Input = Guard.RequireNotNull(input);
        RootBinding = rootBinding;
        Projection = Guard.RequireNotNull(projection);
        OutputMode = outputMode;
        OutputKey = outputKey;
        Invariants = invariants.IsDefault
            ? []
            :
            [
                .. invariants
                    .OrderBy(static invariant => invariant?.Name ?? string.Empty, StringComparer.Ordinal)
            ];
    }

    /// <summary>Stable identity of this draft across content revisions.</summary>
    public RelationDraftId Id { get; init; }

    /// <summary>Identity of the canonical relation produced when this draft is accepted.</summary>
    public RelationId RelationId { get; init; }

    /// <summary>Human-readable name of the canonical relation.</summary>
    public RelationName Name { get; init; }

    /// <summary>Canonical logical query fragment evaluated before the draft projection.</summary>
    public LogicalQueryDefinition Input { get; init; }

    /// <summary>Source binding whose values define rooted relation execution.</summary>
    public ValueBindingId RootBinding { get; init; }

    /// <summary>Potentially incomplete terminal projection.</summary>
    public RelationDraftProjection Projection { get; init; }

    /// <summary>Output cardinality relative to relation roots.</summary>
    [JsonRequired]
    public RelationOutputMode OutputMode { get; init; }

    /// <summary>Optional expression defining stable output identity.</summary>
    public Expr? OutputKey { get; init; }

    /// <summary>Invariants applied to accepted relation outputs, ordered by ordinal name.</summary>
    public ImmutableArray<InvariantDefinition> Invariants { get; init; }
}

/// <summary>
/// Terminal projection of a relation draft, including unresolved semantic assignment slots.
/// </summary>
public sealed record RelationDraftProjection
{
    /// <summary>Creates a draft projection.</summary>
    /// <param name="id">Stable identifier used by the accepted projection node.</param>
    /// <param name="input">Logical query node whose binding environment is projected.</param>
    /// <param name="resultBinding">Binding introduced for each projected result.</param>
    /// <param name="resultShape">Semantic shape produced by the projection.</param>
    /// <param name="assignments">Target assignment slots in the draft.</param>
    [JsonConstructor]
    public RelationDraftProjection(
        QueryNodeId id,
        QueryNodeId input,
        ValueBindingId resultBinding,
        QualifiedShapeId resultShape,
        ImmutableArray<RelationDraftAssignmentSlot> assignments)
    {
        Id = id;
        Input = input;
        ResultBinding = resultBinding;
        ResultShape = resultShape;
        Assignments = assignments.IsDefault
            ? []
            :
            [
                .. assignments
                    .OrderBy(static assignment => assignment?.Id.Value ?? string.Empty, StringComparer.Ordinal)
                    .ThenBy(static assignment => assignment?.Target.ToString() ?? string.Empty, StringComparer.Ordinal)
            ];
    }

    /// <summary>Stable identifier used by the accepted projection node.</summary>
    public QueryNodeId Id { get; init; }

    /// <summary>Logical query node whose binding environment is projected.</summary>
    public QueryNodeId Input { get; init; }

    /// <summary>Binding introduced for each projected result.</summary>
    public ValueBindingId ResultBinding { get; init; }

    /// <summary>Semantic shape produced by the projection.</summary>
    public QualifiedShapeId ResultShape { get; init; }

    /// <summary>Assignment slots ordered by ordinal stable identifier and target path.</summary>
    [JsonRequired]
    public ImmutableArray<RelationDraftAssignmentSlot> Assignments { get; init; }
}

/// <summary>
/// One target field assignment whose semantic value may be selected, omitted, unresolved, or ambiguous.
/// </summary>
public sealed record RelationDraftAssignmentSlot
{
    /// <summary>Creates a relation draft assignment slot.</summary>
    /// <param name="id">Stable assignment identifier reused by the accepted projection assignment.</param>
    /// <param name="target">Target field path assigned by this slot.</param>
    /// <param name="candidates">Semantic value candidates available for selection.</param>
    /// <param name="resolution">Current semantic resolution of the assignment.</param>
    /// <exception cref="ArgumentNullException"><paramref name="resolution"/> is <see langword="null"/>.</exception>
    [JsonConstructor]
    public RelationDraftAssignmentSlot(
        QueryAssignmentId id,
        FieldPath target,
        ImmutableArray<RelationDraftCandidate> candidates,
        RelationDraftAssignmentResolution resolution)
    {
        Id = id;
        Target = target;
        Candidates = candidates.IsDefault ? []
            : [.. candidates.OrderBy(static candidate => candidate?.Id.Value ?? string.Empty, StringComparer.Ordinal)];
        Resolution = Guard.RequireNotNull(resolution);
    }

    /// <summary>Stable assignment identifier reused by the accepted projection assignment.</summary>
    public QueryAssignmentId Id { get; init; }

    /// <summary>Target field path assigned by this slot.</summary>
    public FieldPath Target { get; init; }

    /// <summary>Semantic value candidates ordered by ordinal stable identifier.</summary>
    [JsonRequired]
    public ImmutableArray<RelationDraftCandidate> Candidates { get; init; }

    /// <summary>Current semantic resolution of this assignment.</summary>
    public RelationDraftAssignmentResolution Resolution { get; init; }
}

/// <summary>
/// One portable semantic value candidate for a relation draft assignment.
/// </summary>
public sealed record RelationDraftCandidate
{
    /// <summary>Creates a relation draft candidate.</summary>
    /// <param name="id">Content-derived candidate identifier for the containing slot and expression.</param>
    /// <param name="value">Portable expression that computes the candidate value.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    [JsonConstructor]
    public RelationDraftCandidate(RelationDraftCandidateId id, Expr value)
    {
        Id = id;
        Value = Guard.RequireNotNull(value);
    }

    /// <summary>Content-derived candidate identifier for the containing slot and expression.</summary>
    public RelationDraftCandidateId Id { get; init; }

    /// <summary>Portable expression that computes the candidate value.</summary>
    public Expr Value { get; init; }
}

/// <summary>
/// Closed semantic resolution of a relation draft assignment slot.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = RelationDraftWireNames.ResolutionDiscriminator)]
[JsonDerivedType(typeof(SelectedRelationDraftAssignmentResolution), RelationDraftWireNames.SelectedResolution)]
[JsonDerivedType(typeof(OmittedRelationDraftAssignmentResolution), RelationDraftWireNames.OmittedResolution)]
[JsonDerivedType(typeof(UnresolvedRelationDraftAssignmentResolution), RelationDraftWireNames.UnresolvedResolution)]
[JsonDerivedType(typeof(AmbiguousRelationDraftAssignmentResolution), RelationDraftWireNames.AmbiguousResolution)]
public abstract record RelationDraftAssignmentResolution
{
    /// <summary>Creates a relation draft assignment resolution.</summary>
    private protected RelationDraftAssignmentResolution()
    {
    }
}

/// <summary>
/// Resolution selecting exactly one declared semantic candidate.
/// </summary>
public sealed record SelectedRelationDraftAssignmentResolution : RelationDraftAssignmentResolution
{
    /// <summary>Creates a selected assignment resolution.</summary>
    /// <param name="candidateId">Identifier of the selected candidate.</param>
    [JsonConstructor]
    public SelectedRelationDraftAssignmentResolution(RelationDraftCandidateId candidateId)
    {
        CandidateId = candidateId;
    }

    /// <summary>Identifier of the selected candidate.</summary>
    public RelationDraftCandidateId CandidateId { get; init; }
}

/// <summary>
/// Resolution intentionally omitting an optional target assignment.
/// </summary>
public sealed record OmittedRelationDraftAssignmentResolution : RelationDraftAssignmentResolution
{
    /// <summary>Shared stateless omitted resolution.</summary>
    public static OmittedRelationDraftAssignmentResolution Instance { get; } = new();

    /// <summary>Creates an omitted assignment resolution.</summary>
    public OmittedRelationDraftAssignmentResolution()
    {
    }
}

/// <summary>
/// Resolution indicating that no semantic selection or omission has been recorded.
/// </summary>
public sealed record UnresolvedRelationDraftAssignmentResolution : RelationDraftAssignmentResolution
{
    /// <summary>Creates an unresolved assignment resolution.</summary>
    /// <param name="reasons">Structured reasons that prevent semantic resolution.</param>
    [JsonConstructor]
    public UnresolvedRelationDraftAssignmentResolution(
        ImmutableArray<RelationDraftUnresolvedReason> reasons)
    {
        Reasons = reasons.IsDefault
            ? []
            : [.. reasons.OrderBy(static reason => (int)reason)];
    }

    /// <summary>Structured unresolved reasons ordered by stable enum value.</summary>
    [JsonRequired]
    public ImmutableArray<RelationDraftUnresolvedReason> Reasons { get; init; }
}

/// <summary>
/// Stable reason that an assignment slot cannot yet be selected or explicitly omitted.
/// </summary>
public enum RelationDraftUnresolvedReason
{
    /// <summary>No semantic source candidate was found.</summary>
    NoCandidate = 0,

    /// <summary>Candidate and target types are not directly compatible.</summary>
    IncompatibleType = 1,

    /// <summary>Candidate cardinality cannot safely flow to the target cardinality.</summary>
    UnsafeCardinality = 2,

    /// <summary>Candidate presence is weaker than the target requirement.</summary>
    UnsafePresence = 3,

    /// <summary>Candidate nullability is weaker than the target requirement.</summary>
    UnsafeNullability = 4,

    /// <summary>A portable conversion is required but has not been declared.</summary>
    ConversionRequired = 5,

    /// <summary>The candidate requires structural navigation unsupported by the current producer.</summary>
    UnsupportedStructure = 6,

    /// <summary>The candidate requires a transformation unsupported by the current semantic model.</summary>
    UnsupportedTransformation = 7,

    /// <summary>Multiple producer choices prevent a unique semantic resolution.</summary>
    MultipleCandidates = 8
}

/// <summary>
/// Resolution identifying multiple declared candidates that remain semantically ambiguous.
/// </summary>
public sealed record AmbiguousRelationDraftAssignmentResolution : RelationDraftAssignmentResolution
{
    /// <summary>Creates an ambiguous assignment resolution.</summary>
    /// <param name="candidateIds">Identifiers of candidates participating in the ambiguity.</param>
    [JsonConstructor]
    public AmbiguousRelationDraftAssignmentResolution(ImmutableArray<RelationDraftCandidateId> candidateIds)
    {
        CandidateIds = candidateIds.IsDefault
            ? []
            : [.. candidateIds.OrderBy(static candidateId => candidateId.Value, StringComparer.Ordinal)];
    }

    /// <summary>Candidate identifiers ordered ordinally while retaining duplicates for validation.</summary>
    public ImmutableArray<RelationDraftCandidateId> CandidateIds { get; init; }
}

/// <summary>Canonical wire names for the portable relation-draft contract.</summary>
public static class RelationDraftWireNames
{
    /// <summary>Polymorphic assignment-resolution discriminator property.</summary>
    public const string ResolutionDiscriminator = "$resolution";

    /// <summary>Discriminator value for a selected assignment candidate.</summary>
    public const string SelectedResolution = "selected";

    /// <summary>Discriminator value for an explicitly omitted optional assignment.</summary>
    public const string OmittedResolution = "omitted";

    /// <summary>Discriminator value for an unresolved assignment.</summary>
    public const string UnresolvedResolution = "unresolved";

    /// <summary>Discriminator value for an assignment with multiple viable candidates.</summary>
    public const string AmbiguousResolution = "ambiguous";
}
