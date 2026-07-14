using System.Collections.Immutable;
using Cohesive.Model.Serialization;
using Cohesive.Relations.IR;
using Cohesive.Relations.Model;

namespace Cohesive.Relations.Drafts;

/// <summary>Explicit top-level target-to-source alias used by the direct-field convention matcher.</summary>
public sealed record RelationDraftFieldAlias
{
    /// <summary>Creates an explicit field alias.</summary>
    /// <param name="target">Top-level target field path.</param>
    /// <param name="source">Top-level source field path.</param>
    /// <exception cref="ArgumentException"><paramref name="target"/> or <paramref name="source"/> is empty.</exception>
    public RelationDraftFieldAlias(FieldPath target, FieldPath source)
    {
        if (target.Segments.IsDefaultOrEmpty)
            throw new ArgumentException("A target field path is required.", nameof(target));
        if (source.Segments.IsDefaultOrEmpty)
            throw new ArgumentException("A source field path is required.", nameof(source));

        Target = target;
        Source = source;
    }

    /// <summary>Top-level target field path.</summary>
    public FieldPath Target { get; init; }

    /// <summary>Top-level source field path.</summary>
    public FieldPath Source { get; init; }
}

/// <summary>Inputs used to conventionally produce a direct-field relation draft.</summary>
public sealed record DirectFieldRelationDraftConventionRequest
{
    /// <summary>Creates a direct-field convention request.</summary>
    /// <param name="id">Lifecycle identity assigned to the produced draft.</param>
    /// <param name="relationId">Stable identity of the relation that acceptance will produce.</param>
    /// <param name="name">Human-readable relation name.</param>
    /// <param name="source">Canonical source node and root binding.</param>
    /// <param name="projectionId">Stable identifier for the canonical projection node.</param>
    /// <param name="resultBinding">Binding introduced by the projection.</param>
    /// <param name="targetShape">Graph-qualified target shape.</param>
    /// <param name="aliases">Explicit target-to-source aliases, if any.</param>
    /// <param name="outputMode">Output cardinality relative to each root value.</param>
    /// <param name="outputKey">Optional stable output-key expression.</param>
    /// <param name="invariants">Invariants copied to the accepted relation.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="relationId"/>, <paramref name="name"/>, or <paramref name="source"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="outputMode"/> is unsupported.</exception>
    public DirectFieldRelationDraftConventionRequest(
        RelationDraftId id,
        RelationId relationId,
        RelationName name,
        SourceQueryNode source,
        QueryNodeId projectionId,
        ValueBindingId resultBinding,
        QualifiedShapeId targetShape,
        ImmutableArray<RelationDraftFieldAlias> aliases = default,
        RelationOutputMode outputMode = RelationOutputMode.OnePerRoot,
        Expr? outputKey = null,
        ImmutableArray<InvariantDefinition> invariants = default)
    {
        Id = id;
        RelationId = Guard.RequireNotNull(relationId);
        Name = Guard.RequireNotNull(name);
        Source = Guard.RequireNotNull(source);
        ProjectionId = projectionId;
        ResultBinding = resultBinding;
        TargetShape = targetShape;
        Aliases = aliases.IsDefault
            ? []
            :
            [
                .. aliases
                    .OrderBy(static alias => alias?.Target.ToString() ?? string.Empty, StringComparer.Ordinal)
                    .ThenBy(static alias => alias?.Source.ToString() ?? string.Empty, StringComparer.Ordinal)
            ];
        OutputMode = outputMode;
        OutputKey = outputKey;
        Invariants = invariants.IsDefault ? [] : invariants;

        if (!Enum.IsDefined(outputMode))
            throw new ArgumentOutOfRangeException(nameof(outputMode), outputMode, "Unsupported relation output mode.");
    }

    /// <summary>Lifecycle identity assigned to the produced draft.</summary>
    public RelationDraftId Id { get; init; }

    /// <summary>Stable identity of the relation that acceptance will produce.</summary>
    public RelationId RelationId { get; init; }

    /// <summary>Human-readable relation name.</summary>
    public RelationName Name { get; init; }

    /// <summary>Canonical source node and root binding.</summary>
    public SourceQueryNode Source { get; init; }

    /// <summary>Stable identifier for the canonical projection node.</summary>
    public QueryNodeId ProjectionId { get; init; }

    /// <summary>Binding introduced by the projection.</summary>
    public ValueBindingId ResultBinding { get; init; }

    /// <summary>Graph-qualified target shape.</summary>
    public QualifiedShapeId TargetShape { get; init; }

    /// <summary>Explicit aliases evaluated before name conventions.</summary>
    public ImmutableArray<RelationDraftFieldAlias> Aliases { get; init; }

    /// <summary>Output cardinality relative to relation roots.</summary>
    public RelationOutputMode OutputMode { get; init; }

    /// <summary>Optional stable output-key expression.</summary>
    public Expr? OutputKey { get; init; }

    /// <summary>Invariants copied to the accepted relation.</summary>
    public ImmutableArray<InvariantDefinition> Invariants { get; init; }
}

/// <summary>Explainable convention decision associated with a draft assignment slot or candidate.</summary>
public sealed record RelationDraftConventionDecision
{
    /// <summary>Creates an attributable convention decision.</summary>
    /// <param name="ruleId">Stable versioned rule identifier.</param>
    /// <param name="slotId">Target assignment slot affected by the decision.</param>
    /// <param name="candidateId">Candidate produced by the rule, if any.</param>
    /// <param name="sourceBinding">Source binding inspected by the rule.</param>
    /// <param name="source">Source field path selected or considered by the rule, if any.</param>
    /// <param name="target">Target field path considered by the rule.</param>
    /// <exception cref="ArgumentException"><paramref name="ruleId"/> is empty or consists only of white-space.</exception>
    public RelationDraftConventionDecision(
        string ruleId,
        QueryAssignmentId slotId,
        RelationDraftCandidateId? candidateId,
        ValueBindingId sourceBinding,
        FieldPath? source,
        FieldPath target)
    {
        RuleId = Guard.RequireNotNullOrWhiteSpace(ruleId);
        SlotId = slotId;
        CandidateId = candidateId;
        SourceBinding = sourceBinding;
        Source = source;
        Target = target;
    }

    /// <summary>Stable versioned convention rule identifier.</summary>
    public string RuleId { get; init; }

    /// <summary>Target assignment slot affected by the decision.</summary>
    public QueryAssignmentId SlotId { get; init; }

    /// <summary>Candidate produced by the rule, if any.</summary>
    public RelationDraftCandidateId? CandidateId { get; init; }

    /// <summary>Source binding inspected by the rule.</summary>
    public ValueBindingId SourceBinding { get; init; }

    /// <summary>Source field path selected or considered by the rule, if any.</summary>
    public FieldPath? Source { get; init; }

    /// <summary>Target field path considered by the rule.</summary>
    public FieldPath Target { get; init; }
}

/// <summary>Result of deterministic direct-field convention matching.</summary>
public sealed class RelationDraftConventionMatchResult
{
    /// <summary>Creates a convention-matching result.</summary>
    /// <param name="draft">Produced draft, or <see langword="null"/> when input shapes could not be resolved.</param>
    /// <param name="validation">Structured matching diagnostics.</param>
    /// <param name="decisions">Deterministically ordered convention decisions.</param>
    /// <exception cref="ArgumentNullException"><paramref name="validation"/> is <see langword="null"/>.</exception>
    internal RelationDraftConventionMatchResult(
        RelationDraft? draft,
        DocumentValidationResult validation,
        ImmutableArray<RelationDraftConventionDecision> decisions)
    {
        Draft = draft;
        Validation = Guard.RequireNotNull(validation);
        Decisions = decisions.IsDefault ? [] : decisions;
    }

    /// <summary>Produced draft, including explicit unresolved or ambiguous slots.</summary>
    public RelationDraft? Draft { get; }

    /// <summary>Structured matching diagnostics.</summary>
    public DocumentValidationResult Validation { get; }

    /// <summary>Structured matching diagnostics.</summary>
    public IReadOnlyList<DocumentValidationDiagnostic> Diagnostics => Validation.Diagnostics;

    /// <summary>Deterministically ordered explainability records excluded from draft semantic identity.</summary>
    public ImmutableArray<RelationDraftConventionDecision> Decisions { get; }

    /// <summary>Whether a draft was produced without definition holes.</summary>
    public bool IsComplete => Draft is not null && Validation.IsValid;
}

/// <summary>
/// Deterministic convention producer for direct top-level DTO field mappings.
/// </summary>
public static class DirectFieldRelationDraftConventionMatcher
{
    /// <summary>Rule used when an explicit target-to-source alias is present.</summary>
    public const string ExplicitAliasRuleId = "relation-draft.convention.explicit-alias/v1";

    /// <summary>Rule used for an exact ordinal field-name match.</summary>
    public const string ExactOrdinalRuleId = "relation-draft.convention.exact-ordinal/v1";

    /// <summary>Rule used for an ordinal-ignore-case field-name match.</summary>
    public const string OrdinalIgnoreCaseRuleId = "relation-draft.convention.ordinal-ignore-case/v1";

    /// <summary>Rule recorded when no field-name candidate exists.</summary>
    public const string NoCandidateRuleId = "relation-draft.convention.no-candidate/v1";

    /// <summary>Produces an explainable relation draft from two resolved shapes.</summary>
    /// <param name="request">Semantic relation identity, source, target, and explicit alias policy.</param>
    /// <param name="shapeGraphs">Exact shape-graph snapshots containing the source and target shapes.</param>
    /// <returns>A draft and structured diagnostics; ordinary ambiguity or incompatibility does not throw.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="request"/> or <paramref name="shapeGraphs"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// A convention-derived candidate expression has no canonical relation/query JSON encoding.
    /// </exception>
    /// <exception cref="System.Text.Json.JsonException">
    /// A convention-derived candidate expression cannot be serialized using the canonical wire contract.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// A convention-derived candidate expression contains a runtime type unsupported by canonical serialization.
    /// </exception>
    public static RelationDraftConventionMatchResult Match(
        DirectFieldRelationDraftConventionRequest request,
        IEnumerable<ShapeGraph> shapeGraphs)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(shapeGraphs);

        List<DocumentValidationDiagnostic> diagnostics = [];
        var graphs = IndexGraphs(shapeGraphs, diagnostics);
        var sourceShape = ResolveShape(request.Source.Shape, graphs, "source", diagnostics);
        var targetShape = ResolveShape(request.TargetShape, graphs, "target", diagnostics);
        if (sourceShape is null || targetShape is null)
        {
            return new(
                draft: null,
                DocumentValidationResult.FromDiagnostics(diagnostics),
                decisions: []);
        }

        var aliases = IndexAliases(request.Aliases, targetShape, diagnostics);
        List<RelationDraftAssignmentSlot> slots = [];
        List<RelationDraftConventionDecision> decisions = [];

        foreach (var targetField in targetShape.Fields
                     .Where(static field => field.Role != FieldRole.Computed)
                     .OrderBy(static field => field.Name.Value, StringComparer.Ordinal))
        {
            var targetPath = FieldPath.FromField(targetField.Name.Value);
            var slotId = RelationDraftIdentityConvention.CreateAssignmentSlotId(request.TargetShape, targetPath);

            if (aliases.ConflictedTargets.Contains(targetPath))
            {
                slots.Add(new(
                    slotId,
                    targetPath,
                    candidates: [],
                    new UnresolvedRelationDraftAssignmentResolution(
                        [RelationDraftUnresolvedReason.MultipleCandidates])));
                continue;
            }

            if (aliases.ByTarget.TryGetValue(targetPath, out var alias))
            {
                slots.Add(MatchAlias(
                    request,
                    sourceShape,
                    targetField,
                    targetPath,
                    slotId,
                    alias,
                    diagnostics,
                    decisions));
                continue;
            }

            if (sourceShape.TryGetField(targetField.Name.Value, out var exact))
            {
                slots.Add(MatchCandidates(
                    request,
                    targetField,
                    targetPath,
                    slotId,
                    [(exact, ExactOrdinalRuleId)],
                    diagnostics,
                    decisions));
                continue;
            }

            var caseInsensitive = sourceShape.Fields
                .Where(field => string.Equals(
                    field.Name.Value,
                    targetField.Name.Value,
                    StringComparison.OrdinalIgnoreCase))
                .OrderBy(static field => field.Name.Value, StringComparer.Ordinal)
                .Select(static field => (field, OrdinalIgnoreCaseRuleId))
                .ToImmutableArray();
            if (!caseInsensitive.IsDefaultOrEmpty)
            {
                slots.Add(MatchCandidates(
                    request,
                    targetField,
                    targetPath,
                    slotId,
                    caseInsensitive,
                    diagnostics,
                    decisions));
                continue;
            }

            Add(
                diagnostics,
                "relationDraft.convention.noCandidate",
                DiagnosticSeverity.Error,
                $"Target field '{targetField.Name.Value}' has no direct source-field candidate.",
                SlotLocation(slotId));
            decisions.Add(new(
                NoCandidateRuleId,
                slotId,
                candidateId: null,
                request.Source.Binding,
                source: null,
                targetPath));
            slots.Add(new(
                slotId,
                targetPath,
                candidates: [],
                new UnresolvedRelationDraftAssignmentResolution(
                    [RelationDraftUnresolvedReason.NoCandidate])));
        }

        var draft = new RelationDraft(
            request.Id,
            request.RelationId,
            request.Name,
            new LogicalQueryDefinition([request.Source]),
            request.Source.Binding,
            new RelationDraftProjection(
                request.ProjectionId,
                request.Source.Id,
                request.ResultBinding,
                request.TargetShape,
                [.. slots.OrderBy(static slot => slot.Id.Value, StringComparer.Ordinal)]),
            request.OutputMode,
            request.OutputKey,
            request.Invariants);

        diagnostics.AddRange(RelationDraftValidator.Validate(draft).Diagnostics);

        return new(
            draft,
            DocumentValidationResult.FromDiagnostics(diagnostics),
            [
                .. decisions
                    .OrderBy(static decision => decision.SlotId.Value, StringComparer.Ordinal)
                    .ThenBy(static decision => decision.RuleId, StringComparer.Ordinal)
                    .ThenBy(static decision => decision.Source?.ToString(), StringComparer.Ordinal)
            ]);
    }

    static RelationDraftAssignmentSlot MatchAlias(
        DirectFieldRelationDraftConventionRequest request,
        Shape sourceShape,
        FieldDefinition targetField,
        FieldPath targetPath,
        QueryAssignmentId slotId,
        RelationDraftFieldAlias alias,
        List<DocumentValidationDiagnostic> diagnostics,
        List<RelationDraftConventionDecision> decisions)
    {
        if (!TryGetTopLevelField(alias.Source, out var sourceName))
        {
            decisions.Add(new(
                ExplicitAliasRuleId,
                slotId,
                candidateId: null,
                request.Source.Binding,
                alias.Source,
                targetPath));
            return new(
                slotId,
                targetPath,
                candidates: [],
                new UnresolvedRelationDraftAssignmentResolution(
                    [RelationDraftUnresolvedReason.UnsupportedStructure]));
        }

        if (!sourceShape.TryGetField(sourceName, out var sourceField))
        {
            Add(
                diagnostics,
                "relationDraft.convention.aliasSourceUnknown",
                DiagnosticSeverity.Error,
                $"Explicit alias for target '{targetField.Name.Value}' references unknown top-level source field '{alias.Source}'.",
                SlotLocation(slotId));
            decisions.Add(new(
                ExplicitAliasRuleId,
                slotId,
                candidateId: null,
                request.Source.Binding,
                alias.Source,
                targetPath));
            return new(
                slotId,
                targetPath,
                candidates: [],
                new UnresolvedRelationDraftAssignmentResolution(
                    [RelationDraftUnresolvedReason.NoCandidate]));
        }

        return MatchCandidates(
            request,
            targetField,
            targetPath,
            slotId,
            [(sourceField, ExplicitAliasRuleId)],
            diagnostics,
            decisions);
    }

    static RelationDraftAssignmentSlot MatchCandidates(
        DirectFieldRelationDraftConventionRequest request,
        FieldDefinition targetField,
        FieldPath targetPath,
        QueryAssignmentId slotId,
        IEnumerable<(FieldDefinition Field, string RuleId)> matches,
        List<DocumentValidationDiagnostic> diagnostics,
        List<RelationDraftConventionDecision> decisions)
    {
        List<MatchedCandidate> candidates = [];
        foreach (var (sourceField, ruleId) in matches)
        {
            var sourcePath = FieldPath.FromField(sourceField.Name.Value);
            var value = Expr.Field(request.Source.Binding, sourcePath);
            var candidateId = RelationDraftIdentityConvention.CreateCandidateId(slotId, value);
            var issues = DirectFieldAssignmentCompatibility.Evaluate(
                sourceField,
                request.Source.Shape.GraphId,
                targetField,
                request.TargetShape.GraphId);
            candidates.Add(new(
                new RelationDraftCandidate(candidateId, value),
                sourcePath,
                ruleId,
                issues));
            decisions.Add(new(
                ruleId,
                slotId,
                candidateId,
                request.Source.Binding,
                sourcePath,
                targetPath));
        }

        var viable = candidates.Where(static candidate => candidate.Issues.IsDefaultOrEmpty).ToArray();
        RelationDraftAssignmentResolution resolution;
        if (viable.Length == 1)
        {
            resolution = new SelectedRelationDraftAssignmentResolution(viable[0].Candidate.Id);
        }
        else if (viable.Length > 1)
        {
            resolution = new AmbiguousRelationDraftAssignmentResolution(
                [
                    .. viable
                        .Select(static candidate => candidate.Candidate.Id)
                        .OrderBy(static id => id.Value, StringComparer.Ordinal)
                ]);
            Add(
                diagnostics,
                "relationDraft.convention.multipleCandidates",
                DiagnosticSeverity.Error,
                $"Target field '{targetField.Name.Value}' has multiple equally preferred compatible candidates: {string.Join(", ", viable.Select(static candidate => candidate.Source.ToString()))}.",
                SlotLocation(slotId));
        }
        else
        {
            resolution = new UnresolvedRelationDraftAssignmentResolution(
            [
                .. candidates
                    .SelectMany(static candidate => candidate.Issues)
                    .Select(static issue => issue.Reason)
                    .Distinct()
            ]);
        }

        foreach (var candidate in candidates)
        {
            foreach (var issue in candidate.Issues)
            {
                Add(
                    diagnostics,
                    issue.Code,
                    viable.Length > 0 ? DiagnosticSeverity.Warning : DiagnosticSeverity.Error,
                    $"Source field '{candidate.Source}' cannot safely populate target field '{targetPath}': {issue.Message}",
                    $"{SlotLocation(slotId)}/candidates/{candidate.Candidate.Id.Value}");
            }
        }

        return new(
            slotId,
            targetPath,
            [
                .. candidates
                    .Select(static candidate => candidate.Candidate)
                    .OrderBy(static candidate => candidate.Id.Value, StringComparer.Ordinal)
            ],
            resolution);
    }

    static Dictionary<GraphId, ShapeGraph> IndexGraphs(
        IEnumerable<ShapeGraph> shapeGraphs,
        List<DocumentValidationDiagnostic> diagnostics)
    {
        Dictionary<GraphId, ShapeGraph> graphs = [];
        var index = 0;
        foreach (var graph in shapeGraphs)
        {
            if (graph is null)
            {
                Add(
                    diagnostics,
                    "relationDraft.shapeGraph.missing",
                    DiagnosticSeverity.Error,
                    "A supplied shape-graph snapshot cannot be null.",
                    $"/shapeGraphs/{index}");
            }
            else if (!graphs.TryAdd(graph.Id, graph))
            {
                Add(
                    diagnostics,
                    "relationDraft.shapeGraph.duplicateId",
                    DiagnosticSeverity.Error,
                    $"Multiple supplied shape graphs have id '{graph.Id.Value}'.",
                    $"/shapeGraphs/{index}/id");
            }
            else
            {
                foreach (var graphDiagnostic in graph.Diagnostics.Where(static diagnostic =>
                             diagnostic.Severity == DiagnosticSeverity.Error))
                {
                    Add(
                        diagnostics,
                        "relationDraft.shapeGraph.invalid",
                        DiagnosticSeverity.Error,
                        $"Shape graph '{graph.Id.Value}' is invalid ({graphDiagnostic.Id.Value}): {graphDiagnostic.Message}",
                        $"/shapeGraphs/{index}");
                }
            }
            index++;
        }
        return graphs;
    }

    static Shape? ResolveShape(
        QualifiedShapeId id,
        IReadOnlyDictionary<GraphId, ShapeGraph> graphs,
        string role,
        List<DocumentValidationDiagnostic> diagnostics)
    {
        if (!graphs.TryGetValue(id.GraphId, out var graph)
            || !graph.TryGetShape(id.ShapeId, out var shape))
        {
            Add(
                diagnostics,
                "relationDraft.shapeGraph.shapeUnknown",
                DiagnosticSeverity.Error,
                $"The {role} shape '{id}' is not present in the supplied shape-graph snapshots.",
                role == "source" ? "/request/source/shape" : "/request/targetShape");
            return null;
        }
        return shape;
    }

    static AliasIndex IndexAliases(
        ImmutableArray<RelationDraftFieldAlias> aliases,
        Shape targetShape,
        List<DocumentValidationDiagnostic> diagnostics)
    {
        Dictionary<FieldPath, RelationDraftFieldAlias> byTarget = [];
        HashSet<FieldPath> conflicts = [];
        for (var index = 0; index < aliases.Length; index++)
        {
            var alias = aliases[index];
            if (alias is null)
            {
                Add(
                    diagnostics,
                    "relationDraft.convention.aliasMissing",
                    DiagnosticSeverity.Error,
                    "An explicit field alias cannot be null.",
                    $"/request/aliases/{index}");
                continue;
            }

            if (!TryGetTopLevelField(alias.Target, out var targetName)
                || !targetShape.TryGetField(targetName, out var targetField))
            {
                Add(
                    diagnostics,
                    "relationDraft.convention.aliasTargetUnknown",
                    DiagnosticSeverity.Error,
                    $"Explicit alias target '{alias.Target}' is not a top-level target field.",
                    $"/request/aliases/{index}/target");
                continue;
            }
            if (targetField.Role == FieldRole.Computed)
            {
                Add(
                    diagnostics,
                    "relationDraft.convention.aliasTargetComputed",
                    DiagnosticSeverity.Error,
                    $"Explicit alias target '{alias.Target}' is computed and cannot have an assignment slot.",
                    $"/request/aliases/{index}/target");
                continue;
            }

            if (!TryGetTopLevelField(alias.Source, out _))
            {
                Add(
                    diagnostics,
                    "relationDraft.convention.aliasSourceNotTopLevel",
                    DiagnosticSeverity.Error,
                    $"Explicit alias source '{alias.Source}' is not a top-level source field.",
                    $"/request/aliases/{index}/source");
            }

            if (!byTarget.TryAdd(alias.Target, alias))
            {
                conflicts.Add(alias.Target);
                Add(
                    diagnostics,
                    "relationDraft.convention.aliasTargetDuplicate",
                    DiagnosticSeverity.Error,
                    $"Target field '{alias.Target}' has more than one explicit alias.",
                    $"/request/aliases/{index}/target");
            }
        }

        foreach (var conflict in conflicts)
            byTarget.Remove(conflict);

        return new(byTarget, conflicts);
    }

    static bool TryGetTopLevelField(FieldPath path, out string fieldName)
    {
        if (path.Segments.Length == 1
            && path.Segments[0] is { Kind: SegmentKind.Field, Segment: { } segment }
            && !string.IsNullOrWhiteSpace(segment))
        {
            fieldName = segment;
            return true;
        }

        fieldName = string.Empty;
        return false;
    }

    static string SlotLocation(QueryAssignmentId slotId) =>
        $"/draft/projection/assignments/{slotId.Value}";

    static void Add(
        ICollection<DocumentValidationDiagnostic> diagnostics,
        string code,
        DiagnosticSeverity severity,
        string message,
        string location) =>
        diagnostics.Add(new(code, severity, message, location));

    readonly record struct AliasIndex(
        IReadOnlyDictionary<FieldPath, RelationDraftFieldAlias> ByTarget,
        IReadOnlySet<FieldPath> ConflictedTargets);

    readonly record struct MatchedCandidate(
        RelationDraftCandidate Candidate,
        FieldPath Source,
        string RuleId,
        ImmutableArray<DirectFieldCompatibilityIssue> Issues);
}

readonly record struct DirectFieldCompatibilityIssue(
    string Code,
    string Message,
    RelationDraftUnresolvedReason Reason);

static class DirectFieldAssignmentCompatibility
{
    public static ImmutableArray<DirectFieldCompatibilityIssue> Evaluate(
        FieldDefinition source,
        GraphId sourceGraph,
        FieldDefinition target,
        GraphId targetGraph)
    {
        var issues = ImmutableArray.CreateBuilder<DirectFieldCompatibilityIssue>();

        if (source.Type != target.Type)
        {
            issues.Add(new(
                "relationDraft.assignment.typeIncompatible",
                $"source type '{source.Type}' does not exactly match target type '{target.Type}'.",
                RelationDraftUnresolvedReason.IncompatibleType));
            issues.Add(new(
                "relationDraft.assignment.conversionRequired",
                "a portable conversion would be required, but no conversion semantics are declared.",
                RelationDraftUnresolvedReason.ConversionRequired));
        }
        else if (sourceGraph != targetGraph && ContainsNamedTypeReference(source.Type))
        {
            issues.Add(new(
                "relationDraft.assignment.typeIncompatible",
                "graph-local named type identifiers from different shape graphs are not identical.",
                RelationDraftUnresolvedReason.IncompatibleType));
        }
        else if (ContainsOpaqueRuntimeTypeReference(source.Type))
        {
            issues.Add(new(
                "relationDraft.assignment.transformationUnsupported",
                "an opaque runtime type anywhere in the value does not provide portable assignment semantics.",
                RelationDraftUnresolvedReason.UnsupportedTransformation));
        }

        if (source.Cardinality != target.Cardinality)
        {
            issues.Add(new(
                "relationDraft.assignment.cardinalityUnsafe",
                $"source cardinality '{source.Cardinality}' cannot flow directly to target cardinality '{target.Cardinality}'.",
                RelationDraftUnresolvedReason.UnsafeCardinality));
        }

        if (source.Presence == FieldPresence.Optional
            && target.Presence == FieldPresence.Required)
        {
            issues.Add(new(
                "relationDraft.assignment.presenceUnsafe",
                "an optional source can be absent while the target is required.",
                RelationDraftUnresolvedReason.UnsafePresence));
        }

        if (source.Nullability == FieldNullability.Nullable
            && target.Nullability == FieldNullability.NonNullable)
        {
            issues.Add(new(
                "relationDraft.assignment.nullabilityUnsafe",
                "a nullable source can produce null while the target is non-nullable.",
                RelationDraftUnresolvedReason.UnsafeNullability));
        }

        return issues.ToImmutable();
    }

    static bool ContainsNamedTypeReference(TypeRef type) => type switch
    {
        NamedTypeRef => true,
        ArrayTypeRef array => ContainsNamedTypeReference(array.ElementType),
        ObjectTypeRef obj => obj.Fields.Any(static field => ContainsNamedTypeReference(field.Type)),
        _ => false
    };

    static bool ContainsOpaqueRuntimeTypeReference(TypeRef type) => type switch
    {
        OpaqueRuntimeTypeRef => true,
        ArrayTypeRef array => ContainsOpaqueRuntimeTypeReference(array.ElementType),
        ObjectTypeRef obj => obj.Fields.Any(static field => ContainsOpaqueRuntimeTypeReference(field.Type)),
        _ => false
    };
}
